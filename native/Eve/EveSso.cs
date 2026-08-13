using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;

namespace TriffView.Eve;

internal sealed record EveSsoOptions(
    string ClientId,
    string RedirectUri,
    IReadOnlySet<string> RequiredScopes,
    string UserAgent,
    string ToolName);

internal sealed record EveIdentity(
    long CharacterId,
    string CharacterName,
    string OwnerHash,
    IReadOnlySet<string> Scopes);

internal sealed record EveValidatedToken(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    EveIdentity Identity);

internal sealed class OAuthTokenException : InvalidOperationException
{
    public OAuthTokenException(HttpStatusCode statusCode, string errorCode, string message) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }
    public string ErrorCode { get; }
    public bool IsInvalidGrant => string.Equals(ErrorCode, "invalid_grant", StringComparison.Ordinal);
    public bool IsDefinitiveAuthorizationFailure => ErrorCode is "invalid_grant" or "identity_mismatch" or "owner_changed";
}

internal interface IBrowserLauncher
{
    void Launch(Uri uri);
}

internal sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Launch(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}

internal interface IEveSigningKeySource
{
    Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken);
}

internal sealed class EveSigningKeySource : IEveSigningKeySource
{
    internal static readonly Uri MetadataUri = new("https://login.eveonline.com/.well-known/oauth-authorization-server");
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SecurityKey> _keys = Array.Empty<SecurityKey>();
    private DateTimeOffset _expiresUtc;
    private int _refreshVersion;

