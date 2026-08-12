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

// Read-only references into Fleet Manager (same assembly). TriffSkills reuses these
// types without modifying any TriffFleets file.
using AccessTokenCache = TriffView.TriffFleets.AccessTokenCache;
using CredentialStore = TriffView.TriffFleets.CredentialStore;
using EveJwtIdentity = TriffView.TriffFleets.EveJwtIdentity;
using TokenResponse = TriffView.TriffFleets.TokenResponse;

namespace TriffView.TriffSkills;

internal sealed class TriffSkillsController
{
    // TriffSkills shares Fleet Manager's EVE application registration: an EVE app is
    // pinned to exactly one callback URL, and the two tools ship in the same binary,
    // so the maintainer only adds the two skill scopes to the registration he already
    // has. Scopes travel on the per-request `scope` parameter, so Fleet Manager's
    // consent screen is unchanged; refresh tokens stay separated by the
    // TriffView.TriffSkills. credential prefix. Both tools bind 127.0.0.1:51777 only
    // for the duration of an active authorization.
    //
    // The RedirectUri matches the registered callback verbatim, including the
    // /trifffleets/ path - that string is the registration's, not a claim about which
    // tool is listening.
    private const string DefaultClientId = "7d2454c3191c4254a4b67d8f71f2b972";
    private const string ClientIdEnvVar = "TRIFFVIEW_TRIFFSKILLS_CLIENT_ID";
    private const string RedirectUri = "http://127.0.0.1:51777/trifffleets/callback/";
    private const string AuthorizeEndpoint = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenEndpoint = "https://login.eveonline.com/v2/oauth/token";
    private const string Scopes = "esi-skills.read_skills.v1 esi-skills.read_skillqueue.v1";
    private const string UserAgent = "TriffView/1.0 TriffSkills";

    // How long a single accepted socket gets to deliver its request line and headers.
    // Bounds each candidate connection; the 5-minute CTS in StartAuthAsync bounds the
    // authorization as a whole.
    private static readonly TimeSpan CallbackReadTimeout = TimeSpan.FromSeconds(10);

    // The environment variable exists so a build against a different EVE application
    // (for development, before the shipped registration carries the skill scopes) does
    // not require editing source. Resolved once per process so the authorize URL and
    // the token exchange always use the same value.
    private static readonly string ClientId = ResolveClientId();

    private static string ResolveClientId()
    {
        var fromEnv = Environment.GetEnvironmentVariable(ClientIdEnvVar)?.Trim();
        return string.IsNullOrWhiteSpace(fromEnv) ? DefaultClientId : fromEnv;
    }

    private static bool IsClientIdConfigured(string? clientId)
    {
        return !string.IsNullOrWhiteSpace(clientId);
    }

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    // Used by the ESI transport, the SSO token parse, and PostState's dedupe string.
    // The actual wire message is re-serialized by MainWindow.PostAppEvent with its own
    // CamelCase options; the two agree on naming.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Action<object> _postToHud;
    private readonly TriffSkillsState _state;
    private readonly SkillIdCache _skillIds;
    private readonly Dictionary<long, AccessTokenCache> _accessTokens = new();
    private string _lastPostedStateJson = "";
    private bool _authInProgress;

    private bool _charactersRefreshInFlight;
    private bool _charactersRefreshPending;
    private bool _nameResolveInFlight;
    private List<SkillPlan> _plans = new();
    private DateTimeOffset? _plansUpdatedUtc;

    // Constructed lazily by MainWindow on the first triffskills: message, so users who
    // never open the tool pay no startup or disk cost. Takes no Dispatcher: every
    // entry point is a web message or a continuation of one.
    public TriffSkillsController(Action<object> postToHud)
    {
        _postToHud = postToHud;
        _state = TriffSkillsState.Load();
        _skillIds = SkillIdCache.Load();
        PlanStore.EnsureSeeded(TriffSkillsPaths.PlansDir);
        LoadPlans();
    }

