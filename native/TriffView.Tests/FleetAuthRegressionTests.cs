using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using TriffView.Eve;
using TriffView.TriffFleets;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public sealed class FleetAuthRegressionTests
{
    private const long CharacterId = 42;
    private static readonly HashSet<string> Scopes = new(StringComparer.Ordinal)
    {
        "esi-fleets.read_fleet.v1",
        "esi-fleets.write_fleet.v1",
    };

    [Fact]
    public void AuthenticateVersusForgetDoesNotRestoreForgottenBoss()
    {
        var state = StateWithBoss();
        var credentials = new FleetCredentials((Target(), "old-refresh"));
        var authorization = new TaskCompletionSource<EveValidatedToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sso = new FleetSso { Authorization = authorization.Task };
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(state, credentials, sso, messages: messages);

        controller.HandleWebMessage("trifffleets:start-auth", null);
        Assert.True(SpinWait.SpinUntil(() => sso.AuthorizeCalls == 1, TimeSpan.FromSeconds(2)));
        controller.HandleWebMessage("trifffleets:forget-boss", CharacterMessage());
        Assert.True(SpinWait.SpinUntil(() => state.Bosses.Count == 0 && credentials.Read(Target()) is null, TimeSpan.FromSeconds(2)));
        authorization.SetResult(Token("reauthorized"));

        Assert.True(SpinWait.SpinUntil(() => messages.Any(message => message.Contains("authentication was cancelled", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)));
        Assert.Empty(state.Bosses);
        Assert.Null(credentials.Read(Target()));
    }

    [Fact]
    public void RefreshVersusForgetDoesNotRestoreBossOrCredential()
    {
        var state = StateWithBoss();
        var credentials = new FleetCredentials((Target(), "old-refresh"));
        var refresh = new TaskCompletionSource<EveValidatedToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sso = new FleetSso { Refresh = refresh.Task };
        using var controller = Controller(state, credentials, sso, new UnauthorizedThenMissingFleetHandler());

        controller.HandleWebMessage("trifffleets:detect-fleet", null);
        Assert.True(SpinWait.SpinUntil(() => sso.RefreshCalls == 1, TimeSpan.FromSeconds(2)));
        controller.HandleWebMessage("trifffleets:forget-boss", CharacterMessage());
        refresh.SetResult(Token("rotated-refresh"));

        Assert.True(SpinWait.SpinUntil(() => state.Bosses.Count == 0 && credentials.Read(Target()) is null, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void RefreshPersistsRotatedRefreshToken()
    {
        var state = StateWithBoss();
        var credentials = new FleetCredentials((Target(), "old-refresh"));
        var sso = new FleetSso { RefreshResult = Token("rotated-refresh") };
        using var controller = Controller(state, credentials, sso, new UnauthorizedThenMissingFleetHandler());

        controller.HandleWebMessage("trifffleets:detect-fleet", null);

        Assert.True(SpinWait.SpinUntil(() => credentials.Read(Target()) == "rotated-refresh", TimeSpan.FromSeconds(2)));
        Assert.Equal(2, sso.RefreshCalls);
    }

    [Fact]
    public void StateSaveFailureRestoresPriorCredentialAndBossMetadata()
    {
        var state = StateWithBoss();
        var credentials = new FleetCredentials((Target(), "old-refresh"));
        var sso = new FleetSso { RefreshResult = Token("rotated-refresh", name: "Changed Name") };
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(
            state,
            credentials,
            sso,
            new UnauthorizedThenMissingFleetHandler(),
            messages,
            () => throw new IOException("state save failed"));

        controller.HandleWebMessage("trifffleets:detect-fleet", null);

        Assert.True(SpinWait.SpinUntil(() => messages.Any(message => message.Contains("state save failed", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)));
        Assert.Equal("old-refresh", credentials.Read(Target()));
        Assert.Equal("Pilot", Assert.Single(state.Bosses).CharacterName);
    }

    [Fact]
    public void CredentialRollbackFailureReportsBothFailures()
    {
        var state = StateWithBoss();
        var credentials = new FleetCredentials((Target(), "old-refresh")) { FailWriteOnCall = 2 };
        var sso = new FleetSso { RefreshResult = Token("rotated-refresh") };
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(
            state,
            credentials,
            sso,
            new UnauthorizedThenMissingFleetHandler(),
            messages,
            () => throw new IOException("state save failed"));

        controller.HandleWebMessage("trifffleets:detect-fleet", null);

        Assert.True(SpinWait.SpinUntil(
            () => messages.Any(message => message.Contains("state save failed", StringComparison.Ordinal)
                && message.Contains("credential rollback also failed", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2)));
        Assert.Equal("rotated-refresh", credentials.Read(Target()));
    }

    [Fact]
    public void RefreshedIdentityMustMatchStoredBoss()
    {
        var state = StateWithBoss();
        var credentials = new FleetCredentials((Target(), "old-refresh"));
        var sso = new FleetSso { RefreshResult = Token("rotated-refresh", characterId: 84) };
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(state, credentials, sso, new UnauthorizedThenMissingFleetHandler(), messages);

        controller.HandleWebMessage("trifffleets:detect-fleet", null);

        Assert.True(SpinWait.SpinUntil(() => messages.Any(message => message.Contains("different character", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)));
        Assert.Equal("old-refresh", credentials.Read(Target()));
        Assert.Equal(CharacterId, Assert.Single(state.Bosses).CharacterId);
    }

    [Fact]
    public void MissingOwnerPreservesStoredOwner()
    {
        var state = StateWithBoss();
        var credentials = new FleetCredentials((Target(), "old-refresh"));
        var sso = new FleetSso { RefreshResult = Token("rotated-refresh", owner: null) };
        using var controller = Controller(state, credentials, sso, new UnauthorizedThenMissingFleetHandler());

        controller.HandleWebMessage("trifffleets:detect-fleet", null);

        Assert.True(SpinWait.SpinUntil(() => credentials.Read(Target()) == "rotated-refresh", TimeSpan.FromSeconds(2)));
        Assert.Equal("owner-123456", Assert.Single(state.Bosses).OwnerHash);
    }

    [Fact]
    public void OwnerMismatchKeepsPriorCredentialAndOwner()
    {
        var state = StateWithBoss();
        var credentials = new FleetCredentials((Target(), "old-refresh"));
        var sso = new FleetSso { RefreshResult = Token("rotated-refresh", owner: "different-owner") };
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(state, credentials, sso, new UnauthorizedThenMissingFleetHandler(), messages);

        controller.HandleWebMessage("trifffleets:detect-fleet", null);

        Assert.True(SpinWait.SpinUntil(() => messages.Any(message => message.Contains("ownership changed", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)));
        Assert.Equal("old-refresh", credentials.Read(Target()));
        Assert.Equal("owner-123456", Assert.Single(state.Bosses).OwnerHash);
    }

    [Fact]
    public void FleetCredentialRecoveryCannotReadSkillPlannerNamespace()
    {
        var state = new TriffFleetsLocalState().Normalize();
        var credentials = new FleetCredentials((TriffSkillsAuthentication.CredentialPrefix + CharacterId, "skill-refresh"));
        using var controller = Controller(state, credentials, new FleetSso());

        Assert.Empty(state.Bosses);
        Assert.Equal("skill-refresh", credentials.Read(TriffSkillsAuthentication.CredentialPrefix + CharacterId));
        Assert.Null(credentials.Read(Target()));
    }

    private static TriffFleetsController Controller(
        TriffFleetsLocalState state,
        FleetCredentials credentials,
        FleetSso sso,
        HttpMessageHandler? handler = null,
        ConcurrentQueue<string>? messages = null,
        Action? saveState = null)
    {
        messages ??= new ConcurrentQueue<string>();
        var esi = new EsiClient(
            new HttpClient(handler ?? new MissingFleetHandler()),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
            "TriffView.Tests/1.0");
        return new TriffFleetsController(
            Dispatcher.CurrentDispatcher,
            value => messages.Enqueue(JsonSerializer.Serialize(value)),
            credentials,
            esi,
            sso,
            state,
            saveState ?? (() => { }));
    }

    private static TriffFleetsLocalState StateWithBoss() => new TriffFleetsLocalState
    {
        SelectedBossCharacterId = CharacterId,
        Bosses =
        [
            new FleetBossAuth
            {
                CharacterId = CharacterId,
                CharacterName = "Pilot",
                OwnerHash = "owner-123456",
                Scopes = Scopes.OrderBy(scope => scope, StringComparer.Ordinal).ToList(),
                AuthenticatedUtc = DateTimeOffset.UtcNow,
            },
        ],
    }.Normalize();

    private static JsonObject CharacterMessage() => new() { ["characterId"] = CharacterId };
    private static string Target() => TriffFleetsController.CredentialPrefix + CharacterId;

    private static EveValidatedToken Token(
        string refreshToken,
        string? owner = "owner-123456",
        long characterId = CharacterId,
        string name = "Pilot") => new(
            "access-token",
            refreshToken,
            1_200,
            new EveIdentity(characterId, name, owner, Scopes));

    private sealed class FleetSso : IEveSsoClient
    {
        public int AuthorizeCalls;
        public int RefreshCalls;
        public Task<EveValidatedToken>? Authorization { get; init; }
        public Task<EveValidatedToken>? Refresh { get; init; }
        public EveValidatedToken? RefreshResult { get; init; }

        public Task<EveValidatedToken> AuthorizeAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref AuthorizeCalls);
            return Authorization ?? Task.FromResult(Token("authorized-refresh"));
        }

        public Task<EveValidatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RefreshCalls);
            return Refresh ?? Task.FromResult(RefreshResult ?? Token("rotated-refresh"));
        }
    }

    private sealed class FleetCredentials(params (string Target, string Secret)[] entries) : ICredentialStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(
            entries.ToDictionary(entry => entry.Target, entry => entry.Secret),
            StringComparer.Ordinal);
        private int _writeCalls;
        public int FailWriteOnCall { get; init; }

        public string? Read(string target) => _values.TryGetValue(target, out var value) ? value : null;

        public void Write(string target, string secret)
        {
            var call = Interlocked.Increment(ref _writeCalls);
            if (call == FailWriteOnCall) throw new IOException("credential rollback failed");
            _values[target] = secret;
        }

        public void Delete(string target, bool missingIsSuccess = true)
        {
            if (!_values.TryRemove(target, out _) && !missingIsSuccess) throw new InvalidOperationException("Credential was missing.");
        }

        public IReadOnlyList<string> EnumerateTargets(string exactPrefix)
            => _values.Keys.Where(target => target.StartsWith(exactPrefix, StringComparison.Ordinal)).ToArray();
    }

    private sealed class UnauthorizedThenMissingFleetHandler : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = Interlocked.Increment(ref _calls) == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound;
            return Task.FromResult(Response(status));
        }
    }

    private sealed class MissingFleetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Response(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Response(HttpStatusCode status)
        => new(status) { Content = new StringContent("{\"error\":\"fleet unavailable\"}", Encoding.UTF8, "application/json") };
}