    public EveSigningKeySource(HttpClient http, TimeProvider? time = null)
    {
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _keys.Count > 0 && _expiresUtc > _time.GetUtcNow()) return _keys;
        var observedVersion = _refreshVersion;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _keys.Count > 0 && _expiresUtc > _time.GetUtcNow()) return _keys;
            if (forceRefresh && _keys.Count > 0 && _refreshVersion != observedVersion) return _keys;

            using var metadataResponse = await _http.GetAsync(MetadataUri, cancellationToken);
            metadataResponse.EnsureSuccessStatusCode();
            var metadataText = await ReadBoundedAsync(metadataResponse.Content, 64 * 1024, cancellationToken);
            var metadata = JsonNode.Parse(metadataText)?.AsObject()
                ?? throw new InvalidDataException("EVE SSO metadata was not a JSON object.");

            var issuer = metadata["issuer"]?.GetValue<string>() ?? string.Empty;
            if (!EveJwtValidator.AcceptedIssuers.Contains(issuer))
            {
                throw new InvalidDataException("EVE SSO metadata returned an unexpected issuer.");
            }

            var jwksText = metadata["jwks_uri"]?.GetValue<string>() ?? string.Empty;
            if (!Uri.TryCreate(jwksText, UriKind.Absolute, out var jwksUri)
                || jwksUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(jwksUri.Host, "login.eveonline.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("EVE SSO metadata returned an unexpected JWKS address.");
            }

            using var jwksResponse = await _http.GetAsync(jwksUri, cancellationToken);
            jwksResponse.EnsureSuccessStatusCode();
            var jwks = new JsonWebKeySet(await ReadBoundedAsync(jwksResponse.Content, 256 * 1024, cancellationToken));
            var keys = jwks.GetSigningKeys().Where(key => !string.IsNullOrWhiteSpace(key.KeyId)).ToArray();
            if (keys.Length == 0) throw new InvalidDataException("EVE SSO returned no usable signing keys.");

            _keys = keys;
            _expiresUtc = _time.GetUtcNow().Add(CacheDuration);
            _refreshVersion++;
            return _keys;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[limit + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        if (total > limit) throw new InvalidDataException("EVE SSO response exceeded the configured limit.");
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}

internal sealed partial class EveJwtValidator
{
    internal static readonly HashSet<string> AcceptedIssuers = new(StringComparer.Ordinal)
    {
        "https://login.eveonline.com",
        "https://login.eveonline.com/",
        "login.eveonline.com",
    };

    private static readonly string[] AllowedAlgorithms = [SecurityAlgorithms.RsaSha256];
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private readonly string _clientId;
    private readonly IReadOnlySet<string> _requiredScopes;
    private readonly IEveSigningKeySource _keys;
    private readonly TimeProvider _time;

    public EveJwtValidator(
        string clientId,
        IReadOnlySet<string> requiredScopes,
        IEveSigningKeySource keys,
        TimeProvider? time = null)
    {
        _clientId = clientId;
        _requiredScopes = requiredScopes;
        _keys = keys;
        _time = time ?? TimeProvider.System;
    }

    public async Task<EveIdentity> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 32 * 1024)
        {
            throw new SecurityTokenException("EVE SSO returned an invalid access token.");
        }

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        JwtSecurityToken unvalidated;
        try
        {
            unvalidated = handler.ReadJwtToken(token);
        }
        catch (Exception exception)
        {
            throw new SecurityTokenException("EVE SSO returned an unreadable access token.", exception);
        }

        if (!AllowedAlgorithms.Contains(unvalidated.Header.Alg, StringComparer.Ordinal))
        {
            throw new SecurityTokenInvalidAlgorithmException("EVE SSO access token used an unexpected signing algorithm.");
        }

        var keys = await _keys.GetSigningKeysAsync(forceRefresh: false, cancellationToken);
        var refreshed = false;
        if (string.IsNullOrWhiteSpace(unvalidated.Header.Kid) || keys.All(key => key.KeyId != unvalidated.Header.Kid))
        {
            keys = await _keys.GetSigningKeysAsync(forceRefresh: true, cancellationToken);
            refreshed = true;
        }

        ClaimsPrincipal principal;
        SecurityToken validated;
        try
        {
            principal = handler.ValidateToken(token, Parameters(keys), out validated);
        }
        catch (SecurityTokenSignatureKeyNotFoundException) when (!refreshed)
        {
            keys = await _keys.GetSigningKeysAsync(forceRefresh: true, cancellationToken);
            principal = handler.ValidateToken(token, Parameters(keys), out validated);
        }

        var jwt = validated as JwtSecurityToken
            ?? throw new SecurityTokenException("EVE SSO access token was not a JWT.");
        if (!jwt.Payload.ContainsKey("nbf"))
        {
            throw new SecurityTokenException("EVE SSO access token did not contain a not-before time.");
        }
        var subject = principal.FindFirst("sub")?.Value ?? string.Empty;
        var match = CharacterSubject().Match(subject);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var characterId) || characterId <= 0)
        {
            throw new SecurityTokenException("EVE SSO access token had an invalid character subject.");
        }

        var name = (principal.FindFirst("name")?.Value ?? string.Empty).Trim();
        if (name.Length is < 1 or > 100)
        {
            throw new SecurityTokenException("EVE SSO access token had an invalid character name.");
        }

        var owner = (principal.FindFirst("owner")?.Value ?? string.Empty).Trim();
        if (owner.Length is < 8 or > 256)
        {
            throw new SecurityTokenException("EVE SSO access token did not contain a valid owner claim.");
        }

        var authorizedParty = principal.FindFirst("azp")?.Value ?? string.Empty;
        if (!string.Equals(authorizedParty, _clientId, StringComparison.Ordinal))
        {
            throw new SecurityTokenException("EVE SSO access token was authorized to a different client.");
        }

        var scopes = ReadScopes(jwt.Payload);
        var missing = _requiredScopes.Where(scope => !scopes.Contains(scope)).OrderBy(scope => scope).ToArray();
        if (missing.Length > 0)
        {
            throw new SecurityTokenException($"EVE SSO access token is missing required scopes: {string.Join(", ", missing)}.");
        }

        return new EveIdentity(characterId, name, owner, scopes);
    }

    private TokenValidationParameters Parameters(IEnumerable<SecurityKey> keys) => new()
    {
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKeys = keys,
        ValidAlgorithms = AllowedAlgorithms,
        ValidateIssuer = true,
        ValidIssuers = AcceptedIssuers,
        ValidateAudience = true,
        AudienceValidator = (audiences, _, _) =>
        {
            var values = audiences?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            return values.Contains("EVE Online") && values.Contains(_clientId);
        },
        RequireAudience = true,
        RequireExpirationTime = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            if (notBefore is null || expires is null) return false;
            var now = _time.GetUtcNow().UtcDateTime;
            if (expires.Value < now.Subtract(ClockSkew)) return false;
            return notBefore.Value <= now.Add(ClockSkew);
        },
        NameClaimType = "name",
    };

    private static IReadOnlySet<string> ReadScopes(JwtPayload payload)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        if (!payload.TryGetValue("scp", out var raw) || raw is null) return scopes;
        switch (raw)
        {
            case string text:
                foreach (var scope in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) scopes.Add(scope);
                break;
            case JsonElement element when element.ValueKind == JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } scope) scopes.Add(scope);
                }
                break;
            case IEnumerable<object> items:
                foreach (var item in items)
                {
                    if (item is string scope && !string.IsNullOrWhiteSpace(scope)) scopes.Add(scope);
                }
                break;
        }
        return scopes;
    }

    [GeneratedRegex("^CHARACTER:EVE:([1-9][0-9]{0,18})$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterSubject();
}

internal sealed record PkceValues(string State, string Verifier, string Challenge)
{
    public static PkceValues Create()
    {
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new PkceValues(state, verifier, challenge);
    }

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record OAuthCallback(string? Code, string? Error);

internal interface IEveSsoClient
{
    Task<EveValidatedToken> AuthorizeAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task<EveValidatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}

internal sealed class EveSsoClient : IEveSsoClient
{
    private static readonly Uri AuthorizeEndpoint = new("https://login.eveonline.com/v2/oauth/authorize");
    private static readonly Uri TokenEndpoint = new("https://login.eveonline.com/v2/oauth/token");
    private const int MaxTokenResponseBytes = 64 * 1024;
    private static readonly TimeSpan CandidateReadTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly EveSsoOptions _options;
    private readonly EveJwtValidator _validator;
    private readonly IBrowserLauncher _browser;

    public EveSsoClient(HttpClient http, EveSsoOptions options, EveJwtValidator validator, IBrowserLauncher? browser = null)
    {
        _http = http;
        _options = options;
        _validator = validator;
        _browser = browser ?? new SystemBrowserLauncher();
    }

    public async Task<EveValidatedToken> AuthorizeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var redirect = new Uri(_options.RedirectUri);
        if (redirect.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(redirect.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("EVE SSO callback must be an HTTP loopback address.");
        }

        var pkce = PkceValues.Create();
        using var listener = new TcpListener(address, redirect.Port);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        try
        {
            listener.Start();
            _browser.Launch(BuildAuthorizeUri(pkce));
            var callback = await WaitForCallbackAsync(listener, redirect, pkce.State, linkedCts.Token);
            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                throw new OAuthTokenException(HttpStatusCode.BadRequest, callback.Error, "EVE SSO authentication was cancelled or denied.");
            }
            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                throw new InvalidDataException("EVE SSO did not return an authorization code.");
            }
            return await ExchangeCodeAsync(callback.Code, pkce.Verifier, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("EVE SSO authentication timed out.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<EveValidatedToken> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 2_048 || code.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Authorization code is invalid.", nameof(code));
        }
        if (verifier.Length is < 43 or > 128 || verifier.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~')))
        {
            throw new ArgumentException("PKCE verifier is invalid.", nameof(verifier));
        }
        var token = await SendTokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _options.ClientId,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = _options.RedirectUri,
        }, cancellationToken);
        return await ValidateTokenSetAsync(token, requireRefreshToken: true, cancellationToken);
    }

    public async Task<EveValidatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 2_048 || refreshToken.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Refresh token is invalid.", nameof(refreshToken));
        }
        var token = await SendTokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.ClientId,
        }, cancellationToken);
        return await ValidateTokenSetAsync(token, requireRefreshToken: false, cancellationToken);
    }

    internal Uri BuildAuthorizeUri(PkceValues pkce)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["redirect_uri"] = _options.RedirectUri,
            ["client_id"] = _options.ClientId,
            ["scope"] = string.Join(' ', _options.RequiredScopes.OrderBy(scope => scope, StringComparer.Ordinal)),
            ["state"] = pkce.State,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
        };
        return new Uri(AuthorizeEndpoint + "?" + string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
    }

    private async Task<EveValidatedToken> ValidateTokenSetAsync(RawTokenResponse token, bool requireRefreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken) || token.AccessToken.Length > 32 * 1024) throw new InvalidDataException("EVE SSO returned an invalid access token.");
        if (token.RefreshToken.Length > 2_048 || token.RefreshToken.IndexOf('\0') >= 0) throw new InvalidDataException("EVE SSO returned an invalid refresh token.");
        if (requireRefreshToken && string.IsNullOrWhiteSpace(token.RefreshToken)) throw new InvalidDataException("EVE SSO returned no refresh token.");
        if (token.ExpiresIn is <= 0 or > 86_400) throw new InvalidDataException("EVE SSO returned an invalid token lifetime.");
        if (!string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("EVE SSO returned an unexpected token type.");
        var identity = await _validator.ValidateAsync(token.AccessToken, cancellationToken);
        return new EveValidatedToken(token.AccessToken, token.RefreshToken, token.ExpiresIn, identity);
    }

    private async Task<RawTokenResponse> SendTokenRequestAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var text = await ReadTokenBodyAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = ReadOAuthError(text);
            throw new OAuthTokenException(response.StatusCode, error, $"EVE SSO token request returned {(int)response.StatusCode} ({error}).");
        }

        return JsonSerializer.Deserialize<RawTokenResponse>(text)
            ?? throw new InvalidDataException("EVE SSO returned an empty token response.");
    }

    private async Task<OAuthCallback> WaitForCallbackAsync(TcpListener listener, Uri redirect, string expectedState, CancellationToken cancellationToken)
    {
        var consumed = false;
        while (!consumed)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            using var candidateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            candidateCts.CancelAfter(CandidateReadTimeout);
            using var abort = candidateCts.Token.Register(client.Dispose);
            try
            {
                var stream = client.GetStream();
                var request = await ParseCallbackRequestAsync(stream, redirect, candidateCts.Token);
                if (request is null) continue;
                if (!request.Value.Query.TryGetValue("state", out var returnedState)
                    || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedState), Encoding.UTF8.GetBytes(returnedState)))
                {
                    await WriteFixedReplyAsync(stream, success: false, candidateCts.Token);
                    continue;
                }

                consumed = true;
                var error = request.Value.Query.TryGetValue("error", out var errorValue) ? SafeOAuthCode(errorValue) : null;
                var code = request.Value.Query.TryGetValue("code", out var codeValue) ? codeValue : null;
                await WriteFixedReplyAsync(stream, success: string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code), candidateCts.Token);
                return new OAuthCallback(code, error);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A local preconnect or silent socket does not consume OAuth state.
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A malformed local candidate does not end the sign-in attempt.
            }
            catch (InvalidDataException) when (!cancellationToken.IsCancellationRequested)
            {
                // Oversized or malformed local traffic does not consume OAuth state.
            }
        }
        throw new InvalidOperationException("OAuth state was consumed without a callback result.");
    }

    internal static async Task<(string Method, string Path, Dictionary<string, string> Query)?> ParseCallbackRequestAsync(
        Stream stream,
        Uri redirect,
        CancellationToken cancellationToken)
    {
        var requestLine = await ReadAsciiLineAsync(stream, 8_192, cancellationToken) ?? string.Empty;
        var pieces = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length != 3 || pieces[0] != "GET" || !pieces[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        if (!pieces[1].StartsWith('/')) return null;

        var headerBytes = 0;
        while (true)
        {
            var line = await ReadAsciiLineAsync(stream, 8_192, cancellationToken);
            if (line is null) return null;
            if (line.Length == 0) break;
            headerBytes += line.Length + 2;
            if (headerBytes > 32 * 1024) throw new InvalidDataException("Local callback headers were too large.");
        }

        if (!Uri.TryCreate(redirect, pieces[1], out var callback)
            || callback.Scheme != redirect.Scheme
            || callback.Port != redirect.Port
            || !string.Equals(callback.Host, redirect.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(callback.AbsolutePath.TrimEnd('/'), redirect.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal))
        {
            return null;
        }

        return (pieces[0], callback.AbsolutePath, ParseQuery(callback.Query));
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var bytes = new byte[maxBytes];
        var one = new byte[1];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0) return count == 0 ? null : Encoding.ASCII.GetString(bytes, 0, count);
            if (one[0] == (byte)'\n')
            {
                if (count > 0 && bytes[count - 1] == (byte)'\r') count--;
                return Encoding.ASCII.GetString(bytes, 0, count);
            }
            if (count == maxBytes) throw new InvalidDataException("Local callback line exceeded its configured limit.");
            bytes[count++] = one[0];
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0].Replace("+", " ", StringComparison.Ordinal));
            var value = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1].Replace("+", " ", StringComparison.Ordinal)) : string.Empty;
            if (key.Length > 128 || value.Length > 8_192) throw new InvalidDataException("Local callback query exceeded its configured limit.");
            if (!result.TryAdd(key, value)) throw new InvalidDataException("Local callback query contained a duplicate key.");
        }
        return result;
    }

    private static async Task WriteFixedReplyAsync(Stream stream, bool success, CancellationToken cancellationToken)
    {
        var message = success
            ? "Authentication complete. You can close this tab and return to TriffView."
            : "Authentication was not accepted. You can close this tab and return to TriffView.";
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>TriffView</title></head><body><p>{message}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        try
        {
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            // The browser page is informational; closing the tab does not change auth outcome.
        }
    }

    private static async Task<string> ReadTokenBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxTokenResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxTokenResponseBytes) throw new InvalidDataException("EVE SSO token response exceeded the configured limit.");
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string ReadOAuthError(string text)
    {
        try
        {
            var code = JsonNode.Parse(text)?["error"]?.GetValue<string>();
            return SafeOAuthCode(code);
        }
        catch
        {
            return "oauth_error";
        }
    }

    private static string SafeOAuthCode(string? value)
    {
        var safe = new string((value ?? string.Empty).Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(64).ToArray());
        return safe.Length == 0 ? "oauth_error" : safe;
    }

    private sealed class RawTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;
    }
}
