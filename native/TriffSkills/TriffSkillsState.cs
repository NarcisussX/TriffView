using System.IO;
using System.Text.Json;
using TriffView.Eve;

namespace TriffView.TriffSkills;

internal static class TriffSkillsPaths
{
    private static string? _rootOverride;

    public static void OverrideRoot(string root) => _rootOverride = root;
    public static void ClearOverride() => _rootOverride = null;

    public static string Root => _rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TriffView",
        "TriffSkills");

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
    public string CharacterName { get; set; } = string.Empty;
    public string OwnerHash { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset AuthenticatedUtc { get; set; }
    public DateTimeOffset? FetchedUtc { get; set; }
    public Dictionary<int, int> ActiveLevels { get; set; } = [];
    public Dictionary<int, int> TrainedLevels { get; set; } = [];
    public List<QueueEntry> Queue { get; set; } = [];
    public string Error { get; set; } = string.Empty;
    public bool NeedsReauth { get; set; }

    public TriffSkillsCharacter Clone() => new()
    {
        CharacterId = CharacterId,
        CharacterName = CharacterName,
        OwnerHash = OwnerHash,
        Scopes = [.. Scopes],
        AuthenticatedUtc = AuthenticatedUtc,
        FetchedUtc = FetchedUtc,
        ActiveLevels = new Dictionary<int, int>(ActiveLevels),
        TrainedLevels = new Dictionary<int, int>(TrainedLevels),
        Queue = [.. Queue],
        Error = Error,
        NeedsReauth = NeedsReauth,
    };
}

internal sealed record StateLoadResult(TriffSkillsState State, string Warning);

internal sealed class TriffSkillsState
{
    private const long MaxStateFileBytes = 16 * 1024 * 1024;
    public const int MaxCharacters = 50;
    public List<TriffSkillsCharacter> Characters { get; set; } = [];
    public long SelectedCharacterId { get; set; }

    public static StateLoadResult Load()
    {
        var path = TriffSkillsPaths.StatePath;
        if (!File.Exists(path)) return new StateLoadResult(new TriffSkillsState(), string.Empty);

        try
        {
            return new StateLoadResult(Deserialize(AtomicFile.ReadBoundedText(path, MaxStateFileBytes)), string.Empty);
        }
        catch (Exception primaryJson) when (primaryJson is JsonException or InvalidDataException or System.Text.DecoderFallbackException)
        {
            string preserved = string.Empty;
            try { preserved = AtomicFile.PreserveCorrupt(path); }
            catch (Exception preserveError) when (IsFileFailure(preserveError))
            {
                return new StateLoadResult(new TriffSkillsState(), $"State JSON is corrupt and could not be preserved: {preserveError.Message}");
            }

            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                try
                {
                    var recovered = Deserialize(AtomicFile.ReadBoundedText(backup, MaxStateFileBytes));
                    AtomicFile.WriteText(path, JsonSerializer.Serialize(recovered, TriffSkillsJson.Options));
                    return new StateLoadResult(recovered, $"Recovered corrupt state from last-known-good backup. Corrupt file: {preserved}");
                }
                catch (Exception backupError) when (backupError is JsonException or InvalidDataException or System.Text.DecoderFallbackException || IsFileFailure(backupError))
                {
                    return new StateLoadResult(new TriffSkillsState(), $"State and backup are unreadable. Corrupt file: {preserved}. Backup: {backupError.Message}");
                }
            }
            return new StateLoadResult(new TriffSkillsState(), $"State JSON was corrupt and preserved at {preserved}. No backup was available. {primaryJson.Message}");
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return new StateLoadResult(new TriffSkillsState(), $"Could not read TriffSkills state: {exception.Message}");
        }
    }

    public bool TrySave(out string error)
    {
        try
        {
            Normalize();
            AtomicFile.WriteText(TriffSkillsPaths.StatePath, JsonSerializer.Serialize(this, TriffSkillsJson.Options));
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (IsFileFailure(exception) || exception is JsonException)
        {
            error = exception.Message;
            return false;
        }
    }

    public TriffSkillsState Normalize()
    {
        var deduped = new Dictionary<long, TriffSkillsCharacter>();
        foreach (var character in (Characters ?? []).Take(MaxCharacters * 2))
        {
            if (character is null || character.CharacterId <= 0) continue;
            character.CharacterName = (character.CharacterName ?? string.Empty).Trim();
            character.OwnerHash = (character.OwnerHash ?? string.Empty).Trim();
            character.Scopes = (character.Scopes ?? []).Where(scope => !string.IsNullOrWhiteSpace(scope)).Distinct(StringComparer.Ordinal).Take(100).ToList();
            character.ActiveLevels = NormalizeLevels(character.ActiveLevels);
            character.TrainedLevels = NormalizeLevels(character.TrainedLevels);
            character.Queue = (character.Queue ?? []).Where(entry => entry.SkillId > 0 && entry.FinishedLevel is >= 1 and <= 5).Take(500).ToList();
            character.Error = (character.Error ?? string.Empty).Trim();
            deduped[character.CharacterId] = character;
        }

        Characters = deduped.Values.Take(MaxCharacters).ToList();
        if (!deduped.ContainsKey(SelectedCharacterId)) SelectedCharacterId = Characters.FirstOrDefault()?.CharacterId ?? 0;
        return this;
    }

    public TriffSkillsCharacter? Find(long characterId)
        => characterId <= 0 ? null : Characters.FirstOrDefault(character => character.CharacterId == characterId);

    public TriffSkillsCharacter Upsert(long characterId)
    {
        var existing = Find(characterId);
        if (existing is not null) return existing;
        if (Characters.Count >= MaxCharacters) throw new InvalidOperationException($"TriffSkills supports up to {MaxCharacters} characters.");
        var added = new TriffSkillsCharacter { CharacterId = characterId };
        Characters.Add(added);
        return added;
    }

    public void ApplyFetchSuccess(
        long characterId,
        IReadOnlyDictionary<int, int> activeLevels,
        IReadOnlyDictionary<int, int> trainedLevels,
        IReadOnlyList<QueueEntry> queue,
        DateTimeOffset fetchedUtc)
    {
        var character = Find(characterId);
        if (character is null) return;
        character.ActiveLevels = new Dictionary<int, int>(activeLevels);
        character.TrainedLevels = new Dictionary<int, int>(trainedLevels);
        character.Queue = [.. queue];
        character.FetchedUtc = fetchedUtc;
        character.Error = string.Empty;
        character.NeedsReauth = false;
    }

    public void ApplyFetchFailure(long characterId, string error, bool needsReauth)
    {
        var character = Find(characterId);
        if (character is null) return;
        character.Error = string.IsNullOrWhiteSpace(error) ? "Refresh failed." : error.Trim();
        character.NeedsReauth = character.NeedsReauth || needsReauth;
    }

    private static Dictionary<int, int> NormalizeLevels(Dictionary<int, int>? source)
        => (source ?? []).Where(pair => pair.Key > 0 && pair.Value is >= 0 and <= 5).Take(20_000).ToDictionary(pair => pair.Key, pair => pair.Value);

    private static TriffSkillsState Deserialize(string json)
        => (JsonSerializer.Deserialize<TriffSkillsState>(json, TriffSkillsJson.Options)
            ?? throw new JsonException("State JSON was empty.")).Normalize();

    private static bool IsFileFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
