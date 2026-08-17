using System.Text.Json.Nodes;
using TriffView.Preview;

namespace TriffView.Tests;

public class CycleGroupHotkeyTests
{
    [Fact]
    public void TwoEnabledGroupsPlanAllForwardAndBackwardHotkeys()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Combat One"),
            Group("Support", true, "F15", "F16", "Support One"));

        var registrations = Plan(profile, out var failures);

        Assert.Empty(failures);
        Assert.Equal(new[] { "F13", "F14", "F15", "F16" }, registrations.Select(item => item.Gesture));
    }

    [Fact]
    public void PlannedCommandsCarryTheirGroupAndDirection()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Combat One"),
            Group("Support", true, "F15", "F16", "Support One"));

        var registrations = Plan(profile, out _);

        Assert.Collection(
            registrations,
            item => AssertRegistration(item, "combat", 1),
            item => AssertRegistration(item, "combat", -1),
            item => AssertRegistration(item, "support", 1),
            item => AssertRegistration(item, "support", -1));
    }

    [Fact]
    public void CycleGroupsKeepIndependentCursorKeysAndAnchors()
    {
        var combatKey = TriffViewController.CycleCursorKey("profile", "combat");
        var supportKey = TriffViewController.CycleCursorKey("profile", "support");
        var cursors = new Dictionary<string, nint>
        {
            [combatKey] = (nint)11,
            [supportKey] = (nint)22,
        };

        var combatNext = TriffViewCycleState.NextIndex(new nint[] { 11, 12 }, 1, 0, 0, cursors[combatKey]);
        var supportNext = TriffViewCycleState.NextIndex(new nint[] { 21, 22 }, 1, 0, 0, cursors[supportKey]);

        Assert.NotEqual(combatKey, supportKey);
        Assert.Equal(1, combatNext);
        Assert.Equal(0, supportNext);
        Assert.Equal((nint)22, cursors[supportKey]);
    }

    [Fact]
    public void OverlappingGroupsRemainSeparateRegistrations()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Shared", "Combat One"),
            Group("Support", true, "F15", "F16", "Shared", "Support One"));

        var registrations = Plan(profile, out var failures);

        Assert.Empty(failures);
        Assert.Equal(2, registrations.Select(item => item.GroupId).Distinct().Count());
        Assert.Equal(4, registrations.Count);
    }

    [Fact]
    public void DisabledGroupDoesNotPlanHotkeys()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Combat One"),
            Group("Support", false, "F15", "F16", "Support One"));

        var registrations = Plan(profile, out _);

        Assert.DoesNotContain(registrations, item => item.GroupId == "support");
        Assert.Equal(2, registrations.Count);
    }

    [Fact]
    public void ReenablingGroupRestoresItsBindings()
    {
        var support = Group("Support", false, "F15", "F16", "Support One");
        var profile = Profile(support);
        Assert.Empty(Plan(profile, out _));

        support.Enabled = true;
        var registrations = Plan(profile, out _);

        Assert.Equal(new[] { "F15", "F16" }, registrations.Select(item => item.Gesture));
    }

    [Fact]
    public void EditorSelectionDoesNotChangeRuntimeSignatureOrPlan()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Combat One"),
            Group("Support", true, "F15", "F16", "Support One"));
        profile.SelectedCycleGroupId = "combat";
        var firstSignature = TriffViewOverlayForm.HotkeySignature(profile, Array.Empty<EveClientWindow>(), false);
        var firstPlan = Plan(profile, out _).Select(item => item.Gesture).ToArray();

        profile.SelectedCycleGroupId = "support";
        var secondSignature = TriffViewOverlayForm.HotkeySignature(profile, Array.Empty<EveClientWindow>(), false);
        var secondPlan = Plan(profile, out _).Select(item => item.Gesture).ToArray();

        Assert.Equal(firstSignature, secondSignature);
        Assert.Equal(firstPlan, secondPlan);
    }

    [Fact]
    public void EnabledStatesSurviveStructuredPatchAndSettingsReload()
    {
        var patchedGroups = TriffViewController.ParseCycleGroups(new JsonArray
        {
            new JsonObject
            {
                ["name"] = "Combat",
                ["forwardGestures"] = new JsonArray("F13"),
                ["backwardGestures"] = new JsonArray("F14"),
                ["charactersText"] = "Combat One",
                ["enabled"] = false,
            },
        });
        var settings = new TriffViewSettings
        {
            SelectedProfileId = "default",
            Profiles = new List<TriffViewProfile>
            {
                new() { Id = "default", Name = "Default", CycleGroups = patchedGroups },
            },
        };

        var loaded = TriffViewSettings.FromJson(settings.ToJson());

        Assert.False(Assert.Single(loaded.ActiveProfile().CycleGroups).Enabled);
    }

    [Fact]
    public void LegacyGroupsWithoutEnabledPropertyDefaultToEnabled()
    {
        const string json = """
            {
              "selectedProfileId": "default",
              "profiles": [
                {
                  "id": "default",
                  "name": "Default",
                  "cycleGroups": [
                    {
                      "name": "Legacy",
                      "forwardGestures": ["F13"],
                      "backwardGestures": ["F14"],
                      "characters": ["Legacy One"]
                    }
                  ]
                }
              ]
            }
            """;

        var loaded = TriffViewSettings.FromJson(json);

        Assert.True(Assert.Single(loaded.ActiveProfile().CycleGroups).Enabled);
    }

    [Fact]
    public void DuplicateGestureKeepsFirstAndReportsConflictWithoutDroppingOthers()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Combat One"),
            Group("Support", true, "F13", "F16", "Support One"));

        var registrations = Plan(profile, out var failures);

        Assert.Equal(new[] { "F13", "F14", "F16" }, registrations.Select(item => item.Gesture));
        var failure = Assert.Single(failures);
        Assert.Contains("F13", failure);
        Assert.Contains("Support", failure);
        Assert.Contains("forward", failure);
        Assert.Contains("Combat", failure);
    }

    [Fact]
    public void EmptyGroupMembersFallBackToCharacterOrder()
    {
        var profile = Profile(Group("All", true, "F17", "F18"));
        profile.CharacterOrder = new List<string> { "One", "Two" };

        var registrations = Plan(profile, out var failures);

        Assert.Empty(failures);
        Assert.Equal(2, registrations.Count);
    }

    [Fact]
    public void ExistingSingleGroupBehaviorStillPlansBothDirections()
    {
        var profile = Profile(Group("All", true, "F13", "F14", "One", "Two"));

        var registrations = Plan(profile, out var failures);

        Assert.Empty(failures);
        Assert.Collection(
            registrations,
            item => AssertRegistration(item, "all", 1),
            item => AssertRegistration(item, "all", -1));
    }

    private static IReadOnlyList<TriffViewCycleHotkeyRegistration> Plan(
        TriffViewProfile profile,
        out List<string> failures)
    {
        failures = new List<string>();
        return TriffViewCycleHotkeyPlanner.Plan(profile, null, failures);
    }

    private static TriffViewProfile Profile(params TriffViewCycleGroup[] groups)
    {
        return new TriffViewProfile
        {
            Id = "profile",
            Name = "Profile",
            CycleGroups = groups.ToList(),
            SelectedCycleGroupId = groups.FirstOrDefault()?.Id ?? "",
        };
    }

    private static TriffViewCycleGroup Group(
        string name,
        bool enabled,
        string forward,
        string backward,
        params string[] characters)
    {
        return new TriffViewCycleGroup
        {
            Id = TriffViewCycleGroup.IdFromName(name),
            Name = name,
            Enabled = enabled,
            ForwardGestures = new List<string> { forward },
            BackwardGestures = new List<string> { backward },
            Characters = characters.ToList(),
        };
    }

    private static void AssertRegistration(TriffViewCycleHotkeyRegistration registration, string groupId, int direction)
    {
        Assert.Equal(groupId, registration.GroupId);
        Assert.Equal(direction, registration.Direction);
    }
}
