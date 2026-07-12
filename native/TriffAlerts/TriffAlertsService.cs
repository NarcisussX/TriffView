using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TriffView.Alerts;

public sealed class TriffAlertsSettings
{
    private const int CurrentDefaultsVersion = 2;

    public int DefaultsVersion { get; set; }
    public bool Enabled { get; set; }
    public bool PveMode { get; set; } = true;
    public double MasterVolume { get; set; } = 0.75;
    public Dictionary<string, TriffAlertEventConfig> Events { get; set; } = CreateDefaultEvents();

    public static TriffAlertsSettings CreateDefault()
    {
        var settings = new TriffAlertsSettings();
        settings.Normalize();
        return settings;
    }

    public void Normalize()
    {
        MasterVolume = Math.Max(0, Math.Min(1, MasterVolume));
        Events = new Dictionary<string, TriffAlertEventConfig>(Events ?? new Dictionary<string, TriffAlertEventConfig>(), StringComparer.OrdinalIgnoreCase);

        var defaultsByType = CreateDefaultEvents();
        var legacyDefaultsByType = DefaultsVersion < CurrentDefaultsVersion ? CreateLegacyDefaultEventsV1() : new Dictionary<string, TriffAlertEventConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var (type, defaults) in defaultsByType)
        {
            if (!Events.TryGetValue(type, out var existing) || existing == null)
            {
                Events[type] = defaults;
                continue;
            }

            existing.Type = type;
            if (legacyDefaultsByType.TryGetValue(type, out var legacyDefaults))
            {
                existing.MigrateFromLegacyDefaults(legacyDefaults, defaults);
            }
            existing.Normalize(defaults);
        }

