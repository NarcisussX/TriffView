using System.Globalization;
using System.IO;
using System.Text;

namespace TriffView.TriffSkills;

internal static class PlanNameValidator
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public const int MaxNameLength = 120;

    public static bool TryValidate(string? name, out string normalizedName, out string error)
    {
        try
        {
            normalizedName = (name ?? string.Empty).Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            normalizedName = string.Empty;
            error = "Plan name contains invalid Unicode.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            error = "Plan name cannot be empty.";
            return false;
        }
        if (normalizedName.Length > MaxNameLength)
        {
            error = $"Plan name is too long (max {MaxNameLength} characters).";
            return false;
        }
        if (normalizedName != normalizedName.Trim())
        {
            error = "Plan name cannot start or end with whitespace.";
            return false;
        }
        if (normalizedName.EndsWith('.') || normalizedName.EndsWith(' '))
        {
            error = "Plan name cannot end with a period or space.";
            return false;
        }
        if (normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || normalizedName.Contains("..", StringComparison.Ordinal))
        {
            error = "Plan name contains characters that are not allowed in a file name.";
            return false;
        }
        if (normalizedName.Any(character => CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Control))
        {
            error = "Plan name contains a control character.";
            return false;
        }

        var stem = normalizedName.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem))
        {
            error = $"\"{stem}\" is a reserved Windows device name.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidate(string? name, out string error)
        => TryValidate(name, out _, out error);

    public static bool IsWithin(string fullPath, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(fullPath);
        return candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
