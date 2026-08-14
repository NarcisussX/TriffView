using System.Drawing;
using TriffView;
using TriffView.Preview;

var tests = new (string Name, Action Run)[]
{
    ("named character to character select keeps position", NamedToCharacterSelect),
    ("character select to named character keeps position", CharacterSelectToNamed),
    ("saved current key wins over remembered and title positions", SavedLayoutWins),
    ("reused HWND with a different PID gets no old position", ReusedHandleDoesNotInherit),
    ("dead identities are purged", DeadIdentitiesArePurged),
    ("profile, dimensions, and complete monitor topology invalidate memory", ContextChangesInvalidate),
    ("hidden active preview keeps its live position", HiddenActivePreviewKeepsPosition),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void NamedToCharacterSelect()
{
    var memory = NewMemory();
    var identity = new PreviewClientIdentity((nint)0x101, 42);
    var position = new Rectangle(100, 200, 320, 204);
    memory.Remember(identity, position);

    Equal(position, ResolveRemembered(memory, identity), "character-select transition");
}

static void CharacterSelectToNamed()
{
    var memory = NewMemory();
    var identity = new PreviewClientIdentity((nint)0x102, 43);
    var position = new Rectangle(220, 120, 320, 204);
    memory.Remember(identity, position);

    Equal(position, ResolveRemembered(memory, identity), "named-character transition");
}

static void SavedLayoutWins()
{
    var saved = new Rectangle(10, 20, 300, 180);
    var remembered = new Rectangle(30, 40, 300, 180);
    var title = new Rectangle(50, 60, 300, 180);
    var fallback = new Rectangle(70, 80, 300, 180);

    Equal(saved, TriffViewPreviewPositionMemory.Resolve(saved, remembered, title, fallback), "saved precedence");
    Equal(remembered, TriffViewPreviewPositionMemory.Resolve(null, remembered, title, fallback), "remembered precedence");
    Equal(title, TriffViewPreviewPositionMemory.Resolve(null, null, title, fallback), "title precedence");
    Equal(fallback, TriffViewPreviewPositionMemory.Resolve(null, null, null, fallback), "default precedence");
}

static void ReusedHandleDoesNotInherit()
{
    var memory = NewMemory();
    var oldIdentity = new PreviewClientIdentity((nint)0x201, 51);
    var newIdentity = new PreviewClientIdentity((nint)0x201, 52);
    memory.Remember(oldIdentity, new Rectangle(1, 2, 3, 4));
    memory.PurgeExcept(new HashSet<PreviewClientIdentity> { newIdentity });

    False(memory.TryGet(oldIdentity, out _), "old identity should be purged");
    False(memory.TryGet(newIdentity, out _), "new PID should not inherit the old HWND position");
}

static void DeadIdentitiesArePurged()
{
    var memory = NewMemory();
    var live = new PreviewClientIdentity((nint)0x301, 61);
    var dead = new PreviewClientIdentity((nint)0x302, 62);
    memory.Remember(live, new Rectangle(1, 1, 10, 10));
    memory.Remember(dead, new Rectangle(2, 2, 10, 10));
    memory.PurgeExcept(new HashSet<PreviewClientIdentity> { live });

    True(memory.TryGet(live, out _), "live identity should remain");
    False(memory.TryGet(dead, out _), "dead identity should be removed");
}

static void ContextChangesInvalidate()
{
    var firstMonitor = new ScreenPixelInfo("DISPLAY1", true, new Rectangle(0, 0, 1920, 1080));
    var secondMonitor = new ScreenPixelInfo("DISPLAY2", false, new Rectangle(1920, 0, 1920, 1080));
    var identity = new PreviewClientIdentity((nint)0x401, 71);
    var memory = new TriffViewPreviewPositionMemory();

    memory.BeginContext("profile-a", 320, 204, new[] { firstMonitor, secondMonitor });
    memory.Remember(identity, new Rectangle(5, 5, 320, 204));
    True(memory.BeginContext("profile-b", 320, 204, new[] { firstMonitor, secondMonitor }), "profile change");
    False(memory.TryGet(identity, out _), "profile change should clear memory");

    memory.Remember(identity, new Rectangle(5, 5, 320, 204));
    True(memory.BeginContext("profile-b", 400, 204, new[] { firstMonitor, secondMonitor }), "dimension change");
    False(memory.TryGet(identity, out _), "dimension change should clear memory");

    memory.Remember(identity, new Rectangle(5, 5, 400, 204));
    var movedSecondMonitor = secondMonitor with { Bounds = new Rectangle(-1920, 0, 1920, 1080) };
    True(memory.BeginContext("profile-b", 400, 204, new[] { firstMonitor, movedSecondMonitor }), "secondary topology change");
    False(memory.TryGet(identity, out _), "secondary monitor change should clear memory");
}

static void HiddenActivePreviewKeepsPosition()
{
    var memory = NewMemory();
    var hiddenIdentity = new PreviewClientIdentity((nint)0x501, 81);
    var otherIdentity = new PreviewClientIdentity((nint)0x502, 82);
    var position = new Rectangle(450, 250, 320, 204);
    memory.Remember(hiddenIdentity, position);
    memory.PurgeExcept(new HashSet<PreviewClientIdentity> { hiddenIdentity, otherIdentity });

    Equal(position, ResolveRemembered(memory, hiddenIdentity), "hidden active recreation");
}

static TriffViewPreviewPositionMemory NewMemory()
{
    var memory = new TriffViewPreviewPositionMemory();
    memory.BeginContext(
        "profile-a",
        320,
        204,
        new[] { new ScreenPixelInfo("DISPLAY1", true, new Rectangle(0, 0, 1920, 1080)) });
    return memory;
}

static Rectangle ResolveRemembered(TriffViewPreviewPositionMemory memory, PreviewClientIdentity identity)
{
    True(memory.TryGet(identity, out var remembered), "remembered position should exist");
    return TriffViewPreviewPositionMemory.Resolve(
        savedForCurrentKey: null,
        rememberedForClient: remembered,
        titleFallback: new Rectangle(20, 20, 100, 100),
        defaultStack: new Rectangle(30, 30, 100, 100));
}

static void Equal(Rectangle expected, Rectangle actual, string message)
{
    if (expected != actual) throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void False(bool condition, string message) => True(!condition, message);
