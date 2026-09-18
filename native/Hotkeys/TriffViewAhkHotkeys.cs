namespace TriffView.Preview;

internal sealed record AhkBinding(int Id, string Gesture, TriffViewHotkeyCommand Command, bool NoRepeat);

internal interface IAhkTransport : IDisposable
{
    event Action? Ready;
    event Action<int, int>? Pressed;
    bool IsReady { get; }
    string? Failure { get; }
    void EnsureRunning();
    bool Send(string message);
    bool Activate(nint handle, bool maximize);
}

// All methods run on the UI thread. The helper never receives character names or cycle cursors.
internal sealed class TriffViewAhkHotkeys : IDisposable
{
    private readonly IAhkTransport _transport;
    private string _signature = "";
    private int _generation = Random.Shared.Next(1, int.MaxValue / 2);
    private bool _suspended;
    private bool _requireForeground;
    private bool _disposed;
    private nint[] _handles = [];
    private readonly Dictionary<int, AhkBinding> _bindings = new();
    private readonly HashSet<int> _registered = new();
    private readonly List<string> _planningFailures = new();
    public IReadOnlyList<string> Failures { get; private set; } = Array.Empty<string>();
    public event Action<TriffViewHotkeyCommand>? Pressed;
    public event Action? StatusChanged;

    public TriffViewAhkHotkeys(IAhkTransport transport)
    {
        _transport = transport;
        transport.Ready += Apply;
        transport.Pressed += Dispatch;
    }

    public void Configure(TriffViewProfile profile, IReadOnlyList<EveClientWindow> clients, bool suspended)
    {
        if (_disposed) return;
        var signature = TriffViewOverlayForm.HotkeySignature(profile, clients, suspended);
        if (_signature != signature)
        {
            _signature = signature;
            _generation++;
            _suspended = suspended;
            _requireForeground = profile.HotkeysRequireEveForeground;
            _handles = clients.Select(client => client.Handle).ToArray();
            _bindings.Clear();
            _planningFailures.Clear();
            if (!suspended)
                foreach (var binding in Plan(profile, clients, _planningFailures)) _bindings.Add(binding.Id, binding);
            if (_transport.IsReady) Apply();
        }
        _transport.EnsureRunning();
        if (_transport.Failure is { } failure) SetFailures([.. _planningFailures, failure]);
    }

    internal static IReadOnlyList<AhkBinding> Plan(TriffViewProfile profile, IReadOnlyList<EveClientWindow> clients, IList<string> failures)
    {
        var result = new List<AhkBinding>();
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directGroups = profile.DirectHotkeys
            .Where(binding => binding.Enabled && clients.Any(client =>
                string.Equals(client.CharacterName, binding.CharacterName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(client.StableKey, binding.CharacterName, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(binding => binding.Gestures.Select(gesture => (binding.CharacterName, Gesture: gesture)))
            .GroupBy(binding => AhkGesture.Identity(binding.Gesture), StringComparer.OrdinalIgnoreCase);
        foreach (var group in directGroups)
        {
            claimed[group.Key] = "direct character hotkey";
            Add(group.First().Gesture, new(TriffViewHotkeyKind.Direct, "", "", 0,
                group.Select(binding => binding.CharacterName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        foreach (var registration in TriffViewCycleHotkeyPlanner.Plan(profile, claimed, failures))
            Add(registration.Gesture, new(TriffViewHotkeyKind.Cycle, "", registration.GroupId, registration.Direction));
        return result;

        void Add(string gesture, TriffViewHotkeyCommand command)
        {
            if (string.IsNullOrWhiteSpace(gesture)) return;
            if (gesture.Any(char.IsControl)) { failures.Add($"{gesture}: invalid hotkey text"); return; }
            result.Add(new(result.Count + 1, AhkGesture.Translate(gesture), command, AhkGesture.IsNoRepeat(gesture)));
        }
    }

    private void Apply()
    {
        if (_disposed) return;
        _registered.Clear();
        _generation++;
        var failures = new List<string>(_planningFailures);
        // RESET disables the old generation before any new binding is installed; COMMIT enables it.
        if (!_transport.Send($"RESET\t{_generation}\t{(_requireForeground ? 1 : 0)}\t{string.Join(',', _handles)}"))
        {
            SetFailures([.. failures, "AutoHotkey: could not reload hotkeys"]);
            return;
        }
        foreach (var binding in _bindings.Values.ToArray())
        {
            if (!_transport.Send($"BIND\t{binding.Id}\t{(binding.NoRepeat ? 1 : 0)}\t{binding.Gesture}"))
            {
                failures.Add($"{binding.Gesture}: AutoHotkey could not register hotkey (invalid, duplicate or reserved binding)");
            }
            else _registered.Add(binding.Id);
        }
        if (!_transport.Send($"COMMIT\t{(_suspended ? 0 : 1)}"))
        {
            _registered.Clear();
            failures.Add("AutoHotkey: could not enable hotkeys");
        }
        SetFailures(failures);
    }

    private void Dispatch(int id, int generation)
    {
        if (!_disposed && !_suspended && _transport.IsReady && generation == _generation && _registered.Contains(id) && _bindings.TryGetValue(id, out var binding))
            Pressed?.Invoke(binding.Command);
    }

    public bool Activate(nint handle, bool maximize) => !_disposed && !_suspended && _transport.Activate(handle, maximize);

    private void SetFailures(IReadOnlyList<string> failures)
    {
        if (Failures.SequenceEqual(failures)) return;
        Failures = failures;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _bindings.Clear();
        _transport.Ready -= Apply;
        _transport.Pressed -= Dispatch;
        _transport.Dispose();
    }
}
