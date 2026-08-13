using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TriffView.Eve;
using Xunit;

namespace TriffView.Tests;

public class EsiClientTests
{
    [Theory]
    [InlineData("https://evil.invalid/v1/path/")]
    [InlineData("/latest/path/")]
    [InlineData("/v1/../secret")]
    [InlineData("\\v1\\path")]
    public void RejectsAnythingExceptVersionedRelativeRoutes(string path)
    {
        Assert.Throws<ArgumentException>(() => EsiClient.ValidateAndBuildUri(path));
    }

    [Fact]
    public async Task GetHonorsRetryAfterAndStopsAfterSuccess()
    {
        var handler = new QueueHandler(
            Response(HttpStatusCode.TooManyRequests, "{\"error\":\"slow down\"}", response => response.Headers.TryAddWithoutValidation("Retry-After", "7")),
            Response(HttpStatusCode.OK, "{\"value\":42}"));
        var delays = new List<TimeSpan>();
        var client = Client(handler, (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        var result = await client.SendAsync<ValueResponse>(HttpMethod.Get, "/v1/test/", null);
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value!.Value);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delays));
    }

    [Fact]
    public async Task UnauthorizedAndMutationPostsAreNotRetriedByTransport()
    {
        var unauthorized = new QueueHandler(Response(HttpStatusCode.Unauthorized, "{\"error\":\"nope\"}"));
        var unauthorizedResult = await Client(unauthorized).SendAsync<object>(HttpMethod.Get, "/v1/test/", "token");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResult.StatusCode);
        Assert.Equal(1, unauthorized.Calls);

        var mutation = new QueueHandler(Response(HttpStatusCode.ServiceUnavailable, "{\"error\":\"later\"}"));
        await Client(mutation).SendAsync<object>(HttpMethod.Post, "/v1/fleets/1/wings/", "token", new { name = "x" });
        Assert.Equal(1, mutation.Calls);
    }

    [Fact]
    public async Task ErrorBodiesAreBoundedSanitizedAndNeverDeserialized()
    {
        var handler = new QueueHandler(Response(HttpStatusCode.BadRequest, new string('x', EsiClient.MaxErrorBodyBytes * 3) + "\u0001secret"));
        var result = await Client(handler).SendAsync<ValueResponse>(HttpMethod.Get, "/v1/test/", null);
        Assert.False(result.IsSuccess);
        Assert.True(result.Error.Length <= 2_048);
        Assert.DoesNotContain('\u0001', result.Error);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task SendsStableHeadersAndPropagatesCancellation()
    {
        var handler = new InspectingHandler();
        var client = Client(handler);
        await client.SendAsync<object>(HttpMethod.Get, "/v1/test/", "access");
        Assert.Contains("TriffView.Tests", handler.UserAgent);
        Assert.Equal(EsiClient.CompatibilityDate, handler.CompatibilityDate);
        Assert.Equal("Bearer", handler.AuthorizationScheme);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync<object>(HttpMethod.Get, "/v1/test/", null, cancellationToken: cts.Token));
    }

    private static EsiClient Client(HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(new HttpClient(handler), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, "TriffView.Tests/1.0 (+https://example.invalid)", delay);

    private static HttpResponseMessage Response(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        configure?.Invoke(response);
        return response;
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class InspectingHandler : HttpMessageHandler
    {
        public string UserAgent { get; private set; } = "";
        public string CompatibilityDate { get; private set; } = "";
        public string AuthorizationScheme { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UserAgent = request.Headers.UserAgent.ToString();
            CompatibilityDate = request.Headers.GetValues("X-Compatibility-Date").Single();
            AuthorizationScheme = request.Headers.Authorization?.Scheme ?? "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") });
        }
    }

    private sealed class ValueResponse { public int Value { get; set; } }
}
