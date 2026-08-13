namespace TriffView.TriffSkills;

internal sealed record PlanSummary(string Name, int RequirementCount);
internal sealed record CompactMatrixCell(
    long CharacterId,
    string PlanName,
    PlanReadiness Readiness,
    DateTimeOffset? EstimatedFinishUtc,
    bool QueueTimingUnknown,
    int ActiveCount,
    int TrainedInactiveCount,
    int QueuedCount,
    int MissingCount,
    int UnknownCount);

internal sealed record SkillMatrix(IReadOnlyList<PlanSummary> Plans, IReadOnlyList<CompactMatrixCell> Cells);

internal static class TriffSkillsMatrix
{
    public static SkillMatrix BuildCompact(
        IReadOnlyList<TriffSkillsCharacter> characters,
        IReadOnlyList<SkillPlan> plans,
        IReadOnlyDictionary<string, int> skillIds)
    {
        var summaries = plans.Select(plan => new PlanSummary(plan.Name, plan.Requirements.Count)).ToArray();
        var cells = new List<CompactMatrixCell>(characters.Count * plans.Count);
        foreach (var character in characters)
        {
            var queue = character.Queue.ToLookup(entry => entry.SkillId);
            foreach (var plan in plans)
            {
                var analysis = SkillPlanEvaluator.Evaluate(
                    plan,
                    skillIds,
                    character.ActiveLevels,
                    character.TrainedLevels,
                    queue,
                    character.FetchedUtc is not null);
                cells.Add(ToCompact(character.CharacterId, plan.Name, analysis));
            }
        }
        return new SkillMatrix(summaries, cells);
    }

    public static PlanAnalysis? BuildDetail(
        TriffSkillsCharacter? character,
        SkillPlan? plan,
        IReadOnlyDictionary<string, int> skillIds)
        => character is null || plan is null
            ? null
            : SkillPlanEvaluator.Evaluate(
                plan,
                skillIds,
                character.ActiveLevels,
                character.TrainedLevels,
                character.Queue.ToLookup(entry => entry.SkillId),
                character.FetchedUtc is not null);

    private static CompactMatrixCell ToCompact(long characterId, string planName, PlanAnalysis analysis)
        => new(
            characterId,
            planName,
            analysis.Readiness,
            analysis.EstimatedFinishUtc,
            analysis.QueueTimingUnknown,
            analysis.ActiveCount,
            analysis.TrainedInactiveCount,
            analysis.QueuedCount,
            analysis.MissingCount,
            analysis.UnknownCount);
}
