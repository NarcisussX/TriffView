using TriffView.Preview;

namespace TriffView.Tests;

public class AhkHotkeyTests
{
    [Theory]
    [InlineData("F13", "F13")]
    [InlineData("*F13", "*F13")]
    [InlineData("^+F14", "^+F14")]
    [InlineData("<^>!F13", "<^>!F13")]
    [InlineData("~*F13 up", "~*F13 up")]
    [InlineData("XButton1 & F14", "XButton1 & F14")]
    [InlineData("Ctrl+Shift+F13", "^+F13")]
    [InlineData("Control+Alt+Win+F14", "^!#F14")]
    [InlineData("Shift+Oemtilde", "+vkC0")]
    [InlineData("Ctrl+Plus", "^vkBB")]
    [InlineData("vk70", "vk70")]
    [InlineData("VK_112", "vk70")]
    [InlineData("VK_0x70", "vk70")]
    [InlineData("Control+VK_0x91", "^vk91")]
    [InlineData("Apps", "AppsKey")]
    [InlineData("Control+ArrowUp", "^Up")]
    [InlineData("Cancel", "vk03")]
    [InlineData("0x70", "vk70")]
    [InlineData("+", "+")]
    [InlineData("^,", "^,")]
    public void TranslatesLegacyModifiersAndPassesAhkSyntaxThrough(string input, string expected)
        => Assert.Equal(expected, AhkGesture.Translate(input));

    [Theory]
    [InlineData(",")]
    [InlineData("^,")]
    [InlineData("Ctrl+,")]
    [InlineData("CapsLock & ,")]
    public void CommaKeySurvivesNormalization(string gesture)
    {
        var binding = new TriffViewHotkeyBinding { CharacterName = "One", Gestures = [gesture, "F14"] };
        binding.Normalize();
        Assert.Equal(new[] { gesture.Replace("Ctrl+", "Control+"), "F14" }, binding.Gestures);
        Assert.Equal(new[] { gesture, "F14" }, AhkGesture.SplitList(gesture + ", F14"));
    }

    [Fact]
    public void LegacyCommaSeparatedBindingsStillNormalize()
        => Assert.Equal(new[] { "F13", "*F14", "Ctrl+F15" }, AhkGesture.SplitList("F13, *F14, Ctrl+F15"));

    [Fact]
    public void NoRepeatLegacyFlagIsSeparateFromAhkSyntax()
    {
        Assert.Equal("^F13", AhkGesture.Translate("Ctrl+NoRepeat+F13"));
        Assert.True(AhkGesture.IsNoRepeat("Ctrl+NoRepeat+F13"));
        Assert.True(AhkGesture.IsNoRepeat("*NoRepeat+F13"));
        Assert.False(AhkGesture.IsNoRepeat("F13"));
    }

    [Fact]
    public void WildcardsSurviveLegacySettingsAndJsonRoundTrip()
    {
        var settings = TriffViewSettings.FromJson("""
            {"selectedProfileId":"default","profiles":[{"id":"default","name":"Default",
            "directHotkeys":[{"characterName":"One","gesture":"*F13"}],
            "cycleGroups":[{"name":"All","forwardGesture":"*F14","backwardGesture":"F15","characters":["One","Two"]}]}]}
            """);
        var restored = TriffViewSettings.FromJson(settings.ToJson());
        var profile = restored.ActiveProfile();
        Assert.Equal("*F13", Assert.Single(Assert.Single(profile.DirectHotkeys).Gestures));
        Assert.Equal("*F14", Assert.Single(Assert.Single(profile.CycleGroups).ForwardGestures));
        Assert.Equal("F15", Assert.Single(Assert.Single(profile.CycleGroups).BackwardGestures));
        Assert.Equal(new[] { "One", "Two" }, profile.CycleGroups[0].Characters);
    }

