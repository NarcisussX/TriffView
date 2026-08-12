using System.IO;

namespace TriffView.TriffSkills;

// Authoritative rules for a plan name arriving in a triffskills:import-plan web
// message. The renderer runs an advisory copy of these rules while the user types,
// but a web message can carry any string, so this side rejects rather than
// sanitizes: an unacceptable name is refused, not silently rewritten.
internal static class PlanNameValidator
{
    // Windows reserved device names: reserved as the whole stem or with any extension
    // attached (CON.txt is still CON), case-insensitive.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    public const int MaxNameLength = 120;

    public static bool TryValidate(string? name, out string error)
    {
        name ??= "";

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Plan name cannot be empty.";
            return false;
        }
        if (name.Length > MaxNameLength)
        {
            error = $"Plan name is too long (max {MaxNameLength} characters).";
            return false;
        }
        if (name != name.Trim())
        {
            error = "Plan name cannot start or end with whitespace.";
            return false;
        }
        // Windows silently strips a trailing dot, so "foo." and "foo" would collide.
        if (name.EndsWith('.'))
        {
            error = "Plan name cannot end with a period.";
            return false;
        }
        // GetInvalidFileNameChars covers '/', '\\' and ':'; ".." is made of
        // otherwise-valid characters, so it needs its own check.
        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Contains(".."))
        {
            error = "Plan name contains characters that are not allowed in a file name.";
            return false;
        }
        // The segment before the *first* dot is what Windows reserves -
        // Path.GetFileNameWithoutExtension would miss "CON.txt.bak".
        var stem = name.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem))
        {
            error = $"\"{stem}\" is a reserved Windows device name.";
            return false;
        }

        error = "";
        return true;
    }

    // Defense in depth: whatever TryValidate misses, this is what actually stops a
    // write from landing outside root.
    public static bool IsWithin(string fullPath, string root)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(fullPath);
        return candidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
