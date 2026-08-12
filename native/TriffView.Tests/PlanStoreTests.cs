using System.IO;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class PlanStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "triffskills-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temp directory is harmless.
        }
    }

    [Fact]
    public void SeedsOnlyWhenTheDirectoryDoesNotExist()
    {
        PlanStore.EnsureSeeded(_dir);
        var seeded = Path.Combine(_dir, PlanStore.StarterPlanName + ".txt");
        Assert.True(File.Exists(seeded));

        // A user who deletes every plan must not have the starter reappear.
        File.Delete(seeded);
        PlanStore.EnsureSeeded(_dir);
        Assert.False(File.Exists(seeded));
    }

    [Fact]
    public void TheSeededStarterPlanParsesToRequirements()
    {
        PlanStore.EnsureSeeded(_dir);
        var result = PlanStore.LoadAll(_dir);
        var plan = Assert.Single(result.Plans);
        Assert.Equal(PlanStore.StarterPlanName, plan.Name);
        Assert.NotEmpty(plan.Requirements);
        Assert.Empty(result.SkippedFiles);
    }

    [Fact]
    public void SkipsAndReportsPlansWithNoValidSkillLines()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "good.txt"), "Navigation V\n");
        File.WriteAllText(Path.Combine(_dir, "empty.txt"), "");
        File.WriteAllText(Path.Combine(_dir, "comments-only.txt"), "# nothing here\n# still nothing\n");

        var result = PlanStore.LoadAll(_dir);

        Assert.Equal("good", Assert.Single(result.Plans).Name);
        Assert.Equal(new[] { "comments-only.txt", "empty.txt" }, result.SkippedFiles.OrderBy(name => name).ToArray());
    }

    [Fact]
    public void ReportsTheNewestPlanFileWriteTime()
    {
        Directory.CreateDirectory(_dir);
        var older = Path.Combine(_dir, "older.txt");
        var newer = Path.Combine(_dir, "newer.txt");
        File.WriteAllText(older, "Navigation V\n");
        File.WriteAllText(newer, "Gunnery 3\n");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = PlanStore.LoadAll(_dir);

        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), result.LatestWriteUtc);
    }

    [Fact]
    public void MissingDirectoryYieldsAnEmptyResult()
    {
        var result = PlanStore.LoadAll(Path.Combine(_dir, "does-not-exist"));
        Assert.Empty(result.Plans);
        Assert.Empty(result.SkippedFiles);
        Assert.Null(result.LatestWriteUtc);
    }
}
