using System.IO;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

// These tests redirect TriffSkillsPaths.Root, which is process-global state, so every
// test that touches it lives in this one class (xunit runs tests within a class
// serially) and re-points the override before use.
public class TriffSkillsStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "triffskills-tests", Guid.NewGuid().ToString("N"));

    public TriffSkillsStateTests()
    {
        TriffSkillsPaths.OverrideRoot(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void TrySaveRoundTripsThroughLoad()
    {
        var state = new TriffSkillsState();
        var character = state.Upsert(9001);
        character.CharacterName = "Pilot One";
        character.TrainedLevels[100] = 4;
        character.Queue.Add(new QueueEntry(100, 5, null));
        state.SelectedCharacterId = 9001;

        Assert.True(state.TrySave(out var error));
        Assert.Equal("", error);

        var loaded = TriffSkillsState.Load();
        var roundTripped = Assert.Single(loaded.Characters);
        Assert.Equal("Pilot One", roundTripped.CharacterName);
        Assert.Equal(4, roundTripped.TrainedLevels[100]);
        Assert.Equal(5, Assert.Single(roundTripped.Queue).FinishedLevel);
    }

    [Fact]
    public void TrySaveReportsFailureInsteadOfSwallowingIt()
    {
        // Point the root below a *file* so CreateDirectory fails. The auth commit
        // path depends on this returning false: a refresh token must never be
        // written to Credential Manager after a save that silently failed.
        Directory.CreateDirectory(_dir);
        var blocking = Path.Combine(_dir, "blocking-file");
        File.WriteAllText(blocking, "not a directory");
        TriffSkillsPaths.OverrideRoot(Path.Combine(blocking, "nested"));

        var state = new TriffSkillsState();
        state.Upsert(9001);

        Assert.False(state.TrySave(out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void NormalizeDedupesByCharacterIdAndFixesSelection()
    {
        var state = new TriffSkillsState
        {
            Characters = new List<TriffSkillsCharacter>
            {
                new() { CharacterId = 1, CharacterName = " First " },
                new() { CharacterId = 1, CharacterName = "First Again" }, // last wins
                new() { CharacterId = 0 },  // invalid, dropped
                new() { CharacterId = 2, CharacterName = "Second" },
            },
            SelectedCharacterId = 999, // no longer present
        };

        state.Normalize();

        Assert.Equal(2, state.Characters.Count);
        Assert.Equal("First Again", state.Characters[0].CharacterName);
        Assert.Equal(1, state.SelectedCharacterId); // falls back to the first character
    }

    [Fact]
    public void ApplyFetchResultsIgnoreForgottenCharacters()
    {
        // Forget can complete during a refresh pass's await; a fetch result for a
        // removed character must not resurrect it.
        var state = new TriffSkillsState();
        state.ApplyFetchSuccess(42, new Dictionary<int, int> { [1] = 5 }, new List<QueueEntry>());
        state.ApplyFetchFailure(42, "boom", needsReauth: true);
        Assert.Empty(state.Characters);
    }

    [Fact]
    public void ApplyFetchFailureKeepsLastGoodData()
    {
        var state = new TriffSkillsState();
        var character = state.Upsert(42);
        state.ApplyFetchSuccess(42, new Dictionary<int, int> { [1] = 5 }, new List<QueueEntry> { new(1, 5, null) });
        var fetchedUtc = character.FetchedUtc;

        state.ApplyFetchFailure(42, "ESI fell over", needsReauth: false);

        Assert.Equal(5, character.TrainedLevels[1]);
        Assert.Single(character.Queue);
        Assert.Equal(fetchedUtc, character.FetchedUtc); // stays stale-labelled, not cleared
        Assert.Equal("ESI fell over", character.Error);
    }
}