    private static Task<EsiResponse<T>> SendEsiAsync<T>(HttpMethod method, string path, string? token, object? body = null)
        => EsiTransport.SendAsync<T>(Http, JsonOptions, UserAgent, method, path, token, body);

    // Resolves one batch of skill names through POST /universe/ids/. Unauthenticated
    // by design - name resolution needs no token, so it works even for a character
    // whose credential has expired.
    private async Task<IReadOnlyList<SkillsUniverseIdName>> ResolveNamesBatchAsync(IReadOnlyList<string> batch)
    {
        var response = await SendEsiAsync<SkillsUniverseIdsResponse>(HttpMethod.Post, "/universe/ids/", token: null, body: batch);
        response.ThrowIfFailed();

        // Names ESI does not recognise are omitted from inventory_types; they stay out
        // of the cache and surface as UnknownSkills on the plan.
        return response.Value?.InventoryTypes ?? new List<SkillsUniverseIdName>();
    }

    public bool HandleWebMessage(string type, JsonObject? message)
    {
        switch (type)
        {
            case "triffskills:get-state":
                PostState(force: true);
                // On a fresh install nothing has resolved the seeded plan's skill
                // names yet; fire-and-forget so first open does not need a manual
                // "Reload plans". No-op once every name is cached.
                _ = ResolvePlanNamesAsync();
                return true;
            case "triffskills:auth":
                _ = StartAuthAsync();
                return true;
            case "triffskills:forget-character":
                ForgetCharacter(ReadLong(message, "characterId"));
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
            case "triffskills:import-plan":
                _ = ImportPlanAsync(message);
                return true;
            default:
                return false;
        }
    }