    [Fact]
    public void OldDecimalVirtualKeysMigrateOnceWhileNewAhkVirtualKeysStayHexadecimal()
    {
        var oldSettings = TriffViewSettings.FromJson("""
            {"profiles":[{"id":"default","name":"Default","directHotkeys":[
            {"characterName":"One","gesture":"Control+VK70"}]}]}
            """);
        Assert.Equal("Control+VK_70", oldSettings.ActiveProfile().DirectHotkeys[0].Gesture);
        Assert.Equal("^vk46", AhkGesture.Translate(oldSettings.ActiveProfile().DirectHotkeys[0].Gesture));
        var profile = oldSettings.ActiveProfile();
        profile.DirectHotkeys[0].Gestures = ["vk70"];
        var reloaded = TriffViewSettings.FromJson(oldSettings.ToJson());
        Assert.Equal("vk70", reloaded.ActiveProfile().DirectHotkeys[0].Gesture);
        Assert.Equal(1, reloaded.HotkeySyntaxVersion);
    }

    [Theory]
    [InlineData("*F13")]
    [InlineData("~XButton1")]
    [InlineData("<^>!F14")]
    [InlineData("CapsLock & F13")]
    public void EveXImportDoesNotStripAhkFeatures(string gesture)
        => Assert.Equal(gesture, TriffViewController.NormalizeEveXGesture(gesture));

    [Fact]
    public void MapsDirectAndBothCycleDirectionsWithoutChangingGroupOrder()
    {
        var profile = Profile();
        profile.DirectHotkeys.Add(new() { CharacterName = "One", Gestures = ["*F13"] });
        profile.CycleGroups.Add(new() { Id = "support", Name = "Support", Characters = ["Two"], ForwardGestures = ["F16"] });
        var failures = new List<string>();
        var plan = TriffViewAhkHotkeys.Plan(profile, Clients(), failures);
        Assert.Empty(failures);
        Assert.Equal(new[] { "*F13", "*F14", "*F15", "F16" }, plan.Select(item => item.Gesture));
        Assert.Equal("One", Assert.Single(plan[0].Command.CharacterNames));
        Assert.Equal(new[] { "all", "all", "support" }, plan.Skip(1).Select(item => item.Command.GroupId));
        Assert.Equal(new[] { 1, -1, 1 }, plan.Skip(1).Select(item => item.Command.Direction));
    }

    [Fact]
    public void LegacyAndAhkEquivalentModifiersConflictButWildcardRemainsDistinct()
    {
        var profile = Profile();
        profile.CycleGroups[0].ForwardGestures = ["Ctrl+Shift+F13", "+^F13", "*F13", "F13"];
        var failures = new List<string>();
        var plan = TriffViewAhkHotkeys.Plan(profile, Clients(), failures);
        Assert.Single(failures);
        Assert.Equal(new[] { "^+F13", "*F13", "F13", "*F15" }, plan.Select(binding => binding.Gesture));
    }

    [Fact]
    public void HookAndPassThroughOptionsDoNotSilentlyReplaceAnotherCommand()
    {
        var profile = Profile();
        profile.CycleGroups[0].ForwardGestures = ["F13", "$F13", "~F13", "*F13", "<^+F14", "+<^F14"];
        var failures = new List<string>();
        var plan = TriffViewAhkHotkeys.Plan(profile, Clients(), failures);
        Assert.Equal(3, failures.Count);
        Assert.Equal(new[] { "F13", "*F13", "<^+F14", "*F15" }, plan.Select(binding => binding.Gesture));
    }

