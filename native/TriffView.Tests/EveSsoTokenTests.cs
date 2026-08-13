using System.Net;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TriffView.Eve;
using Xunit;

namespace TriffView.Tests;

public class EveSsoTokenTests
{
    private const string ClientId = "test-client";
    private static readonly HashSet<string> Scopes = new(StringComparer.Ordinal) { "scope.read" };

    [Theory]
    [InlineData("{\"access_token\":\"not-a-jwt\",\"refresh_token\":\"refresh\",\"expires_in\":1200,\"token_type\":\"Basic\"}")]
    [InlineData("{\"access_token\":\"not-a-jwt\",\"refresh_token\":\"refresh\",\"expires_in\":0,\"token_type\":\"Bearer\"}")]
    [InlineData("{\"access_token\":\"not-a-jwt\",\"refresh_token\":\"refresh\",\"expires_in\":86401,\"token_type\":\"Bearer\"}")]
    public async Task RejectsInvalidTokenEnvelopeBeforeJwtUse(string responseJson)
    {
        var client = Client(responseJson);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ExchangeCodeAsync("code", new string('a', 43), CancellationToken.None));
    }

    [Fact]
    public async Task RejectsInvalidCodeAndPkceInputsBeforeNetworkUse()
    {
        var handler = new JsonHandler("{}");
        var client = Client(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExchangeCodeAsync("", new string('a', 43), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExchangeCodeAsync("code", "short", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExchangeCodeAsync("code", new string('!', 43), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    private static EveSsoClient Client(string responseJson) => Client(new JsonHandler(responseJson));

    private static EveSsoClient Client(JsonHandler handler)
    {
        var http = new HttpClient(handler);
        var validator = new EveJwtValidator(ClientId, Scopes, new NoKeys());
        return new EveSsoClient(
            http,
            new EveSsoOptions(ClientId, "http://127.0.0.1:51777/test/callback/", Scopes, "TriffView.Tests/1.0"),
            validator,
            new NoBrowser());
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class NoKeys : IEveSigningKeySource
    {
        public Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken)
            => throw new InvalidOperationException("JWT validation should not be reached for an invalid token envelope.");
    }

    private sealed class NoBrowser : IBrowserLauncher
    {
        public void Launch(Uri uri) => throw new InvalidOperationException("Browser should not be launched.");
    }
}
