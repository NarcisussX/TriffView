using System.Net;
using TriffView.EveSettings;
using Xunit;

namespace TriffView.Tests;

public class CharacterNameBatchResolverTests
{
    [Theory]
    [InlineData(/* lang=json,strict */ "{\"error\":\"Ensure all IDs are valid before resolving.\"}")]
    [InlineData(/* lang=json,strict */ "{\"error\":\"some reworded message\"}")]
    public void IsInvalidIdsBody_TrueForJsonErrorShape(string body)
    {
        Assert.True(CharacterNameBatchResolver.IsInvalidIdsBody(body));
    }

    [Theory]
    [InlineData("404 page not found")]
    [InlineData("")]
    [InlineData(/* lang=json,strict */ "{\"other\":\"field\"}")]
    [InlineData(/* lang=json,strict */ "{\"error\":\"\"}")]
    [InlineData(/* lang=json,strict */ "{\"error\":null}")]
    [InlineData(/* lang=json,strict */ "[1,2,3]")]
    [InlineData("not json at all {")]
    public void IsInvalidIdsBody_FalseForRouteLevelOrUnrecognizedBodies(string body)
    {
        Assert.False(CharacterNameBatchResolver.IsInvalidIdsBody(body));
    }

    [Fact]
    public void ClassifyResponse_NotFoundWithJsonErrorBody_IsInvalidIds()
    {
        var outcome = CharacterNameBatchResolver.ClassifyResponse(
            HttpStatusCode.NotFound,
            /* lang=json,strict */ "{\"error\":\"Ensure all IDs are valid before resolving.\"}");

        Assert.Equal(NameBatchOutcomeKind.InvalidIds, outcome.Kind);
    }