    [Fact]
    public void RejectedRegistrationCannotDispatchAndIsRetriedAfterRestart()
    {
        var transport = new FakeTransport { Accept = message => !message.EndsWith("\t*F14") };
        using var hotkeys = new TriffViewAhkHotkeys(transport);
        var received = new List<TriffViewHotkeyCommand>();
        hotkeys.Pressed += received.Add;
        hotkeys.Configure(Profile(), Clients(), false);
        Assert.Single(hotkeys.Failures);
        transport.Press(1, transport.Generation);
        Assert.Empty(received);
        transport.Press(2, transport.Generation);
        Assert.Equal(-1, Assert.Single(received).Direction);
        transport.Accept = _ => true;
        var previousGeneration = transport.Generation;
        transport.Connect();
        Assert.Empty(hotkeys.Failures);
        transport.Press(1, previousGeneration);
        Assert.Single(received);
        transport.Press(1, transport.Generation);
        Assert.Equal(1, received.Last().Direction);
    }

    [Theory]
    [InlineData("RESET")]
    [InlineData("COMMIT")]
    public void FailedReloadDoesNotDispatchUncommittedCommands(string failedOperation)
    {
        var transport = new FakeTransport { Accept = message => !message.StartsWith(failedOperation) };
        using var hotkeys = new TriffViewAhkHotkeys(transport);
        var received = new List<TriffViewHotkeyCommand>();
        hotkeys.Pressed += received.Add;
        hotkeys.Configure(Profile(), Clients(), false);
        Assert.NotEmpty(hotkeys.Failures);
        transport.Press(1, transport.Generation);
        Assert.Empty(received);
    }

    [Fact]
    public void SharedDirectBindingKeepsClientOrderAndCannotBeReassignedToCycle()
    {
        var profile = Profile();
        profile.DirectHotkeys = [new() { CharacterName = "One", Gestures = ["Ctrl+F13"] }, new() { CharacterName = "Two", Gestures = ["^F13"] }];
        profile.CycleGroups[0].ForwardGestures = ["^F13"];
        var failures = new List<string>();
        var plan = TriffViewAhkHotkeys.Plan(profile, Clients(), failures);
        Assert.Single(failures);
        Assert.Equal(new[] { "One", "Two" }, plan[0].Command.CharacterNames);
        Assert.Equal(new[] { "^F13", "*F15" }, plan.Select(item => item.Gesture));
    }

    [Fact]
    public void ActivationUsesOnlySelectedHwndAndRespectsSuspendAndDisposal()
    {
        var transport = new FakeTransport();
        var hotkeys = new TriffViewAhkHotkeys(transport);
        hotkeys.Configure(Profile(), Clients(), false);
        Assert.True(hotkeys.Activate(102, true));
        Assert.Equal(((nint)102, true), Assert.Single(transport.Activations));
        hotkeys.Configure(Profile(), Clients(), true);
        Assert.False(hotkeys.Activate(101, false));
        hotkeys.Dispose();
        Assert.False(hotkeys.Activate(101, false));
        Assert.Single(transport.Activations);
    }

    [Fact]
    public void ReloadRejectsOldCommandsAndUnchangedSettingsDoNotReregister()
    {
        var transport = new FakeTransport();
        using var hotkeys = new TriffViewAhkHotkeys(transport);
        var received = new List<TriffViewHotkeyCommand>();
        hotkeys.Pressed += received.Add;
        var profile = Profile();
        hotkeys.Configure(profile, Clients(), false);
        var oldGeneration = transport.Generation;
        var sends = transport.Messages.Count;
        hotkeys.Configure(profile, Clients(), false);
        Assert.Equal(sends, transport.Messages.Count);
        profile.CycleGroups[0].ForwardGestures = ["F17"];
        hotkeys.Configure(profile, Clients(), false);
        Assert.Contains(transport.Messages, text => text.EndsWith("\tF17"));
        transport.Press(1, oldGeneration);
        Assert.Empty(received);
        transport.Press(1, transport.Generation);
        Assert.Equal(1, Assert.Single(received).Direction);
    }

