using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class SkillIdCacheTests
{
    [Fact]
    public void FromJsonPreservesCaseInsensitiveLookup()
    {
        // System.Text.Json deserializes into a default-comparer dictionary; FromJson
        // must copy entries into a case-insensitive map or hand-written plan casing
        // ("caldari frigate") stops resolving after a restart.
        var cache = SkillIdCache.FromJson("""{"Caldari Frigate": 123, " Padded ": 456}""");

        Assert.Equal(123, cache.Map["caldari frigate"]);
        Assert.Equal(456, cache.Map["padded"]); // keys are trimmed on load
    }

    [Fact]
    public void FromJsonDropsInvalidEntries()
    {
        var cache = SkillIdCache.FromJson("""{"": 1, "  ": 2, "Zeroed": 0, "Negative": -5, "Good": 9}""");
        Assert.Single(cache.Map);
        Assert.Equal(9, cache.Map["Good"]);
    }

    [Fact]
    public void UnresolvedDedupesCaseInsensitivelyAndSkipsCached()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Known"] = 1 };
        var missing = SkillIdCache.Unresolved(map, new[] { "known", "New Skill", "new skill", " New Skill ", "", "  " });
        Assert.Equal(new[] { "New Skill" }, missing);
    }

    [Fact]
    public void MergeAddsOnlyValidNewEntries()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Existing"] = 1 };
        var added = SkillIdCache.Merge(map, new[]
        {
            new SkillsUniverseIdName { Id = 2, Name = "Existing" },   // already present
            new SkillsUniverseIdName { Id = 3, Name = "Fresh" },
            new SkillsUniverseIdName { Id = 0, Name = "Zero Id" },
            new SkillsUniverseIdName { Id = 4, Name = "  " },
        });

        Assert.Equal(1, added);
        Assert.Equal(1, map["Existing"]); // first resolution wins
        Assert.Equal(3, map["Fresh"]);
    }

    [Fact]
    public void BatchSplitsAtTheRequestedSize()
    {
        var names = Enumerable.Range(0, 1201).Select(i => $"skill-{i}").ToList();
        var batches = SkillIdCache.Batch(names, 500);
        Assert.Equal(3, batches.Count);
        Assert.Equal(500, batches[0].Count);
        Assert.Equal(500, batches[1].Count);
        Assert.Equal(201, batches[2].Count);
    }

    [Fact]
    public async Task ResolveMissingAsyncSendsOnlyUncachedNames()
    {
        var cache = SkillIdCache.FromJson("""{"Cached": 1}""");
        var requested = new List<string>();
        var added = await cache.ResolveMissingAsync(
            new[] { "Cached", "New One" },
            batch =>
            {
                requested.AddRange(batch);
                return Task.FromResult<IReadOnlyList<SkillsUniverseIdName>>(
                    new[] { new SkillsUniverseIdName { Id = 7, Name = "New One" } });
            },
            persist: false);

        Assert.Equal(1, added);
        Assert.Equal(new[] { "New One" }, requested);
        Assert.Equal(7, cache.Map["new one"]);
    }
}
