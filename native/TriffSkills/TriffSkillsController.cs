using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TriffView.Eve;

namespace TriffView.TriffSkills;

internal sealed class TriffSkillsController : IDisposable
{
    private const string DefaultClientId = "7d2454c3191c4254a4b67d8f71f2b972";
    private const string RedirectUri = "http://127.0.0.1:51777/trifffleets/callback/";
    private const string UserAgent = "TriffView/1.6.2 (+https://github.com/NarcisussX/TriffView)";
    private const int SkillCategoryId = 16;
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> RequiredScopes = new(StringComparer.Ordinal)
    {
        "esi-skills.read_skills.v1",
        "esi-skills.read_skillqueue.v1",
    };

#if DEBUG
    private static readonly string ClientId = Environment.GetEnvironmentVariable("TRIFFVIEW_TRIFFSKILLS_CLIENT_ID")?.Trim() is { Length: > 0 } value
        ? value
        : DefaultClientId;
#else
    private const string ClientId = DefaultClientId;
#endif

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Action<object> _post;
    private readonly EsiClient _esi;
    private readonly TimeProvider _time;
    private readonly TriffSkillsAuthentication _authentication;
    private readonly PlanImportWorkflow _planImports;
    private readonly Func<string?> _saveState;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _resolveGate = new(1, 1);
    private readonly SemaphoreSlim _resolvePassGate = new(1, 1);
    private readonly ConcurrentDictionary<int, int> _groupCategories = [];
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
        TimeProvider time,
        Func<string?>? saveState = null)
    {
        _post = post;
        _esi = esi;
        _time = time;

        var stateLoad = TriffSkillsState.Load();
        _state = stateLoad.State;
        if (!string.IsNullOrWhiteSpace(stateLoad.Warning)) _warnings.Add(stateLoad.Warning);
        _saveState = saveState ?? (() => _state.TrySave(out var error) ? null : error);
        _authentication = new TriffSkillsAuthentication(_state, credentials, sso, time, _saveState);
        _planImports = new PlanImportWorkflow(TriffSkillsPaths.PlansDir, time, ResolveAndValidateNamesAsync);

        var cacheLoad = SkillIdCache.Load();
        _skillIds = cacheLoad.Cache;
        if (!string.IsNullOrWhiteSpace(cacheLoad.Warning)) _warnings.Add(cacheLoad.Warning);

        _warnings.AddRange(_authentication.RecoverOwnCredentials());
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
            new EveSsoOptions(ClientId, RedirectUri, RequiredScopes, UserAgent),
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
            case "triffskills:reorder-characters":
                ReorderCharacters(message);
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
    }

    private async Task StartAuthAsync()
    {
        if (!await _authGate.WaitAsync(0, _lifetime.Token))
        {
            PostError("auth", "Character authentication is already in progress.");
            return;
        }

        _authInProgress = true;
        _authCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        PostState(force: true);
        try
        {
            await _authentication.AuthorizeAsync(AuthTimeout, _authCancellation.Token);
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

    private async Task ForgetCharacterAsync(long characterId)
    {
        var result = await _authentication.ForgetAsync(characterId, _lifetime.Token);
        if (!result.Success)
        {
            PostError("forget", result.Error);
        }
        PostState(force: true);
    }

    private void ReorderCharacters(JsonObject? message)
    {
        if (message?["characterIds"] is not JsonArray nodes || nodes.Count > TriffSkillsState.MaxCharacters)
        {
            PostError("reorder-characters", "Character order was invalid.");
            return;
        }

        var characterIds = new List<long>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node is not JsonValue value || !value.TryGetValue<long>(out var characterId) || characterId <= 0)
            {
                PostError("reorder-characters", "Character order was invalid.");
                return;
            }
            characterIds.Add(characterId);
        }

        var previous = _state.Characters;
        if (!_state.TryReorderCharacters(characterIds))
        {
            PostError("reorder-characters", "Character order no longer matched the current characters.");
            PostState(force: true);
            return;
        }

        if (_saveState() is not null)
        {
            _state.Characters = previous;
            PostError("reorder-characters", "Character order could not be saved.");
        }
        PostState(force: true);
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
            var saveError = _saveState();
            if (saveError is not null)
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
            PersistRefreshFailure(characterId);
            PostError("refresh-characters", $"{character.CharacterName}: {_state.Find(characterId)?.Error}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _state.ApplyFetchFailure(characterId, $"Refresh failed; last-good data remains available. {exception.Message}", needsReauth: false);
            PersistRefreshFailure(characterId);
            PostError("refresh-characters", $"{character.CharacterName}: {_state.Find(characterId)?.Error}");
        }
    }

    private async Task<EsiResponse<T>> SendCharacterEsiAsync<T>(
        long characterId,
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        var token = await _authentication.AccessTokenAsync(characterId, forceRefresh: false, rejectedAccessToken: null, cancellationToken);
        var response = await _esi.SendAsync<T>(method, path, token, cancellationToken: cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        token = await _authentication.AccessTokenAsync(characterId, forceRefresh: true, rejectedAccessToken: token, cancellationToken);
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
        PersistRefreshFailure(characterId);
        PostError("refresh-characters", $"{character.CharacterName}: {message}");
        return false;
    }

    private void PersistRefreshFailure(long characterId)
    {
        var error = _saveState();
        if (error is not null)
        {
            PostError("refresh-characters", $"Character {characterId}: refresh failure state could not be saved: {error}");
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
        var revision = ReadRevision(message);
        var name = ReadString(message, "name", PlanNameValidator.MaxNameLength + 8);
        var contents = ReadRawString(message, "contents");
        try
        {
            var result = await _planImports.PreviewAsync(requestId, revision, name, contents, _lifetime.Token);
            PostPlanPreview(result);
        }
        catch (Exception exception)
        {
            PostPlanPreview(new PlanPreviewResult(
                requestId,
                revision,
                null,
                [new PlanDiagnostic(0, $"Could not validate skill names: {exception.Message}")]));
        }
    }

    private async Task CommitPlanAsync(JsonObject? message)
    {
        var requestId = ReadRequestId(message);
        if (requestId.Length == 0) return;
        var revision = ReadRevision(message);
        var result = _planImports.Commit(requestId, revision, ReadBool(message, "replace"));
        if (result.Collision)
        {
            _post(new { type = "triffskills:plan-commit", requestId, revision, ok = false, collision = true, expired = false, name = result.Name });
            return;
        }
        if (!result.Success)
        {
            PostRequestError("plan-commit", requestId, revision, result.Error, result.Expired);
            return;
        }

        LoadPlans();
        var loaded = _plans.FirstOrDefault(plan => string.Equals(plan.Name, result.Name, StringComparison.OrdinalIgnoreCase));
        if (loaded is null)
        {
            PostRequestError("plan-commit", requestId, revision, "Plan was saved but the plans folder could not reload it.", expired: false);
            return;
        }

        PostState(force: true);
        _post(new { type = "triffskills:plan-commit", requestId, revision, ok = true, collision = false, expired = false, name = result.Name });
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

    private void PostState(bool force = false)
    {
        var matrix = TriffSkillsMatrix.BuildCompact(_state.Characters, _plans, _skillIds.TypeIds());
        var state = new
        {
            type = "triffskills:state",
            authConfigured = !string.IsNullOrWhiteSpace(ClientId),
            authInProgress = _authInProgress,
            refreshInFlight = _refreshGate.CurrentCount == 0,
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

    private void PostPlanPreview(PlanPreviewResult result)
    {
        _post(new
        {
            type = "triffskills:plan-preview",
            result.RequestId,
            result.Revision,
            ok = result.Plan is not null && result.Diagnostics.Count == 0,
            name = result.Plan?.Name ?? string.Empty,
            requirementCount = result.Plan?.Requirements.Count ?? 0,
            requirements = result.Plan?.Requirements.Take(50).ToArray() ?? [],
            diagnostics = result.Diagnostics.Take(100).ToArray(),
        });
    }

    private void PostRequestError(string action, string requestId, string message)
        => _post(new { type = $"triffskills:{action}", requestId, ok = false, collision = false, message });

    private void PostRequestError(string action, string requestId, long revision, string message, bool expired)
        => _post(new { type = $"triffskills:{action}", requestId, revision, ok = false, collision = false, expired, message });

    private void PostError(string action, string message)
        => _post(new { type = "triffskills:error", action, message });

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

    private static long ReadRevision(JsonObject? message)
        => message?["revision"] is JsonValue value && value.TryGetValue<long>(out var revision) && revision >= 0
            ? revision
            : -1;

    private static string ReadRequestId(JsonObject? message)
    {
        var value = ReadString(message, "requestId", 65);
        return value.Length is >= 8 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : string.Empty;
    }

}
