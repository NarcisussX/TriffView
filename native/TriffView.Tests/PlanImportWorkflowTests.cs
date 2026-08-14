using TriffView.TriffSkills;

namespace TriffView.Tests;

public class PlanImportWorkflowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "triffskills-import-tests", Guid.NewGuid().ToString("N"));
    private readonly MutableTimeProvider _time = new(DateTimeOffset.UtcNow);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task CommitRequiresTheValidatedInputRevision()
    {
        var workflow = Workflow();
        var preview = await workflow.PreviewAsync("request_1234", 7, "Plan", "Navigation V\n", CancellationToken.None);
        Assert.NotNull(preview.Plan);

        var stale = workflow.Commit("request_1234", 8, replace: false);
        Assert.True(stale.Expired);
        Assert.False(File.Exists(Path.Combine(_dir, "Plan.txt")));

        var current = workflow.Commit("request_1234", 7, replace: false);
        Assert.True(current.Success);
        Assert.True(File.Exists(Path.Combine(_dir, "Plan.txt")));
    }

    [Fact]
    public async Task ExpiredPreviewCannotBeCommitted()
    {
        var workflow = Workflow();
        await workflow.PreviewAsync("request_5678", 1, "Plan", "Navigation V\n", CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(11));

        var result = workflow.Commit("request_5678", 1, replace: false);

        Assert.True(result.Expired);
        Assert.False(File.Exists(Path.Combine(_dir, "Plan.txt")));
    }

    [Fact]
    public async Task CollisionReplacementUsesTheSamePreview()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Plan.txt"), "Gunnery I\n");
        var workflow = Workflow();
        await workflow.PreviewAsync("request_9012", 3, "Plan", "Navigation V\n", CancellationToken.None);

        Assert.True(workflow.Commit("request_9012", 3, replace: false).Collision);
        Assert.True(workflow.Commit("request_9012", 3, replace: true).Success);
        Assert.Contains("Navigation V", File.ReadAllText(Path.Combine(_dir, "Plan.txt")), StringComparison.Ordinal);
    }

    private PlanImportWorkflow Workflow()
        => new(_dir, _time, (_, _) => Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
