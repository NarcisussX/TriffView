using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TriffView.TriffSkills;

// Name -> typeID map persisted at %APPDATA%\TriffHud\TriffSkills\skill-ids.json.
// Load-bearing, not an optimisation: skill names are immutable once resolved, so only
// unseen names are ever sent to ESI. Do not add a TTL and do not clear it on refresh.
internal sealed class SkillIdCache
{
    // POST /universe/ids/ declares maxItems: 500 on its request body.
    public const int BatchSize = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Case-insensitive because plan files are hand-written and ESI resolves names
    // case-insensitively.
    public Dictionary<string, int> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static string CachePath => TriffSkillsPaths.SkillIdsPath;

    public static SkillIdCache Load()
    {
        try
        {
            return File.Exists(CachePath) ? FromJson(File.ReadAllText(CachePath)) : new SkillIdCache();
        }
        catch
        {
            // A corrupt cache costs one round of re-resolution, not a broken tool.
            return new SkillIdCache();
        }
    }

    public static SkillIdCache FromJson(string json)
    {
        var cache = new SkillIdCache();
        var map = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
        foreach (var pair in map ?? new Dictionary<string, int>())
        {
            // Copied entry by entry: System.Text.Json builds its own dictionary with
            // the default comparer, which would lose case-insensitivity.
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0) continue;
            cache.Map[pair.Key.Trim()] = pair.Value;
        }

        return cache;
    }

    // Write-temp-then-replace with a unique temp name, same contract as
    // TriffSkillsState.TrySave. Pure derived data, so a failed write is logged and
    // dropped rather than propagated into the refresh that triggered it.
    public void Save()
    {
        var path = CachePath;
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.WriteAllText(temp, JsonSerializer.Serialize(Map, JsonOptions), new UTF8Encoding(false));

            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"TriffSkills: skill id cache save failed: {ex.Message}");
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // A stray temp file is harmless.
            }
        }
    }

    public static List<string> Unresolved(IReadOnlyDictionary<string, int> map, IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var name in names)
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) continue;
            if (map.ContainsKey(trimmed)) continue;
            if (!seen.Add(trimmed)) continue;
            missing.Add(trimmed);
        }

        return missing;
    }

    public static List<List<string>> Batch(IReadOnlyList<string> names, int batchSize)
    {
        var size = Math.Max(1, batchSize);
        var batches = new List<List<string>>();
        for (var start = 0; start < names.Count; start += size)
        {
            batches.Add(names.Skip(start).Take(size).ToList());
        }

        return batches;
    }

    public static int Merge(IDictionary<string, int> map, IEnumerable<SkillsUniverseIdName> resolved)
    {
        var added = 0;
        foreach (var entry in resolved)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Id <= 0) continue;
            var name = entry.Name.Trim();
            if (map.ContainsKey(name)) continue;
            map[name] = entry.Id;
            added++;
        }

        return added;
    }

    public async Task<int> ResolveMissingAsync(
        IEnumerable<string> names,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<SkillsUniverseIdName>>> resolver,
        bool persist = true)
    {
        var missing = Unresolved(Map, names);
        if (missing.Count == 0) return 0;

        var added = 0;
        foreach (var batch in Batch(missing, BatchSize))
        {
            // Names ESI does not recognise are simply absent from the response, stay
            // out of the map, and surface downstream as UnknownSkills rather than as
            // satisfied requirements.
            var resolved = await resolver(batch);
            added += Merge(Map, resolved);
        }

        if (added > 0 && persist) Save();
        return added;
    }
}
