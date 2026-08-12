namespace TriffView.TriffSkills;

// One ESI skill-queue row. ESI emits a separate row per level, and omits FinishDate
// entirely while the queue is paused.
internal sealed record QueueEntry(int SkillId, int FinishedLevel, DateTimeOffset? FinishDate);

internal enum PlanReadiness
{
    Ready,
    Training,
    Missing,
}

internal sealed record MissingRequirement(string SkillName, int Level);

internal sealed record PlanAnalysis(
    PlanReadiness Readiness,
    DateTimeOffset? EstimatedFinishUtc,
    IReadOnlyList<MissingRequirement> MissingSkills,
    IReadOnlyList<string> UnknownSkills);

internal static class SkillPlanEvaluator
{
    // Classifies a plan for one character. Missing always wins over Training: a plan
    // that cannot be completed from the current queue is never reported with an ETA.
    // The queue arrives pre-grouped by skill ID so scoring a whole matrix does not
    // rescan the full queue once per requirement.
    public static PlanAnalysis Evaluate(
        SkillPlan plan,
        IReadOnlyDictionary<string, int> skillIds,
        IReadOnlyDictionary<int, int> trainedLevels,
        ILookup<int, QueueEntry> queue)
    {
        var missing = new List<MissingRequirement>();
        var unknown = new List<string>();
        var anyTraining = false;
        var etaIsKnown = true;
        DateTimeOffset? eta = null;

        foreach (var requirement in plan.Requirements)
        {
            if (!skillIds.TryGetValue(requirement.SkillName, out var skillId))
            {
                // Unresolvable: cannot be evaluated, so it must never count as satisfied.
                unknown.Add(requirement.SkillName);
                continue;
            }

            if (trainedLevels.TryGetValue(skillId, out var trained) && trained >= requirement.Level)
            {
                continue;
            }

            var entry = SmallestSufficientEntry(queue[skillId], requirement.Level);
            if (entry is null)
            {
                missing.Add(new MissingRequirement(requirement.SkillName, requirement.Level));
                continue;
            }

            anyTraining = true;
            if (entry.FinishDate is null)
            {
                // Paused queue: still training, but the completion date is unknowable.
                etaIsKnown = false;
            }
            else if (eta is null || entry.FinishDate > eta)
            {
                eta = entry.FinishDate;
            }
        }

        if (missing.Count > 0 || unknown.Count > 0)
        {
            return new PlanAnalysis(PlanReadiness.Missing, null, missing, unknown);
        }

        if (anyTraining)
        {
            return new PlanAnalysis(
                PlanReadiness.Training,
                etaIsKnown ? eta?.ToUniversalTime() : null,
                Array.Empty<MissingRequirement>(),
                Array.Empty<string>());
        }

        return new PlanAnalysis(
            PlanReadiness.Ready,
            null,
            Array.Empty<MissingRequirement>(),
            Array.Empty<string>());
    }

    // ESI queues one entry per level, so the requirement is satisfied by the earliest
    // level that reaches it - level III with III/IV/V queued resolves to the III entry.
    private static QueueEntry? SmallestSufficientEntry(IEnumerable<QueueEntry> entries, int requiredLevel)
    {
        QueueEntry? best = null;
        foreach (var entry in entries)
        {
            if (entry.FinishedLevel < requiredLevel)
            {
                continue;
            }

            if (best is null || entry.FinishedLevel < best.FinishedLevel)
            {
                best = entry;
            }
        }

        return best;
    }
}
