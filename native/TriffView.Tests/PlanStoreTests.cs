using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class PlanStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "triffskills-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void SeedsOnlyAPreviouslyMissingDirectory()
    {
        Assert.Equal(string.Empty, PlanStore.EnsureSeeded(_dir));
        var seed = Path.Combine(_dir, PlanStore.StarterPlanName + ".txt");
        Assert.True(File.Exists(seed));
        File.Delete(seed);
        Assert.Equal(string.Empty, PlanStore.EnsureSeeded(_dir));
        Assert.False(File.Exists(seed));
    }

    [Fact]
    public void StrictLoadReportsInvalidFilesWithoutPartiallyLoadingThem()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "good.txt"), "Navigation V\n");
        File.WriteAllText(Path.Combine(_dir, "mixed.txt"), "Gunnery III\nmalformed\n");
        File.WriteAllText(Path.Combine(_dir, "empty.txt"), "# comment\n");
        var result = PlanStore.LoadAll(_dir);
        Assert.Equal("good", Assert.Single(result.Plans).Name);
        Assert.Equal(2, result.Issues.Count);
        Assert.Contains(result.Issues, issue => issue.FileName == "mixed.txt" && issue.Diagnostics.Any(diagnostic => diagnostic.Line == 2));
    }

    [Fact]
    public void CommitRequiresPreviewThenCollisionRequiresExplicitReplace()
    {
        var parsed = SkillPlanParser.Parse("Plan", "Navigation V\n");
        var first = PlanStore.CommitValidated(_dir, "Plan", "Navigation V\n", parsed.Plan!, replace: false);
        var collision = PlanStore.CommitValidated(_dir, "plan", "Navigation V\n", parsed.Plan!, replace: false);
        var replacementPlan = SkillPlanParser.Parse("plan", "Gunnery III\n").Plan!;
        var replaced = PlanStore.CommitValidated(_dir, "plan", "Gunnery III\n", replacementPlan, replace: true);
        Assert.True(first.Success);
        Assert.True(collision.Collision);
        Assert.True(replaced.Success);
        Assert.Equal("Gunnery", Assert.Single(PlanStore.LoadAll(_dir).Plans).Requirements.Single().SkillName);
    }

    [Fact]
    public void CommitRefusesPreviewContentMismatch()
    {
        var preview = SkillPlanParser.Parse("Plan", "Navigation V\n").Plan!;
        var result = PlanStore.CommitValidated(_dir, "Plan", "Gunnery III\n", preview, replace: false);
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(_dir, "Plan.txt")));
    }

    [Fact]
    public void LoadCapsPlanCountAndReportsTheLimit()
    {
        Directory.CreateDirectory(_dir);
        for (var index = 0; index <= PlanStore.MaxPlanFiles; index++) File.WriteAllText(Path.Combine(_dir, $"p-{index:D3}.txt"), "Navigation I\n");
        var result = PlanStore.LoadAll(_dir);
        Assert.Equal(PlanStore.MaxPlanFiles, result.Plans.Count);
        Assert.Contains(result.Issues, issue => issue.FileName == "plans");
    }
}
