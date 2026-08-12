using System.IO;
using System.Text;

namespace TriffView.TriffSkills;

// What a read of the plans directory produced: the usable plans, the .txt files that
// contained no valid skill line (surfaced so a typo'd file does not silently vanish),
// and the newest file write time for the "Plans updated" stamp.
internal sealed record PlanLoadResult(
    IReadOnlyList<SkillPlan> Plans,
    IReadOnlyList<string> SkippedFiles,
    DateTimeOffset? LatestWriteUtc);

// Owns the on-disk plans directory: seeds it on first run and reads whatever is there.
// Plan files are dropped into %APPDATA%\TriffHud\TriffSkills\plans by hand or written
// by the clipboard import. The directory is the authoritative copy of the user's plans.
internal static class PlanStore
{
    public const string StarterPlanName = "Core Ship Skills";

    // Written on first run so the tool opens with a populated matrix instead of an
    // empty grid. Original content: a plain list of core support skills nearly every
    // character trains anyway, written for this purpose.
    private const string StarterPlanContents = """
        # Core support skills - the ones nearly every ship benefits from.
        # Format: a skill name, then a level as 1-5 or I-V. Delete this file if you like.
        CPU Management IV
        Power Grid Management IV
        Capacitor Management III
        Capacitor Systems Operation III
        Mechanics IV
        Hull Upgrades III
        Shield Operation III
        Shield Management III
        Navigation IV
        Afterburner III
        Evasive Maneuvering III
        Warp Drive Operation II
        Long Range Targeting III
        Target Management III
        Weapon Upgrades III
        Drones III
        """;

    // Seeds the starter plan, keyed on the plans directory not existing at all - the
    // only reliable first-run signal. A user who deletes the starter plan, or every
    // plan, must not have one reappear. Best-effort: a failure here costs a
    // convenience file and must not take the tool down.
    public static void EnsureSeeded(string plansDir)
    {
        try
        {
            if (Directory.Exists(plansDir)) return;

            Directory.CreateDirectory(plansDir);
            File.WriteAllText(
                Path.Combine(plansDir, StarterPlanName + ".txt"),
                // CRLF so Notepad on older Windows renders the line breaks.
                StarterPlanContents.ReplaceLineEndings("\r\n"),
                new UTF8Encoding(false)
            );
        }
        catch (Exception)
        {
            // Permission-denied %APPDATA%, read-only profile: the tool works with no
            // plans at all, so there is nothing to report and nothing to retry.
        }
    }

    // Parses every .txt into a plan named after its file stem. A file that cannot be
    // read or parsed is skipped rather than failing the whole load, and a file with no
    // valid skill line is reported in SkippedFiles rather than scored: a plan with
    // zero requirements would evaluate as Ready for every character.
    public static PlanLoadResult LoadAll(string plansDir)
    {
        if (!Directory.Exists(plansDir))
        {
            return new PlanLoadResult(Array.Empty<SkillPlan>(), Array.Empty<string>(), null);
        }

        var plans = new List<SkillPlan>();
        var skipped = new List<string>();
        DateTimeOffset? latest = null;
        foreach (var path in Directory.GetFiles(plansDir, "*.txt").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var plan = SkillPlanParser.Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));
                if (plan.Requirements.Count == 0)
                {
                    skipped.Add(Path.GetFileName(path));
                    continue;
                }

                plans.Add(plan);
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (latest is null || written > latest) latest = written;
            }
            catch (Exception)
            {
                // Unreadable (locked, permission-denied) or unparseable: skip this one
                // file, keep the rest.
                skipped.Add(Path.GetFileName(path));
            }
        }

        return new PlanLoadResult(plans, skipped, latest);
    }
}