    // Web-message fields are untrusted input: the renderer can be stale, and
    // JsonNode.GetValue<T> throws on any mismatch. A throw here would escape
    // HandleWebMessage and take down the message pump for every tool, so each field is
    // read defensively and a bad value degrades to a default.
    private static long ReadLong(JsonObject? message, string key)
    {
        if (message?[key] is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        // Renderers that stringify ids to dodge the JS 2^53 limit land here.
        return value.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    private static string ReadString(JsonObject? message, string key)
    {
        return message?[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : "";
    }

    private static bool ReadBool(JsonObject? message, string key)
    {
        return message?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string BuildQuery(Dictionary<string, string> values)
    {
        return string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        query = query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0].Replace("+", " "));
            var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1].Replace("+", " ")) : "";
            result[key] = value;
        }
        return result;
    }

    private static string BuildAuthorizeUrl(string state, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["scope"] = Scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return $"{AuthorizeEndpoint}?{BuildQuery(query)}";
    }

    // A TcpListener rather than HttpListener: HttpListener on Windows needs a URL ACL
    // reservation or elevation. Read the request line, drain the headers, then write a
    // minimal HTTP response so the browser tab shows a completion message.
    private static async Task<Uri> ReadCallbackUrlAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken) ?? "";
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidDataException("Local SSO callback was not a valid HTTP request.");
        }

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
            // Drain headers before writing the callback page.
        }

        return new Uri(new Uri(RedirectUri), parts[1]);
    }

    // Case and trailing-slash are normalised away: a near-miss redirect would otherwise
    // be silently discarded and present as a five-minute hang.
    private static bool IsCallbackPath(string requestPath, string callbackPath)
        => string.Equals(
            requestPath.TrimEnd('/'),
            callbackPath.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static async Task WriteCallbackHtmlAsync(NetworkStream stream, string message)
    {
        var html = $"""
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>TriffSkills</title></head>
        <body style="margin:0;background:#05070b;color:#d9e2ee;font-family:Segoe UI,Arial,sans-serif;">
          <main style="max-width:520px;margin:80px auto;border:1px solid #303640;background:#090d14;padding:24px;">
            <h1 style="color:#53b6ff;font-size:18px;text-transform:uppercase;">TriffSkills</h1>
            <p>{WebUtility.HtmlEncode(message)}</p>
          </main>
        </body>
        </html>
        """;
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n"
        );
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
    }

    // Writing the browser page is best-effort and must never decide the outcome of an
    // authorization: by the time the success page is written the outcome is already
    // committed, and a closed tab must not convert it into a reported failure.
    private static async Task TryWriteCallbackHtmlAsync(NetworkStream stream, string message)
    {
        try
        {
            await WriteCallbackHtmlAsync(stream, message);
        }
        catch
        {
            // Tab closed, browser gone, connection reset. Nothing to report.
        }
    }

    // Reads the unverified payload of the access token for the character ID, name, and
    // granted scopes. The token came directly from the SSO token endpoint over TLS,
    // which is the trust boundary; its signature is not re-checked here.
    private static EveJwtIdentity DecodeEveJwt(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) throw new InvalidDataException("EVE SSO returned an invalid access token.");
        var payload = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
        var node = JsonNode.Parse(payload)?.AsObject() ?? throw new InvalidDataException("EVE SSO access token payload could not be read.");
        var characterId = 0L;

        foreach (var key in new[] { "character_id", "CharacterID", "characterID", "characterId" })
        {
            if (node[key] == null) continue;
            try
            {
                characterId = node[key]!.GetValue<long>();
                if (characterId > 0) break;
            }
            catch
            {
                var value = node[key]?.GetValue<string>() ?? "";
                long.TryParse(value, out characterId);
                if (characterId > 0) break;
            }
        }

        if (characterId <= 0)
        {
            var sub = node["sub"]?.GetValue<string>() ?? "";
            var numericTail = sub.Split(':', '/', '|').LastOrDefault(part => long.TryParse(part, out _)) ?? "";
            long.TryParse(numericTail, out characterId);
        }

        if (characterId <= 0)
        {
            throw new InvalidDataException("The EVE SSO token did not include a usable character ID.");
        }

        var name = node["name"]?.GetValue<string>() ?? $"Character {characterId}";
        var scopes = new List<string>();
        if (node["scp"] is JsonArray array)
        {
            scopes.AddRange(array.Select(scope => scope?.GetValue<string>() ?? "").Where(scope => !string.IsNullOrWhiteSpace(scope)));
        }
        else if (node["scp"] != null)
        {
            scopes.AddRange((node["scp"]?.GetValue<string>() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        return new EveJwtIdentity(characterId, name, scopes);
    }

    // The distinct TriffView.TriffSkills. prefix is the credential isolation:
    // TriffSkills never reads a Fleet Manager token, and can never grant itself skill
    // access on the back of a fleets grant.
    private static string RefreshTokenTarget(long characterId) => $"TriffView.TriffSkills.RefreshToken.{characterId}";

    private static async Task<TokenResponse> SendTokenRequestAsync(Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            // ReadError reduces the body to its "error" field instead of interpolating
            // the whole response into a message the UI shows verbatim.
            throw new InvalidOperationException($"EVE SSO returned {(int)response.StatusCode}: {EsiTransport.ReadError(text)}");
        }

        return JsonSerializer.Deserialize<TokenResponse>(text, JsonOptions)
            ?? throw new InvalidDataException("EVE SSO returned an empty token response.");
    }

    private static async Task<TokenResponse> ExchangeCodeAsync(string code, string verifier)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = RedirectUri,
        };
        return await SendTokenRequestAsync(form);
    }

    // No scope validation happens on refresh: a refresh token minted under a different
    // registration refreshes happily and only fails with a 403 on the first skills
    // call. CharacterResponseIsUsable is what turns that 403 into "re-authenticate
    // this character".
    private async Task<TokenResponse> RefreshTokenAsync(long characterId)
    {
        var refreshToken = CredentialStore.Read(RefreshTokenTarget(characterId));
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("This character needs to authenticate again.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        };
        var token = await SendTokenRequestAsync(form);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            CredentialStore.Write(RefreshTokenTarget(characterId), token.RefreshToken);
        }
        _accessTokens[characterId] = new AccessTokenCache(token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60)));
        return token;
    }

    // Every caller that needs a bearer token goes through this cache: a refresh is a
    // full SSO round trip plus a Credential Manager write (CCP rotates the refresh
    // token), so it must not run once per character per pass when the previous pass's
    // token is still good. The 30-second margin keeps a nearly-expired token from
    // being handed out just before the request that would use it.
    private async Task<string> AccessTokenForAsync(long characterId)
    {
        if (_accessTokens.TryGetValue(characterId, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return cached.AccessToken;
        }

        var token = await RefreshTokenAsync(characterId);
        return token.AccessToken;
    }

    private async Task StartAuthAsync()
    {
        if (_authInProgress)
        {
            PostError("auth", "Character authentication is already in progress.");
            return;
        }

        var clientId = ClientId;
        if (!IsClientIdConfigured(clientId))
        {
            PostError("auth", $"TriffSkills has no EVE SSO client ID. Set {ClientIdEnvVar} and restart.");
            PostState(force: true);
            return;
        }

        _authInProgress = true;
        PostState(force: true);

        // Port comes from RedirectUri rather than a second literal: EVE SSO redirects
        // to the registered URL, so a listener on any other port would wait out the
        // full timeout with nothing to accept.
        using var listener = new TcpListener(IPAddress.Loopback, new Uri(RedirectUri).Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var authSucceeded = false;
        try
        {
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            listener.Start();

            var authUrl = BuildAuthorizeUrl(state, challenge);
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            var callbackPath = new Uri(RedirectUri).AbsolutePath;

            // A browser routinely opens more than one connection around a redirect
            // (preconnects, an abandoned tab), and any of them can win the accept ahead
            // of the real callback. Loop until a request actually lands on the callback
            // path carrying `code` or `error`, discarding everything else. Two budgets:
            // a short per-candidate one (CallbackReadTimeout) so no silent socket can
            // stall the loop, and the one 5-minute cts covering the authorization.
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);

                // .NET cannot cancel a socket receive already in flight, so a peer that
                // connects and then sends nothing would block forever on ReadLineAsync.
                // Registering Dispose on the candidate token is what actually breaks the
                // read: closing the socket faults the pending receive into the catch
                // below and the loop moves on.
                using var candidateCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                candidateCts.CancelAfter(CallbackReadTimeout);
                using var candidateAbort = candidateCts.Token.Register(client.Dispose);

                Uri callbackUrl;
                try
                {
                    // GetStream() throws if the peer already went away; inside the try
                    // so that aborts this candidate rather than the authorization.
                    callbackUrl = await ReadCallbackUrlAsync(client.GetStream(), candidateCts.Token);
                }
                catch (Exception)
                {
                    // If the overall budget expired, the read faulted because the
                    // registration closed the socket - re-raise it as the timeout it is.
                    cts.Token.ThrowIfCancellationRequested();

                    // Otherwise: not a well-formed HTTP request in time. Discard this
                    // candidate and keep waiting.
                    continue;
                }

                // Retire the candidate deadline before the token exchange, which can
                // outlast it and would close the socket under the reply page.
                candidateCts.CancelAfter(Timeout.InfiniteTimeSpan);
                var stream = client.GetStream();

                var query = ParseQuery(callbackUrl.Query);
                var hasCode = query.ContainsKey("code");
                var hasError = query.ContainsKey("error");
                if (!IsCallbackPath(callbackUrl.AbsolutePath, callbackPath) || (!hasCode && !hasError))
                {
                    continue;
                }

                var error = hasError ? query["error"] : "";
                var code = hasCode ? query["code"] : "";
                var returnedState = query.TryGetValue("state", out var stateValue) ? stateValue : "";

                // State is checked before anything else that ends the wait, error
                // included: any local process can reach this port, and an
                // unauthenticated caller must not be able to abort a pending login just
                // by sending ?error=.
                if (!string.Equals(state, returnedState, StringComparison.Ordinal))
                {
                    await TryWriteCallbackHtmlAsync(stream, "TriffSkills blocked this login because the SSO state did not match. You can close this tab.");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    await TryWriteCallbackHtmlAsync(stream, "TriffSkills authentication was cancelled or denied. You can close this tab.");
                    PostError("auth", $"EVE SSO returned: {error}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    await TryWriteCallbackHtmlAsync(stream, "TriffSkills did not receive an authorization code. You can close this tab.");
                    PostError("auth", "EVE SSO did not return an authorization code.");
                    return;
                }

                var token = await ExchangeCodeAsync(code, verifier);
                var identity = DecodeEveJwt(token.AccessToken);

                if (!identity.Scopes.Contains("esi-skills.read_skills.v1") || !identity.Scopes.Contains("esi-skills.read_skillqueue.v1"))
                {
                    throw new InvalidDataException("The selected character did not grant the required skill scopes (esi-skills.read_skills.v1, esi-skills.read_skillqueue.v1).");
                }

                if (string.IsNullOrWhiteSpace(token.RefreshToken))
                {
                    throw new InvalidDataException("EVE SSO did not return a refresh token.");
                }

                // Commit order: state first, then the credential, and each step only
                // if the previous one landed. A refresh token in Credential Manager
                // with no character row has no UI that can remove it, so the row must
                // exist durably before the credential is written - and a save that
                // fails must stop the commit, not just log.
                var isNewCharacter = _state.Characters.All(existing => existing.CharacterId != identity.CharacterId);
                var character = _state.Upsert(identity.CharacterId);
                character.CharacterName = identity.CharacterName;
                character.Scopes = identity.Scopes.ToList();
                character.AuthenticatedUtc = DateTimeOffset.UtcNow;
                character.Error = "";
                character.NeedsReauth = false;
                _state.SelectedCharacterId = identity.CharacterId;

                if (!_state.TrySave(out var saveError))
                {
                    if (isNewCharacter)
                    {
                        _state.Characters.RemoveAll(existing => existing.CharacterId == identity.CharacterId);
                    }
                    await TryWriteCallbackHtmlAsync(stream, "TriffSkills could not save this character, so the sign-in was not stored. You can close this tab.");
                    PostError("auth", $"Could not save character state, so the sign-in was not stored: {saveError}");
                    return;
                }

                try
                {
                    CredentialStore.Write(RefreshTokenTarget(identity.CharacterId), token.RefreshToken);
                }
                catch (Exception ex)
                {
                    // The row is persisted but the credential is not. A new character
                    // is removed outright; a re-authenticating one keeps its last-good
                    // data and is flagged for re-auth, since its old credential may
                    // also be gone.
                    if (isNewCharacter)
                    {
                        _state.Characters.RemoveAll(existing => existing.CharacterId == identity.CharacterId);
                    }
                    else
                    {
                        character.Error = "Storing the new refresh token failed - re-authenticate this character.";
                        character.NeedsReauth = true;
                    }
                    _state.Save();
                    await TryWriteCallbackHtmlAsync(stream, "TriffSkills could not store the sign-in. You can close this tab.");
                    PostError("auth", $"Could not store the refresh token: {ex.Message}");
                    return;
                }

                _accessTokens[identity.CharacterId] = new AccessTokenCache(token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60)));
                authSucceeded = true;

                await TryWriteCallbackHtmlAsync(stream, "TriffSkills authentication complete. You can close this tab and return to TriffView.");
                return;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            PostError("auth", "EVE SSO authentication timed out.");
        }
        catch (SocketException ex)
        {
            PostError("auth", $"Could not open the local SSO callback listener at {RedirectUri}. {ex.Message}");
        }
        catch (Exception ex)
        {
            PostError("auth", ex.Message);
        }
        finally
        {
            _authInProgress = false;
            cts.Cancel();
            listener.Stop();
            PostState(force: true);

            // After the repost, so the user sees the new character row appear and then
            // fill in. RefreshCharactersAsync degrades per character and never throws
            // out, so this unobserved task cannot surface as an unhandled exception.
            if (authSucceeded)
            {
                _ = RefreshCharactersAsync();
            }
        }
    }

    private void ForgetCharacter(long characterId)
    {
        if (characterId <= 0) return;

        // Deletes only the TriffSkills-prefixed credential; Fleet Manager's entry for
        // the same character is a different target name and is left alone.
        CredentialStore.Delete(RefreshTokenTarget(characterId));
        _state.Characters.RemoveAll(character => character.CharacterId == characterId);
        if (_state.SelectedCharacterId == characterId)
        {
            _state.SelectedCharacterId = _state.Characters.FirstOrDefault()?.CharacterId ?? 0;
        }
        _accessTokens.Remove(characterId);
        _state.Save();
        PostState(force: true);
    }

    // Refreshes every authenticated character's skills and queue. Every failure
    // degrades exactly one character - it keeps its previous data untouched and gains
    // an error string - and nothing throws out to the caller.
    private async Task RefreshCharactersAsync()
    {
        // A request that arrives while a pass is running is deferred, not dropped:
        // StartAuthAsync adds a character and then asks for a refresh, and a pass that
        // has already walked past that point would otherwise never fetch it. All state
        // mutation happens on the WebView2 dispatcher thread, so there is exactly one
        // writer and these plain bools suffice.
        if (_charactersRefreshInFlight)
        {
            _charactersRefreshPending = true;
            return;
        }

        _charactersRefreshInFlight = true;
        PostState(force: true);
        try
        {
            do
            {
                // Cleared before the pass, so a request landing mid-pass schedules
                // another one rather than being absorbed.
                _charactersRefreshPending = false;

                foreach (var character in _state.Characters.ToArray())
                {
                    try
                    {
                        await RefreshOneCharacterAsync(character);
                    }
                    catch (Exception ex)
                    {
                        // Backstop for whatever RefreshOneCharacterAsync throws that its
                        // own guards do not cover - e.g. a 200 whose body does not match
                        // the DTO throws JsonException out of the transport. NeedsReauth
                        // is left alone: an unrecognised body says nothing about the
                        // credential.
                        character.Error = $"Refresh failed unexpectedly: {ex.Message}";
                        PostError("refresh-characters", $"{character.CharacterName}: {character.Error}");
                    }

                    _state.Save();
                    PostState(force: true);
                }
            }
            while (_charactersRefreshPending);
        }
        finally
        {
            _charactersRefreshInFlight = false;
            _charactersRefreshPending = false;
            PostState(force: true);
        }
    }

    private async Task RefreshOneCharacterAsync(TriffSkillsCharacter character)
    {
        string token;
        try
        {
            token = await AccessTokenForAsync(character.CharacterId);
        }
        catch (Exception ex)
        {
            // Token refresh failed; the last-good record stays visible and is rendered
            // stale by its unchanged FetchedUtc.
            _state.ApplyFetchFailure(
                character.CharacterId,
                $"Sign-in expired - re-authenticate this character. {ex.Message}",
                needsReauth: true);
            PostError("refresh-characters", $"{character.CharacterName}: {character.Error}");
            return;
        }

        var skills = await SendEsiAsync<CharacterSkillsResponse>(
            HttpMethod.Get, $"/characters/{character.CharacterId}/skills/", token);
        if (!CharacterResponseIsUsable(character, skills)) return;

        var queue = await SendEsiAsync<List<SkillQueueItem>>(
            HttpMethod.Get, $"/characters/{character.CharacterId}/skillqueue/", token);
        if (!CharacterResponseIsUsable(character, queue)) return;

        // Written only once BOTH calls have succeeded, so a character is never left
        // holding fresh skills next to a stale queue.
        _state.ApplyFetchSuccess(
            character.CharacterId,
            EsiSkillMapper.ToTrainedLevels(skills.Value),
            EsiSkillMapper.ToQueue(queue.Value));
    }

    // Returns true when the response can be used. On failure the character's previous
    // record is left alone and the error is surfaced on the record and via PostError.
    private bool CharacterResponseIsUsable<T>(TriffSkillsCharacter character, EsiResponse<T> response)
    {
        if (response.IsSuccess) return true;

        string error;
        var forbidden = response.StatusCode == HttpStatusCode.Forbidden;
        var unauthorized = response.StatusCode == HttpStatusCode.Unauthorized;
        // A failure that is neither 401 nor 403 says nothing about the credential, so
        // it must not clear a re-auth flag an earlier 401/403 raised; only a successful
        // fetch clears it.
        var needsReauth = forbidden || unauthorized || character.NeedsReauth;
        if (forbidden)
        {
            // 403 on a skills endpoint is a scope problem: a token minted under a
            // registration without the skill scopes refreshes happily and fails here.
            error = $"Re-authenticate this character: the stored token does not carry {Scopes}.";
        }
        else if (unauthorized)
        {
            // 401 means ESI rejected the bearer token itself. Drop the cached access
            // token so the next pass performs a real refresh instead of presenting the
            // same rejected token again.
            _accessTokens.Remove(character.CharacterId);
            error = "Re-authenticate this character: EVE rejected the stored sign-in (401).";
        }
        else
        {
            error = $"{response.Method} {response.Path} returned {(int)response.StatusCode}: {response.Error}";
        }

        _state.ApplyFetchFailure(character.CharacterId, error, needsReauth);
        PostError("refresh-characters", $"{character.CharacterName}: {character.Error}");
        return false;
    }

    private void LoadPlans()
    {
        try
        {
            var result = PlanStore.LoadAll(TriffSkillsPaths.PlansDir);
            _plans = result.Plans.ToList();
            _plansUpdatedUtc = result.LatestWriteUtc;
            if (result.SkippedFiles.Count > 0)
            {
                PostError("plans", $"Ignored {result.SkippedFiles.Count} plan file(s) with no valid skill lines: {string.Join(", ", result.SkippedFiles)}");
            }
        }
        catch (Exception ex)
        {
            // Directory-level failure (permission-denied %APPDATA%, an antivirus hold):
            // keep the already-loaded plans rather than discarding them on a possibly
            // transient re-read.
            PostError("plans", $"Could not read the plans folder: {ex.Message}");
        }
    }

    // Resolves any plan skill names the ID cache has not seen. Single-flight and
    // best-effort: plans are usable either way, and a resolution outage only means
    // those names read Unknown for now, which is exactly the degradation the matrix is
    // built to show.
    private async Task ResolvePlanNamesAsync()
    {
        if (_nameResolveInFlight) return;
        _nameResolveInFlight = true;
        try
        {
            var names = _plans
                .SelectMany(plan => plan.Requirements.Select(requirement => requirement.SkillName))
                .Where(name => !_skillIds.Map.ContainsKey(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count > 0)
            {
                var added = await _skillIds.ResolveMissingAsync(names, ResolveNamesBatchAsync);
                if (added > 0) PostState(force: true);
            }
        }
        catch (Exception ex)
        {
            PostError("plans", $"Some skill names could not be resolved: {ex.Message}");
        }
        finally
        {
            _nameResolveInFlight = false;
        }
    }

    // Re-reads the plans folder so a file the user just dropped in is picked up
    // without restarting the app.
    private async Task ReloadPlansAsync()
    {
        LoadPlans();
        PostState(force: true);
        await ResolvePlanNamesAsync();
    }

    // Opens the plans folder in Explorer; without this nobody finds the path. Created
    // first so the button works on a fresh install. Takes no argument from the web
    // message, so PlansDir itself is the only path in play.
    private void OpenPlansFolder()
    {
        try
        {
            var full = TriffSkillsPaths.PlansDir;
            Directory.CreateDirectory(full);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PostError("open-plans-folder", ex.Message);
        }
    }

    // Writes a plan copied from EVE's in-game skill plan window to PlansDir. The
    // clipboard text is written verbatim - SkillPlanParser reads it back at load time,
    // so what the game put on the clipboard is exactly what gets parsed. The name is
    // untrusted (it arrives in a web message), so PlanNameValidator is the
    // authoritative check regardless of what the renderer's advisory copy allowed.
    private async Task ImportPlanAsync(JsonObject? message)
    {
        // Fired without awaiting, so an escaping exception would leave the import
        // modal waiting forever; every exit path must post a reply.
        var name = ReadString(message, "name");
        try
        {
            await ImportPlanCoreAsync(name, ReadString(message, "contents"), ReadBool(message, "replace"));
        }
        catch (Exception ex)
        {
            PostError("import-plan", ex.Message);
        }
    }

    private async Task ImportPlanCoreAsync(string name, string contents, bool replace)
    {
        if (!PlanNameValidator.TryValidate(name, out var nameError))
        {
            PostError("import-plan", nameError);
            return;
        }

        string fullPath;
        try
        {
            Directory.CreateDirectory(TriffSkillsPaths.PlansDir);
            fullPath = Path.GetFullPath(Path.Combine(TriffSkillsPaths.PlansDir, name + ".txt"));
        }
        catch (Exception ex)
        {
            PostError("import-plan", ex.Message);
            return;
        }

        if (!PlanNameValidator.IsWithin(fullPath, TriffSkillsPaths.PlansDir))
        {
            // TryValidate should already have rejected anything that gets here; this is
            // the backstop, not the primary defense.
            PostError("import-plan", "That plan name is not allowed.");
            return;
        }

        // The disk is the only source of truth for whether this would overwrite
        // something - the renderer's plan list can be stale. A collision is reported
        // back so the dialog can offer Replace as a distinct, deliberate action.
        if (File.Exists(fullPath) && !replace)
        {
            _postToHud(new { type = "triffskills:import-collision", name });
            return;
        }

        try
        {
            await File.WriteAllTextAsync(fullPath, contents);
        }
        catch (Exception ex)
        {
            PostError("import-plan", ex.Message);
            return;
        }

        await ReloadPlansAsync();
        _postToHud(new { type = "triffskills:import-done", name });
    }

    private void PostState(bool force = false)
    {
        try
        {
            _state.Normalize();
            var matrix = TriffSkillsMatrix.Build(_state.Characters, _plans, _skillIds.Map);
            var wire = TriffSkillsMatrix.ToWire(matrix);
            var state = new
            {
                type = "triffskills:state",
                authConfigured = IsClientIdConfigured(ClientId),
                authInProgress = _authInProgress,
                refreshInFlight = _charactersRefreshInFlight,
                characters = _state.Characters.Select(character => new
                {
                    character.CharacterId,
                    character.CharacterName,
                    character.Scopes,
                    character.AuthenticatedUtc,
                    character.FetchedUtc,
                    character.Error,
                    character.NeedsReauth,
                }).ToArray(),
                plans = wire.Plans,
                matrix = wire.Matrix,
                // The UI types this as a string and renders "No plans yet" when empty.
                plansUpdatedUtc = _plansUpdatedUtc?.ToString("o") ?? "",
            };
            var json = JsonSerializer.Serialize(state, JsonOptions);
            if (!force && string.Equals(json, _lastPostedStateJson, StringComparison.Ordinal)) return;
            _lastPostedStateJson = json;
            _postToHud(state);
        }
        catch (Exception ex)
        {
            PostError("state", ex.Message);
        }
    }

    private void PostError(string action, string message)
    {
        _postToHud(new
        {
            type = "triffskills:error",
            action,
            message,
        });
    }
}
