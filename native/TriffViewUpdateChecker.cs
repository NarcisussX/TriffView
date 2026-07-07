using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TriffView;

internal sealed class TriffViewUpdateChecker
{
    public const string ReleasesPageUrl = "https://github.com/NarcisussX/TriffView/releases";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/NarcisussX/TriffView/releases/latest";
    private const string UserAgent = "TriffView/1.0 (+https://triff.tools)";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _statePath;
    private TriffViewUpdateLocalState _state;

    public TriffViewUpdateChecker()
    {
        _statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TriffView",
            "update-state.json"
        );
        _state = LoadState();
    }

    public string CurrentVersion { get; } = ReadCurrentVersion();

    public async Task<TriffViewUpdateSnapshot> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return TriffViewUpdateSnapshot.Failed(CurrentVersion, $"GitHub returned {(int)response.StatusCode}.");
        }

        var release = JsonSerializer.Deserialize<GitHubReleaseResponse>(content, JsonOptions);
        if (release == null || string.IsNullOrWhiteSpace(release.TagName))
        {
            return TriffViewUpdateSnapshot.Failed(CurrentVersion, "GitHub latest release response was missing a release tag.");
        }

        var latestVersion = NormalizeVersion(release.TagName);
        var currentVersion = NormalizeVersion(CurrentVersion);
        var isNewer = CompareVersionStrings(latestVersion, currentVersion) > 0;
        var ignored = string.Equals(_state.IgnoredVersion, latestVersion, StringComparison.OrdinalIgnoreCase);
        var status = isNewer ? "available" : "current";

        return new TriffViewUpdateSnapshot(
            status,
            CurrentVersion,
            latestVersion,
            release.TagName.Trim(),
            string.IsNullOrWhiteSpace(release.Name) ? release.TagName.Trim() : release.Name.Trim(),
            string.IsNullOrWhiteSpace(release.HtmlUrl) ? ReleasesPageUrl : release.HtmlUrl.Trim(),
            release.PublishedAt,
            isNewer,
            ignored,
            ""
        );
    }

    public void IgnoreVersion(string? version)
    {
        var normalized = NormalizeVersion(version ?? "");
        if (string.IsNullOrWhiteSpace(normalized)) return;
        _state.IgnoredVersion = normalized;
        SaveState();
    }

    private TriffViewUpdateLocalState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return new TriffViewUpdateLocalState();
            return JsonSerializer.Deserialize<TriffViewUpdateLocalState>(File.ReadAllText(_statePath), JsonOptions)
                   ?? new TriffViewUpdateLocalState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load update state: {ex}");
            return new TriffViewUpdateLocalState();
        }
    }

    private void SaveState()
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state, JsonOptions));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save update state: {ex}");
        }
    }

    private static string ReadCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Trim();
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static int CompareVersionStrings(string left, string right)
    {
        var leftParts = VersionParts(left);
        var rightParts = VersionParts(right);
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < count; index++)
        {
            var leftValue = index < leftParts.Length ? leftParts[index] : 0;
            var rightValue = index < rightParts.Length ? rightParts[index] : 0;
            var comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0) return comparison;
        }

        return 0;
    }

    private static int[] VersionParts(string version)
    {
        return NormalizeVersion(version)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out var value) ? value : 0;
            })
            .ToArray();
    }

    private static string NormalizeVersion(string version)
    {
        var normalized = version.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)) normalized = normalized[1..];
        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0) normalized = normalized[..suffixIndex];
        return normalized.Trim();
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }
    }
}

internal sealed record TriffViewUpdateSnapshot(
    string Status,
    string CurrentVersion,
    string LatestVersion,
    string LatestTag,
    string Title,
    string ReleaseUrl,
    DateTimeOffset? PublishedAt,
    bool UpdateAvailable,
    bool Ignored,
    string Error
)
{
    public static TriffViewUpdateSnapshot Idle(string currentVersion) =>
        new("idle", currentVersion, "", "", "", TriffViewUpdateChecker.ReleasesPageUrl, null, false, false, "");

    public static TriffViewUpdateSnapshot Checking(string currentVersion) =>
        new("checking", currentVersion, "", "", "", TriffViewUpdateChecker.ReleasesPageUrl, null, false, false, "");

    public static TriffViewUpdateSnapshot Failed(string currentVersion, string error) =>
        new("failed", currentVersion, "", "", "", TriffViewUpdateChecker.ReleasesPageUrl, null, false, false, error);
}

internal sealed class TriffViewUpdateLocalState
{
    public string IgnoredVersion { get; set; } = "";
}
