using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TriffView.Eve;

internal sealed class EveSsoClient : IEveSsoClient
{
    private static readonly Uri AuthorizeEndpoint = new("https://login.eveonline.com/v2/oauth/authorize");
    private static readonly Uri TokenEndpoint = new("https://login.eveonline.com/v2/oauth/token");
    private static readonly SemaphoreSlim AuthorizationGate = new(1, 1);
    private const int MaxTokenResponseBytes = 64 * 1024;
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
        if (!await AuthorizationGate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("Another EVE authentication is already in progress.");
        }

        try
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
                var callback = await EveLoopbackCallback.WaitAsync(listener, redirect, pkce.State, linkedCts.Token);
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
        finally
        {
            AuthorizationGate.Release();
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
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var text = await ReadTokenBodyAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = ReadOAuthError(text);
            throw new OAuthTokenException(response.StatusCode, error, $"EVE SSO token request returned {(int)response.StatusCode} ({error}).");
        }

        try
        {
            return JsonSerializer.Deserialize<RawTokenResponse>(text)
                ?? throw new InvalidDataException("EVE SSO returned an empty token response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("EVE SSO returned an invalid token response.", exception);
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
            return EveLoopbackCallback.SafeOAuthCode(JsonNode.Parse(text)?["error"]?.GetValue<string>());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return "oauth_error";
        }
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
