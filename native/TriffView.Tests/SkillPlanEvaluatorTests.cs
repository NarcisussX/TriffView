using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class SkillPlanEvaluatorTests
{
    private static readonly DateTimeOffset Soon = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, int> Ids = new(StringComparer.OrdinalIgnoreCase) { ["Navigation"] = 100, ["Gunnery"] = 200 };
    private static SkillPlan Plan(params (string Name, int Level)[] values) => new("p", values.Select(value => new PlanRequirement(value.Name, value.Level)).ToArray());
    private static ILookup<int, QueueEntry> Queue(params QueueEntry[] entries) => entries.ToLookup(entry => entry.SkillId);

    [Fact]
    public void ReadyRequiresEveryRequirementAtItsActiveLevel()
    {
        var ready = Evaluate(Plan(("Navigation", 4)), active: Levels((100, 4)), trained: Levels((100, 5)));
        var inactive = Evaluate(Plan(("Navigation", 4)), active: Levels((100, 3)), trained: Levels((100, 5)));
        Assert.Equal(PlanReadiness.Ready, ready.Readiness);
        Assert.Equal(PlanReadiness.Locked, inactive.Readiness);
        Assert.Equal(RequirementState.TrainedInactive, Assert.Single(inactive.Requirements).State);
    }

    [Fact]
    public void TrainingUsesSmallestSufficientQueueEntryAndLatestPlanEta()
    {
        var analysis = Evaluate(
            Plan(("Navigation", 3), ("Gunnery", 3)),
            active: Levels(),
            trained: Levels(),
            Queue(
                new QueueEntry(100, 5, Soon, Later, 3),
                new QueueEntry(100, 3, Soon, Soon, 1),
                new QueueEntry(200, 3, Soon, Later, 2)));
        Assert.Equal(PlanReadiness.Training, analysis.Readiness);
        Assert.Equal(Later, analysis.EstimatedFinishUtc);
        Assert.Equal(Soon, analysis.Requirements[0].QueuedFinishUtc);
    }

    [Fact]
    public void PausedQueueIsTrainingWithExplicitUnknownTiming()
    {
        var analysis = Evaluate(Plan(("Navigation", 4)), Levels(), Levels(), Queue(new QueueEntry(100, 4, null, null)));
        Assert.Equal(PlanReadiness.Training, analysis.Readiness);
        Assert.True(analysis.QueueTimingUnknown);
        Assert.Null(analysis.EstimatedFinishUtc);
    }

    [Fact]
    public void MissingAndUnknownRemainDistinct()
    {
        var missing = Evaluate(Plan(("Navigation", 4)), Levels(), Levels());
        var unknown = Evaluate(Plan(("Mystery", 1)), Levels(), Levels());
        Assert.Equal(PlanReadiness.Missing, missing.Readiness);
        Assert.Equal(RequirementState.Missing, Assert.Single(missing.Requirements).State);
        Assert.Equal(PlanReadiness.Unknown, unknown.Readiness);
        Assert.Equal(RequirementState.Unknown, Assert.Single(unknown.Requirements).State);
    }

    [Fact]
    public void NeverFetchedIsUnscoredAndDoesNotPretendMissing()
    {
        var analysis = SkillPlanEvaluator.Evaluate(Plan(("Navigation", 1)), Ids, Levels(), Levels(), Queue(), hasSnapshot: false);
        Assert.Equal(PlanReadiness.Unscored, analysis.Readiness);
        Assert.Empty(analysis.Requirements);
    }

    [Fact]
    public void StatusPrecedencePreservesConservativeMixedOutcomes()
    {
        Assert.Equal(PlanReadiness.Unknown, SkillPlanEvaluator.CompactStatus([
            Item(RequirementState.Missing), Item(RequirementState.Unknown)]));
        Assert.Equal(PlanReadiness.Missing, SkillPlanEvaluator.CompactStatus([
            Item(RequirementState.Queued), Item(RequirementState.Missing)]));
        Assert.Equal(PlanReadiness.Locked, SkillPlanEvaluator.CompactStatus([
            Item(RequirementState.Queued), Item(RequirementState.TrainedInactive)]));
    }

    private static PlanAnalysis Evaluate(
        SkillPlan plan,
        IReadOnlyDictionary<int, int> active,
        IReadOnlyDictionary<int, int> trained,
        ILookup<int, QueueEntry>? queue = null)
        => SkillPlanEvaluator.Evaluate(plan, Ids, active, trained, queue ?? Queue(), hasSnapshot: true);

    private static RequirementAnalysis Item(RequirementState state) => new("x", 1, 0, 0, state, null, false);
    private static Dictionary<int, int> Levels(params (int Id, int Level)[] entries)
        => entries.ToDictionary(entry => entry.Id, entry => entry.Level);
}
