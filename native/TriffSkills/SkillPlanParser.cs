using System.Globalization;

namespace TriffView.TriffSkills;

// A single "train skill X to level N" line from a plan file.
internal sealed record PlanRequirement(string SkillName, int Level);

// A parsed plan. Requirements keeps first-appearance order so output is deterministic.
internal sealed record SkillPlan(string Name, IReadOnlyList<PlanRequirement> Requirements);

internal static class SkillPlanParser
{
    private static readonly Dictionary<string, int> RomanLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["I"] = 1,
        ["II"] = 2,
        ["III"] = 3,
        ["IV"] = 4,
        ["V"] = 5,
    };

    // Parses "Skill Name <level>" lines, where the level is 1-5 or I-V. Blank lines and
    // lines starting with '#' are comments. Other malformed lines are skipped rather than
    // throwing - plan files are hand-written, and one bad line must not cost the plan.
    // A skill listed more than once keeps its highest level; names merge
    // case-insensitively because ESI resolves them case-insensitively.
    public static SkillPlan Parse(string name, string contents)
    {
        var order = new List<string>();
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in (contents ?? string.Empty).Split('\n'))
        {
            // Trim also strips the '\r' of a CRLF file.
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var lastSpace = line.LastIndexOf(' ');
            if (lastSpace < 0)
            {
                continue;
            }

            // TrimEnd because the split is on the *last* space, so column-aligned lines
            // ("Survey  IV") leave extra spaces on the name, and an untrimmed name never
            // resolves to a typeID.
            var skillName = line[..lastSpace].TrimEnd();
            if (!TryParseLevel(line[(lastSpace + 1)..], out var level))
            {
                continue;
            }

            if (levels.TryGetValue(skillName, out var existing))
            {
                if (level > existing)
                {
                    levels[skillName] = level;
                }
            }
            else
            {
                levels[skillName] = level;
                order.Add(skillName);
            }
        }

        var requirements = new List<PlanRequirement>(order.Count);
        foreach (var skillName in order)
        {
            requirements.Add(new PlanRequirement(skillName, levels[skillName]));
        }

        return new SkillPlan(name, requirements);
    }

    private static bool TryParseLevel(string token, out int level)
    {
        if (RomanLevels.TryGetValue(token, out level))
        {
            return true;
        }

        // EVE skills only go to V, so a numeric level outside 1-5 is a malformed line,
        // not a requirement the evaluator could ever satisfy (or trivially satisfies).
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out level)
            && level >= 1 && level <= 5)
        {
            return true;
        }

        level = 0;
        return false;
    }
}
