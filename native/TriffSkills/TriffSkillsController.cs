using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TriffView.Eve;

namespace TriffView.TriffSkills;

internal sealed class TriffSkillsController : IDisposable
{
    internal const string CredentialPrefix = "TriffView.TriffSkills.RefreshToken.";
    internal const string FleetCredentialPrefix = "TriffView.TriffFleets.RefreshToken.";
    internal const int MaxBridgePayloadBytes = 600 * 1024;
    private const string DefaultClientId = "7d2454c3191c4254a4b67d8f71f2b972";
    private const string RedirectUri = "http://127.0.0.1:51777/trifffleets/callback/";
    private const string UserAgent = "TriffView/1.6.2 (+https://github.com/NarcisussX/TriffView)";
    private const int SkillCategoryId = 16;
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> RequiredScopes = new(StringComparer.Ordinal)
    {
        "esi-skills.read_skills.v1",
        "esi-skills.read_skillqueue.v1",
    };

#if DEBUG
    private static readonly string ClientId = Environment.GetEnvironmentVariable("TRIFFVIEW_TRIFFSKILLS_CLIENT_ID")?.Trim() is { Length: > 0 } value
        ? value
        : DefaultClientId;
    private const bool ClientIdOverrideAllowed = true;
#else
    private const string ClientId = DefaultClientId;
    private const bool ClientIdOverrideAllowed = false;
#endif

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Action<object> _post;
    private readonly ICredentialStore _credentials;
    private readonly EsiClient _esi;
    private readonly IEveSsoClient _sso;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _characterLocks = new();
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _resolveGate = new(1, 1);
    private readonly SemaphoreSlim _resolvePassGate = new(1, 1);
    private readonly ConcurrentDictionary<long, AccessTokenCache> _accessTokens = [];
    private readonly ConcurrentDictionary<int, int> _groupCategories = [];
    private readonly ConcurrentDictionary<string, PendingPlanPreview> _pendingPreviews = new(StringComparer.Ordinal);
    private readonly TriffSkillsState _state;
    private readonly SkillIdCache _skillIds;
    private readonly List<string> _warnings = [];
    private List<SkillPlan> _plans = [];
    private List<PlanFileIssue> _planIssues = [];
    private DateTimeOffset? _plansUpdatedUtc;
    private CancellationTokenSource? _authCancellation;
    private bool _authInProgress;
    private int _refreshRequested;
    private int _resolveRequested;
    private string _lastPostedState = string.Empty;

    public TriffSkillsController(Action<object> post)
        : this(post, new WindowsCredentialStore(), CreateEsiClient(), CreateSsoClient(), TimeProvider.System)
    {
    }

    internal TriffSkillsController(
        Action<object> post,
        ICredentialStore credentials,
        EsiClient esi,
        IEveSsoClient sso,
        TimeProvider time)
    {
        _post = post;
        _credentials = credentials;
        _esi = esi;
        _sso = sso;
        _time = time;

        var stateLoad = TriffSkillsState.Load();
        _state = stateLoad.State;
        if (!string.IsNullOrWhiteSpace(stateLoad.Warning)) _warnings.Add(stateLoad.Warning);

        var cacheLoad = SkillIdCache.Load();
        _skillIds = cacheLoad.Cache;
        if (!string.IsNullOrWhiteSpace(cacheLoad.Warning)) _warnings.Add(cacheLoad.Warning);

        RecoverOwnCredentials();
        var seedWarning = PlanStore.EnsureSeeded(TriffSkillsPaths.PlansDir);
        if (!string.IsNullOrWhiteSpace(seedWarning)) _warnings.Add(seedWarning);
        LoadPlans();
    }

    private static EsiClient CreateEsiClient() => new(SharedHttp, JsonOptions, UserAgent);

    private static IEveSsoClient CreateSsoClient()
    {
        var keys = new EveSigningKeySource(SharedHttp);
        var validator = new EveJwtValidator(ClientId, RequiredScopes, keys);
        return new EveSsoClient(
            SharedHttp,
            new EveSsoOptions(ClientId, RedirectUri, RequiredScopes, UserAgent, "TriffSkills"),
            validator);
    }

