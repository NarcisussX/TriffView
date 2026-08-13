using System.Text;
using System.Text.Json;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class MatrixBoundsTests
{
    [Fact]
    public void ThirtyByOneHundredMatrixStaysCompactAndDeterministic()
    {
        const int characterCount = 30;
        const int planCount = 100;
        const int requirementsPerPlan = 20;
        var ids = Enumerable.Range(1, 200).ToDictionary(index => $"Skill {index}", index => index, StringComparer.OrdinalIgnoreCase);
        var characters = Enumerable.Range(1, characterCount).Select(index => new TriffSkillsCharacter
        {
            CharacterId = index,
            CharacterName = $"Pilot {index}",
            FetchedUtc = DateTimeOffset.UnixEpoch,
            ActiveLevels = ids.Values.ToDictionary(id => id, id => (id + index) % 6),
            TrainedLevels = ids.Values.ToDictionary(id => id, id => Math.Min(5, ((id + index) % 6) + 1)),
            Queue = [new QueueEntry((index % 200) + 1, 5, null, null)],
        }).ToArray();
        var plans = Enumerable.Range(1, planCount).Select(planIndex => new SkillPlan(
            $"Plan {planIndex}",
            Enumerable.Range(0, requirementsPerPlan)
                .Select(offset => new PlanRequirement($"Skill {((planIndex + offset) % 200) + 1}", (offset % 5) + 1))
                .ToArray())).ToArray();

        var first = TriffSkillsMatrix.BuildCompact(characters, plans, ids);
        var second = TriffSkillsMatrix.BuildCompact(characters, plans, ids);
        var firstJson = JsonSerializer.Serialize(first);
        var secondJson = JsonSerializer.Serialize(second);

        Assert.Equal(characterCount * planCount, first.Cells.Count);
        Assert.All(first.Cells, cell => Assert.InRange(
            cell.ActiveCount + cell.TrainedInactiveCount + cell.QueuedCount + cell.MissingCount + cell.UnknownCount,
            0,
            requirementsPerPlan));
        Assert.Equal(firstJson, secondJson);
        Assert.True(Encoding.UTF8.GetByteCount(firstJson) < 1_500_000, "Compact matrix exceeded the 1.5 MB regression ceiling.");
    }
}
