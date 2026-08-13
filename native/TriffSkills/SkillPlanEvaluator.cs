namespace TriffView.TriffSkills;

internal sealed record QueueEntry(
    int SkillId,
    int FinishedLevel,
    DateTimeOffset? StartDate,
    DateTimeOffset? FinishDate,
    int QueuePosition = 0);

internal enum RequirementState
{
    Active,
    TrainedInactive,
    Queued,
    Missing,
    Unknown,
}

internal enum PlanReadiness
{
    Ready,
    Training,
    Locked,
    Missing,
    Unknown,
    Unscored,
}

internal sealed record RequirementAnalysis(
    string SkillName,
    int RequiredLevel,
    int? ActiveLevel,
    int? TrainedLevel,
    RequirementState State,
    DateTimeOffset? QueuedFinishUtc,
    bool QueueTimingUnknown);

internal sealed record PlanAnalysis(
    PlanReadiness Readiness,
    DateTimeOffset? EstimatedFinishUtc,
    bool QueueTimingUnknown,
    IReadOnlyList<RequirementAnalysis> Requirements)
{
    public int ActiveCount => Requirements.Count(item => item.State == RequirementState.Active);
    public int TrainedInactiveCount => Requirements.Count(item => item.State == RequirementState.TrainedInactive);
    public int QueuedCount => Requirements.Count(item => item.State == RequirementState.Queued);
    public int MissingCount => Requirements.Count(item => item.State == RequirementState.Missing);
    public int UnknownCount => Requirements.Count(item => item.State == RequirementState.Unknown);
}

internal static class SkillPlanEvaluator
{
    public static PlanAnalysis Evaluate(
        SkillPlan plan,
        IReadOnlyDictionary<string, int> skillIds,
        IReadOnlyDictionary<int, int> activeLevels,
        IReadOnlyDictionary<int, int> trainedLevels,
        ILookup<int, QueueEntry> queue,
        bool hasSnapshot)
    {
        if (!hasSnapshot)
        {
            return new PlanAnalysis(PlanReadiness.Unscored, null, false, []);
        }

        var requirements = new List<RequirementAnalysis>(plan.Requirements.Count);
        var timingUnknown = false;
        DateTimeOffset? eta = null;
        foreach (var requirement in plan.Requirements)
        {
            if (!skillIds.TryGetValue(requirement.SkillName, out var skillId))
            {
                requirements.Add(new RequirementAnalysis(requirement.SkillName, requirement.Level, null, null, RequirementState.Unknown, null, false));
                continue;
            }

            activeLevels.TryGetValue(skillId, out var active);
            trainedLevels.TryGetValue(skillId, out var trained);
            if (active >= requirement.Level)
            {
                requirements.Add(new RequirementAnalysis(requirement.SkillName, requirement.Level, active, trained, RequirementState.Active, null, false));
                continue;
            }
            if (trained >= requirement.Level)
            {
                requirements.Add(new RequirementAnalysis(requirement.SkillName, requirement.Level, active, trained, RequirementState.TrainedInactive, null, false));
                continue;
            }

            var queued = EarliestSufficientEntry(queue[skillId], requirement.Level);
            if (queued is not null)
            {
                var unknown = queued.FinishDate is null;
                timingUnknown |= unknown;
                if (!unknown && (eta is null || queued.FinishDate > eta)) eta = queued.FinishDate;
                requirements.Add(new RequirementAnalysis(requirement.SkillName, requirement.Level, active, trained, RequirementState.Queued, queued.FinishDate, unknown));
                continue;
            }

            requirements.Add(new RequirementAnalysis(requirement.SkillName, requirement.Level, active, trained, RequirementState.Missing, null, false));
        }

        var readiness = CompactStatus(requirements);
        return new PlanAnalysis(
            readiness,
            readiness == PlanReadiness.Training && !timingUnknown ? eta?.ToUniversalTime() : null,
            readiness == PlanReadiness.Training && timingUnknown,
            requirements);
    }

    internal static PlanReadiness CompactStatus(IReadOnlyList<RequirementAnalysis> requirements)
    {
        if (requirements.Count == 0) return PlanReadiness.Unknown;
        if (requirements.Any(item => item.State == RequirementState.Unknown)) return PlanReadiness.Unknown;
        if (requirements.Any(item => item.State == RequirementState.Missing)) return PlanReadiness.Missing;
        if (requirements.Any(item => item.State == RequirementState.TrainedInactive)) return PlanReadiness.Locked;
        if (requirements.Any(item => item.State == RequirementState.Queued)) return PlanReadiness.Training;
        return PlanReadiness.Ready;
    }

    private static QueueEntry? EarliestSufficientEntry(IEnumerable<QueueEntry> entries, int requiredLevel)
        => entries
            .Where(entry => entry.FinishedLevel >= requiredLevel)
            .OrderBy(entry => entry.FinishedLevel)
            .ThenBy(entry => entry.QueuePosition)
            .FirstOrDefault();
}
