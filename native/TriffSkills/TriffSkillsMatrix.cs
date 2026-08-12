namespace TriffView.TriffSkills;

internal sealed record PlanSummary(string Name, int RequirementCount);

internal sealed record MatrixCell(
    long CharacterId,
    string PlanName,
    PlanReadiness Readiness,
    DateTimeOffset? EstimatedFinishUtc,
    IReadOnlyList<MissingRequirement> MissingSkills,
    IReadOnlyList<string> UnknownSkills);

internal sealed record SkillMatrix(IReadOnlyList<PlanSummary> Plans, IReadOnlyList<MatrixCell> Cells);

// The wire shape PostState splices into its posted state object under the "plans" and
// "matrix" keys. WPF-free so the projection is assertable outside the app assembly.
internal sealed record MatrixWire(IReadOnlyList<object> Plans, IReadOnlyList<object> Matrix);

internal static class TriffSkillsMatrix
{
    // Scores every character against every plan; no path omits a pair. A character
    // whose last fetch failed is scored off its last-good skills and queue, and a plan
    // naming an unresolvable skill is scored Missing rather than dropped - the UI
    // renders failure as cell or row state, so an omitted pair would read as a hole.
    public static SkillMatrix Build(
        IReadOnlyList<TriffSkillsCharacter> characters,
        IReadOnlyList<SkillPlan> plans,
        IReadOnlyDictionary<string, int> skillIds)
    {
        var summaries = new List<PlanSummary>(plans.Count);
        foreach (var plan in plans)
        {
            summaries.Add(new PlanSummary(plan.Name, plan.Requirements.Count));
        }

        var cells = new List<MatrixCell>(characters.Count * plans.Count);
        foreach (var character in characters)
        {
            var trained = character.TrainedLevels ?? new Dictionary<int, int>();
            // Grouped once per character so the evaluator does not rescan the whole
            // queue for every requirement of every plan.
            var queue = (character.Queue ?? new List<QueueEntry>()).ToLookup(entry => entry.SkillId);

            foreach (var plan in plans)
            {
                var analysis = SkillPlanEvaluator.Evaluate(plan, skillIds, trained, queue);
                cells.Add(new MatrixCell(
                    character.CharacterId,
                    plan.Name,
                    analysis.Readiness,
                    analysis.EstimatedFinishUtc,
                    analysis.MissingSkills,
                    analysis.UnknownSkills));
            }
        }

        return new SkillMatrix(summaries, cells);
    }

    public static MatrixWire ToWire(SkillMatrix matrix)
    {
        var plans = matrix.Plans.Select(plan => (object)new
        {
            plan.Name,
            plan.RequirementCount,
        }).ToArray();

        var cells = matrix.Cells.Select(cell => (object)new
        {
            cell.CharacterId,
            cell.PlanName,
            // Explicit string, not the enum: System.Text.Json serializes an enum as its
            // integer value by default, and the UI keys its rendering off
            // "Ready" / "Training" / "Missing".
            readiness = cell.Readiness.ToString(),
            cell.EstimatedFinishUtc,
            missingSkills = cell.MissingSkills.Select(skill => new
            {
                skill.SkillName,
                skill.Level,
            }).ToArray(),
            cell.UnknownSkills,
        }).ToArray();

        return new MatrixWire(plans, cells);
    }
}