    [Fact]
    public void SuspendDisablesRegistrationsResumeRestoresLatestProfileAndDisposeStopsTransport()
    {
        var transport = new FakeTransport();
        var hotkeys = new TriffViewAhkHotkeys(transport);
        var received = new List<TriffViewHotkeyCommand>();
        hotkeys.Pressed += received.Add;
        var profile = Profile();
        hotkeys.Configure(profile, Clients(), false);
        hotkeys.Configure(profile, Clients(), true);
        Assert.Equal("COMMIT\t0", transport.Messages.Last());
        transport.Press(1, transport.Generation);
        Assert.Empty(received);
        profile.Id = "second";
        profile.CycleGroups[0].ForwardGestures = ["*F18"];
        hotkeys.Configure(profile, Clients(), false);
        Assert.Contains(transport.Messages, text => text.EndsWith("\t*F18"));
        Assert.Equal("COMMIT\t1", transport.Messages.Last());
        transport.Press(1, transport.Generation);
        Assert.Single(received);
        hotkeys.Dispose();
        hotkeys.Dispose();
        transport.Press(1, transport.Generation);
        Assert.Single(received);
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public void StartupAndRestartApplyLatestConfigurationAndReportFailure()
    {
        var transport = new FakeTransport { IsReady = false, Failure = "Missing runtime" };
        using var hotkeys = new TriffViewAhkHotkeys(transport);
        var profile = Profile();
        hotkeys.Configure(profile, Clients(), false);
        Assert.Contains("Missing runtime", hotkeys.Failures);
        profile.CycleGroups[0].ForwardGestures = ["*F19"];
        hotkeys.Configure(profile, Clients(), false);
        transport.Connect();
        Assert.Empty(hotkeys.Failures);
        Assert.Contains(transport.Messages, text => text.EndsWith("\t*F19"));
        var count = transport.Messages.Count;
        transport.Connect();
        Assert.True(transport.Messages.Count > count);
    }

    [Fact]
    public void ScopeAndReplacementClientHandleCauseReload()
    {
        var transport = new FakeTransport();
        using var hotkeys = new TriffViewAhkHotkeys(transport);
        var profile = Profile();
        hotkeys.Configure(profile, Clients(), false);
        var generation = transport.Generation;
        profile.HotkeysRequireEveForeground = !profile.HotkeysRequireEveForeground;
        hotkeys.Configure(profile, Clients(), false);
        Assert.NotEqual(generation, transport.Generation);
        generation = transport.Generation;
        hotkeys.Configure(profile, [Clients()[0] with { Handle = 999 }], false);
        Assert.NotEqual(generation, transport.Generation);
    }

    internal static TriffViewProfile Profile() => new()
    {
        Id = "default", Name = "Default", CharacterOrder = ["One", "Two"],
        CycleGroups = [new() { Id = "all", Name = "All", Characters = ["One", "Two"], ForwardGestures = ["*F14"], BackwardGestures = ["*F15"] }],
    };

    internal static EveClientWindow[] Clients() =>
        [new((nint)101, "EVE - One", "One", 1, false, false), new((nint)102, "EVE - Two", "Two", 2, false, false)];

    private sealed class FakeTransport : IAhkTransport
    {
        public event Action? Ready;
        public event Action<int, int>? Pressed;
        public bool IsReady { get; set; } = true;
        public string? Failure { get; set; }
        public List<string> Messages { get; } = [];
        public int Generation { get; private set; }
        public int Disposals { get; private set; }
        public Func<string, bool> Accept { get; set; } = _ => true;
        public List<(nint, bool)> Activations { get; } = [];
        public void EnsureRunning() { }
        public bool Send(string message)
        {
            Messages.Add(message);
            if (message.StartsWith("RESET\t")) Generation = int.Parse(message.Split('\t')[1]);
            return Accept(message);
        }
        public bool Activate(nint handle, bool maximize) { Activations.Add((handle, maximize)); return true; }
        public void Press(int id, int generation) => Pressed?.Invoke(id, generation);
        public void Connect() { IsReady = true; Failure = null; Ready?.Invoke(); }
        public void Dispose() => Disposals++;
    }
}
