using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TriffView.TriffSkills;

internal static class TriffSkillsPaths
{
    private static string? _rootOverride;

    // Test seam: TriffView.Tests redirects the root to a temp directory. Production
    // code never calls this.
    public static void OverrideRoot(string root) => _rootOverride = root;

    // Paired with OverrideRoot: the override is process-global, so a test class that
    // does not clear it leaves every later reader pointed at a deleted temp directory.
    public static void ClearOverride() => _rootOverride = null;

    public static string Root => _rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TriffHud",
        "TriffSkills"
    );

    public static string StatePath => Path.Combine(Root, "state.json");
    public static string SkillIdsPath => Path.Combine(Root, "skill-ids.json");
    public static string PlansDir => Path.Combine(Root, "plans");
}

internal static class TriffSkillsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

internal sealed class TriffSkillsCharacter
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public List<string> Scopes { get; set; } = new();
    public DateTimeOffset AuthenticatedUtc { get; set; }
    public DateTimeOffset? FetchedUtc { get; set; }
    public Dictionary<int, int> TrainedLevels { get; set; } = new();   // typeID -> level
    public List<QueueEntry> Queue { get; set; } = new();
    public string Error { get; set; } = "";
    public bool NeedsReauth { get; set; }
}

internal sealed class TriffSkillsState
{
    public List<TriffSkillsCharacter> Characters { get; set; } = new();
    public long SelectedCharacterId { get; set; }

    public static TriffSkillsState Load()
    {
        try
        {
            if (!File.Exists(TriffSkillsPaths.StatePath)) return new TriffSkillsState();
            var state = JsonSerializer.Deserialize<TriffSkillsState>(
                File.ReadAllText(TriffSkillsPaths.StatePath),
                TriffSkillsJson.Options
            );
            return state?.Normalize() ?? new TriffSkillsState();
        }
        catch
        {
            return new TriffSkillsState();
        }
    }

    // Best-effort save for refresh passes: a transient write failure (antivirus lock,
    // redirected profile) must not abort the pass that called it - the in-memory state
    // is still correct and the next save retries.
    public void Save()
    {
        if (!TrySave(out var error))
        {
            Debug.WriteLine($"TriffSkills: state save failed: {error}");
        }
    }

    // The authentication commit path uses this directly: a refresh token must not be
    // written to Credential Manager unless the character row it belongs to was durably
    // persisted first, so that path needs to know whether the save actually landed.
    //
    // Write-temp-then-replace so a crash mid-write leaves the previous file rather than
    // a truncated one. The temp name is unique per save so concurrent saves cannot
    // consume each other's file; the last Replace wins, which is the intended semantics
    // for a full-state snapshot.
    public bool TrySave(out string error)
    {
        var tempPath = $"{TriffSkillsPaths.StatePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(TriffSkillsPaths.Root);
            var json = JsonSerializer.Serialize(Normalize(), TriffSkillsJson.Options);

            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            if (File.Exists(TriffSkillsPaths.StatePath))
            {
                File.Replace(tempPath, TriffSkillsPaths.StatePath, null);
            }
            else
            {
                File.Move(tempPath, TriffSkillsPaths.StatePath);
            }

            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            TryDelete(tempPath);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temp file is strictly better than throwing out of a cleanup path.
        }
    }

    public TriffSkillsState Normalize()
    {
        var deduped = new Dictionary<long, TriffSkillsCharacter>();
        foreach (var character in Characters ?? new List<TriffSkillsCharacter>())
        {
            if (character == null || character.CharacterId <= 0) continue;
            character.CharacterName = character.CharacterName?.Trim() ?? "";
            character.Scopes ??= new List<string>();
            character.TrainedLevels ??= new Dictionary<int, int>();
            character.Queue ??= new List<QueueEntry>();
            character.Error = character.Error?.Trim() ?? "";
            deduped[character.CharacterId] = character;   // last wins
        }

        Characters = deduped.Values.ToList();
        if (!deduped.ContainsKey(SelectedCharacterId))
        {
            SelectedCharacterId = Characters.FirstOrDefault()?.CharacterId ?? 0;
        }

        return this;
    }

    public TriffSkillsCharacter Upsert(long characterId)
    {
        var existing = Characters.FirstOrDefault(character => character.CharacterId == characterId);
        if (existing != null) return existing;

        var added = new TriffSkillsCharacter { CharacterId = characterId };
        Characters.Add(added);
        return added;
    }

    // Both Apply* methods look the character up instead of upserting, and do nothing
    // when it is gone: Forget character can complete during a refresh pass's await, and
    // upserting here would re-add a character whose credential was just destroyed.
    // Adding characters is the authorization path's job.
    private TriffSkillsCharacter? Find(long characterId)
    {
        return characterId <= 0
            ? null
            : Characters.FirstOrDefault(character => character.CharacterId == characterId);
    }

    public void ApplyFetchSuccess(long characterId, Dictionary<int, int>? trainedLevels, List<QueueEntry>? queue)
    {
        var character = Find(characterId);
        if (character == null) return;

        // Copied rather than aliased so later caller-side mutation cannot silently
        // edit persisted state.
        character.TrainedLevels = trainedLevels == null
            ? new Dictionary<int, int>()
            : new Dictionary<int, int>(trainedLevels);
        character.Queue = queue == null ? new List<QueueEntry>() : new List<QueueEntry>(queue);
        character.FetchedUtc = DateTimeOffset.UtcNow;
        character.Error = "";
        character.NeedsReauth = false;
    }

    public void ApplyFetchFailure(long characterId, string error, bool needsReauth)
    {
        var character = Find(characterId);
        if (character == null) return;

        // TrainedLevels, Queue and FetchedUtc stay untouched so the last-good record
        // remains scoreable; the UI labels it stale by FetchedUtc.
        character.Error = string.IsNullOrWhiteSpace(error) ? "ESI request failed." : error.Trim();
        character.NeedsReauth = needsReauth;
    }
}