    [Fact]
    public void ClassifyResponse_NotFoundWithPlainTextBody_IsTransient()
    {
        var outcome = CharacterNameBatchResolver.ClassifyResponse(HttpStatusCode.NotFound, "404 page not found");

        Assert.Equal(NameBatchOutcomeKind.Transient, outcome.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData((HttpStatusCode)420)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void ClassifyResponse_OtherNonSuccessStatus_IsTransient(HttpStatusCode status)
    {
        var outcome = CharacterNameBatchResolver.ClassifyResponse(status, "irrelevant body");

        Assert.Equal(NameBatchOutcomeKind.Transient, outcome.Kind);
    }

    [Fact]
    public void ClassifyResponse_SuccessWithWellFormedArray_IsResolvedWithNames()
    {
        var outcome = CharacterNameBatchResolver.ClassifyResponse(
            HttpStatusCode.OK,
            /* lang=json,strict */ "[{\"id\":1,\"name\":\"Alice\"},{\"id\":2,\"name\":\"Bob\"}]");

        Assert.Equal(NameBatchOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(2, outcome.Names!.Count);
        Assert.Equal("Alice", outcome.Names[1]);
        Assert.Equal("Bob", outcome.Names[2]);
    }

    [Fact]
    public void ClassifyResponse_SuccessWithZeroOrNegativeId_SkipsThatEntry()
    {
        var outcome = CharacterNameBatchResolver.ClassifyResponse(
            HttpStatusCode.OK,
            /* lang=json,strict */ "[{\"id\":0,\"name\":\"Nobody\"},{\"id\":-1,\"name\":\"Nobody\"},{\"id\":3,\"name\":\"Carl\"}]");

        Assert.Equal(NameBatchOutcomeKind.Resolved, outcome.Kind);
        Assert.Single(outcome.Names!);
        Assert.Equal("Carl", outcome.Names![3]);
    }

    [Fact]
    public void ClassifyResponse_SuccessWithBlankName_SkipsThatEntry()
    {
        var outcome = CharacterNameBatchResolver.ClassifyResponse(
            HttpStatusCode.OK,
            /* lang=json,strict */ "[{\"id\":1,\"name\":\"  \"},{\"id\":2,\"name\":\"Dana\"}]");

        Assert.Equal(NameBatchOutcomeKind.Resolved, outcome.Kind);
        Assert.Single(outcome.Names!);
        Assert.Equal("Dana", outcome.Names![2]);
    }

    [Theory]
    [InlineData(/* lang=json,strict */ "{\"id\":1,\"name\":\"Alice\"}")]
    [InlineData("not json")]
    [InlineData("")]
    public void ClassifyResponse_SuccessWithNonArrayBody_IsTransient(string body)
    {
        var outcome = CharacterNameBatchResolver.ClassifyResponse(HttpStatusCode.OK, body);

        Assert.Equal(NameBatchOutcomeKind.Transient, outcome.Kind);
    }

    [Fact]
    public async Task AllGoodBatch_IsOneRequest()
    {
        var requests = new List<IReadOnlyList<long>>();
        Task<NameBatchOutcome> Resolve(IReadOnlyList<long> ids, CancellationToken ct)
        {
            requests.Add(ids);
            return Task.FromResult(NameBatchOutcome.Resolved(ids.ToDictionary(id => id, id => $"Char {id}")));
        }

        var knownInvalid = new HashSet<long>();
        var ids = new long[] { 1, 2, 3 };
        var result = await CharacterNameBatchResolver.ResolveAsync(ids, knownInvalid, Resolve);

        Assert.Single(requests);
        Assert.Equal(3, result.Count);
        Assert.Equal("Char 1", result[1]);
        Assert.Equal("Char 2", result[2]);
        Assert.Equal("Char 3", result[3]);
        Assert.Empty(knownInvalid);
    }

    [Fact]
    public async Task OneBadIdAmongMany_StillResolvesAllGoodIds()
    {
        const long badId = 99;
        var requestCount = 0;

        Task<NameBatchOutcome> Resolve(IReadOnlyList<long> ids, CancellationToken ct)
        {
            requestCount++;
            if (ids.Contains(badId)) return Task.FromResult(NameBatchOutcome.InvalidIds);
            return Task.FromResult(NameBatchOutcome.Resolved(ids.ToDictionary(id => id, id => $"Char {id}")));
        }

        var knownInvalid = new HashSet<long>();
        var ids = new long[] { 1, 2, 3, badId, 5, 6, 7, 8 };
        var result = await CharacterNameBatchResolver.ResolveAsync(ids, knownInvalid, Resolve);

        foreach (var id in ids.Where(id => id != badId))
        {
            Assert.Equal($"Char {id}", result[id]);
        }
        Assert.False(result.ContainsKey(badId));
        Assert.Contains(badId, knownInvalid);
        Assert.True(requestCount > 1, "expected the rejected batch to be bisected into more than one request");
    }

    [Fact]
    public async Task BadId_IsNotRequestedAgainOnASecondCall()
    {
        const long badId = 99;
        var requestedIds = new List<long>();

        Task<NameBatchOutcome> Resolve(IReadOnlyList<long> ids, CancellationToken ct)
        {
            requestedIds.AddRange(ids);
            if (ids.Contains(badId)) return Task.FromResult(NameBatchOutcome.InvalidIds);
            return Task.FromResult(NameBatchOutcome.Resolved(ids.ToDictionary(id => id, id => $"Char {id}")));
        }

        var knownInvalid = new HashSet<long>();
        var ids = new long[] { 1, badId };
        await CharacterNameBatchResolver.ResolveAsync(ids, knownInvalid, Resolve);
        Assert.Contains(badId, knownInvalid);

        requestedIds.Clear();
        var result = await CharacterNameBatchResolver.ResolveAsync(ids, knownInvalid, Resolve);

        Assert.DoesNotContain(badId, requestedIds);
        Assert.Equal("Char 1", result[1]);
    }

    [Fact]
    public async Task TransientFailure_DoesNotBisectOrBlacklist()
    {
        var requestCount = 0;
        Task<NameBatchOutcome> Resolve(IReadOnlyList<long> ids, CancellationToken ct)
        {
            requestCount++;
            return Task.FromResult(NameBatchOutcome.Transient);
        }

        var knownInvalid = new HashSet<long>();
        var ids = new long[] { 1, 2, 3 };
        var result = await CharacterNameBatchResolver.ResolveAsync(ids, knownInvalid, Resolve);

        Assert.Equal(1, requestCount);
        Assert.Empty(result);
        Assert.Empty(knownInvalid);
    }

    [Fact]
    public async Task EmptyIds_DoesNothing()
    {
        var called = false;
        Task<NameBatchOutcome> Resolve(IReadOnlyList<long> ids, CancellationToken ct)
        {
            called = true;
            return Task.FromResult(NameBatchOutcome.Resolved(new Dictionary<long, string>()));
        }

        var knownInvalid = new HashSet<long>();
        var result = await CharacterNameBatchResolver.ResolveAsync(Array.Empty<long>(), knownInvalid, Resolve);

        Assert.False(called);
        Assert.Empty(result);
    }

    [Fact]
    public async Task AllIdsAlreadyKnownInvalid_DoesNothing()
    {
        var called = false;
        Task<NameBatchOutcome> Resolve(IReadOnlyList<long> ids, CancellationToken ct)
        {
            called = true;
            return Task.FromResult(NameBatchOutcome.Resolved(new Dictionary<long, string>()));
        }

        var knownInvalid = new HashSet<long> { 1, 2 };
        var result = await CharacterNameBatchResolver.ResolveAsync(new long[] { 1, 2 }, knownInvalid, Resolve);

        Assert.False(called);
        Assert.Empty(result);
    }

    [Fact]
    public async Task EveryIdBad_ResolvesNoneAndBlacklistsAllWithoutUnboundedFanOut()
    {
        var requestCount = 0;
        Task<NameBatchOutcome> Resolve(IReadOnlyList<long> ids, CancellationToken ct)
        {
            requestCount++;
            return Task.FromResult(NameBatchOutcome.InvalidIds);
        }

        var knownInvalid = new HashSet<long>();
        var ids = Enumerable.Range(1, 16).Select(i => (long)i).ToArray();
        var result = await CharacterNameBatchResolver.ResolveAsync(ids, knownInvalid, Resolve);

        Assert.Empty(result);
        Assert.Equal(ids.Length, knownInvalid.Count);
        // A full binary bisect of n bad ids costs 2n-1 requests; anything beyond that would
        // indicate the recursion is not properly halving the batch each time.
        Assert.Equal(2 * ids.Length - 1, requestCount);
    }
}