    public bool HandleWebMessage(string type, JsonObject? message)
    {
        if (type.Length > 80) return false;
        switch (type)
        {
            case "triffskills:get-state":
                PostState(force: true);
                _ = ResolvePlanNamesAsync();
                return true;
            case "triffskills:auth":
                _ = StartAuthAsync();
                return true;
            case "triffskills:cancel-auth":
                _authCancellation?.Cancel();
                return true;
            case "triffskills:forget-character":
                _ = ForgetCharacterAsync(ReadLong(message, "characterId"));
                return true;
            case "triffskills:refresh-characters":
                _ = RefreshCharactersAsync();
                return true;
            case "triffskills:refresh-plans":
                _ = ReloadPlansAsync();
                return true;
            case "triffskills:open-plans-folder":
                OpenPlansFolder();
                return true;
            case "triffskills:preview-plan":
                _ = PreviewPlanAsync(message);
                return true;
            case "triffskills:commit-plan":
                _ = CommitPlanAsync(message);
                return true;
            case "triffskills:get-cell-detail":
                PostCellDetail(message);
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _authCancellation?.Cancel();
        _authCancellation?.Dispose();
        _lifetime.Dispose();
        // In-flight operations observe _lifetime and finish shortly. Their gates are
        // intentionally left for GC so cancellation cannot race a disposed semaphore.
    }

    private async Task StartAuthAsync()
    {
        if (!await _authGate.WaitAsync(0, _lifetime.Token))
        {
            PostError("auth", "Character authentication is already in progress.");
            return;
        }

        _authInProgress = true;
        var charactersAtStart = _state.Characters.Select(character => character.CharacterId).ToHashSet();
        _authCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        PostState(force: true);
        try
        {
            var token = await _sso.AuthorizeAsync(AuthTimeout, _authCancellation.Token);
            var characterId = token.Identity.CharacterId;
            var gate = CharacterLock(characterId);
            await gate.WaitAsync(_authCancellation.Token);
            try
            {
                if (charactersAtStart.Contains(characterId) && _state.Find(characterId) is null)
                {
                    throw new OperationCanceledException("The character was forgotten while reauthorization was in progress.");
                }
                CommitAuthentication(token);
            }
            finally
            {
                gate.Release();
            }

            PostState(force: true);
            _ = RefreshCharactersAsync();
        }
        catch (OperationCanceledException)
        {
            PostError("auth", "EVE SSO authentication was cancelled.");
        }
        catch (TimeoutException exception)
        {
            PostError("auth", exception.Message);
        }
        catch (SocketException exception)
        {
            PostError("auth", $"Could not open the local SSO callback listener at {RedirectUri}. {exception.Message}");
        }
        catch (OAuthTokenException exception)
        {
            PostError("auth", exception.Message);
        }
        catch (Exception exception)
        {
            PostError("auth", exception.Message);
        }
        finally
        {
            _authInProgress = false;
            _authCancellation?.Dispose();
            _authCancellation = null;
            PostState(force: true);
            _authGate.Release();
        }
    }

    private void CommitAuthentication(EveValidatedToken token)
    {
        if (string.IsNullOrWhiteSpace(token.RefreshToken)) throw new InvalidDataException("EVE SSO returned no refresh token.");
        var identity = token.Identity;
        var target = CredentialTarget(identity.CharacterId);
        var previousSecret = _credentials.Read(target);
        var existingIndex = _state.Characters.FindIndex(character => character.CharacterId == identity.CharacterId);
        var previousCharacter = existingIndex >= 0 ? _state.Characters[existingIndex].Clone() : null;
        var previousSelection = _state.SelectedCharacterId;

        _credentials.Write(target, token.RefreshToken);
        try
        {
            var character = _state.Upsert(identity.CharacterId);
            var ownershipChanged = !string.IsNullOrWhiteSpace(character.OwnerHash)
                && !string.Equals(character.OwnerHash, identity.OwnerHash, StringComparison.Ordinal);
            character.CharacterName = identity.CharacterName;
            character.OwnerHash = identity.OwnerHash;
            character.Scopes = identity.Scopes.OrderBy(scope => scope, StringComparer.Ordinal).ToList();
            character.AuthenticatedUtc = _time.GetUtcNow();
            character.Error = ownershipChanged ? "Character ownership changed; cached skill data was cleared." : string.Empty;
            character.NeedsReauth = false;
            if (ownershipChanged)
            {
                character.ActiveLevels.Clear();
                character.TrainedLevels.Clear();
                character.Queue.Clear();
                character.FetchedUtc = null;
            }
            _state.SelectedCharacterId = identity.CharacterId;

            if (!_state.TrySave(out var saveError))
            {
                throw new IOException($"Could not save character state: {saveError}");
            }
        }
        catch
        {
            if (previousCharacter is null) _state.Characters.RemoveAll(character => character.CharacterId == identity.CharacterId);
            else _state.Characters[existingIndex] = previousCharacter;
            _state.SelectedCharacterId = previousSelection;
            try
            {
                if (previousSecret is null) _credentials.Delete(target);
                else _credentials.Write(target, previousSecret);
            }
            catch (Exception rollback)
            {
                _warnings.Add($"Authentication rollback could not restore the prior credential: {rollback.Message}");
            }
            throw;
        }

        _accessTokens[identity.CharacterId] = Cache(token);
    }

    private async Task ForgetCharacterAsync(long characterId)
    {
        if (characterId <= 0) return;
        var gate = CharacterLock(characterId);
        await gate.WaitAsync(_lifetime.Token);
        try
        {
            var character = _state.Find(characterId);
            if (character is null) return;
            var previousSecret = _credentials.Read(CredentialTarget(characterId));
            try
            {
                _credentials.Delete(CredentialTarget(characterId));
            }
            catch (Exception exception)
            {
                character.Error = $"Credential deletion failed; the character was not forgotten. {exception.Message}";
                PostError("forget", character.Error);
                PostState(force: true);
                return;
            }

            var index = _state.Characters.IndexOf(character);
            var previous = character.Clone();
            var previousSelection = _state.SelectedCharacterId;
            _state.Characters.RemoveAt(index);
            if (_state.SelectedCharacterId == characterId) _state.SelectedCharacterId = _state.Characters.FirstOrDefault()?.CharacterId ?? 0;
            if (!_state.TrySave(out var saveError))
            {
                _state.Characters.Insert(index, previous);
                _state.SelectedCharacterId = previousSelection;
                try
                {
                    if (previousSecret is not null) _credentials.Write(CredentialTarget(characterId), previousSecret);
                    previous.Error = $"Forget was rolled back because state could not be saved: {saveError}";
                }
                catch (Exception rollback)
                {
                    previous.NeedsReauth = true;
                    previous.Error = $"Credential was deleted and state could not be saved; credential rollback also failed: {rollback.Message}";
                }
                PostError("forget", previous.Error);
                PostState(force: true);
                return;
            }

            _accessTokens.TryRemove(characterId, out _);
            PostState(force: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RefreshCharactersAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        Interlocked.Exchange(ref _refreshRequested, 1);
        if (!await _refreshGate.WaitAsync(0, _lifetime.Token))
        {
            return;
        }

        PostState(force: true);
        try
        {
            while (Interlocked.Exchange(ref _refreshRequested, 0) == 1)
            {
                var characters = _state.Characters.ToArray();
                for (var index = 0; index < characters.Length; index++)
                {
                    var character = characters[index];
                    await RefreshOneCharacterAsync(character.CharacterId, _lifetime.Token);
                    PostProgress(character.CharacterId, index + 1, characters.Length);
                }
            }
        }
        finally
        {
            _refreshGate.Release();
            PostState(force: true);
            if (!_lifetime.IsCancellationRequested && Volatile.Read(ref _refreshRequested) == 1) _ = RefreshCharactersAsync();
        }
    }

    private async Task RefreshOneCharacterAsync(long characterId, CancellationToken cancellationToken)
    {
        var character = _state.Find(characterId);
        if (character is null) return;
        try
        {
            var skills = await SendCharacterEsiAsync<CharacterSkillsResponse>(
                characterId,
                HttpMethod.Get,
                $"/v4/characters/{characterId}/skills/",
                cancellationToken);
            if (!UseCharacterResponse(characterId, skills)) return;

            var queue = await SendCharacterEsiAsync<List<SkillQueueItem>>(
                characterId,
                HttpMethod.Get,
                $"/v2/characters/{characterId}/skillqueue/",
                cancellationToken);
            if (!UseCharacterResponse(characterId, queue)) return;

            var snapshot = EsiSkillMapper.ToSnapshot(skills.Value);
            _state.ApplyFetchSuccess(characterId, snapshot.ActiveLevels, snapshot.TrainedLevels, EsiSkillMapper.ToQueue(queue.Value), _time.GetUtcNow());
            if (!_state.TrySave(out var saveError))
            {
                _state.ApplyFetchFailure(characterId, $"Fresh data is in memory but was not saved for offline use: {saveError}", needsReauth: false);
                PostError("refresh-characters", $"{character.CharacterName}: state persistence failed.");
            }
        }
        catch (OAuthTokenException exception)
        {
            var definitive = exception.IsDefinitiveAuthorizationFailure;
            _state.ApplyFetchFailure(characterId, definitive
                ? "Re-authenticate this character; EVE rejected the stored authorization."
                : $"Could not refresh the sign-in; last-good data remains available. {exception.Message}", definitive);
            PostError("refresh-characters", $"{character.CharacterName}: {_state.Find(characterId)?.Error}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _state.ApplyFetchFailure(characterId, $"Refresh failed; last-good data remains available. {exception.Message}", needsReauth: false);
            PostError("refresh-characters", $"{character.CharacterName}: {_state.Find(characterId)?.Error}");
        }
    }

    private async Task<EsiResponse<T>> SendCharacterEsiAsync<T>(
        long characterId,
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        var token = await AccessTokenForAsync(characterId, forceRefresh: false, rejectedAccessToken: null, cancellationToken);
        var response = await _esi.SendAsync<T>(method, path, token, cancellationToken: cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        token = await AccessTokenForAsync(characterId, forceRefresh: true, rejectedAccessToken: token, cancellationToken);
        return await _esi.SendAsync<T>(method, path, token, cancellationToken: cancellationToken);
    }

    private bool UseCharacterResponse<T>(long characterId, EsiResponse<T> response)
    {
        if (response.IsSuccess) return true;
        var character = _state.Find(characterId);
        if (character is null) return false;

        var definitive = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "Re-authenticate this character; the granted token is missing a required skill scope.",
            HttpStatusCode.Unauthorized => "Re-authenticate this character; EVE rejected the refreshed access token.",
            _ => $"{response.Method} {response.Path} returned {(int)response.StatusCode}: {response.Error}",
        };
        _state.ApplyFetchFailure(characterId, message, definitive);
        PostError("refresh-characters", $"{character.CharacterName}: {message}");
        return false;
    }

    private async Task<string> AccessTokenForAsync(
        long characterId,
        bool forceRefresh,
        string? rejectedAccessToken,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_accessTokens.TryGetValue(characterId, out var cached)
            && cached.ExpiresUtc > now.AddSeconds(30)
            && (!forceRefresh || !string.Equals(cached.AccessToken, rejectedAccessToken, StringComparison.Ordinal)))
        {
            return cached.AccessToken;
        }

        var gate = CharacterLock(characterId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            now = _time.GetUtcNow();
            if (_accessTokens.TryGetValue(characterId, out cached)
                && cached.ExpiresUtc > now.AddSeconds(30)
                && (!forceRefresh || !string.Equals(cached.AccessToken, rejectedAccessToken, StringComparison.Ordinal)))
            {
                return cached.AccessToken;
            }
            if (forceRefresh) _accessTokens.TryRemove(characterId, out _);

            var character = _state.Find(characterId) ?? throw new OperationCanceledException("Character was forgotten.");
            var target = CredentialTarget(characterId);
            var previousRefresh = _credentials.Read(target);
            if (string.IsNullOrWhiteSpace(previousRefresh))
            {
                throw new OAuthTokenException(HttpStatusCode.Unauthorized, "invalid_grant", "No TriffSkills refresh token is stored for this character.");
            }

            var token = await _sso.RefreshAsync(previousRefresh, cancellationToken);
            if (token.Identity.CharacterId != characterId)
            {
                throw new OAuthTokenException(HttpStatusCode.Unauthorized, "identity_mismatch", "Refreshed token belongs to a different character.");
            }
            if (!string.IsNullOrWhiteSpace(character.OwnerHash)
                && !string.Equals(character.OwnerHash, token.Identity.OwnerHash, StringComparison.Ordinal))
            {
                throw new OAuthTokenException(HttpStatusCode.Unauthorized, "owner_changed", "Character ownership changed.");
            }

            var replacement = string.IsNullOrWhiteSpace(token.RefreshToken) ? previousRefresh : token.RefreshToken;
            if (!string.Equals(replacement, previousRefresh, StringComparison.Ordinal))
            {
                try
                {
                    _credentials.Write(target, replacement);
                }
                catch
                {
                    try { _credentials.Write(target, previousRefresh); } catch { /* surfaced by the original write failure */ }
                    throw;
                }
            }

            var previousCharacter = character.Clone();
            character.CharacterName = token.Identity.CharacterName;
            character.OwnerHash = token.Identity.OwnerHash;
            character.Scopes = token.Identity.Scopes.OrderBy(scope => scope, StringComparer.Ordinal).ToList();
            if (!_state.TrySave(out var stateError))
            {
                character.CharacterName = previousCharacter.CharacterName;
                character.OwnerHash = previousCharacter.OwnerHash;
                character.Scopes = previousCharacter.Scopes;
                throw new IOException($"Authorization metadata could not be saved; the validated rotated credential was retained: {stateError}");
            }
            _accessTokens[characterId] = Cache(token);
            return token.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private void LoadPlans()
    {
        try
        {
            var result = PlanStore.LoadAll(TriffSkillsPaths.PlansDir);
            _plans = result.Plans.ToList();
            _planIssues = result.Issues.ToList();
            _plansUpdatedUtc = result.LatestWriteUtc;
        }
        catch (Exception exception)
        {
            _planIssues = [new PlanFileIssue("plans", $"Could not read plans folder: {exception.Message}", [])];
        }
    }

    private async Task ReloadPlansAsync()
    {
        LoadPlans();
        PostState(force: true);
        await ResolvePlanNamesAsync();
    }

    private async Task ResolvePlanNamesAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        Interlocked.Exchange(ref _resolveRequested, 1);
        if (!await _resolvePassGate.WaitAsync(0, _lifetime.Token)) return;

        try
        {
            while (Interlocked.Exchange(ref _resolveRequested, 0) == 1)
            {
                var names = _plans.SelectMany(plan => plan.Requirements).Select(requirement => requirement.SkillName).ToArray();
                var failures = await ResolveAndValidateNamesAsync(names, _lifetime.Token);
                if (failures.Count > 0)
                {
                    PostError("plans", $"{failures.Count} plan skill name(s) are unresolved or are not EVE skills.");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App is closing.
        }
        catch (Exception exception)
        {
            PostError("plans", $"Skill-name validation failed: {exception.Message}");
        }
        finally
        {
            _resolvePassGate.Release();
            PostState(force: true);
            if (!_lifetime.IsCancellationRequested && Volatile.Read(ref _resolveRequested) == 1) _ = ResolvePlanNamesAsync();
        }
    }

    private async Task<Dictionary<string, string>> ResolveAndValidateNamesAsync(
        IEnumerable<string> requestedNames,
        CancellationToken cancellationToken)
    {
        await _resolveGate.WaitAsync(cancellationToken);
        try
        {
            var missing = _skillIds.Unresolved(requestedNames);
            var failures = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (missing.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var resolved = new Dictionary<string, SkillsUniverseIdName>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in SkillIdCache.Batch(missing))
            {
                var response = await _esi.SendAsync<SkillsUniverseIdsResponse>(
                    HttpMethod.Post,
                    "/v3/universe/ids/",
                    accessToken: null,
                    body: batch,
                    cancellationToken);
                response.ThrowIfFailed();
                foreach (var item in response.Value?.InventoryTypes ?? [])
                {
                    if (item.Id > 0 && !string.IsNullOrWhiteSpace(item.Name)) resolved[item.Name.Trim()] = item;
                }
            }

            var validated = new ConcurrentBag<(string Name, ValidatedSkillType Skill)>();
            using var concurrency = new SemaphoreSlim(4, 4);
            var checks = missing.Select(async name =>
            {
                if (!resolved.TryGetValue(name, out var item))
                {
                    failures[name] = "Name was not resolved by ESI.";
                    return;
                }
                await concurrency.WaitAsync(cancellationToken);
                try
                {
                    var type = await _esi.SendAsync<UniverseTypeResponse>(HttpMethod.Get, $"/v3/universe/types/{item.Id}/", null, cancellationToken: cancellationToken);
                    type.ThrowIfFailed();
                    var groupId = type.Value?.GroupId ?? 0;
                    if (groupId <= 0)
                    {
                        failures[name] = "Resolved type had no valid group.";
                        return;
                    }

                    if (!_groupCategories.TryGetValue(groupId, out var categoryId))
                    {
                        var group = await _esi.SendAsync<UniverseGroupResponse>(HttpMethod.Get, $"/v1/universe/groups/{groupId}/", null, cancellationToken: cancellationToken);
                        group.ThrowIfFailed();
                        categoryId = group.Value?.CategoryId ?? 0;
                        _groupCategories[groupId] = categoryId;
                    }
                    if (categoryId != SkillCategoryId)
                    {
                        failures[name] = "Resolved inventory type is not in EVE's skill category.";
                        return;
                    }
                    validated.Add((name, new ValidatedSkillType(item.Id, groupId, categoryId)));
                }
                finally
                {
                    concurrency.Release();
                }
            }).ToArray();
            await Task.WhenAll(checks);

            if (_skillIds.Merge(validated) > 0 && !_skillIds.TrySave(out var cacheError))
            {
                PostError("plans", $"Validated skill names are available now but were not cached for offline use: {cacheError}");
            }
            return new Dictionary<string, string>(failures, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _resolveGate.Release();
        }
    }

    private async Task PreviewPlanAsync(JsonObject? message)
    {
        var requestId = ReadRequestId(message);
        if (requestId.Length == 0) return;
        var name = ReadString(message, "name", PlanNameValidator.MaxNameLength + 8);
        var contents = ReadRawString(message, "contents");
        if (contents.Length > SkillPlanParser.MaxContentCharacters)
        {
            PostPlanPreview(requestId, null, [new PlanDiagnostic(0, $"Plan exceeds {SkillPlanParser.MaxContentCharacters:N0} characters.")]);
            return;
        }
        if (Encoding.UTF8.GetByteCount(contents) > MaxBridgePayloadBytes)
        {
            PostPlanPreview(requestId, null, [new PlanDiagnostic(0, "Plan payload exceeds the bridge-size limit.")]);
            return;
        }
        if (!PlanNameValidator.TryValidate(name, out var normalizedName, out var nameError))
        {
            PostPlanPreview(requestId, null, [new PlanDiagnostic(0, nameError)]);
            return;
        }

        var parsed = SkillPlanParser.Parse(normalizedName, contents);
        if (!parsed.IsValid || parsed.Plan is null)
        {
            PostPlanPreview(requestId, null, parsed.Diagnostics);
            return;
        }

        try
        {
            var failures = await ResolveAndValidateNamesAsync(parsed.Plan.Requirements.Select(requirement => requirement.SkillName), _lifetime.Token);
            if (failures.Count > 0)
            {
                PostPlanPreview(requestId, null, failures.Select(pair => new PlanDiagnostic(0, $"{pair.Key}: {pair.Value}")).ToArray());
                return;
            }

            PrunePreviews();
            _pendingPreviews[requestId] = new PendingPlanPreview(normalizedName, contents, parsed.Plan, _time.GetUtcNow().Add(PreviewLifetime));
            PostPlanPreview(requestId, parsed.Plan, []);
        }
        catch (Exception exception)
        {
            PostPlanPreview(requestId, null, [new PlanDiagnostic(0, $"Could not validate skill names: {exception.Message}")]);
        }
    }

    private async Task CommitPlanAsync(JsonObject? message)
    {
        var requestId = ReadRequestId(message);
        if (requestId.Length == 0) return;
        PrunePreviews();
        if (!_pendingPreviews.TryGetValue(requestId, out var preview))
        {
            PostRequestError("plan-commit", requestId, "Validated preview expired or was already used. Preview the plan again.");
            return;
        }

        var result = PlanStore.CommitValidated(
            TriffSkillsPaths.PlansDir,
            preview.Name,
            preview.Contents,
            preview.Plan,
            ReadBool(message, "replace"));
        if (result.Collision)
        {
            _post(new { type = "triffskills:plan-commit", requestId, ok = false, collision = true, name = result.Name });
            return;
        }
        if (!result.Success)
        {
            PostRequestError("plan-commit", requestId, result.Error);
            return;
        }

        LoadPlans();
        var loaded = _plans.FirstOrDefault(plan => string.Equals(plan.Name, result.Name, StringComparison.OrdinalIgnoreCase));
        if (loaded is null || !loaded.Requirements.SequenceEqual(preview.Plan.Requirements))
        {
            PostRequestError("plan-commit", requestId, "Plan was written but the plans folder did not reload it successfully.");
            return;
        }

        _pendingPreviews.TryRemove(requestId, out _);
        PostState(force: true);
        _post(new { type = "triffskills:plan-commit", requestId, ok = true, collision = false, name = result.Name });
        await ResolvePlanNamesAsync();
    }

    private void PostCellDetail(JsonObject? message)
    {
        var requestId = ReadRequestId(message);
        if (requestId.Length == 0) return;
        var characterId = ReadLong(message, "characterId");
        var planName = ReadString(message, "planName", PlanNameValidator.MaxNameLength);
        var character = _state.Find(characterId);
        var plan = _plans.FirstOrDefault(item => string.Equals(item.Name, planName, StringComparison.OrdinalIgnoreCase));
        var analysis = TriffSkillsMatrix.BuildDetail(character, plan, _skillIds.TypeIds());
        if (analysis is null)
        {
            PostRequestError("cell-detail", requestId, "Character or plan no longer exists.");
            return;
        }

        _post(new
        {
            type = "triffskills:cell-detail",
            requestId,
            ok = true,
            characterId,
            planName = plan!.Name,
            readiness = analysis.Readiness.ToString(),
            analysis.EstimatedFinishUtc,
            analysis.QueueTimingUnknown,
            requirements = analysis.Requirements.Select(item => new
            {
                item.SkillName,
                item.RequiredLevel,
                item.ActiveLevel,
                item.TrainedLevel,
                state = item.State.ToString(),
                item.QueuedFinishUtc,
                item.QueueTimingUnknown,
            }).ToArray(),
        });
    }

    private void OpenPlansFolder()
    {
        try
        {
            Directory.CreateDirectory(TriffSkillsPaths.PlansDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TriffSkillsPaths.PlansDir}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            PostError("open-plans-folder", exception.Message);
        }
    }

    private void RecoverOwnCredentials()
    {
        try
        {
            var recovered = 0;
            foreach (var target in _credentials.EnumerateTargets(CredentialPrefix))
            {
                var suffix = target[CredentialPrefix.Length..];
                if (!long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var characterId) || characterId <= 0) continue;
                if (_state.Find(characterId) is not null) continue;
                var character = _state.Upsert(characterId);
                character.CharacterName = $"Recovered character {characterId}";
                character.Error = "Recovered a TriffSkills credential whose state row was missing. Refresh it or forget it.";
                recovered++;
            }
            if (recovered > 0)
            {
                _warnings.Add($"Recovered {recovered} TriffSkills credential(s) that had no visible state row.");
                if (!_state.TrySave(out var error)) _warnings.Add($"Recovered credential rows could not be saved: {error}");
            }
        }
        catch (Exception exception)
        {
            _warnings.Add($"Could not inspect the TriffSkills credential namespace for recovery: {exception.Message}");
        }
    }

    private void PostState(bool force = false)
    {
        var matrix = TriffSkillsMatrix.BuildCompact(_state.Characters, _plans, _skillIds.TypeIds());
        var state = new
        {
            type = "triffskills:state",
            authConfigured = !string.IsNullOrWhiteSpace(ClientId),
            authInProgress = _authInProgress,
            refreshInFlight = _refreshGate.CurrentCount == 0,
            clientIdOverrideAllowed = ClientIdOverrideAllowed,
            characters = _state.Characters.Select(character => new
            {
                character.CharacterId,
                character.CharacterName,
                character.FetchedUtc,
                character.Error,
                character.NeedsReauth,
                stale = character.FetchedUtc is not null && !string.IsNullOrWhiteSpace(character.Error),
            }).ToArray(),
            plans = matrix.Plans,
            matrix = matrix.Cells.Select(cell => new
            {
                cell.CharacterId,
                cell.PlanName,
                readiness = cell.Readiness.ToString(),
                cell.EstimatedFinishUtc,
                cell.QueueTimingUnknown,
                cell.ActiveCount,
                cell.TrainedInactiveCount,
                cell.QueuedCount,
                cell.MissingCount,
                cell.UnknownCount,
            }).ToArray(),
            planIssues = _planIssues.Select(issue => new
            {
                issue.FileName,
                issue.Message,
                diagnostics = issue.Diagnostics.Take(20).ToArray(),
            }).ToArray(),
            warnings = _warnings.Take(20).ToArray(),
            plansUpdatedUtc = _plansUpdatedUtc?.ToString("o") ?? string.Empty,
        };
        var json = JsonSerializer.Serialize(state, JsonOptions);
        if (!force && string.Equals(json, _lastPostedState, StringComparison.Ordinal)) return;
        _lastPostedState = json;
        _post(state);
    }

    private void PostProgress(long characterId, int completed, int total)
    {
        var character = _state.Find(characterId);
        _post(new
        {
            type = "triffskills:refresh-progress",
            characterId,
            completed,
            total,
            error = character?.Error ?? string.Empty,
            needsReauth = character?.NeedsReauth ?? false,
            fetchedUtc = character?.FetchedUtc,
        });
    }

    private void PostPlanPreview(string requestId, SkillPlan? plan, IReadOnlyList<PlanDiagnostic> diagnostics)
    {
        _post(new
        {
            type = "triffskills:plan-preview",
            requestId,
            ok = plan is not null && diagnostics.Count == 0,
            name = plan?.Name ?? string.Empty,
            requirementCount = plan?.Requirements.Count ?? 0,
            requirements = plan?.Requirements.Take(50).ToArray() ?? [],
            diagnostics = diagnostics.Take(100).ToArray(),
        });
    }

    private void PostRequestError(string action, string requestId, string message)
        => _post(new { type = $"triffskills:{action}", requestId, ok = false, collision = false, message });

    private void PostError(string action, string message)
        => _post(new { type = "triffskills:error", action, message });

    private void PrunePreviews()
    {
        var now = _time.GetUtcNow();
        foreach (var key in _pendingPreviews.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _pendingPreviews.TryRemove(key, out _);
        }
        while (_pendingPreviews.Count >= 5) _pendingPreviews.TryRemove(_pendingPreviews.First().Key, out _);
    }

    private SemaphoreSlim CharacterLock(long characterId) => _characterLocks.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
    private static string CredentialTarget(long characterId) => CredentialPrefix + characterId.ToString(CultureInfo.InvariantCulture);
    private AccessTokenCache Cache(EveValidatedToken token)
        => new(token.AccessToken, _time.GetUtcNow().AddSeconds(Math.Max(30, token.ExpiresIn - 60)));

    private static long ReadLong(JsonObject? message, string key)
    {
        if (message?[key] is not JsonValue value) return 0;
        if (value.TryGetValue<long>(out var number)) return number;
        return value.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static string ReadString(JsonObject? message, string key, int maxLength)
    {
        if (message?[key] is not JsonValue value || !value.TryGetValue<string>(out var text)) return string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string ReadRawString(JsonObject? message, string key)
        => message?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static bool ReadBool(JsonObject? message, string key)
        => message?[key] is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static string ReadRequestId(JsonObject? message)
    {
        var value = ReadString(message, "requestId", 65);
        return value.Length is >= 8 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : string.Empty;
    }

    private sealed record AccessTokenCache(string AccessToken, DateTimeOffset ExpiresUtc);
    private sealed record PendingPlanPreview(string Name, string Contents, SkillPlan Plan, DateTimeOffset ExpiresUtc);
}