        DefaultsVersion = CurrentDefaultsVersion;
    }

    public TriffAlertEventConfig Event(string type)
    {
        Normalize();
        return Events.TryGetValue(type, out var config) ? config : CreateDefaultEvents()[type];
    }

    public object ToState()
    {
        Normalize();
        return new
        {
            defaultsVersion = DefaultsVersion,
            enabled = Enabled,
            pveMode = PveMode,
            masterVolume = MasterVolume,
            events = Events.ToDictionary(
                item => item.Key,
                item => item.Value.ToState(),
                StringComparer.OrdinalIgnoreCase
            ),
        };
    }

    private static Dictionary<string, TriffAlertEventConfig> CreateDefaultEvents()
    {
        return new Dictionary<string, TriffAlertEventConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["attack"] = new()
            {
                Type = "attack",
                Label = "Attack",
                Enabled = true,
                Severity = TriffAlertSeverity.Critical,
                CooldownSeconds = 1,
                FlashEnabled = true,
                FlashColor = "#FF3B3B",
                FlashThickness = 24,
                FlashDurationMs = 900,
                FlashPulseCount = 2,
            },
            ["warp_scramble"] = new()
            {
                Type = "warp_scramble",
                Label = "Warp scramble",
                Enabled = true,
                Severity = TriffAlertSeverity.Warning,
                CooldownSeconds = 8,
                FlashEnabled = true,
                FlashColor = "#737B8C",
                FlashThickness = 24,
                FlashDurationMs = 400,
                FlashPulseCount = 1,
            },
            ["decloak"] = new()
            {
                Type = "decloak",
                Label = "Decloak",
                Enabled = true,
                Severity = TriffAlertSeverity.Critical,
                CooldownSeconds = 8,
                FlashEnabled = true,
                FlashColor = "#FFCC4D",
                FlashThickness = 24,
                FlashDurationMs = 4500,
                FlashPulseCount = 6,
            },
            ["fleet_invite"] = new()
            {
                Type = "fleet_invite",
                Label = "Fleet invite",
                Enabled = true,
                Severity = TriffAlertSeverity.Info,
                CooldownSeconds = 10,
                FlashEnabled = true,
                FlashColor = "#53B6FF",
                FlashThickness = 24,
                FlashDurationMs = 400,
                FlashPulseCount = 1,
            },
            ["convo_request"] = new()
            {
                Type = "convo_request",
                Label = "Convo request",
                Enabled = true,
                Severity = TriffAlertSeverity.Info,
                CooldownSeconds = 10,
                FlashEnabled = true,
                FlashColor = "#B58CFF",
                FlashThickness = 24,
                FlashDurationMs = 400,
                FlashPulseCount = 1,
            },
            ["system_change"] = new()
            {
                Type = "system_change",
                Label = "System change",
                Enabled = true,
                Severity = TriffAlertSeverity.Info,
                CooldownSeconds = 10,
                FlashEnabled = true,
                FlashColor = "#52FF54",
                FlashThickness = 24,
                FlashDurationMs = 400,
                FlashPulseCount = 1,
            },
        };
    }

    private static Dictionary<string, TriffAlertEventConfig> CreateLegacyDefaultEventsV1()
    {
        return new Dictionary<string, TriffAlertEventConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["attack"] = new()
            {
                Severity = TriffAlertSeverity.Critical,
                CooldownSeconds = 5,
                FlashColor = "#FF3B3B",
                FlashThickness = 6,
                FlashDurationMs = 4500,
                FlashPulseCount = 6,
            },
            ["warp_scramble"] = new()
            {
                Severity = TriffAlertSeverity.Critical,
                CooldownSeconds = 8,
                FlashColor = "#FF2F7D",
                FlashThickness = 6,
                FlashDurationMs = 5200,
                FlashPulseCount = 7,
            },
            ["decloak"] = new()
            {
                Severity = TriffAlertSeverity.Critical,
                CooldownSeconds = 8,
                FlashColor = "#FFB84D",
                FlashThickness = 5,
                FlashDurationMs = 4500,
                FlashPulseCount = 6,
            },
            ["fleet_invite"] = new()
            {
                Severity = TriffAlertSeverity.Warning,
                CooldownSeconds = 10,
                FlashColor = "#FFD166",
                FlashThickness = 4,
                FlashDurationMs = 3500,
                FlashPulseCount = 4,
            },
            ["convo_request"] = new()
            {
                Severity = TriffAlertSeverity.Warning,
                CooldownSeconds = 10,
                FlashColor = "#B58CFF",
                FlashThickness = 4,
                FlashDurationMs = 3500,
                FlashPulseCount = 4,
            },
            ["system_change"] = new()
            {
                Severity = TriffAlertSeverity.Info,
                CooldownSeconds = 1,
                FlashColor = "#53B6FF",
                FlashThickness = 3,
                FlashDurationMs = 2200,
                FlashPulseCount = 3,
            },
        };
    }
}

