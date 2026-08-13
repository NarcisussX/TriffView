using System.Globalization;
using System.Text;

namespace TriffView.TriffSkills;

internal sealed record PlanRequirement(string SkillName, int Level);
internal sealed record SkillPlan(string Name, IReadOnlyList<PlanRequirement> Requirements);
internal sealed record PlanDiagnostic(int Line, string Message);
internal sealed record PlanParseResult(SkillPlan? Plan, IReadOnlyList<PlanDiagnostic> Diagnostics)
{
    public bool IsValid => Plan is not null && Diagnostics.Count == 0;
}

internal static class SkillPlanParser
{
    public const int MaxContentCharacters = 512 * 1024;
    public const int MaxLines = 5_000;
    public const int MaxLineCharacters = 512;
    public const int MaxRequirements = 2_000;

    private static readonly Dictionary<string, int> RomanLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["I"] = 1,
        ["II"] = 2,
        ["III"] = 3,
        ["IV"] = 4,
        ["V"] = 5,
    };

    public static PlanParseResult Parse(string name, string? contents)
    {
        contents ??= string.Empty;
        var diagnostics = new List<PlanDiagnostic>();
        if (contents.Length > MaxContentCharacters)
        {
            diagnostics.Add(new PlanDiagnostic(0, $"Plan exceeds the {MaxContentCharacters / 1024} KiB character limit."));
            return new PlanParseResult(null, diagnostics);
        }

        var rawLines = contents.Split('\n');
        if (rawLines.Length > MaxLines)
        {
            diagnostics.Add(new PlanDiagnostic(0, $"Plan exceeds the {MaxLines:N0}-line limit."));
            return new PlanParseResult(null, diagnostics);
        }

        var order = new List<string>();
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rawLines.Length; index++)
        {
            var lineNumber = index + 1;
            var raw = rawLines[index].TrimEnd('\r');
            if (raw.Length > MaxLineCharacters)
            {
                diagnostics.Add(new PlanDiagnostic(lineNumber, $"Line exceeds the {MaxLineCharacters}-character limit."));
                continue;
            }

            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var split = LastWhitespace(line);
            if (split < 0)
            {
                diagnostics.Add(new PlanDiagnostic(lineNumber, "Expected a skill name followed by level I-V or 1-5."));
                continue;
            }

            var skillName = line[..split].Trim();
            var token = line[(split + 1)..].Trim();
            if (skillName.Length == 0)
            {
                diagnostics.Add(new PlanDiagnostic(lineNumber, "Skill name cannot be empty."));
                continue;
            }
            if (skillName.Length > 200)
            {
                diagnostics.Add(new PlanDiagnostic(lineNumber, "Skill name is too long."));
                continue;
            }
            try
            {
                skillName = skillName.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                diagnostics.Add(new PlanDiagnostic(lineNumber, "Skill name contains invalid Unicode."));
                continue;
            }
            if (skillName.Any(char.IsControl))
            {
                diagnostics.Add(new PlanDiagnostic(lineNumber, "Skill name contains a control character."));
                continue;
            }
            if (!TryParseLevel(token, out var level))
            {
                diagnostics.Add(new PlanDiagnostic(lineNumber, "Skill level must be I-V or 1-5."));
                continue;
            }

            if (levels.TryGetValue(skillName, out var existing))
            {
                if (level > existing) levels[skillName] = level;
            }
            else
            {
                levels[skillName] = level;
                order.Add(skillName);
            }
        }

        if (levels.Count == 0 && diagnostics.Count == 0)
        {
            diagnostics.Add(new PlanDiagnostic(0, "Plan contains no skill requirements."));
        }
        if (levels.Count > MaxRequirements)
        {
            diagnostics.Add(new PlanDiagnostic(0, $"Plan exceeds the {MaxRequirements:N0}-requirement limit."));
        }
        if (diagnostics.Count > 0) return new PlanParseResult(null, diagnostics);

        return new PlanParseResult(
            new SkillPlan(name, order.Select(skillName => new PlanRequirement(skillName, levels[skillName])).ToArray()),
            Array.Empty<PlanDiagnostic>());
    }

    private static int LastWhitespace(string value)
    {
        for (var index = value.Length - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(value[index])) return index;
        }
        return -1;
    }

    private static bool TryParseLevel(string token, out int level)
    {
        if (RomanLevels.TryGetValue(token, out level)) return true;
        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out level) && level is >= 1 and <= 5;
    }
}
