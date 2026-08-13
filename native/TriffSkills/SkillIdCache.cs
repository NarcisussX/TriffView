using System.IO;
using System.Text.Json;
using TriffView.Eve;

namespace TriffView.TriffSkills;

internal sealed record ValidatedSkillType(int TypeId, int GroupId, int CategoryId = 16);
internal sealed class SkillIdCacheModel
{
    public int Version { get; set; }
    public Dictionary<string, ValidatedSkillType> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
internal sealed record SkillCacheLoadResult(SkillIdCache Cache, string Warning);

internal sealed class SkillIdCache
{
    private const long MaxCacheFileBytes = 4 * 1024 * 1024;
    public const int SchemaVersion = 3;
    public const int BatchSize = 500;
    public const int MaxEntries = 20_000;

    public Dictionary<string, ValidatedSkillType> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static SkillCacheLoadResult Load()
    {
        var path = TriffSkillsPaths.SkillIdsPath;
        if (!File.Exists(path)) return new SkillCacheLoadResult(new SkillIdCache(), string.Empty);
        try
        {
            return new SkillCacheLoadResult(FromJson(AtomicFile.ReadBoundedText(path, MaxCacheFileBytes)), string.Empty);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or System.Text.DecoderFallbackException)
        {
            var preserved = string.Empty;
            try { preserved = AtomicFile.PreserveCorrupt(path); }
            catch (Exception preserveError) when (IsFileFailure(preserveError))
            {
                return new SkillCacheLoadResult(new SkillIdCache(), $"Skill cache is corrupt and could not be preserved: {preserveError.Message}");
            }

            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                try
                {
                    var recovered = FromJson(AtomicFile.ReadBoundedText(backup, MaxCacheFileBytes));
                    recovered.TrySave(out _);
                    return new SkillCacheLoadResult(recovered, $"Recovered the validated skill cache from backup. Corrupt file: {preserved}");
                }
                catch (Exception backupError) when (backupError is JsonException or InvalidDataException or System.Text.DecoderFallbackException || IsFileFailure(backupError))
                {
                    return new SkillCacheLoadResult(new SkillIdCache(), $"Skill cache and backup are unreadable. Corrupt file: {preserved}. {backupError.Message}");
                }
            }
            return new SkillCacheLoadResult(new SkillIdCache(), $"Skill cache was corrupt and preserved at {preserved}. {exception.Message}");
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return new SkillCacheLoadResult(new SkillIdCache(), $"Could not read validated skill cache: {exception.Message}");
        }
    }

    public static SkillIdCache FromJson(string json)
    {
        var model = JsonSerializer.Deserialize<SkillIdCacheModel>(json, TriffSkillsJson.Options)
            ?? throw new JsonException("Skill cache JSON was empty.");
        if (model.Version != SchemaVersion) throw new JsonException($"Unsupported skill cache version {model.Version}.");
        var cache = new SkillIdCache();
        foreach (var pair in (model.Skills ?? []).Take(MaxEntries))
        {
            if (string.IsNullOrWhiteSpace(pair.Key)
                || pair.Value is null
                || pair.Value.TypeId <= 0
                || pair.Value.GroupId <= 0
                || pair.Value.CategoryId != 16) continue;
            cache.Map[pair.Key.Trim()] = pair.Value;
        }
        return cache;
    }

    public bool TrySave(out string error)
    {
        try
        {
            var model = new SkillIdCacheModel
            {
                Version = SchemaVersion,
                Skills = Map.Take(MaxEntries).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            };
            AtomicFile.WriteText(TriffSkillsPaths.SkillIdsPath, JsonSerializer.Serialize(model, TriffSkillsJson.Options));
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (IsFileFailure(exception) || exception is JsonException)
        {
            error = exception.Message;
            return false;
        }
    }

    public Dictionary<string, int> TypeIds()
        => Map.ToDictionary(pair => pair.Key, pair => pair.Value.TypeId, StringComparer.OrdinalIgnoreCase);

    public List<string> Unresolved(IEnumerable<string> names)
        => names.Select(name => (name ?? string.Empty).Trim())
            .Where(name => name.Length > 0 && !Map.ContainsKey(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public int Merge(IEnumerable<(string Name, ValidatedSkillType Skill)> resolved)
    {
        var added = 0;
        foreach (var (rawName, skill) in resolved)
        {
            var name = (rawName ?? string.Empty).Trim();
            if (name.Length == 0 || skill.TypeId <= 0 || skill.GroupId <= 0 || skill.CategoryId != 16 || Map.ContainsKey(name) || Map.Count >= MaxEntries) continue;
            Map[name] = skill;
            added++;
        }
        return added;
    }

    public static IReadOnlyList<IReadOnlyList<string>> Batch(IEnumerable<string> names)
    {
        var values = names.ToList();
        var batches = new List<IReadOnlyList<string>>();
        for (var offset = 0; offset < values.Count; offset += BatchSize) batches.Add(values.Skip(offset).Take(BatchSize).ToArray());
        return batches;
    }

    private static bool IsFileFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
