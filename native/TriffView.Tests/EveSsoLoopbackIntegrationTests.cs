using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using TriffView.Eve;
using Xunit;

namespace TriffView.Tests;

public class EveSsoLoopbackIntegrationTests : IDisposable
{
    private const string ClientId = "test-client";
    private static readonly HashSet<string> Scopes = new(StringComparer.Ordinal) { "scope.read" };
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public EveSsoLoopbackIntegrationTests() => _key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
    public void Dispose() => _rsa.Dispose();

    [Fact]
    public async Task WrongStateDoesNotConsumeCallbackAndCorrectStateCompletes()
    {
        var redirect = FreeRedirect();
        var browser = new ScriptedBrowser(async authorizeUri =>
        {
            var state = Query(authorizeUri)["state"];
            await SendCallbackAsync(redirect, "code=wrong&state=not-the-state");
            await SendCallbackAsync(redirect, $"code=accepted&state={Uri.EscapeDataString(state)}");
        });
        var handler = new TokenHandler(Token());

        var result = await Client(redirect, browser, handler).AuthorizeAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        await browser.Completion;

        Assert.Equal(123456789, result.Identity.CharacterId);
        Assert.Equal("refresh-token", result.RefreshToken);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OAuthErrorConsumesValidStateWithoutCallingTokenEndpoint()
    {
        var redirect = FreeRedirect();
        var browser = new ScriptedBrowser(async authorizeUri =>
        {
            var state = Query(authorizeUri)["state"];
            await SendCallbackAsync(redirect, $"error=access_denied&state={Uri.EscapeDataString(state)}");
        });
        var handler = new TokenHandler(Token());

        var error = await Assert.ThrowsAsync<OAuthTokenException>(
            () => Client(redirect, browser, handler).AuthorizeAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        await browser.Completion;

        Assert.Equal("access_denied", error.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CallbackTimeoutAndCallerCancellationRemainDistinct()
    {
        var timeoutBrowser = new ScriptedBrowser(_ => Task.CompletedTask);
        await Assert.ThrowsAsync<TimeoutException>(
            () => Client(FreeRedirect(), timeoutBrowser, new TokenHandler(Token())).AuthorizeAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancelBrowser = new ScriptedBrowser(_ => Task.CompletedTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(FreeRedirect(), cancelBrowser, new TokenHandler(Token())).AuthorizeAsync(TimeSpan.FromSeconds(3), cancellation.Token));
    }

    [Fact]
    public async Task SimultaneousAuthorizationFailsBeforeBindingAnotherCallbackPort()
    {
        var firstBrowser = new ScriptedBrowser(_ => Task.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        var first = Client(FreeRedirect(), firstBrowser, new TokenHandler(Token()))
            .AuthorizeAsync(TimeSpan.FromSeconds(3), cancellation.Token);
        Assert.True(SpinWait.SpinUntil(() => !first.IsCompleted, TimeSpan.FromSeconds(1)));

        var second = Client(FreeRedirect(), new ScriptedBrowser(_ => Task.CompletedTask), new TokenHandler(Token()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.AuthorizeAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Contains("already in progress", error.Message, StringComparison.Ordinal);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    private EveSsoClient Client(string redirect, IBrowserLauncher browser, TokenHandler handler)
    {
        var validator = new EveJwtValidator(ClientId, Scopes, new StaticKeys(_key));
        return new EveSsoClient(
            new HttpClient(handler),
            new EveSsoOptions(ClientId, redirect, Scopes, "TriffView.Tests/1.0"),
            validator,
            browser);
    }

    private string Token()
    {
        var now = DateTime.UtcNow;
        var claims = new[]
        {
            new Claim("sub", "CHARACTER:EVE:123456789"),
            new Claim("name", "Pilot"),
            new Claim("owner", "owner-123456"),
            new Claim("azp", ClientId),
        };
        var payload = new JwtPayload("https://login.eveonline.com", null, claims, now.AddMinutes(-1), now.AddMinutes(20));
        payload["aud"] = new[] { "EVE Online", ClientId };
        payload["scp"] = Scopes.ToArray();
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(_key, SecurityAlgorithms.RsaSha256)),
            payload));
    }

    private static string FreeRedirect()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return $"http://127.0.0.1:{port}/test/callback/";
    }

    private static async Task SendCallbackAsync(string redirectText, string query)
    {
        var redirect = new Uri(redirectText);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, redirect.Port);
        var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes($"GET {redirect.AbsolutePath}?{query} HTTP/1.1\r\nHost: {redirect.Host}:{redirect.Port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        var buffer = new byte[1024];
        while (await stream.ReadAsync(buffer) > 0) { }
    }

    private static Dictionary<string, string> Query(Uri uri)
        => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]), StringComparer.Ordinal);

    private sealed class StaticKeys(params SecurityKey[] keys) : IEveSigningKeySource
    {
        public Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SecurityKey>>(keys);
    }

    private sealed class ScriptedBrowser(Func<Uri, Task> script) : IBrowserLauncher
    {
        private Task _completion = Task.CompletedTask;
        public Task Completion => _completion;
        public void Launch(Uri uri) => _completion = Task.Run(() => script(uri));
    }

    private sealed class TokenHandler(string accessToken) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var json = JsonSerializer.Serialize(new
            {
                access_token = accessToken,
                refresh_token = "refresh-token",
                expires_in = 1_200,
                token_type = "Bearer",
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
