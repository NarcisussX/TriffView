using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class TriffSkillsStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "triffskills-tests", Guid.NewGuid().ToString("N"));

    public TriffSkillsStateTests() => TriffSkillsPaths.OverrideRoot(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        finally { TriffSkillsPaths.ClearOverride(); }
    }

    [Fact]
    public void RoundTripPreservesActiveTrainedQueueAndOwnerMetadata()
    {
        var state = new TriffSkillsState();
        var character = state.Upsert(9001);
        character.CharacterName = "Pilot";
        character.OwnerHash = "owner-hash";
        character.ActiveLevels[100] = 3;
        character.TrainedLevels[100] = 5;
        character.Queue.Add(new QueueEntry(100, 5, null, null));
        Assert.True(state.TrySave(out var error), error);
        var loaded = TriffSkillsState.Load();
        var restored = Assert.Single(loaded.State.Characters);
        Assert.Equal(3, restored.ActiveLevels[100]);
        Assert.Equal(5, restored.TrainedLevels[100]);
        Assert.Equal("owner-hash", restored.OwnerHash);
        Assert.Single(restored.Queue);
    }

    [Fact]
    public void CorruptPrimaryIsPreservedAndBackupIsRecovered()
    {
        var state = new TriffSkillsState();
        state.Upsert(1).CharacterName = "First";
        Assert.True(state.TrySave(out _));
        state.Find(1)!.CharacterName = "Second";
        Assert.True(state.TrySave(out _));
        File.WriteAllText(TriffSkillsPaths.StatePath, "{not-json");
        var loaded = TriffSkillsState.Load();
        Assert.Contains("Recovered", loaded.Warning);
        Assert.Equal("First", Assert.Single(loaded.State.Characters).CharacterName);
        Assert.NotEmpty(Directory.GetFiles(_dir, "state.json.corrupt-*"));
    }

    [Fact]
    public void FetchFailureKeepsLastGoodDataAndOnlyDefinitiveFailureSetsReauth()
    {
        var state = new TriffSkillsState();
        var character = state.Upsert(42);
        state.ApplyFetchSuccess(42, new Dictionary<int, int> { [1] = 3 }, new Dictionary<int, int> { [1] = 5 }, [new QueueEntry(1, 5, null, null)], DateTimeOffset.UtcNow);
        state.ApplyFetchFailure(42, "temporary", needsReauth: false);
        Assert.Equal(3, character.ActiveLevels[1]);
        Assert.Equal(5, character.TrainedLevels[1]);
        Assert.False(character.NeedsReauth);
        state.ApplyFetchFailure(42, "invalid grant", needsReauth: true);
        Assert.True(character.NeedsReauth);
    }

    [Fact]
    public void NormalizeDropsInvalidRowsAndEnforcesCharacterCap()
    {
        var state = new TriffSkillsState
        {
            Characters = Enumerable.Range(0, TriffSkillsState.MaxCharacters + 10)
                .Select(index => new TriffSkillsCharacter { CharacterId = index, CharacterName = $" {index} " })
                .ToList(),
        };
        state.Normalize();
        Assert.Equal(TriffSkillsState.MaxCharacters, state.Characters.Count);
        Assert.DoesNotContain(state.Characters, character => character.CharacterId <= 0);
    }

    [Fact]
    public void PathsUseStandaloneTriffViewNamespace()
    {
        TriffSkillsPaths.ClearOverride();
        Assert.Contains(Path.Combine("TriffView", "TriffSkills"), TriffSkillsPaths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TriffHud", TriffSkillsPaths.Root, StringComparison.OrdinalIgnoreCase);
        TriffSkillsPaths.OverrideRoot(_dir);
    }
}
