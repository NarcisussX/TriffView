using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class SkillPlanEvaluatorTests
{
    private static readonly DateTimeOffset Soon = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static SkillPlan Plan(params (string Name, int Level)[] requirements)
        => new("p", requirements.Select(r => new PlanRequirement(r.Name, r.Level)).ToList());

    private static ILookup<int, QueueEntry> Queue(params QueueEntry[] entries)
        => entries.ToLookup(entry => entry.SkillId);

    private static readonly Dictionary<string, int> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Navigation"] = 100,
        ["Gunnery"] = 200,
    };

    [Fact]
    public void ReadyWhenEveryRequirementIsTrained()
    {
        var analysis = SkillPlanEvaluator.Evaluate(
            Plan(("Navigation", 4), ("Gunnery", 3)),
            Ids,
            new Dictionary<int, int> { [100] = 5, [200] = 3 },
            Queue());

        Assert.Equal(PlanReadiness.Ready, analysis.Readiness);
        Assert.Null(analysis.EstimatedFinishUtc);
    }

    [Fact]
    public void TrainingWhenRemainderIsQueued_EtaIsTheLatestRelevantEntry()
    {
        var analysis = SkillPlanEvaluator.Evaluate(
            Plan(("Navigation", 4), ("Gunnery", 3)),
            Ids,
            new Dictionary<int, int> { [100] = 3, [200] = 2 },
            Queue(
                new QueueEntry(100, 4, Later),
                new QueueEntry(200, 3, Soon)));

        Assert.Equal(PlanReadiness.Training, analysis.Readiness);
        Assert.Equal(Later, analysis.EstimatedFinishUtc);
    }

    [Fact]
    public void MissingWinsOverTraining()
    {
        var analysis = SkillPlanEvaluator.Evaluate(
            Plan(("Navigation", 4), ("Gunnery", 3)),
            Ids,
            new Dictionary<int, int> { [100] = 3 },
            Queue(new QueueEntry(100, 4, Soon))); // Gunnery neither trained nor queued

        Assert.Equal(PlanReadiness.Missing, analysis.Readiness);
        Assert.Null(analysis.EstimatedFinishUtc);
        Assert.Equal("Gunnery", Assert.Single(analysis.MissingSkills).SkillName);
    }

    [Fact]
    public void UnresolvedSkillNameMakesThePlanMissing_NeverSatisfied()
    {
        var analysis = SkillPlanEvaluator.Evaluate(
            Plan(("Mystery Skill", 1)),
            Ids,
            new Dictionary<int, int> { [100] = 5 },
            Queue());

        Assert.Equal(PlanReadiness.Missing, analysis.Readiness);
        Assert.Equal("Mystery Skill", Assert.Single(analysis.UnknownSkills));
    }

    [Fact]
    public void PausedQueueIsTrainingWithNoEta()
    {
        var analysis = SkillPlanEvaluator.Evaluate(
            Plan(("Navigation", 4)),
            Ids,
            new Dictionary<int, int> { [100] = 3 },
            Queue(new QueueEntry(100, 4, FinishDate: null)));

        Assert.Equal(PlanReadiness.Training, analysis.Readiness);
        Assert.Null(analysis.EstimatedFinishUtc);
    }

    [Fact]
    public void RequirementIsSatisfiedByTheSmallestSufficientQueueEntry()
    {
        // Level 3 required with III/IV/V queued: the ETA is the III entry's, not V's.
        var analysis = SkillPlanEvaluator.Evaluate(
            Plan(("Navigation", 3)),
            Ids,
            new Dictionary<int, int>(),
            Queue(
                new QueueEntry(100, 5, Later),
                new QueueEntry(100, 3, Soon),
                new QueueEntry(100, 4, Later)));

        Assert.Equal(PlanReadiness.Training, analysis.Readiness);
        Assert.Equal(Soon, analysis.EstimatedFinishUtc);
    }

    [Fact]
    public void EmptyPlanEvaluatesReady_WhichIsWhyPlanStoreMustSkipEmptyPlans()
    {
        // Documents the vacuous-truth behavior PlanStore.LoadAll guards against:
        // a plan with zero requirements satisfies every character trivially.
        var analysis = SkillPlanEvaluator.Evaluate(Plan(), Ids, new Dictionary<int, int>(), Queue());
        Assert.Equal(PlanReadiness.Ready, analysis.Readiness);
    }
}
