using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using TriffView.Eve;

namespace TriffView.TriffSkills;

internal sealed record ForgetCharacterResult(bool Success, string Error);

internal sealed class TriffSkillsAuthentication
{
    internal const string CredentialPrefix = "TriffView.TriffSkills.RefreshToken.";
    private readonly TriffSkillsState _state;
    private readonly ICredentialStore _credentials;
    private readonly IEveSsoClient _sso;
    private readonly TimeProvider _time;
    private readonly Func<string?> _saveState;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _characterLocks = new();
    private readonly ConcurrentDictionary<long, AccessTokenCache> _accessTokens = new();

    public TriffSkillsAuthentication(
        TriffSkillsState state,
        ICredentialStore credentials,
        IEveSsoClient sso,
        TimeProvider time,
        Func<string?> saveState)
    {
        _state = state;
        _credentials = credentials;
        _sso = sso;
        _time = time;
        _saveState = saveState;
    }

    public async Task<EveValidatedToken> AuthorizeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var charactersAtStart = _state.Characters.Select(character => character.CharacterId).ToHashSet();
        var token = await _sso.AuthorizeAsync(timeout, cancellationToken);
        var characterId = token.Identity.CharacterId;
        var gate = CharacterLock(characterId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (charactersAtStart.Contains(characterId) && _state.Find(characterId) is null)
            {
                throw new OperationCanceledException("The character was forgotten while reauthorization was in progress.");
            }
            CommitAuthentication(token);
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ForgetCharacterResult> ForgetAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0) return new ForgetCharacterResult(false, "Character ID is invalid.");
        var gate = CharacterLock(characterId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var character = _state.Find(characterId);
            if (character is null) return new ForgetCharacterResult(true, string.Empty);
            var target = CredentialTarget(characterId);
            string? previousSecret;
            try
            {
                previousSecret = _credentials.Read(target);
            }
            catch (Exception exception)
            {
                return new ForgetCharacterResult(false, $"Credential lookup failed; the character was not forgotten. {exception.Message}");
            }
            try
            {
                _credentials.Delete(target);
            }
            catch (Exception exception)
            {
                return new ForgetCharacterResult(false, $"Credential deletion failed; the character was not forgotten. {exception.Message}");
            }

            var index = _state.Characters.IndexOf(character);
            var previous = character.Clone();
            var previousSelection = _state.SelectedCharacterId;
            _state.Characters.RemoveAt(index);
            if (_state.SelectedCharacterId == characterId) _state.SelectedCharacterId = _state.Characters.FirstOrDefault()?.CharacterId ?? 0;
            var saveError = _saveState();
            if (saveError is not null)
            {
                _state.Characters.Insert(index, previous);
                _state.SelectedCharacterId = previousSelection;
                try
                {
                    if (previousSecret is not null) _credentials.Write(target, previousSecret);
                    return new ForgetCharacterResult(false, $"Forget was rolled back because state could not be saved: {saveError}");
                }
                catch (Exception rollback)
                {
                    previous.NeedsReauth = true;
                    return new ForgetCharacterResult(false, $"State could not be saved and credential rollback also failed: {rollback.Message}");
                }
            }

            _accessTokens.TryRemove(characterId, out _);
            return new ForgetCharacterResult(true, string.Empty);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> AccessTokenAsync(
        long characterId,
        bool forceRefresh,
        string? rejectedAccessToken,
        CancellationToken cancellationToken)
    {
        var gate = CharacterLock(characterId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var character = _state.Find(characterId) ?? throw new OperationCanceledException("Character was forgotten.");
            var now = _time.GetUtcNow();
            if (_accessTokens.TryGetValue(characterId, out var cached)
                && cached.ExpiresUtc > now.AddSeconds(30)
                && (!forceRefresh || !string.Equals(cached.AccessToken, rejectedAccessToken, StringComparison.Ordinal)))
            {
                return cached.AccessToken;
            }
            if (forceRefresh) _accessTokens.TryRemove(characterId, out _);

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
                && !string.IsNullOrWhiteSpace(token.Identity.OwnerHash)
                && !string.Equals(character.OwnerHash, token.Identity.OwnerHash, StringComparison.Ordinal))
            {
                throw new OAuthTokenException(HttpStatusCode.Unauthorized, "owner_changed", "Character ownership changed.");
            }

            var replacement = string.IsNullOrWhiteSpace(token.RefreshToken) ? previousRefresh : token.RefreshToken;
            var credentialChanged = !string.Equals(replacement, previousRefresh, StringComparison.Ordinal);
            if (credentialChanged)
            {
                try
                {
                    _credentials.Write(target, replacement);
                }
                catch (Exception original)
                {
                    Exception? rollback = null;
                    try
                    {
                        _credentials.Write(target, previousRefresh);
                    }
                    catch (Exception exception)
                    {
                        rollback = exception;
                    }
                    throw TransactionFailure("Rotated credential could not be saved", original, rollback);
                }
            }

            var owner = string.IsNullOrWhiteSpace(token.Identity.OwnerHash) ? character.OwnerHash : token.Identity.OwnerHash;
            var scopes = token.Identity.Scopes.OrderBy(scope => scope, StringComparer.Ordinal).ToList();
            var metadataChanged = !string.Equals(character.CharacterName, token.Identity.CharacterName, StringComparison.Ordinal)
                || !string.Equals(character.OwnerHash, owner, StringComparison.Ordinal)
                || !character.Scopes.SequenceEqual(scopes, StringComparer.Ordinal);
            if (metadataChanged)
            {
                var previousCharacter = character.Clone();
                character.CharacterName = token.Identity.CharacterName;
                character.OwnerHash = owner ?? string.Empty;
                character.Scopes = scopes;
                var stateError = _saveState();
                if (stateError is not null)
                {
                    character.CharacterName = previousCharacter.CharacterName;
                    character.OwnerHash = previousCharacter.OwnerHash;
                    character.Scopes = previousCharacter.Scopes;
                    Exception? rollback = null;
                    if (credentialChanged)
                    {
                        try
                        {
                            _credentials.Write(target, previousRefresh);
                        }
                        catch (Exception exception)
                        {
                            rollback = exception;
                        }
                    }
                    throw TransactionFailure("Authorization metadata could not be saved", new IOException(stateError), rollback);
                }
            }

            _accessTokens[characterId] = Cache(token);
            return token.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyList<string> RecoverOwnCredentials()
    {
        var warnings = new List<string>();
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
                warnings.Add($"Recovered {recovered} TriffSkills credential(s) that had no visible state row.");
                var error = _saveState();
                if (error is not null) warnings.Add($"Recovered credential rows could not be saved: {error}");
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"Could not inspect the TriffSkills credential namespace for recovery: {exception.Message}");
        }
        return warnings;
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
                && !string.IsNullOrWhiteSpace(identity.OwnerHash)
                && !string.Equals(character.OwnerHash, identity.OwnerHash, StringComparison.Ordinal);
            character.CharacterName = identity.CharacterName;
            if (!string.IsNullOrWhiteSpace(identity.OwnerHash)) character.OwnerHash = identity.OwnerHash;
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
            var saveError = _saveState();
            if (saveError is not null) throw new IOException($"Could not save character state: {saveError}");
        }
        catch (Exception original)
        {
            if (previousCharacter is null) _state.Characters.RemoveAll(character => character.CharacterId == identity.CharacterId);
            else _state.Characters[existingIndex] = previousCharacter;
            _state.SelectedCharacterId = previousSelection;
            Exception? rollback = null;
            try
            {
                if (previousSecret is null) _credentials.Delete(target);
                else _credentials.Write(target, previousSecret);
            }
            catch (Exception exception)
            {
                rollback = exception;
            }
            throw TransactionFailure("Authentication state could not be saved", original, rollback);
        }

        _accessTokens[identity.CharacterId] = Cache(token);
    }

    private static Exception TransactionFailure(string message, Exception original, Exception? rollback)
        => rollback is null
            ? new InvalidOperationException($"{message}: {original.Message}", original)
            : new InvalidOperationException($"{message}; credential rollback also failed: {rollback.Message}", new AggregateException(original, rollback));

    private SemaphoreSlim CharacterLock(long characterId)
        => _characterLocks.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));

    internal static string CredentialTarget(long characterId)
        => CredentialPrefix + characterId.ToString(CultureInfo.InvariantCulture);

    private AccessTokenCache Cache(EveValidatedToken token)
        => new(token.AccessToken, _time.GetUtcNow().AddSeconds(Math.Max(30, token.ExpiresIn - 60)));

    private sealed record AccessTokenCache(string AccessToken, DateTimeOffset ExpiresUtc);
}
