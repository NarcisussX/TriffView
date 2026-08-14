using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TriffView.Eve;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class ControllerLifecycleTests : IDisposable
{
    private static readonly HashSet<string> Scopes = new(StringComparer.Ordinal)
    {
        "esi-skills.read_skills.v1",
        "esi-skills.read_skillqueue.v1",
    };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "triffskills-controller-tests", Guid.NewGuid().ToString("N"));

    public ControllerLifecycleTests() => TriffSkillsPaths.OverrideRoot(_root);

    public void Dispose()
    {
        TriffSkillsPaths.ClearOverride();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void OverlappingRefreshesShareOneTokenRefreshAndPreserveRotatedToken()
    {
        SaveCharacter();
        var credentials = new MemoryCredentials((Target(), "old-refresh"));
        var sso = new ControlledSso { RefreshResult = ValidToken("rotated-refresh") };
        var handler = new SkillHandler();
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(credentials, sso, handler, messages);

        controller.HandleWebMessage("triffskills:refresh-characters", null);
        controller.HandleWebMessage("triffskills:refresh-characters", null);

        Assert.True(SpinWait.SpinUntil(() => handler.Calls >= 4, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, sso.RefreshCalls);
        Assert.Equal("rotated-refresh", credentials.Read(Target()));
        var character = Assert.Single(TriffSkillsState.Load().State.Characters);
        Assert.Equal(3, character.ActiveLevels[100]);
        Assert.Equal(5, character.TrainedLevels[100]);
    }

    [Fact]
    public void UnauthorizedResponseTriggersOneForcedRefreshAndOneRetry()
    {
        SaveCharacter();
        var credentials = new MemoryCredentials((Target(), "old-refresh"));
        var sso = new ControlledSso { RefreshResult = ValidToken("rotated") };
        var handler = new SkillHandler(unauthorizedFirstSkills: true);
        using var controller = Controller(credentials, sso, handler, new());

        controller.HandleWebMessage("triffskills:refresh-characters", null);

        Assert.True(SpinWait.SpinUntil(() => TriffSkillsState.Load().State.Characters.Single().FetchedUtc is not null, TimeSpan.FromSeconds(3)));
        Assert.Equal(2, sso.RefreshCalls);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public void ColdRefreshWritesStateOnceWhenAuthenticationMetadataIsUnchanged()
    {
        SaveCharacter();
        var credentials = new MemoryCredentials((Target(), "old-refresh"));
        var saves = 0;
        using var controller = Controller(
            credentials,
            new ControlledSso { RefreshResult = ValidToken("rotated") },
            new SkillHandler(),
            new(),
            () =>
            {
                Interlocked.Increment(ref saves);
                return null;
            });

        controller.HandleWebMessage("triffskills:refresh-characters", null);

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref saves) == 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, saves);
    }

    [Fact]
    public void MissingOwnerOnRefreshPreservesStoredOwner()
    {
        SaveCharacter();
        var credentials = new MemoryCredentials((Target(), "old-refresh"));
        var sso = new ControlledSso { RefreshResult = ValidToken("rotated", owner: null) };
        using var controller = Controller(credentials, sso, new SkillHandler(), new());

        controller.HandleWebMessage("triffskills:refresh-characters", null);

        Assert.True(SpinWait.SpinUntil(() => TriffSkillsState.Load().State.Characters.Single().FetchedUtc is not null, TimeSpan.FromSeconds(3)));
        Assert.Equal("owner-123456", TriffSkillsState.Load().State.Characters.Single().OwnerHash);
    }

    [Fact]
    public void OwnerMismatchRequiresReauthenticationWithoutReplacingCredential()
    {
        SaveCharacter();
        var credentials = new MemoryCredentials((Target(), "old-refresh"));
        var sso = new ControlledSso { RefreshResult = ValidToken("rotated", owner: "different-owner") };
        using var controller = Controller(credentials, sso, new SkillHandler(), new());

        controller.HandleWebMessage("triffskills:refresh-characters", null);

        Assert.True(SpinWait.SpinUntil(() => TriffSkillsState.Load().State.Characters.Single().NeedsReauth, TimeSpan.FromSeconds(3)));
        Assert.Equal("old-refresh", credentials.Read(Target()));
        Assert.Equal("owner-123456", TriffSkillsState.Load().State.Characters.Single().OwnerHash);
    }

    [Fact]
    public void ForgetDuringRefreshCannotRecreateCharacterOrCredential()
    {
        SaveCharacter();
        var credentials = new MemoryCredentials((Target(), "old-refresh"));
        var refresh = new TaskCompletionSource<EveValidatedToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sso = new ControlledSso { RefreshTask = refresh.Task };
        var handler = new SkillHandler();
        using var controller = Controller(credentials, sso, handler, new());

        controller.HandleWebMessage("triffskills:refresh-characters", null);
        Assert.True(SpinWait.SpinUntil(() => sso.RefreshCalls == 1, TimeSpan.FromSeconds(2)));
        controller.HandleWebMessage("triffskills:forget-character", JsonNode.Parse("""{"characterId":42}""")!.AsObject());
        refresh.SetResult(ValidToken("rotated"));

        Assert.True(SpinWait.SpinUntil(
            () => credentials.Read(Target()) is null && TriffSkillsState.Load().State.Characters.Count == 0,
            TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ForgetDuringReauthorizationCannotRecreateCharacterOrCredential()
    {
        SaveCharacter();
        var credentials = new MemoryCredentials((Target(), "old-refresh"));
        var authorization = new TaskCompletionSource<EveValidatedToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sso = new ControlledSso { AuthorizeTask = authorization.Task };
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(credentials, sso, new SkillHandler(), messages);

        controller.HandleWebMessage("triffskills:auth", null);
        Assert.True(SpinWait.SpinUntil(() => sso.AuthorizeCalls == 1, TimeSpan.FromSeconds(2)));
        controller.HandleWebMessage("triffskills:forget-character", JsonNode.Parse("""{"characterId":42}""")!.AsObject());
        Assert.True(SpinWait.SpinUntil(() => credentials.Read(Target()) is null, TimeSpan.FromSeconds(2)));
        authorization.SetResult(ValidToken("reauthorized"));

        Assert.True(SpinWait.SpinUntil(() => messages.Any(json => json.Contains("authentication was cancelled", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)));
        Assert.Empty(TriffSkillsState.Load().State.Characters);
        Assert.Null(credentials.Read(Target()));
    }

    [Fact]
    public void ConcurrentAuthStartsOnlyOneFlowAndCancellationReleasesTheGate()
    {
        var credentials = new MemoryCredentials();
        var authorize = new TaskCompletionSource<EveValidatedToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sso = new ControlledSso { AuthorizeTask = authorize.Task };
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(credentials, sso, new SkillHandler(), messages);

        controller.HandleWebMessage("triffskills:auth", null);
        controller.HandleWebMessage("triffskills:auth", null);
        Assert.True(SpinWait.SpinUntil(() => sso.AuthorizeCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Contains(messages, json => json.Contains("already in progress", StringComparison.Ordinal));

        controller.HandleWebMessage("triffskills:cancel-auth", null);
        authorize.TrySetCanceled();
        Assert.True(SpinWait.SpinUntil(() => messages.Any(json => json.Contains("authentication was cancelled", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void CredentialDeleteFailureKeepsCharacterAndTokenVisible()
    {
        SaveCharacter();
        var credentials = new MemoryCredentials((Target(), "old-refresh")) { FailDelete = true };
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(credentials, new ControlledSso(), new SkillHandler(), messages);

        controller.HandleWebMessage("triffskills:forget-character", JsonNode.Parse("""{"characterId":42}""")!.AsObject());

        Assert.True(SpinWait.SpinUntil(() => messages.Any(json => json.Contains("Credential deletion failed", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)));
        Assert.Single(TriffSkillsState.Load().State.Characters);
        Assert.Equal("old-refresh", credentials.Read(Target()));
    }

    [Fact]
    public void CredentialReadFailureKeepsCharacterVisible()
    {
        SaveCharacter();
        var credentials = new MemoryCredentials((Target(), "old-refresh")) { FailRead = true };
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(credentials, new ControlledSso(), new SkillHandler(), messages);

        controller.HandleWebMessage("triffskills:forget-character", JsonNode.Parse("""{"characterId":42}""")!.AsObject());

        Assert.True(SpinWait.SpinUntil(() => messages.Any(json => json.Contains("Credential lookup failed", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)));
        Assert.Single(TriffSkillsState.Load().State.Characters);
    }

    [Fact]
    public void CredentialWriteFailureDoesNotCreateAuthenticatedRow()
    {
        var credentials = new MemoryCredentials { FailWrite = true };
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(credentials, new ControlledSso(), new SkillHandler(), messages);

        controller.HandleWebMessage("triffskills:auth", null);

        Assert.True(SpinWait.SpinUntil(() => messages.Any(json => json.Contains("credential write failed", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)));
        Assert.Empty(TriffSkillsState.Load().State.Characters);
    }

    [Fact]
    public void PreviewCorrelatesResponseAndRejectsResolvedNonSkillType()
    {
        const string requestId = "request_1234";
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(new MemoryCredentials(), new ControlledSso(), new PlanValidationHandler(), messages);
        var message = new JsonObject
        {
            ["requestId"] = requestId,
            ["revision"] = 1,
            ["name"] = "Bad plan",
            ["contents"] = "Bogus Module V\n",
        };

        controller.HandleWebMessage("triffskills:preview-plan", message);

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains(requestId, StringComparison.Ordinal) && json.Contains("not in EVE", StringComparison.Ordinal) && json.Contains("skill category", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)),
            string.Join(Environment.NewLine, messages));
        Assert.Empty(Directory.EnumerateFiles(TriffSkillsPaths.PlansDir, "Bad plan.txt"));
    }

    private TriffSkillsController Controller(
        MemoryCredentials credentials,
        ControlledSso sso,
        HttpMessageHandler handler,
        ConcurrentQueue<string> messages,
        Func<string?>? saveState = null)
    {
        var esi = new EsiClient(new HttpClient(handler), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, "TriffView.Tests/1.0");
        return new TriffSkillsController(value => messages.Enqueue(JsonSerializer.Serialize(value)), credentials, esi, sso, TimeProvider.System, saveState);
    }

    private void SaveCharacter()
    {
        var state = new TriffSkillsState();
        var character = state.Upsert(42);
        character.CharacterName = "Pilot";
        character.OwnerHash = "owner-123456";
        character.Scopes = Scopes.OrderBy(scope => scope, StringComparer.Ordinal).ToList();
        Assert.True(state.TrySave(out var error), error);
    }

    private static string Target() => TriffSkillsAuthentication.CredentialPrefix + "42";
    private static EveValidatedToken ValidToken(string refresh, string? owner = "owner-123456", long characterId = 42) => new(
        "access-token",
        refresh,
        1_200,
        new EveIdentity(characterId, "Pilot", owner, Scopes));

    private sealed class ControlledSso : IEveSsoClient
    {
        public int AuthorizeCalls;
        public int RefreshCalls;
        public Task<EveValidatedToken>? AuthorizeTask { get; init; }
        public Task<EveValidatedToken>? RefreshTask { get; init; }
        public EveValidatedToken? RefreshResult { get; init; }

        public Task<EveValidatedToken> AuthorizeAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref AuthorizeCalls);
            return AuthorizeTask ?? Task.FromResult(ValidToken("auth-refresh"));
        }

        public Task<EveValidatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RefreshCalls);
            return RefreshTask ?? Task.FromResult(RefreshResult ?? ValidToken("rotated"));
        }
    }

    private sealed class MemoryCredentials(params (string Target, string Secret)[] entries) : ICredentialStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(entries.ToDictionary(entry => entry.Target, entry => entry.Secret), StringComparer.Ordinal);
        public bool FailDelete { get; init; }
        public bool FailRead { get; init; }
        public bool FailWrite { get; init; }
        public string? Read(string target)
        {
            if (FailRead) throw new IOException("credential read failed");
            return _values.TryGetValue(target, out var value) ? value : null;
        }
        public void Write(string target, string secret)
        {
            if (FailWrite) throw new IOException("credential write failed");
            _values[target] = secret;
        }
        public void Delete(string target, bool missingIsSuccess = true)
        {
            if (FailDelete) throw new IOException("credential delete failed");
            if (!_values.TryRemove(target, out _) && !missingIsSuccess) throw new InvalidOperationException("missing");
        }
        public IReadOnlyList<string> EnumerateTargets(string exactPrefix) => _values.Keys.Where(key => key.StartsWith(exactPrefix, StringComparison.Ordinal)).ToArray();
    }

    private sealed class SkillHandler(bool unauthorizedFirstSkills = false) : HttpMessageHandler
    {
        private int _unauthorizedRemaining = unauthorizedFirstSkills ? 1 : 0;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (request.RequestUri!.AbsolutePath.Contains("/skills/", StringComparison.Ordinal) && Interlocked.Exchange(ref _unauthorizedRemaining, 0) == 1)
            {
                return Task.FromResult(Response(HttpStatusCode.Unauthorized, "{\"error\":\"expired\"}"));
            }
            if (request.RequestUri.AbsolutePath.Contains("/skills/", StringComparison.Ordinal))
            {
                return Task.FromResult(Response(HttpStatusCode.OK, "{\"skills\":[{\"skill_id\":100,\"active_skill_level\":3,\"trained_skill_level\":5}]}"));
            }
            if (request.RequestUri.AbsolutePath.Contains("/skillqueue/", StringComparison.Ordinal))
            {
                return Task.FromResult(Response(HttpStatusCode.OK, "[]"));
            }
            return Task.FromResult(Response(HttpStatusCode.NotFound, "{\"error\":\"unexpected\"}"));
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class PlanValidationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v3/universe/ids/") return Task.FromResult(Response(HttpStatusCode.OK, "{\"inventory_types\":[{\"id\":123,\"name\":\"Bogus Module\"}]}"));
            if (path == "/v3/universe/types/123/") return Task.FromResult(Response(HttpStatusCode.OK, "{\"group_id\":456}"));
            if (path == "/v1/universe/groups/456/") return Task.FromResult(Response(HttpStatusCode.OK, "{\"category_id\":7}"));
            return Task.FromResult(Response(HttpStatusCode.NotFound, "{\"error\":\"unexpected\"}"));
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
