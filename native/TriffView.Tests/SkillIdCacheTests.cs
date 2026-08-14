using System.Text.Json;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class SkillIdCacheTests
{
    [Fact]
    public void VersionedCacheLoadsOnlyValidatedPositiveTypeAndGroupIds()
    {
        var cache = SkillIdCache.FromJson("""{"version":3,"skills":{"Caldari Frigate":{"typeId":123,"groupId":45,"categoryId":16},"Bad":{"typeId":0,"groupId":2,"categoryId":16},"Wrong category":{"typeId":8,"groupId":2,"categoryId":6}," Padded ":{"typeId":9,"groupId":3,"categoryId":16}}}""");
        Assert.Equal(123, cache.Map["caldari frigate"].TypeId);
        Assert.Equal(9, cache.Map["padded"].TypeId);
        Assert.False(cache.Map.ContainsKey("Bad"));
        Assert.False(cache.Map.ContainsKey("Wrong category"));
    }

    [Fact]
    public void LegacyArbitraryIdMapIsNotAcceptedAsValidatedData()
    {
        Assert.Throws<JsonException>(() => SkillIdCache.FromJson("""{"Navigation":123}"""));
    }

    [Fact]
    public void UnresolvedDeduplicatesAndMergeNeverOverwritesFirstValidation()
    {
        var cache = new SkillIdCache();
        Assert.Equal(1, cache.Merge([("Known", new ValidatedSkillType(1, 2))]));
        Assert.Equal(new[] { "New" }, cache.Unresolved(["known", "New", " new ", ""]));
        Assert.Equal(1, cache.Merge([
            ("Known", new ValidatedSkillType(8, 9)),
            ("New", new ValidatedSkillType(3, 4)),
            ("Bad", new ValidatedSkillType(0, 4))]));
        Assert.Equal(1, cache.Map["Known"].TypeId);
    }

    [Fact]
    public void BatchUsesTheBoundedEsiSize()
    {
        var batches = SkillIdCache.Batch(Enumerable.Range(0, 1201).Select(index => $"skill-{index}"));
        Assert.Equal([500, 500, 201], batches.Select(batch => batch.Count).ToArray());
    }
}
