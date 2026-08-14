using System.IO;
using System.Security;
using System.Text;
using TriffView.Eve;

namespace TriffView.TriffSkills;

internal sealed record PlanFileIssue(string FileName, string Message, IReadOnlyList<PlanDiagnostic> Diagnostics);
internal sealed record PlanLoadResult(
    IReadOnlyList<SkillPlan> Plans,
    IReadOnlyList<PlanFileIssue> Issues,
    DateTimeOffset? LatestWriteUtc);
internal sealed record PlanCommitResult(bool Success, bool Collision, string Name, SkillPlan? Plan, string Error);

internal static class PlanStore
{
    public const string StarterPlanName = "Core Ship Skills";
    public const int MaxPlanFiles = 200;
    public const long MaxPlanFileBytes = 512 * 1024;

    private const string StarterPlanContents = """
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

    public static string EnsureSeeded(string plansDir)
    {
        try
        {
            if (Directory.Exists(plansDir)) return string.Empty;
            Directory.CreateDirectory(plansDir);
            AtomicFile.WriteText(
                Path.Combine(plansDir, StarterPlanName + ".txt"),
                StarterPlanContents.ReplaceLineEndings("\r\n"));
            return string.Empty;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return $"Could not create the starter plan: {exception.Message}";
        }
    }

    public static PlanLoadResult LoadAll(string plansDir)
    {
        if (!Directory.Exists(plansDir)) return new PlanLoadResult([], [], null);

        var paths = Directory.EnumerateFiles(plansDir, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxPlanFiles + 1)
            .ToArray();
        var plans = new List<SkillPlan>();
        var issues = new List<PlanFileIssue>();
        DateTimeOffset? latest = null;

        if (paths.Length > MaxPlanFiles)
        {
            issues.Add(new PlanFileIssue("plans", $"Only the first {MaxPlanFiles:N0} plan files were loaded.", []));
            paths = paths.Take(MaxPlanFiles).ToArray();
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            try
            {
                if (!PlanNameValidator.TryValidate(Path.GetFileNameWithoutExtension(path), out var planName, out var nameError))
                {
                    issues.Add(new PlanFileIssue(fileName, nameError, []));
                    continue;
                }
                if (!seenNames.Add(planName))
                {
                    issues.Add(new PlanFileIssue(fileName, "Plan name collides case-insensitively with another file.", []));
                    continue;
                }

                var info = new FileInfo(path);
                if (info.Length > MaxPlanFileBytes)
                {
                    issues.Add(new PlanFileIssue(fileName, $"Plan exceeds the {MaxPlanFileBytes / 1024} KiB file limit.", []));
                    continue;
                }

                var parsed = SkillPlanParser.Parse(planName, AtomicFile.ReadBoundedText(path, MaxPlanFileBytes));
                if (!parsed.IsValid)
                {
                    issues.Add(new PlanFileIssue(fileName, "Plan has invalid lines and was not loaded.", parsed.Diagnostics));
                    continue;
                }

                plans.Add(parsed.Plan!);
                var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (latest is null || written > latest) latest = written;
            }
            catch (Exception exception) when (IsFileFailure(exception) || exception is InvalidDataException)
            {
                issues.Add(new PlanFileIssue(fileName, $"Could not read plan: {exception.Message}", []));
            }
        }
        return new PlanLoadResult(plans, issues, latest);
    }

    public static PlanCommitResult CommitValidated(
        string plansDir,
        string requestedName,
        string contents,
        SkillPlan validatedPlan,
        bool replace)
    {
        if (!PlanNameValidator.TryValidate(requestedName, out var name, out var error))
        {
            return new PlanCommitResult(false, false, requestedName, null, error);
        }

        var parsedContents = SkillPlanParser.Parse(name, contents);
        if (!parsedContents.IsValid || parsedContents.Plan is null || !SameRequirements(validatedPlan, parsedContents.Plan))
        {
            return new PlanCommitResult(false, false, name, null, "Plan contents no longer match the validated preview.");
        }

        string? path = null;
        var existed = false;
        var writeAttempted = false;
        try
        {
            Directory.CreateDirectory(plansDir);
            var existing = FindExistingPath(plansDir, name);
            if (existing is not null && !replace) return new PlanCommitResult(false, true, name, null, string.Empty);
            if (existing is null && Directory.EnumerateFiles(plansDir, "*.txt", SearchOption.TopDirectoryOnly).Take(MaxPlanFiles).Count() >= MaxPlanFiles)
            {
                return new PlanCommitResult(false, false, name, null, $"Plan folder already contains the {MaxPlanFiles:N0}-file maximum.");
            }

            path = existing ?? Path.GetFullPath(Path.Combine(plansDir, name + ".txt"));
            if (!PlanNameValidator.IsWithin(path, plansDir))
            {
                return new PlanCommitResult(false, false, name, null, "Plan path escaped the plans folder.");
            }

            existed = File.Exists(path);
            writeAttempted = true;
            AtomicFile.WriteText(path, contents.ReplaceLineEndings("\r\n"));
            var info = new FileInfo(path);
            if (info.Length > MaxPlanFileBytes) throw new InvalidDataException("Saved plan exceeded the file-size limit.");
            var reloaded = SkillPlanParser.Parse(name, AtomicFile.ReadBoundedText(path, MaxPlanFileBytes));
            if (!reloaded.IsValid || reloaded.Plan is null || !SameRequirements(validatedPlan, reloaded.Plan))
            {
                throw new InvalidDataException("Saved plan did not reload as the validated preview.");
            }
            return new PlanCommitResult(true, false, name, reloaded.Plan, string.Empty);
        }
        catch (Exception exception) when (IsFileFailure(exception) || exception is InvalidDataException)
        {
            if (!writeAttempted || path is null)
            {
                return new PlanCommitResult(false, false, name, null, $"Plan was not saved ({DescribeFailure(exception)}).");
            }

            try
            {
                if (existed) AtomicFile.RestoreBackup(path, MaxPlanFileBytes);
                else
                {
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
                }
            }
            catch (Exception rollback) when (IsFileFailure(rollback))
            {
                return new PlanCommitResult(
                    false,
                    false,
                    name,
                    null,
                    $"Plan was not saved ({DescribeFailure(exception)}); rollback also failed ({DescribeFailure(rollback)}).");
            }
            return new PlanCommitResult(false, false, name, null, $"Plan was not saved ({DescribeFailure(exception)}); the previous file was restored.");
        }
    }

    private static string? FindExistingPath(string plansDir, string name)
        => Directory.EnumerateFiles(plansDir, "*.txt", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => PlanNameValidator.TryValidate(Path.GetFileNameWithoutExtension(path), out var existingName, out _)
                && string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase));

    private static bool SameRequirements(SkillPlan left, SkillPlan right)
        => left.Requirements.SequenceEqual(right.Requirements);

    private static bool IsFileFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;

    private static string DescribeFailure(Exception exception) => exception switch
    {
        InvalidDataException => exception.Message,
        UnauthorizedAccessException or SecurityException => "access denied",
        ArgumentException or NotSupportedException => "invalid path",
        IOException => "I/O error",
        _ => "unexpected file error",
    };
}