public sealed class TriffAlertEventConfig
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Severity { get; set; } = TriffAlertSeverity.Info;
    public int CooldownSeconds { get; set; } = 5;
    public bool FlashEnabled { get; set; } = true;
    public string FlashColor { get; set; } = "#53B6FF";
    public int FlashThickness { get; set; } = 4;
    public int FlashDurationMs { get; set; } = 3000;
    public int FlashPulseCount { get; set; } = 4;
    public string Sound { get; set; } = "none";
    public bool TrayNotification { get; set; }

    [JsonIgnore]
    public int SeverityRank => TriffAlertSeverity.Rank(Severity);

    public void Normalize(TriffAlertEventConfig defaults)
    {
        Type = string.IsNullOrWhiteSpace(Type) ? defaults.Type : Type.Trim();
        Label = string.IsNullOrWhiteSpace(Label) ? defaults.Label : Label.Trim();
        Severity = TriffAlertSeverity.Normalize(Severity, defaults.Severity);
        CooldownSeconds = Math.Max(0, Math.Min(120, CooldownSeconds));
        FlashColor = NormalizeColor(FlashColor, defaults.FlashColor);
        FlashThickness = Math.Max(1, Math.Min(24, FlashThickness));
        FlashDurationMs = Math.Max(250, Math.Min(15000, FlashDurationMs));
        FlashPulseCount = Math.Max(1, Math.Min(16, FlashPulseCount));
        Sound = NormalizeSound(Sound);
    }

    public void MigrateFromLegacyDefaults(TriffAlertEventConfig legacyDefaults, TriffAlertEventConfig newDefaults)
    {
        if (string.Equals(Severity, legacyDefaults.Severity, StringComparison.OrdinalIgnoreCase)) Severity = newDefaults.Severity;
        if (CooldownSeconds == legacyDefaults.CooldownSeconds) CooldownSeconds = newDefaults.CooldownSeconds;
        if (string.Equals(FlashColor, legacyDefaults.FlashColor, StringComparison.OrdinalIgnoreCase)) FlashColor = newDefaults.FlashColor;
        if (FlashThickness == legacyDefaults.FlashThickness) FlashThickness = newDefaults.FlashThickness;
        if (FlashDurationMs == legacyDefaults.FlashDurationMs) FlashDurationMs = newDefaults.FlashDurationMs;
        if (FlashPulseCount == legacyDefaults.FlashPulseCount) FlashPulseCount = newDefaults.FlashPulseCount;
    }

    public object ToState()
    {
        return new
        {
            type = Type,
            label = Label,
            enabled = Enabled,
            severity = Severity,
            cooldownSeconds = CooldownSeconds,
            flashEnabled = FlashEnabled,
            flashColor = FlashColor,
            flashThickness = FlashThickness,
            flashDurationMs = FlashDurationMs,
            flashPulseCount = FlashPulseCount,
            sound = Sound,
            trayNotification = TrayNotification,
        };
    }

    private static string NormalizeSound(string? sound)
    {
        var clean = (sound ?? "none").Trim().ToLowerInvariant();
        return clean is "none" or "alarm" or "woop" or "siren" or "ding" ? clean : "none";
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        var clean = (value ?? "").Trim();
        if (Regex.IsMatch(clean, "^#[0-9a-fA-F]{6}$")) return clean.ToUpperInvariant();
        return fallback;
    }
}

public static class TriffAlertSeverity
{
    public const string Critical = "critical";
    public const string Warning = "warning";
    public const string Info = "info";

    public static string Normalize(string? value, string fallback = Info)
    {
        var clean = (value ?? fallback).Trim().ToLowerInvariant();
        return clean is Critical or Warning or Info ? clean : fallback;
    }

    public static int Rank(string? value)
    {
        return Normalize(value) switch
        {
            Critical => 3,
            Warning => 2,
            _ => 1,
        };
    }
}

public sealed class TriffAlertEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public string Severity { get; set; } = TriffAlertSeverity.Info;
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool Test { get; set; }

    [JsonIgnore]
    public int SeverityRank => TriffAlertSeverity.Rank(Severity);

    public object ToState()
    {
        return new
        {
            id = Id,
            type = Type,
            label = Label,
            characterName = CharacterName,
            severity = Severity,
            source = Source,
            message = Message,
            timestamp = TimestampUtc.ToString("O"),
            test = Test,
        };
    }
}

public sealed class TriffAlertsService : IDisposable
{
    private const int MaxTrackedFiles = 200;
    private const int MaxHistory = 200;
    private static readonly TimeSpan InitialLogCutoff = TimeSpan.FromHours(12);
    private static readonly TimeSpan CharacterInactiveRetireAge = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan HardRetireAge = TimeSpan.FromHours(18);
    private static readonly TimeSpan RecoveryDiscoveryInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly string _gamelogsPath;
    private readonly Dictionary<string, LogFileState> _logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _latestLogByCharacter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TriffAlertEvent> _history = new();
    private readonly HashSet<string> _activeCharacters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<LogFileChange> _changedPaths = new();
    private readonly ConcurrentQueue<TriffAlertEvent> _pendingNotifications = new();
    private System.Threading.Timer? _pollTimer;
    private FileSystemWatcher? _watcher;
    private TriffAlertsSettings _settings = TriffAlertsSettings.CreateDefault();
    private bool _disposed;
    private bool _discoveryRequested = true;
    private bool _monitoring;
    private DateTime _monitorStartedUtc = DateTime.MinValue;
    private DateTime _lastDiscoveryUtc = DateTime.MinValue;
    private int _scanInProgress;
    private int _fastReadScheduled;
    private int _notificationDispatchScheduled;

