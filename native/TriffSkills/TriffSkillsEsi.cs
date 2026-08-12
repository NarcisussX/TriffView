using System.Text.Json.Serialization;

namespace TriffView.TriffSkills;

// GET /characters/{character_id}/skills/ - requires esi-skills.read_skills.v1
internal sealed class CharacterSkillsResponse
{
    [JsonPropertyName("skills")]
    public List<CharacterSkill> Skills { get; set; } = new();

    [JsonPropertyName("total_sp")]
    public long TotalSp { get; set; }
}

internal sealed class CharacterSkill
{
    [JsonPropertyName("skill_id")]
    public int SkillId { get; set; }

    [JsonPropertyName("active_skill_level")]
    public int ActiveSkillLevel { get; set; }

    [JsonPropertyName("trained_skill_level")]
    public int TrainedSkillLevel { get; set; }

    [JsonPropertyName("skillpoints_in_skill")]
    public long SkillpointsInSkill { get; set; }
}

// GET /characters/{character_id}/skillqueue/ - requires esi-skills.read_skillqueue.v1.
// The response is a bare array, so deserialize it as List<SkillQueueItem>.
internal sealed class SkillQueueItem
{
    [JsonPropertyName("skill_id")]
    public int SkillId { get; set; }

    [JsonPropertyName("finished_level")]
    public int FinishedLevel { get; set; }

    // ESI omits finish_date while the skill queue is paused, which is why
    // QueueEntry.FinishDate is nullable and a fully-queued plan can have no ETA.
    [JsonPropertyName("finish_date")]
    public DateTimeOffset? FinishDate { get; set; }

    [JsonPropertyName("start_date")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("queue_position")]
    public int QueuePosition { get; set; }
}

// POST /universe/ids/ returns one array per resolved category; skills come back
// under "inventory_types".
internal sealed class SkillsUniverseIdsResponse
{
    [JsonPropertyName("inventory_types")]
    public List<SkillsUniverseIdName> InventoryTypes { get; set; } = new();
}

internal sealed class SkillsUniverseIdName
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal static class EsiSkillMapper
{
    public static Dictionary<int, int> ToTrainedLevels(CharacterSkillsResponse? response)
    {
        var levels = new Dictionary<int, int>();
        foreach (var skill in response?.Skills ?? new List<CharacterSkill>())
        {
            if (skill.SkillId <= 0) continue;

            // active_skill_level, deliberately, not trained_skill_level. An alpha clone
            // can hold SP for level 4 in a skill its clone state caps at 2; active is
            // what the character can actually use, which is what "can it fly X" means.
            levels[skill.SkillId] = skill.ActiveSkillLevel;
        }

        return levels;
    }

    public static List<QueueEntry> ToQueue(IReadOnlyList<SkillQueueItem>? items)
    {
        var queue = new List<QueueEntry>();
        var ordered = (items ?? Array.Empty<SkillQueueItem>()).OrderBy(item => item.QueuePosition);
        foreach (var item in ordered)
        {
            if (item.SkillId <= 0) continue;
            queue.Add(new QueueEntry(item.SkillId, item.FinishedLevel, item.FinishDate));
        }

        return queue;
    }
}
