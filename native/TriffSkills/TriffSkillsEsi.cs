using System.Text.Json.Serialization;

namespace TriffView.TriffSkills;

internal sealed class CharacterSkillsResponse
{
    [JsonPropertyName("skills")]
    public List<CharacterSkill> Skills { get; set; } = [];
}

internal sealed class CharacterSkill
{
    [JsonPropertyName("skill_id")]
    public int SkillId { get; set; }

    [JsonPropertyName("active_skill_level")]
    public int ActiveSkillLevel { get; set; }

    [JsonPropertyName("trained_skill_level")]
    public int TrainedSkillLevel { get; set; }
}

internal sealed class SkillQueueItem
{
    [JsonPropertyName("skill_id")]
    public int SkillId { get; set; }

    [JsonPropertyName("finished_level")]
    public int FinishedLevel { get; set; }

    [JsonPropertyName("finish_date")]
    public DateTimeOffset? FinishDate { get; set; }

    [JsonPropertyName("start_date")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("queue_position")]
    public int QueuePosition { get; set; }
}

internal sealed class SkillsUniverseIdsResponse
{
    [JsonPropertyName("inventory_types")]
    public List<SkillsUniverseIdName> InventoryTypes { get; set; } = [];
}

internal sealed class SkillsUniverseIdName
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

internal sealed class UniverseTypeResponse
{
    [JsonPropertyName("group_id")]
    public int GroupId { get; set; }
}

internal sealed class UniverseGroupResponse
{
    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }
}

internal sealed record SkillSnapshot(
    IReadOnlyDictionary<int, int> ActiveLevels,
    IReadOnlyDictionary<int, int> TrainedLevels);

internal static class EsiSkillMapper
{
    public static SkillSnapshot ToSnapshot(CharacterSkillsResponse? response)
    {
        var active = new Dictionary<int, int>();
        var trained = new Dictionary<int, int>();
        foreach (var skill in response?.Skills ?? [])
        {
            if (skill.SkillId <= 0) continue;
            active[skill.SkillId] = Math.Clamp(skill.ActiveSkillLevel, 0, 5);
            trained[skill.SkillId] = Math.Clamp(skill.TrainedSkillLevel, 0, 5);
        }
        return new SkillSnapshot(active, trained);
    }

    public static List<QueueEntry> ToQueue(IReadOnlyList<SkillQueueItem>? items)
        => (items ?? [])
            .Where(item => item.SkillId > 0 && item.FinishedLevel is >= 1 and <= 5)
            .OrderBy(item => item.QueuePosition)
            .Take(500)
            .Select(item => new QueueEntry(item.SkillId, item.FinishedLevel, item.StartDate, item.FinishDate, item.QueuePosition))
            .ToList();
}