    public TriffAlertsService(string? gamelogsPath = null)
    {
        _gamelogsPath = string.IsNullOrWhiteSpace(gamelogsPath)
            ? DefaultGamelogsPath
            : Path.GetFullPath(gamelogsPath);
    }

    public event EventHandler<TriffAlertEvent>? AlertTriggered;

    public IReadOnlyList<TriffAlertEvent> History
    {
        get
        {
            lock (_gate)
            {
                return _history.Select(CloneEvent).ToArray();
            }
        }
    }

    public void UpdateSettings(TriffAlertsSettings settings)
    {
        lock (_gate)
        {
            _settings = settings ?? TriffAlertsSettings.CreateDefault();
            _settings.Normalize();
            if (_settings.Enabled) StartMonitoringLocked();
            else StopMonitoringLocked();
        }
    }

    public void SetActiveCharacters(IEnumerable<string> names)
    {
        lock (_gate)
        {
            _activeCharacters.Clear();
            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name)) _activeCharacters.Add(name.Trim());
            }
        }
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
        }
    }

    public void TestAlert(string? type, string? characterName)
    {
        var cleanType = string.IsNullOrWhiteSpace(type) ? "attack" : type.Trim();
        var cleanCharacter = string.IsNullOrWhiteSpace(characterName) ? "Test Pilot" : characterName.Trim();
        TriffAlertEvent? alert;
        lock (_gate)
        {
            _settings.Normalize();
            alert = BuildEvent(cleanType, cleanCharacter, "TriffAlerts test", "Preview flash test", test: true);
            AppendHistoryLocked(alert);
        }

        AlertTriggered?.Invoke(this, alert);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopMonitoringLocked();
            _logs.Clear();
        }
    }

    private void StartMonitoringLocked()
    {
        if (_monitoring || _disposed) return;
        _monitoring = true;
        _monitorStartedUtc = DateTime.UtcNow;
        _discoveryRequested = true;
        Directory.CreateDirectory(_gamelogsPath);

        _watcher = new FileSystemWatcher(_gamelogsPath, "*.txt")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, eventArgs) => QueueFastRead(eventArgs.FullPath, liveFromStart: true);
        _watcher.Changed += (_, eventArgs) => QueueFastRead(eventArgs.FullPath, liveFromStart: false);
        _watcher.Renamed += (_, eventArgs) => QueueFastRead(eventArgs.FullPath, liveFromStart: true);
        _watcher.Error += (_, _) => RequestRecoveryDiscovery();
        _watcher.EnableRaisingEvents = true;

        try
        {
            ScanRecentLogsLocked();
            ReadTrackedLogsLocked();
            RetireStaleLogsLocked();
            _lastDiscoveryUtc = DateTime.UtcNow;
            _discoveryRequested = false;
        }
        catch
        {
            _discoveryRequested = true;
        }
        _pollTimer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1));
    }

    private void StopMonitoringLocked()
    {
        _monitoring = false;
        _watcher?.Dispose();
        _watcher = null;
        _pollTimer?.Dispose();
        _pollTimer = null;
        _logs.Clear();
        _latestLogByCharacter.Clear();
        _cooldowns.Clear();
        while (_changedPaths.TryDequeue(out _)) { }
    }

    private void QueueFastRead(string path, bool liveFromStart)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _changedPaths.Enqueue(new LogFileChange(path, liveFromStart));
        ScheduleFastRead();
    }

    private void RequestRecoveryDiscovery()
    {
        lock (_gate)
        {
            if (_disposed || !_monitoring) return;
            _discoveryRequested = true;
        }
    }

    private void ScheduleFastRead()
    {
        if (Interlocked.CompareExchange(ref _fastReadScheduled, 1, 0) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(static service => service.DrainChangedPaths(), this, preferLocal: false);
    }

    private void DrainChangedPaths()
    {
        try
        {
            while (true)
            {
                var pending = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                while (_changedPaths.TryDequeue(out var change))
                {
                    pending[change.Path] = pending.TryGetValue(change.Path, out var existing)
                        ? existing || change.LiveFromStart
                        : change.LiveFromStart;
                }

                if (pending.Count == 0) return;

                lock (_gate)
                {
                    if (_disposed || !_monitoring || !_settings.Enabled) return;

                    foreach (var (path, liveFromStart) in pending)
                    {
                        TrackFileLocked(path, liveFromStart || IsFileFromCurrentMonitoringSession(path));
                    }

                    ReadTrackedLogsLocked(pending.Keys);
                    RetireStaleLogsLocked();
                }
            }
        }
        catch
        {
            RequestRecoveryDiscovery();
        }
        finally
        {
            Interlocked.Exchange(ref _fastReadScheduled, 0);
            if (!_changedPaths.IsEmpty) ScheduleFastRead();
        }
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _scanInProgress, 1) == 1) return;
        try
        {
            lock (_gate)
            {
                if (_disposed || !_monitoring || !_settings.Enabled) return;
                if (_discoveryRequested || DateTime.UtcNow - _lastDiscoveryUtc > RecoveryDiscoveryInterval)
                {
                    ScanRecentLogsLocked();
                    _lastDiscoveryUtc = DateTime.UtcNow;
                    _discoveryRequested = false;
                }

                ReadTrackedLogsLocked();
                RetireStaleLogsLocked();
            }
        }
        catch
        {
            // Log monitoring is best-effort and must never take down the app.
        }
        finally
        {
            Interlocked.Exchange(ref _scanInProgress, 0);
        }
    }

    private void ScanRecentLogsLocked()
    {
        if (!Directory.Exists(_gamelogsPath)) return;

        var cutoff = DateTime.UtcNow - InitialLogCutoff;
        foreach (var file in Directory.EnumerateFiles(_gamelogsPath, "*.txt")
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTrackedFiles)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.LastWriteTimeUtc >= cutoff)
            .OrderByDescending(file => file.CreationTimeUtc))
        {
            TrackFileLocked(file.FullName, liveFromStart: false);
        }
    }

    private bool IsFileFromCurrentMonitoringSession(string path)
    {
        try
        {
            var createdUtc = File.GetCreationTimeUtc(path);
            return createdUtc >= _monitorStartedUtc - TimeSpan.FromSeconds(2);
        }
        catch
        {
            return false;
        }
    }

    private void TrackFileLocked(string path, bool liveFromStart)
    {
        if (string.IsNullOrWhiteSpace(path) || _logs.ContainsKey(path)) return;
        if (!File.Exists(path)) return;

        var info = new FileInfo(path);
        if (info.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) == false) return;
        _logs[path] = new LogFileState(path)
        {
            Position = liveFromStart ? 0 : info.Length,
            SessionStartedUtc = info.CreationTimeUtc,
            LastWriteUtc = info.LastWriteTimeUtc,
        };
    }

    private void ReadTrackedLogsLocked(IEnumerable<string>? paths = null)
    {
        var states = paths == null
            ? _logs.Values.ToArray()
            : paths.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => _logs.TryGetValue(path, out var state) ? state : null)
                .Where(state => state != null)
                .Cast<LogFileState>()
                .ToArray();

        foreach (var state in states)
        {
            if (!File.Exists(state.Path))
            {
                RemoveTrackedLogLocked(state.Path);
                continue;
            }

            var info = new FileInfo(state.Path);
            state.LastWriteUtc = info.LastWriteTimeUtc;
            if (info.Length <= state.Position && state.CharacterName.Length > 0) continue;

            try
            {
                using var stream = new FileStream(state.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < state.Position)
                {
                    state.Position = 0;
                    state.PartialLine = "";
                    state.CharacterName = "";
                }

                if (state.CharacterName.Length == 0)
                {
                    if (TryReadHeader(stream, out var listener))
                    {
                        state.CharacterName = listener;
                        if (!RegisterCharacterSessionLocked(state)) continue;
                    }
                    else
                    {
                        state.HeaderAttempts++;
                        if (state.HeaderAttempts > 12 || DateTime.UtcNow - state.FirstSeenUtc > TimeSpan.FromMinutes(2))
                        {
                            RemoveTrackedLogLocked(state.Path);
                        }
                        continue;
                    }
                }

                stream.Position = Math.Min(state.Position, stream.Length);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                var appended = reader.ReadToEnd();
                state.Position = stream.Length;
                if (string.IsNullOrEmpty(appended)) continue;

                ProcessTextLocked(state, appended);
            }
            catch (IOException)
            {
                // EVE may be rotating or writing the file. The polling pass will pick it up next tick.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above; do not tear down monitoring for a transient file lock.
            }
        }
    }

    private bool RegisterCharacterSessionLocked(LogFileState candidate)
    {
        if (!_latestLogByCharacter.TryGetValue(candidate.CharacterName, out var currentPath)
            || !_logs.TryGetValue(currentPath, out var current))
        {
            _latestLogByCharacter[candidate.CharacterName] = candidate.Path;
            return true;
        }

        if (string.Equals(current.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)) return true;

        var comparison = candidate.SessionStartedUtc.CompareTo(current.SessionStartedUtc);
        if (comparison == 0)
        {
            comparison = string.Compare(candidate.Path, current.Path, StringComparison.OrdinalIgnoreCase);
        }

        if (comparison <= 0)
        {
            _logs.Remove(candidate.Path);
            return false;
        }

        _latestLogByCharacter[candidate.CharacterName] = candidate.Path;
        _logs.Remove(current.Path);
        return true;
    }

    private void RemoveTrackedLogLocked(string path)
    {
        if (!_logs.Remove(path, out var removed)) return;
        if (removed.CharacterName.Length == 0) return;
        if (!_latestLogByCharacter.TryGetValue(removed.CharacterName, out var latest)
            || !string.Equals(latest, path, StringComparison.OrdinalIgnoreCase)) return;

        _latestLogByCharacter.Remove(removed.CharacterName);
        var replacement = _logs.Values
            .Where(state => string.Equals(state.CharacterName, removed.CharacterName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(state => state.SessionStartedUtc)
            .ThenByDescending(state => state.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (replacement != null) _latestLogByCharacter[removed.CharacterName] = replacement.Path;
    }

    private void ProcessTextLocked(LogFileState state, string text)
    {
        var combined = state.PartialLine + text;
        var endsWithNewline = combined.EndsWith('\n') || combined.EndsWith('\r');
        var lines = combined.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var limit = endsWithNewline ? lines.Length : Math.Max(0, lines.Length - 1);

        for (var index = 0; index < limit; index++)
        {
            var line = lines[index].TrimEnd();
            if (line.Length == 0) continue;
            var alert = ParseLine(state.CharacterName, line);
            if (alert != null) EmitLocked(alert);
        }

        state.PartialLine = endsWithNewline ? "" : lines.LastOrDefault() ?? "";
    }

    private void RetireStaleLogsLocked()
    {
        var now = DateTime.UtcNow;
        foreach (var state in _logs.Values.ToArray())
        {
            if (state.CharacterName.Length > 0
                && _latestLogByCharacter.TryGetValue(state.CharacterName, out var latest)
                && !string.Equals(latest, state.Path, StringComparison.OrdinalIgnoreCase))
            {
                RemoveTrackedLogLocked(state.Path);
                continue;
            }

            var inactiveAge = now - state.LastWriteUtc;
            if (inactiveAge > HardRetireAge)
            {
                RemoveTrackedLogLocked(state.Path);
                continue;
            }

            if (_activeCharacters.Count > 0
                && state.CharacterName.Length > 0
                && !_activeCharacters.Contains(state.CharacterName)
                && inactiveAge > CharacterInactiveRetireAge)
            {
                RemoveTrackedLogLocked(state.Path);
            }
        }
    }

    private TriffAlertEvent? ParseLine(string characterName, string rawLine)
    {
        var line = rawLine.Trim();
        var lower = line.ToLowerInvariant();

        if (IsIncomingDamageLine(lower, line, out var damageSource, out var damageMessage))
        {
            if (_settings.PveMode && IsLikelyNpcSource(damageSource)) return null;
            return BuildEvent("attack", characterName, damageSource, damageMessage);
        }

        if (IsMissLine(lower, line, out var missSource))
        {
            if (_settings.PveMode && IsLikelyNpcSource(missSource)) return null;
            return BuildEvent("attack", characterName, missSource, "Incoming attack missed you");
        }

        if (lower.Contains("warp scramble attempt") || lower.Contains("warp disruption attempt") || lower.Contains("warp disruption zone"))
        {
            return BuildEvent("warp_scramble", characterName, ExtractSource(line), "Warp disruption detected");
        }

        if (lower.Contains("(notify)") && lower.Contains("cloak deactivates"))
        {
            return BuildEvent("decloak", characterName, ExtractSource(line), StripMarkup(line));
        }

        if (lower.Contains("(question)") && lower.Contains("join their fleet"))
        {
            return BuildEvent("fleet_invite", characterName, ExtractAnchorText(line), "Fleet invite received");
        }

        if (lower.Contains("conversation") && (lower.Contains("invite") || lower.Contains("inviting") || lower.Contains("wants to start")))
        {
            return BuildEvent("convo_request", characterName, ExtractAnchorText(line), "Conversation request received");
        }

        if (lower.Contains("jumping from ") || (lower.Contains("undocking from ") && lower.Contains(" solar system")))
        {
            return BuildEvent("system_change", characterName, "", StripMarkup(line));
        }

        return null;
    }

    private void EmitLocked(TriffAlertEvent alert)
    {
        _settings.Normalize();
        var config = _settings.Event(alert.Type);
        if (!_settings.Enabled || !config.Enabled) return;

        var cooldownKey = $"{alert.CharacterName}|{alert.Type}";
        var now = DateTime.UtcNow;
        if (_cooldowns.TryGetValue(cooldownKey, out var last) && now - last < TimeSpan.FromSeconds(config.CooldownSeconds))
        {
            return;
        }

        _cooldowns[cooldownKey] = now;
        AppendHistoryLocked(alert);
        _pendingNotifications.Enqueue(alert);
        ScheduleNotificationDispatch();
    }

    private void ScheduleNotificationDispatch()
    {
        if (Interlocked.CompareExchange(ref _notificationDispatchScheduled, 1, 0) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(static service => service.DispatchPendingNotifications(), this, preferLocal: false);
    }

    private void DispatchPendingNotifications()
    {
        try
        {
            while (_pendingNotifications.TryDequeue(out var alert))
            {
                AlertTriggered?.Invoke(this, alert);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _notificationDispatchScheduled, 0);
            if (!_pendingNotifications.IsEmpty) ScheduleNotificationDispatch();
        }
    }

    private void AppendHistoryLocked(TriffAlertEvent alert)
    {
        _history.Insert(0, CloneEvent(alert));
        if (_history.Count > MaxHistory)
        {
            _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
        }
    }

    private TriffAlertEvent BuildEvent(string type, string characterName, string source, string message, bool test = false)
    {
        var config = _settings.Event(type);
        return new TriffAlertEvent
        {
            Type = type,
            Label = config.Label,
            CharacterName = characterName,
            Severity = config.Severity,
            Source = source,
            Message = message,
            TimestampUtc = DateTime.UtcNow,
            Test = test,
        };
    }

    private static bool TryReadHeader(Stream stream, out string listener)
    {
        listener = "";
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        for (var i = 0; i < 30 && !reader.EndOfStream; i++)
        {
            var line = reader.ReadLine() ?? "";
            var cleanLine = line.TrimStart();
            if (!cleanLine.StartsWith("Listener:", StringComparison.OrdinalIgnoreCase)) continue;
            listener = cleanLine["Listener:".Length..].Trim();
            return listener.Length > 0 && !listener.Equals("EVE", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsIncomingDamageLine(string lower, string line, out string source, out string message)
    {
        source = "";
        message = "";
        if (!lower.Contains("(combat)") || !lower.Contains("0xffcc0000")) return false;
        if (!lower.Contains(" from</font>") && !lower.Contains(">from</font>")) return false;

        source = ExtractSource(line);
        message = StripMarkup(line);
        return true;
    }

    private static bool IsMissLine(string lower, string line, out string source)
    {
        source = "";
        if (!lower.Contains("(combat)") || !lower.Contains("misses you")) return false;
        var clean = StripMarkup(line);
        var match = Regex.Match(clean, @"\]\s*\(combat\)\s*(?<source>.+?)\s+misses you", RegexOptions.IgnoreCase);
        source = match.Success ? match.Groups["source"].Value.Trim() : ExtractSource(line);
        return true;
    }

    private static string ExtractSource(string line)
    {
        var fromMatch = Regex.Match(line, @"<font[^>]*>\s*from\s*</font>\s*(?<source>.+?)(?:\s*<font|\s*-\s*|\s*to\s*<|$)", RegexOptions.IgnoreCase);
        if (fromMatch.Success) return StripMarkup(fromMatch.Groups["source"].Value).Trim(' ', '-', '.');

        var clean = StripMarkup(line);
        var textMatch = Regex.Match(clean, @"from\s+(?<source>.+?)(?:\s+to\s+|\s+-\s+|$)", RegexOptions.IgnoreCase);
        return textMatch.Success ? textMatch.Groups["source"].Value.Trim(' ', '-', '.') : "";
    }

    private static string ExtractAnchorText(string line)
    {
        var match = Regex.Match(line, @"<a\s+[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase);
        return match.Success ? StripMarkup(match.Groups["text"].Value) : "";
    }

    private static string StripMarkup(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        var clean = Regex.Replace(decoded, "<.*?>", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        var bracket = clean.IndexOf("] ", StringComparison.Ordinal);
        return bracket >= 0 && bracket + 2 < clean.Length ? clean[(bracket + 2)..].Trim() : clean;
    }

    private static bool IsLikelyNpcSource(string source)
    {
        var clean = source.Trim();
        if (clean.Length == 0) return false;
        if (clean.Contains('[') || clean.Contains(']') || clean.Contains('(') || clean.Contains(')')) return false;
        if (clean.Contains("'s ", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static TriffAlertEvent CloneEvent(TriffAlertEvent alert)
    {
        return new TriffAlertEvent
        {
            Id = alert.Id,
            Type = alert.Type,
            Label = alert.Label,
            CharacterName = alert.CharacterName,
            Severity = alert.Severity,
            Source = alert.Source,
            Message = alert.Message,
            TimestampUtc = alert.TimestampUtc,
            Test = alert.Test,
        };
    }

    private static string DefaultGamelogsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Gamelogs");

    private readonly record struct LogFileChange(string Path, bool LiveFromStart);

    private sealed class LogFileState(string path)
    {
        public string Path { get; } = path;
        public string CharacterName { get; set; } = "";
        public long Position { get; set; }
        public string PartialLine { get; set; } = "";
        public DateTime FirstSeenUtc { get; } = DateTime.UtcNow;
        public DateTime SessionStartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastWriteUtc { get; set; } = DateTime.UtcNow;
        public int HeaderAttempts { get; set; }
    }
}
