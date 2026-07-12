using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using TriffView.Alerts;
using Forms = System.Windows.Forms;

namespace TriffView.Preview;

internal sealed class TriffViewController : IDisposable
{
    private const int SwitchSettleBeforeMinimizeMs = 10;
    private readonly Dispatcher _dispatcher;
    private readonly Action<object> _postToHud;
    private readonly Action _reassertHudTopmost;
    private readonly Action<bool> _applySettingsAlwaysOnTop;
    private readonly EveWindowTracker _tracker = new();
    private readonly object _trackerGate = new();
    private readonly TriffAlertsService _alerts = new();
    private readonly ConcurrentQueue<TriffAlertEvent> _pendingAlerts = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _switchStateTimer;
    private readonly TriffViewOverlayForm _overlay;
    private bool _settingsPanelOpen;
    private bool _nativeMenuOpen;
    private bool _disposed;
    private IReadOnlyList<EveClientWindow> _clients = Array.Empty<EveClientWindow>();
    private string _lastPostedStateJson = "";
    private string _autoRestoredProfileId = "";
    private readonly HashSet<string> _autoRestoredClientKeys = new(StringComparer.OrdinalIgnoreCase);
    private nint _activeClientHandle;
    private int _alertDispatchScheduled;
    private int _periodicRefreshInProgress;
    private string _lastClientTopologySignature = "";
    private string _lastClientStateSignature = "";

    public TriffViewSettings Settings { get; }
    public bool SettingsPanelOpen => _settingsPanelOpen;
    public event Action<TriffAlertEvent>? AlertNotificationRequested;

    public TriffViewController(Dispatcher dispatcher, Action<object> postToHud, Action reassertHudTopmost, Action<bool> applySettingsAlwaysOnTop)
    {
        _dispatcher = dispatcher;
        _postToHud = postToHud;
        _reassertHudTopmost = reassertHudTopmost;
        _applySettingsAlwaysOnTop = applySettingsAlwaysOnTop;
        Settings = TriffViewSettings.Load();
        _overlay = new TriffViewOverlayForm();
        _overlay.ActivateRequested += ActivateClient;
        _overlay.MinimizeRequested += MinimizeClient;
        _overlay.PreviewLayoutChanged += SavePreviewLayout;
        _overlay.HotkeyPressed += HandleHotkey;
        _alerts.AlertTriggered += OnAlertTriggered;
        _alerts.UpdateSettings(Settings.Alerts);

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _timer.Tick += (_, _) => QueuePeriodicRefresh();

        _switchStateTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _switchStateTimer.Tick += (_, _) =>
        {
            _switchStateTimer.Stop();
            PostState();
        };
    }

    public void Start()
    {
        _applySettingsAlwaysOnTop(Settings.SettingsWindowAlwaysOnTop);

        if (Settings.Enabled)
        {
            _overlay.SizeToVirtualDesktop();
            Refresh(showOverlayAfterRefresh: true);
            _timer.Start();
        }

        PostState();
    }

    public bool HandleWebMessage(string type, JsonObject? message)
    {
        switch (type)
        {
            case "triffview:get-state":
                PostState(force: true);
                return true;
            case "triffview:set-enabled":
                SetEnabled(message?["enabled"]?.GetValue<bool>() == true);
                return true;
            case "triffview:set-hotkeys-suspended":
                SetHotkeysSuspended(message?["suspended"]?.GetValue<bool>() == true);
                return true;
            case "triffview:set-settings-open":
                SetSettingsPanelOpen(message?["open"]?.GetValue<bool>() == true);
                return true;
            case "triffview:set-settings-window-always-on-top":
                SetSettingsWindowAlwaysOnTop(message?["alwaysOnTop"]?.GetValue<bool>() != false);
                return true;
            case "triffview:set-guide-completed":
                SetGuideCompleted(message?["completed"]?.GetValue<bool>() == true);
                return true;
            case "triffview:update-profile":
                ApplyProfilePatch(message?["patch"] as JsonObject);
                return true;
            case "triffalerts:update-settings":
                ApplyAlertsPatch(message?["patch"] as JsonObject);
                return true;
            case "triffalerts:update-event":
                ApplyAlertEventPatch(message?["eventType"]?.GetValue<string>(), message?["patch"] as JsonObject);
                return true;
            case "triffalerts:test":
                TestAlert(message?["eventType"]?.GetValue<string>(), message?["characterName"]?.GetValue<string>());
                return true;
            case "triffalerts:clear-history":
                _alerts.ClearHistory();
                PostState(force: true);
                return true;
            case "triffview:set-profile":
                SelectProfile(message?["profileId"]?.GetValue<string>());
                return true;
            case "triffview:add-profile":
                AddProfile(message?["name"]?.GetValue<string>());
                return true;
            case "triffview:import-eveo-profile":
                ImportEveOPreviewProfile();
                return true;
            case "triffview:import-evex-profile":
                ImportEveXPreviewProfiles();
                return true;
            case "triffview:export-settings-backup":
                ExportSettingsBackup();
                return true;
            case "triffview:restore-settings-backup":
                RestoreSettingsBackup();
                return true;
            case "triffview:delete-profile":
                DeleteProfile(message?["profileId"]?.GetValue<string>());
                return true;
            case "triffview:save-preview-layout":
                SaveCurrentPreviewLayouts();
                return true;
            case "triffview:save-client-layouts":
                SaveClientLayouts();
                return true;
            case "triffview:restore-client-layouts":
                RestoreClientLayouts();
                return true;
            case "triffview:close-clients":
                CloseClients();
                return true;
            case "triffview:hide-all":
                SetEnabled(false);
                return true;
            default:
                return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (Settings.Enabled == enabled)
        {
            PostState();
            return;
        }

        Settings.Enabled = enabled;
        Settings.Save();

        if (enabled)
        {
            _timer.Start();
            _overlay.SizeToVirtualDesktop();
            Refresh(showOverlayAfterRefresh: true);
        }
        else
        {
            _timer.Stop();
            _overlay.Hide();
            _overlay.ClearThumbnails();
        }

        PostState();
    }

    public void SetHotkeysSuspended(bool suspended)
    {
        Settings.HotkeysSuspended = suspended;
        Settings.Save();
        _overlay.ConfigureHotkeys(Settings.ActiveProfile(), _clients, Settings.HotkeysSuspended);
        PostState();
    }

    public void SetSettingsPanelOpen(bool open)
    {
        if (_settingsPanelOpen == open)
        {
            ApplyTopmostPolicy(force: true);
            return;
        }

        _settingsPanelOpen = open;
        ApplyTopmostPolicy(force: true);
        _reassertHudTopmost();
    }

    private void SetSettingsWindowAlwaysOnTop(bool alwaysOnTop)
    {
        if (Settings.SettingsWindowAlwaysOnTop == alwaysOnTop)
        {
            _applySettingsAlwaysOnTop(alwaysOnTop);
            PostState();
            return;
        }

        Settings.SettingsWindowAlwaysOnTop = alwaysOnTop;
        Settings.Save();
        _applySettingsAlwaysOnTop(alwaysOnTop);
        PostState(force: true);
    }

    private void SetGuideCompleted(bool completed)
    {
        Settings.GuideCompleted = completed;
        Settings.GuideVersion = completed ? TriffViewSettings.CurrentGuideVersion : "";
        Settings.Save();
        PostState(force: true);
    }

    public void SetNativeMenuOpen(bool open)
    {
        _nativeMenuOpen = open;
        if (!open)
        {
            ApplyTopmostPolicy(force: true);
        }
    }

    public void BringToTop()
    {
        ApplyTopmostPolicy(force: true);
    }

    public void ResizeToVirtualDesktop()
    {
        _overlay.SizeToVirtualDesktop();
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _switchStateTimer.Stop();
        _alerts.Dispose();
        _overlay.Dispose();
    }

    private void ShowOverlay()
    {
        _overlay.SizeToVirtualDesktop();
        if (!_overlay.Visible) _overlay.Show();
        ApplyTopmostPolicy(force: true);
    }

    private void ApplyTopmostPolicy(bool force = false)
    {
        if (_disposed) return;
        if (_nativeMenuOpen) return;

        _overlay.AllowTopmost = Settings.Enabled;
        if (_overlay.Visible) _overlay.ApplyTopmostPolicy(force);
    }

    private void Refresh(bool showOverlayAfterRefresh = false)
    {
        if (_disposed || !Settings.Enabled) return;

        try
        {
            var foreground = TriffViewNativeMethods.GetForegroundWindow();
            ApplyClientRefresh(GetTrackedClients(foreground), foreground, showOverlayAfterRefresh, forceFullRefresh: true);
        }
        catch (Exception ex)
        {
            PostError("refresh", ex.Message);
        }
    }

    private async void QueuePeriodicRefresh()
    {
        if (_disposed || !Settings.Enabled) return;
        if (Interlocked.CompareExchange(ref _periodicRefreshInProgress, 1, 0) != 0) return;

        try
        {
            var observedForeground = TriffViewNativeMethods.GetForegroundWindow();
            var clients = await Task.Run(() => GetTrackedClients(observedForeground));
            if (_disposed || !Settings.Enabled) return;
            var foreground = TriffViewNativeMethods.GetForegroundWindow();
            ApplyClientRefresh(clients, foreground, showOverlayAfterRefresh: false, forceFullRefresh: false);
        }
        catch (Exception ex)
        {
            if (!_disposed) PostError("refresh", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _periodicRefreshInProgress, 0);
        }
    }

    private IReadOnlyList<EveClientWindow> GetTrackedClients(nint foreground)
    {
        lock (_trackerGate)
        {
            return _tracker.GetClients(foreground);
        }
    }

    private void ApplyClientRefresh(
        IReadOnlyList<EveClientWindow> discoveredClients,
        nint foreground,
        bool showOverlayAfterRefresh,
        bool forceFullRefresh)
    {
        var profile = Settings.ActiveProfileFast();
        var clients = SortClients(discoveredClients, profile).ToArray();

        if (clients.Any(client => client.Handle == foreground))
        {
            _activeClientHandle = foreground;
        }

        if (_activeClientHandle != nint.Zero && clients.All(client => client.Handle != _activeClientHandle))
        {
            _activeClientHandle = nint.Zero;
        }

        var activeHandle = _activeClientHandle != nint.Zero ? _activeClientHandle : foreground;
        clients = clients
            .Select(client => client with { IsForeground = client.Handle == activeHandle })
            .ToArray();

        var topologySignature = ClientTopologySignature(clients);
        var stateSignature = ClientStateSignature(clients, foreground);
        var topologyChanged = forceFullRefresh || !string.Equals(topologySignature, _lastClientTopologySignature, StringComparison.Ordinal);
        var stateChanged = !string.Equals(stateSignature, _lastClientStateSignature, StringComparison.Ordinal);

        _clients = clients;
        _lastClientTopologySignature = topologySignature;
        _lastClientStateSignature = stateSignature;
        _alerts.SetActiveCharacters(_clients.Select(client => client.CharacterName));

        if (topologyChanged)
        {
            AutoRestoreClientLayouts(profile, _clients);
            _overlay.SetClients(_clients, profile, foreground, activeHandle: activeHandle);
            _overlay.ConfigureHotkeys(profile, _clients, Settings.HotkeysSuspended);
        }
        else
        {
            _overlay.SyncClientStates(_clients, foreground, activeHandle);
        }

        if (showOverlayAfterRefresh) ShowOverlay();
        if (topologyChanged || stateChanged) PostState();
    }

    private static string ClientTopologySignature(IReadOnlyList<EveClientWindow> clients)
    {
        return string.Join(";", clients.Select(client => $"{client.Handle:X}|{client.ProcessId}|{client.Title}|{client.CharacterName}"));
    }

    private static string ClientStateSignature(IReadOnlyList<EveClientWindow> clients, nint foreground)
    {
        return $"{foreground:X}:" + string.Join(";", clients.Select(client => $"{client.Handle:X}|{client.IsMinimized}|{client.IsForeground}"));
    }

    private static IEnumerable<EveClientWindow> SortClients(IReadOnlyList<EveClientWindow> clients, TriffViewProfile profile)
    {
        var order = profile.CharacterOrder
            .Select((name, index) => (Name: name.Trim(), Index: index))
            .Where(item => item.Name.Length > 0)
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);

        return clients
            .Where(client => !profile.HiddenClients.Contains(client.StableKey, StringComparer.OrdinalIgnoreCase)
                && !profile.HiddenClients.Contains(client.CharacterName, StringComparer.OrdinalIgnoreCase)
                && !profile.HiddenClients.Contains(client.Title, StringComparer.OrdinalIgnoreCase))
            .OrderBy(client => order.TryGetValue(client.CharacterName, out var index) ? index : int.MaxValue)
            .ThenBy(client => client.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(client => client.Title, StringComparer.OrdinalIgnoreCase);
    }

    private void ActivateClient(EveClientWindow client)
    {
        ActivateClient(client, Settings.ActiveProfileFast());
    }

    private void ActivateClient(EveClientWindow client, TriffViewProfile profile)
    {
        try
        {
            var previousClient = ResolvePreviousActiveClient(client.Handle);
            if (!ActivateWindow(client, profile)) return;

            _activeClientHandle = client.Handle;
            MarkActiveClient(client.Handle);

            if (ShouldMinimizePreviousClient(profile, previousClient, client.Handle))
            {
                if (SwitchSettleBeforeMinimizeMs > 0)
                {
                    Thread.Sleep(SwitchSettleBeforeMinimizeMs);
                }

                SendMinimizeClient(previousClient!.Handle);
                ActivateWindow(client, profile);
            }
        }
        catch (Exception ex)
        {
            PostError("activate", ex.Message);
        }
    }

    private static void MinimizeClient(EveClientWindow client)
    {
        TriffViewNativeMethods.ShowWindow(client.Handle, TriffViewNativeMethods.SwMinimize);
    }

    private void SavePreviewLayout(string key, TriffViewRect rect)
    {
        var profile = Settings.ActiveProfile();
        profile.PreviewLayouts[key] = rect;
        Settings.Save();
        PostState();
    }

    private void SaveCurrentPreviewLayouts()
    {
        var layouts = _overlay.CurrentPreviewLayouts();
        var profile = Settings.ActiveProfile();
        foreach (var layout in layouts)
        {
            profile.PreviewLayouts[layout.Key] = layout.Value;
        }

        Settings.Save();
        PostState();
    }

    private void SaveClientLayouts()
    {
        var profile = Settings.ActiveProfile();
        foreach (var client in GetTrackedClients(TriffViewNativeMethods.GetForegroundWindow()))
        {
            if (!TriffViewNativeMethods.GetWindowPlacement(client.Handle, out var placement)) continue;
            var normal = placement.NormalPosition;
            profile.ClientPlacements[client.StableKey] = new TriffViewClientPlacement
            {
                Title = client.Title,
                CharacterName = client.CharacterName,
                X = normal.Left,
                Y = normal.Top,
                Width = Math.Max(1, normal.Right - normal.Left),
                Height = Math.Max(1, normal.Bottom - normal.Top),
                Maximized = placement.ShowCmd == TriffViewNativeMethods.SwMaximize,
            };
        }

        Settings.Save();
        PostState();
    }

    private void RestoreClientLayouts()
    {
        var profile = Settings.ActiveProfile();
        foreach (var client in GetTrackedClients(TriffViewNativeMethods.GetForegroundWindow()))
        {
            RestoreClientLayout(profile, client);
        }

        ResetAutoRestoreTracking(profile.Id);
        Refresh();
    }

    private void AutoRestoreClientLayouts(TriffViewProfile profile, IReadOnlyList<EveClientWindow> clients)
    {
        if (!profile.AutoRestoreClientLayouts)
        {
            ResetAutoRestoreTracking(profile.Id);
            return;
        }

        if (!string.Equals(_autoRestoredProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            ResetAutoRestoreTracking(profile.Id);
        }

        foreach (var client in clients)
        {
            var restoreKey = $"{client.StableKey}:{client.Handle}";
            if (_autoRestoredClientKeys.Contains(restoreKey)) continue;
            if (!RestoreClientLayout(profile, client)) continue;
            _autoRestoredClientKeys.Add(restoreKey);
        }

        foreach (var key in _autoRestoredClientKeys.Where(key => clients.All(client => key != $"{client.StableKey}:{client.Handle}")).ToArray())
        {
            _autoRestoredClientKeys.Remove(key);
        }
    }

    private void ResetAutoRestoreTracking(string profileId)
    {
        _autoRestoredProfileId = profileId;
        _autoRestoredClientKeys.Clear();
    }

    private static bool RestoreClientLayout(TriffViewProfile profile, EveClientWindow client)
    {
        if (!profile.ClientPlacements.TryGetValue(client.StableKey, out var placement)) return false;

        TriffViewNativeMethods.ShowWindow(client.Handle, TriffViewNativeMethods.SwRestore);
        TriffViewNativeMethods.MoveWindow(
            client.Handle,
            placement.X,
            placement.Y,
            Math.Max(1, placement.Width),
            Math.Max(1, placement.Height),
            true
        );

        if (placement.Maximized)
        {
            TriffViewNativeMethods.ShowWindow(client.Handle, TriffViewNativeMethods.SwMaximize);
        }

        return true;
    }

    private void CloseClients()
    {
        foreach (var client in GetTrackedClients(TriffViewNativeMethods.GetForegroundWindow()))
        {
            TriffViewNativeMethods.PostMessage(client.Handle, TriffViewNativeMethods.WmClose, nint.Zero, nint.Zero);
        }

        Refresh();
    }

    private EveClientWindow? ResolvePreviousActiveClient(nint nextHandle)
    {
        var foreground = TriffViewNativeMethods.GetForegroundWindow();
        var foregroundClient = _clients.FirstOrDefault(client => client.Handle == foreground && client.Handle != nextHandle);
        if (foregroundClient != null) return foregroundClient;

        return _clients.FirstOrDefault(client => client.Handle == _activeClientHandle && client.Handle != nextHandle);
    }

    private static bool ShouldMinimizePreviousClient(TriffViewProfile profile, EveClientWindow? previousClient, nint activeHandle)
    {
        if (!profile.MinimizeInactiveClients || previousClient == null) return false;
        if (previousClient.Handle == activeHandle) return false;

        return !ShouldSkipInactiveMinimize(profile, previousClient);
    }

    private static void SendMinimizeClient(nint handle)
    {
        TriffViewNativeMethods.SendMessage(handle, TriffViewNativeMethods.WmSysCommand, TriffViewNativeMethods.ScMinimize, nint.Zero);
    }

    private static bool ShouldSkipInactiveMinimize(TriffViewProfile profile, EveClientWindow client)
    {
        var neverMinimize = profile.NeverMinimizeClients;
        return neverMinimize.Contains(client.StableKey, StringComparer.OrdinalIgnoreCase)
            || neverMinimize.Contains(client.CharacterName, StringComparer.OrdinalIgnoreCase)
            || neverMinimize.Contains(client.Title, StringComparer.OrdinalIgnoreCase);
    }

    private void HandleHotkey(TriffViewHotkeyCommand command)
    {
        var profile = Settings.ActiveProfileFast();
        if (Settings.HotkeysSuspended) return;
        var foreground = TriffViewNativeMethods.GetForegroundWindow();
        if (profile.HotkeysRequireEveForeground
            && _clients.All(client => client.Handle != foreground))
        {
            return;
        }

        if (command.Kind == TriffViewHotkeyKind.Direct)
        {
            var characterNames = command.CharacterNames.Count > 0
                ? command.CharacterNames
                : new[] { command.CharacterName };
            var target = _clients.FirstOrDefault(client => characterNames.Any(characterName =>
                string.Equals(client.CharacterName, characterName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(client.StableKey, characterName, StringComparison.OrdinalIgnoreCase)));
            if (target != null) ActivateClient(target, profile);
            return;
        }

        if (command.Kind == TriffViewHotkeyKind.Cycle)
        {
            CycleClient(profile, command.GroupId, command.Direction);
        }
    }

    private void CycleClient(TriffViewProfile profile, string groupId, int direction)
    {
        var group = profile.CycleGroups.FirstOrDefault(item => string.Equals(item.Id, groupId, StringComparison.OrdinalIgnoreCase));
        var cycleNames = group?.Characters.Count > 0 ? group.Characters : profile.CharacterOrder;
        if (cycleNames.Count == 0) return;

        var candidates = ResolveCycleCandidates(cycleNames);
        if (candidates.Count == 0) return;

        var activeIndex = candidates.FindIndex(client => client.Handle == _activeClientHandle);
        if (activeIndex < 0)
        {
            activeIndex = candidates.FindIndex(client => client.IsForeground);
        }
        var nextIndex = activeIndex < 0
            ? 0
            : (activeIndex + direction + candidates.Count) % candidates.Count;
        ActivateClient(candidates[nextIndex], profile);
    }

    private List<EveClientWindow> ResolveCycleCandidates(IReadOnlyList<string> cycleNames)
    {
        var candidates = new List<EveClientWindow>();
        var usedHandles = new HashSet<nint>();

        foreach (var name in cycleNames)
        {
            var client = _clients.FirstOrDefault(item =>
                string.Equals(item.CharacterName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.StableKey, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Title, name, StringComparison.OrdinalIgnoreCase));
            if (client == null || !usedHandles.Add(client.Handle)) continue;
            candidates.Add(client);
        }

        return candidates;
    }

    private void ApplyProfilePatch(JsonObject? patch)
    {
        if (patch == null) return;
        var profile = Settings.ActiveProfile();
        var previewSizeChanged = false;

        foreach (var (key, value) in patch)
        {
            switch (key)
            {
                case "name":
                    profile.Name = value?.GetValue<string>()?.Trim() ?? profile.Name;
                    break;
                case "previewWidth":
                    var previewWidth = ClampInt(value, 120, 1200, profile.PreviewWidth);
                    previewSizeChanged |= previewWidth != profile.PreviewWidth;
                    profile.PreviewWidth = previewWidth;
                    break;
                case "previewHeight":
                    var previewHeight = ClampInt(value, 90, 900, profile.PreviewHeight);
                    previewSizeChanged |= previewHeight != profile.PreviewHeight;
                    profile.PreviewHeight = previewHeight;
                    break;
                case "opacity":
                    profile.Opacity = ClampDouble(value, 0.2, 1, profile.Opacity);
                    break;
                case "showLabels":
                    profile.ShowLabels = value?.GetValue<bool>() == true;
                    break;
                case "showActiveHighlight":
                    profile.ShowActiveHighlight = value?.GetValue<bool>() == true;
                    break;
                case "showInactiveBorders":
                    profile.ShowInactiveBorders = value?.GetValue<bool>() == true;
                    break;
                case "lockPreviews":
                    profile.LockPreviews = value?.GetValue<bool>() == true;
                    break;
                case "borderThickness":
                    profile.BorderThickness = ClampInt(value, 1, 16, profile.BorderThickness);
                    break;
                case "snapEnabled":
                    profile.SnapEnabled = value?.GetValue<bool>() == true;
                    break;
                case "snapDistance":
                    profile.SnapDistance = ClampInt(value, 0, 80, profile.SnapDistance);
                    break;
                case "hideActivePreview":
                    profile.HideActivePreview = value?.GetValue<bool>() == true;
                    break;
                case "hideOnLostFocus":
                    profile.HideOnLostFocus = value?.GetValue<bool>() == true;
                    break;
                case "minimizeInactiveClients":
                    profile.MinimizeInactiveClients = value?.GetValue<bool>() == true;
                    break;
                case "alwaysMaximizeClients":
                    profile.AlwaysMaximizeClients = value?.GetValue<bool>() == true;
                    break;
                case "autoRestoreClientLayouts":
                    profile.AutoRestoreClientLayouts = value?.GetValue<bool>() == true;
                    ResetAutoRestoreTracking(profile.Id);
                    break;
                case "hotkeysRequireEveForeground":
                    profile.HotkeysRequireEveForeground = value?.GetValue<bool>() == true;
                    break;
                case "activeBorderColor":
                    profile.ActiveBorderColor = CleanColor(value, profile.ActiveBorderColor);
                    break;
                case "inactiveBorderColor":
                    profile.InactiveBorderColor = CleanColor(value, profile.InactiveBorderColor);
                    break;
                case "labelTextColor":
                    profile.LabelTextColor = CleanColor(value, profile.LabelTextColor);
                    break;
                case "labelBackgroundColor":
                    profile.LabelBackgroundColor = CleanColor(value, profile.LabelBackgroundColor);
                    break;
                case "labelBackgroundTransparent":
                    profile.LabelBackgroundTransparent = value?.GetValue<bool>() == true;
                    break;
                case "labelPosition":
                    profile.LabelPosition = value?.GetValue<string>()?.Trim() ?? profile.LabelPosition;
                    break;
                case "labelFontSize":
                    profile.LabelFontSize = ClampInt(value, 8, 32, profile.LabelFontSize);
                    break;
                case "previewLabels":
                    profile.PreviewLabels = ParseStringMap(value as JsonObject);
                    break;
                case "characterOrderText":
                    profile.CharacterOrder = SplitLines(value?.GetValue<string>());
                    break;
                case "hiddenClientsText":
                    profile.HiddenClients = SplitLines(value?.GetValue<string>());
                    break;
                case "neverMinimizeClientsText":
                    profile.NeverMinimizeClients = SplitLines(value?.GetValue<string>());
                    break;
                case "directHotkeysText":
                    profile.DirectHotkeys = ParseDirectHotkeys(value?.GetValue<string>());
                    break;
                case "cycleGroupsText":
                    profile.CycleGroups = ParseCycleGroups(value?.GetValue<string>());
                    break;
                case "selectedCycleGroupId":
                    profile.SelectedCycleGroupId = value?.GetValue<string>()?.Trim() ?? "";
                    break;
            }
        }

        profile.Normalize();
        if (previewSizeChanged)
        {
            ApplyProfilePreviewSizeToSavedLayouts(profile);
        }
        Settings.Save();
        Refresh();
    }

    private void ApplyAlertsPatch(JsonObject? patch)
    {
        if (patch == null) return;
        var alerts = Settings.Alerts;
        foreach (var (key, value) in patch)
        {
            switch (key)
            {
                case "enabled":
                    alerts.Enabled = value?.GetValue<bool>() == true;
                    break;
                case "pveMode":
                    alerts.PveMode = value?.GetValue<bool>() == true;
                    break;
                case "masterVolume":
                    alerts.MasterVolume = ClampDouble(value, 0, 1, alerts.MasterVolume);
                    break;
            }
        }

        alerts.Normalize();
        Settings.Save();
        _alerts.UpdateSettings(Settings.Alerts);
        PostState(force: true);
    }

    private void ApplyAlertEventPatch(string? eventType, JsonObject? patch)
    {
        if (string.IsNullOrWhiteSpace(eventType) || patch == null) return;
        var alerts = Settings.Alerts;
        alerts.Normalize();
        if (!alerts.Events.TryGetValue(eventType.Trim(), out var config)) return;

        foreach (var (key, value) in patch)
        {
            switch (key)
            {
                case "enabled":
                    config.Enabled = value?.GetValue<bool>() == true;
                    break;
                case "severity":
                    config.Severity = TriffAlertSeverity.Normalize(value?.GetValue<string>(), config.Severity);
                    break;
                case "cooldownSeconds":
                    config.CooldownSeconds = ClampInt(value, 0, 120, config.CooldownSeconds);
                    break;
                case "flashEnabled":
                    config.FlashEnabled = value?.GetValue<bool>() == true;
                    break;
                case "flashColor":
                    config.FlashColor = CleanColor(value, config.FlashColor);
                    break;
                case "flashThickness":
                    config.FlashThickness = ClampInt(value, 1, 24, config.FlashThickness);
                    break;
                case "flashDurationMs":
                    config.FlashDurationMs = ClampInt(value, 250, 15000, config.FlashDurationMs);
                    break;
                case "flashPulseCount":
                    config.FlashPulseCount = ClampInt(value, 1, 16, config.FlashPulseCount);
                    break;
                case "sound":
                    config.Sound = (value?.GetValue<string>() ?? "none").Trim().ToLowerInvariant();
                    break;
                case "trayNotification":
                    config.TrayNotification = value?.GetValue<bool>() == true;
                    break;
            }
        }

        alerts.Normalize();
        Settings.Save();
        _alerts.UpdateSettings(Settings.Alerts);
        PostState(force: true);
    }

    private void TestAlert(string? eventType, string? characterName)
    {
        var target = characterName;
        if (string.IsNullOrWhiteSpace(target))
        {
            target = _clients.FirstOrDefault()?.CharacterName;
        }

        _alerts.TestAlert(eventType, target);
        PostState(force: true);
    }

    private void OnAlertTriggered(object? sender, TriffAlertEvent alert)
    {
        if (_disposed) return;
        _pendingAlerts.Enqueue(alert);
        SchedulePendingAlertDispatch();
    }

    private void SchedulePendingAlertDispatch()
    {
        if (Interlocked.CompareExchange(ref _alertDispatchScheduled, 1, 0) != 0) return;
        _dispatcher.InvokeAsync(ProcessPendingAlerts, DispatcherPriority.Input);
    }

    private void ProcessPendingAlerts()
    {
        try
        {
            if (_disposed) return;

            while (_pendingAlerts.TryDequeue(out var alert))
            {
                var config = Settings.Alerts.Events.TryGetValue(alert.Type, out var configured)
                    ? configured
                    : Settings.Alerts.Event(alert.Type);
                if (config.FlashEnabled)
                {
                    _overlay.ShowAlert(alert.CharacterName, new TriffViewPreviewAlert(
                        config.SeverityRank,
                        config.FlashColor,
                        config.FlashThickness,
                        config.FlashDurationMs,
                        config.FlashPulseCount
                    ));
                }

                if (config.TrayNotification)
                {
                    AlertNotificationRequested?.Invoke(alert);
                }
            }

            ScheduleSwitchStatePost();
        }
        finally
        {
            Interlocked.Exchange(ref _alertDispatchScheduled, 0);
            if (!_pendingAlerts.IsEmpty && !_disposed) SchedulePendingAlertDispatch();
        }
    }

    private static void ApplyProfilePreviewSizeToSavedLayouts(TriffViewProfile profile)
    {
        foreach (var layout in profile.PreviewLayouts.Values)
        {
            if (!layout.IsUsable) continue;
            layout.Width = profile.PreviewWidth;
            layout.Height = profile.PreviewHeight;
        }
    }

    private void SelectProfile(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;
        if (Settings.Profiles.All(profile => !string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))) return;
        Settings.SelectedProfileId = profileId;
        Settings.Save();
        Refresh();
    }

    private void AddProfile(string? name)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "New profile" : name.Trim();
        var profile = TriffViewProfile.CreateDefault(cleanName);
        Settings.Profiles.Add(profile);
        Settings.SelectedProfileId = profile.Id;
        Settings.Save();
        Refresh();
    }

    private void ExportSettingsBackup()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export TriffView settings backup",
                Filter = "TriffView backup JSON (*.json)|*.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"triffview-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                DefaultExt = ".json",
                AddExtension = true,
                OverwritePrompt = true,
            };

            if (dialog.ShowDialog() != true) return;

            File.WriteAllText(dialog.FileName, Settings.ToJson());
            PostState(force: true);
        }
        catch (Exception ex)
        {
            PostError("export-settings-backup", ex.Message);
        }
    }

    private void RestoreSettingsBackup()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Restore TriffView settings backup",
                Filter = "TriffView backup JSON (*.json)|*.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            if (dialog.ShowDialog() != true) return;

            var confirm = Forms.MessageBox.Show(
                "Restoring this backup will overwrite all current TriffView data, including every profile, preview layout, client layout, color, hotkey, and enable/suspend setting.\n\nContinue?",
                "Restore TriffView backup",
                Forms.MessageBoxButtons.YesNo,
                Forms.MessageBoxIcon.Warning,
                Forms.MessageBoxDefaultButton.Button2
            );
            if (confirm != Forms.DialogResult.Yes) return;

            var imported = TriffViewSettings.FromJson(File.ReadAllText(dialog.FileName));
            ReplaceSettings(imported);
        }
        catch (Exception ex)
        {
            PostError("restore-settings-backup", ex.Message);
        }
    }

    private void ReplaceSettings(TriffViewSettings imported)
    {
        _timer.Stop();
        _overlay.ConfigureHotkeys(Settings.ActiveProfile(), _clients, suspended: true);

        Settings.Enabled = imported.Enabled;
        Settings.HotkeysSuspended = imported.HotkeysSuspended;
        Settings.SettingsWindowAlwaysOnTop = imported.SettingsWindowAlwaysOnTop;
        Settings.GuideCompleted = imported.GuideCompleted;
        Settings.GuideVersion = imported.GuideVersion;
        Settings.SelectedProfileId = imported.SelectedProfileId;
        Settings.Profiles = imported.Profiles;
        Settings.Alerts = imported.Alerts;
        Settings.Save();
        _alerts.UpdateSettings(Settings.Alerts);

        if (Settings.Enabled)
        {
            _overlay.SizeToVirtualDesktop();
            Refresh(showOverlayAfterRefresh: true);
            _timer.Start();
        }
        else
        {
            _clients = Array.Empty<EveClientWindow>();
            _overlay.Hide();
            _overlay.ClearThumbnails();
        }

        PostState(force: true);
    }

    private void ImportEveOPreviewProfile()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import EVE-O Preview settings",
                Filter = "EVE-O Preview JSON (*.json)|*.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            if (dialog.ShowDialog() != true) return;

            var root = JsonNode.Parse(File.ReadAllText(dialog.FileName)) as JsonObject;
            if (root == null)
            {
                PostError("import-eveo-profile", "The selected file is not a valid EVE-O Preview JSON file.");
                return;
            }

            var profile = BuildEveOProfile(root);
            Settings.Profiles.Add(profile);
            Settings.SelectedProfileId = profile.Id;
            Settings.Save();
            Refresh();
            PostState(force: true);
        }
        catch (Exception ex)
        {
            PostError("import-eveo-profile", ex.Message);
        }
    }

    private void ImportEveXPreviewProfiles()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import EVE-X Preview settings",
                Filter = "EVE-X Preview JSON (*.json)|*.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            if (dialog.ShowDialog() != true) return;

            var root = JsonNode.Parse(File.ReadAllText(dialog.FileName)) as JsonObject;
            if (root?["_Profiles"] is not JsonObject profilesRoot || profilesRoot.Count == 0)
            {
                PostError("import-evex-profile", "The selected file is not a valid EVE-X Preview JSON file.");
                return;
            }

            var globalSettings = root["global_Settings"] as JsonObject;
            var lastUsedProfile = EveXString(globalSettings?["LastUsedProfile"]);
            var importedProfiles = new List<(string SourceName, TriffViewProfile Profile)>();

            foreach (var (profileName, node) in profilesRoot)
            {
                if (node is not JsonObject sourceProfile) continue;
                importedProfiles.Add((profileName, BuildEveXProfile(profileName, sourceProfile, globalSettings)));
            }

            if (importedProfiles.Count == 0)
            {
                PostError("import-evex-profile", "No EVE-X profiles were found in the selected file.");
                return;
            }

            Settings.Profiles.AddRange(importedProfiles.Select(item => item.Profile));
            var selected = importedProfiles.FirstOrDefault(item => string.Equals(item.SourceName, lastUsedProfile, StringComparison.OrdinalIgnoreCase)).Profile
                ?? importedProfiles[^1].Profile;
            Settings.SelectedProfileId = selected.Id;
            Settings.Save();
            Refresh();
            PostState(force: true);
        }
        catch (Exception ex)
        {
            PostError("import-evex-profile", ex.Message);
        }
    }

    private TriffViewProfile BuildEveOProfile(JsonObject root)
    {
        var profile = TriffViewProfile.CreateDefault(UniqueProfileName("Imported from EVE-O"));
        profile.HotkeysRequireEveForeground = false;

        if (TryParseSize(JsonString(root, "ThumbnailSize"), out var previewSize))
        {
            profile.PreviewWidth = Math.Max(120, previewSize.Width);
            profile.PreviewHeight = Math.Max(90, previewSize.Height);
        }

        profile.Opacity = Math.Max(0.2, Math.Min(1, JsonDouble(root, "ThumbnailsOpacity", profile.Opacity)));
        profile.SnapEnabled = JsonBool(root, "EnableThumbnailSnap", profile.SnapEnabled);
        profile.HideActivePreview = JsonBool(root, "HideActiveClientThumbnail", profile.HideActivePreview);
        profile.HideOnLostFocus = JsonBool(root, "HideThumbnailsOnLostFocus", profile.HideOnLostFocus);
        profile.MinimizeInactiveClients = JsonBool(root, "MinimizeInactiveClients", profile.MinimizeInactiveClients);
        profile.ShowLabels = JsonBool(root, "ShowThumbnailOverlays", profile.ShowLabels);
        profile.ShowInactiveBorders = JsonBool(root, "ShowThumbnailFrames", profile.ShowInactiveBorders);
        profile.ShowActiveHighlight = JsonBool(root, "EnableActiveClientHighlight", profile.ShowActiveHighlight);
        profile.BorderThickness = Math.Max(1, Math.Min(16, JsonInt(root, "ActiveClientHighlightThickness", profile.BorderThickness)));
        profile.ActiveBorderColor = ColorNameToHex(JsonString(root, "ActiveClientHighlightColor"), profile.ActiveBorderColor);

        ImportPreviewLayouts(root["FlatLayout"] as JsonObject, profile);
        ImportHiddenPreviews(root["DisableThumbnail"] as JsonObject, profile);
        profile.DirectHotkeys = ImportDirectHotkeys(root["ClientHotkey"] as JsonObject);
        profile.CycleGroups = ImportCycleGroups(root);

        if (profile.CycleGroups.Count > 0)
        {
            profile.SelectedCycleGroupId = profile.CycleGroups[0].Id;
            profile.CharacterOrder = profile.CycleGroups[0].Characters.ToList();
        }

        profile.Normalize();
        return profile;
    }

    private TriffViewProfile BuildEveXProfile(string sourceName, JsonObject root, JsonObject? globalSettings)
    {
        var cleanSourceName = string.IsNullOrWhiteSpace(sourceName) ? "Profile" : sourceName.Trim();
        var profile = TriffViewProfile.CreateDefault(UniqueProfileName($"Imported from EVE-X - {cleanSourceName}"));
        var thumbnailSettings = root["Thumbnail Settings"] as JsonObject;
        var clientSettings = root["Client Settings"] as JsonObject;

        profile.HotkeysRequireEveForeground = !EveXBool(globalSettings?["Global_Hotkeys"], fallback: true);
        profile.SnapEnabled = EveXBool(globalSettings?["ThumbnailSnap"], profile.SnapEnabled);
        profile.SnapDistance = EveXInt(globalSettings?["ThumbnailSnap_Distance"], profile.SnapDistance);

        if (TryReadEveXRect(root["Thumbnail Positions"] as JsonObject, out var firstPreviewRect)
            || TryParseEveXRect(globalSettings?["ThumbnailStartLocation"] as JsonObject, out firstPreviewRect))
        {
            profile.PreviewWidth = Math.Max(120, firstPreviewRect.Width);
            profile.PreviewHeight = Math.Max(90, firstPreviewRect.Height);
        }

        if (thumbnailSettings != null)
        {
            profile.Opacity = NormalizeEveXOpacity(thumbnailSettings["ThumbnailOpacity"], profile.Opacity);
            profile.HideOnLostFocus = EveXBool(thumbnailSettings["HideThumbnailsOnLostFocus"], profile.HideOnLostFocus);
            profile.ShowLabels = EveXBool(thumbnailSettings["ShowThumbnailTextOverlay"], profile.ShowLabels);
            profile.ShowActiveHighlight = EveXBool(thumbnailSettings["ShowClientHighlightBorder"], profile.ShowActiveHighlight);
            profile.ShowInactiveBorders = EveXBool(thumbnailSettings["ShowAllColoredBorders"], profile.ShowInactiveBorders);
            profile.BorderThickness = Math.Max(1, Math.Min(16, EveXInt(thumbnailSettings["ClientHighligtBorderthickness"], profile.BorderThickness)));
            profile.ActiveBorderColor = CleanImportedColor(EveXString(thumbnailSettings["ClientHighligtColor"]), profile.ActiveBorderColor);
            profile.InactiveBorderColor = CleanImportedColor(EveXString(thumbnailSettings["InactiveClientBorderColor"]), profile.InactiveBorderColor);
            profile.LabelTextColor = CleanImportedColor(EveXString(thumbnailSettings["ThumbnailTextColor"]), profile.LabelTextColor);
            profile.LabelFontSize = Math.Max(8, Math.Min(32, EveXInt(thumbnailSettings["ThumbnailTextSize"], profile.LabelFontSize)));
        }

        var backgroundColor = CleanImportedColor(EveXString(globalSettings?["ThumbnailBackgroundColor"]), "");
        if (!string.IsNullOrWhiteSpace(backgroundColor))
        {
            profile.LabelBackgroundColor = backgroundColor;
        }

        if (clientSettings != null)
        {
            profile.AlwaysMaximizeClients = EveXBool(clientSettings["AlwaysMaximize"], profile.AlwaysMaximizeClients);
            profile.MinimizeInactiveClients = EveXBool(clientSettings["MinimizeInactiveClients"], profile.MinimizeInactiveClients);
            profile.AutoRestoreClientLayouts = EveXBool(clientSettings["TrackClientPossitions"], profile.AutoRestoreClientLayouts);
            profile.NeverMinimizeClients = EveXStringList(clientSettings["Dont_Minimize_Clients"]);
        }

        ImportEveXPreviewLayouts(root["Thumbnail Positions"] as JsonObject, profile);
        ImportEveXClientPlacements(root["Client Possitions"] as JsonObject, profile);
        ImportEveXHiddenPreviews(root["Thumbnail Visibility"] as JsonObject, profile);
        profile.DirectHotkeys = ImportEveXDirectHotkeys(root["Hotkeys"] as JsonArray);
        profile.CycleGroups = ImportEveXCycleGroups(root["Hotkey Groups"] as JsonObject);

        if (profile.CycleGroups.Count > 0)
        {
            profile.SelectedCycleGroupId = profile.CycleGroups[0].Id;
            profile.CharacterOrder = profile.CycleGroups[0].Characters.ToList();
        }
        else
        {
            profile.CharacterOrder = profile.PreviewLayouts.Keys
                .Where(name => !string.Equals(name, "EVE", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        profile.Normalize();
        return profile;
    }

    private string UniqueProfileName(string baseName)
    {
        if (Settings.Profiles.All(profile => !string.Equals(profile.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{baseName} {index}";
            if (Settings.Profiles.All(profile => !string.Equals(profile.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"{baseName} {DateTime.Now:yyyyMMdd-HHmmss}";
    }

    private static void ImportPreviewLayouts(JsonObject? layouts, TriffViewProfile profile)
    {
        if (layouts == null) return;

        foreach (var (title, node) in layouts)
        {
            var characterName = NormalizeEveWindowTitle(title);
            if (string.IsNullOrWhiteSpace(characterName)) continue;
            if (!TryParsePoint(node?.GetValue<string>(), out var point)) continue;

            profile.PreviewLayouts[characterName] = new TriffViewRect
            {
                X = point.X,
                Y = point.Y,
                Width = profile.PreviewWidth,
                Height = profile.PreviewHeight,
            };
        }
    }

    private static void ImportHiddenPreviews(JsonObject? hidden, TriffViewProfile profile)
    {
        if (hidden == null) return;

        profile.HiddenClients = hidden
            .Where(item => item.Value?.GetValue<bool>() == true)
            .Select(item => NormalizeEveWindowTitle(item.Key))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ImportEveXPreviewLayouts(JsonObject? layouts, TriffViewProfile profile)
    {
        if (layouts == null) return;

        foreach (var (title, node) in layouts)
        {
            var characterName = NormalizeEveWindowTitle(title);
            if (string.IsNullOrWhiteSpace(characterName)) continue;
            if (!TryParseEveXRect(node as JsonObject, out var rect)) continue;

            profile.PreviewLayouts[characterName] = new TriffViewRect
            {
                X = rect.X,
                Y = rect.Y,
                Width = Math.Max(80, rect.Width),
                Height = Math.Max(60, rect.Height),
            };
        }
    }

    private static void ImportEveXClientPlacements(JsonObject? placements, TriffViewProfile profile)
    {
        if (placements == null) return;

        foreach (var (title, node) in placements)
        {
            var characterName = NormalizeEveWindowTitle(title);
            if (string.IsNullOrWhiteSpace(characterName)) continue;
            if (!TryParseEveXRect(node as JsonObject, out var rect)) continue;

            profile.ClientPlacements[characterName] = new TriffViewClientPlacement
            {
                Title = characterName,
                CharacterName = characterName,
                X = rect.X,
                Y = rect.Y,
                Width = Math.Max(1, rect.Width),
                Height = Math.Max(1, rect.Height),
                Maximized = EveXBool((node as JsonObject)?["IsMaximized"], fallback: false),
            };
        }
    }

    private static void ImportEveXHiddenPreviews(JsonObject? hidden, TriffViewProfile profile)
    {
        if (hidden == null) return;

        profile.HiddenClients = hidden
            .Where(item => EveXBool(item.Value, fallback: false))
            .Select(item => NormalizeEveWindowTitle(item.Key))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<TriffViewHotkeyBinding> ImportDirectHotkeys(JsonObject? hotkeys)
    {
        if (hotkeys == null) return new List<TriffViewHotkeyBinding>();

        return hotkeys
            .Select(item => new TriffViewHotkeyBinding
            {
                CharacterName = NormalizeEveWindowTitle(item.Key),
                Gestures = GestureList(item.Value),
                Enabled = true,
            })
            .Where(binding => binding.CharacterName.Length > 0 && binding.Gestures.Count > 0)
            .ToList();
    }

    private static List<TriffViewCycleGroup> ImportCycleGroups(JsonObject root)
    {
        var groups = new List<TriffViewCycleGroup>();

        for (var index = 1; index <= 8; index++)
        {
            var order = root[$"CycleGroup{index}ClientsOrder"] as JsonObject;
            if (order == null || order.Count == 0) continue;

            var group = new TriffViewCycleGroup
            {
                Name = index == 1 ? "Imported Main" : $"Imported Group {index}",
                ForwardGestures = GestureList(root[$"CycleGroup{index}ForwardHotkeys"]),
                BackwardGestures = GestureList(root[$"CycleGroup{index}BackwardHotkeys"]),
                Characters = OrderedNamesFromObject(order),
                Enabled = true,
            };
            group.Normalize();
            groups.Add(group);
        }

        return groups;
    }

    private static List<string> OrderedNamesFromObject(JsonObject order)
    {
        return order
            .Select((item, ordinal) => new
            {
                Name = NormalizeEveWindowTitle(item.Key),
                Order = item.Value?.GetValue<int>() ?? int.MaxValue,
                Ordinal = ordinal,
            })
            .Where(item => item.Name.Length > 0)
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Ordinal)
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GestureList(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            return CleanGestureList(array.Select(gesture => gesture?.GetValue<string>() ?? ""));
        }

        return CleanGestureList(new[] { value?.GetValue<string>() ?? "" });
    }

    private static string NormalizeEveWindowTitle(string title)
    {
        var clean = (title ?? "").Trim();
        return clean.StartsWith("EVE - ", StringComparison.OrdinalIgnoreCase) ? clean[6..].Trim() : clean;
    }

    private static string NormalizeGesture(string gesture)
    {
        return (gesture ?? "").Trim().Replace("Ctrl+", "Control+", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CleanGestureList(IEnumerable<string>? gestures)
    {
        return (gestures ?? Array.Empty<string>())
            .SelectMany(gesture => (gesture ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(NormalizeGesture)
            .Where(gesture => gesture.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<TriffViewHotkeyBinding> ImportEveXDirectHotkeys(JsonArray? hotkeys)
    {
        if (hotkeys == null) return new List<TriffViewHotkeyBinding>();

        var result = new List<TriffViewHotkeyBinding>();
        foreach (var node in hotkeys)
        {
            if (node is not JsonObject item) continue;
            foreach (var (characterName, gestureNode) in item)
            {
                var gestures = CleanGestureList(new[] { NormalizeEveXGesture(EveXString(gestureNode)) });
                if (gestures.Count == 0) continue;

                result.Add(new TriffViewHotkeyBinding
                {
                    CharacterName = NormalizeEveWindowTitle(characterName),
                    Gestures = gestures,
                    Enabled = true,
                });
            }
        }

        return result
            .Where(binding => binding.CharacterName.Length > 0 && binding.Gestures.Count > 0)
            .ToList();
    }

    private static string? JsonString(JsonObject root, string key)
    {
        return root.TryGetPropertyValue(key, out var node) ? node?.GetValue<string>() : null;
    }

    private static bool JsonBool(JsonObject root, string key, bool fallback)
    {
        return root.TryGetPropertyValue(key, out var node) && node != null ? node.GetValue<bool>() : fallback;
    }

    private static int JsonInt(JsonObject root, string key, int fallback)
    {
        return root.TryGetPropertyValue(key, out var node) && node != null ? node.GetValue<int>() : fallback;
    }

    private static double JsonDouble(JsonObject root, string key, double fallback)
    {
        return root.TryGetPropertyValue(key, out var node) && node != null ? node.GetValue<double>() : fallback;
    }

    private static bool TryParseSize(string? value, out Size size)
    {
        size = Size.Empty;
        var parts = (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height)) return false;
        size = new Size(width, height);
        return true;
    }

    private static bool TryParsePoint(string? value, out Point point)
    {
        point = Point.Empty;
        var parts = (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return false;
        point = new Point(x, y);
        return true;
    }

    private static string ColorNameToHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (value.StartsWith("#", StringComparison.Ordinal)) return value;

        var color = Color.FromName(value.Trim());
        if (color.A == 0 && !string.Equals(value, "Transparent", StringComparison.OrdinalIgnoreCase)) return fallback;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static string NormalizeEveXGesture(string gesture)
    {
        var clean = (gesture ?? "").Trim();
        if (clean.Length == 0) return "";

        if (clean.Contains("XButton", StringComparison.OrdinalIgnoreCase)
            || clean.Contains("MButton", StringComparison.OrdinalIgnoreCase)
            || clean.Contains("LButton", StringComparison.OrdinalIgnoreCase)
            || clean.Contains("RButton", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (clean.Contains('&'))
        {
            var chordParts = clean.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeEveXGesturePart)
                .Where(part => part.Length > 0)
                .ToArray();
            return chordParts.Length > 0 ? NormalizeGesture(string.Join("+", chordParts)) : "";
        }

        var modifiers = new List<string>();
        while (clean.Length > 0)
        {
            var modifier = clean[0] switch
            {
                '^' => "Control",
                '!' => "Alt",
                '+' => "Shift",
                '#' => "Win",
                _ => "",
            };
            if (modifier.Length == 0) break;
            modifiers.Add(modifier);
            clean = clean[1..].TrimStart();
        }

        clean = string.Join("+", clean
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeEveXGesturePart)
            .Where(part => part.Length > 0));

        if (clean.Length == 0) return "";
        return NormalizeGesture(modifiers.Count > 0 ? $"{string.Join("+", modifiers)}+{clean}" : clean);
    }

    private static string NormalizeEveXGesturePart(string value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "ctrl" or "control" => "Control",
            "alt" => "Alt",
            "shift" => "Shift",
            "win" or "windows" => "Win",
            var key => key.Length > 0 ? trimmed.Replace(" ", "") : "",
        };
    }

    private static string EveXString(JsonNode? node)
    {
        if (node == null) return "";
        try
        {
            return node.GetValue<string>()?.Trim() ?? "";
        }
        catch
        {
            return node.ToJsonString().Trim('"').Trim();
        }
    }

    private static bool EveXBool(JsonNode? node, bool fallback)
    {
        if (node == null) return fallback;
        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            var value = EveXString(node);
            if (int.TryParse(value, out var numeric)) return numeric != 0;
            if (bool.TryParse(value, out var boolean)) return boolean;
            return fallback;
        }
    }

    private static int EveXInt(JsonNode? node, int fallback)
    {
        var value = EveXString(node);
        return int.TryParse(value, out var numeric) ? numeric : fallback;
    }

    private static double EveXDouble(JsonNode? node, double fallback)
    {
        var value = EveXString(node);
        return double.TryParse(value, out var numeric) ? numeric : fallback;
    }

    private static double NormalizeEveXOpacity(JsonNode? node, double fallback)
    {
        var value = EveXDouble(node, fallback);
        if (value > 1) value /= 100.0;
        return Math.Max(0.2, Math.Min(1, value));
    }

    private static List<string> EveXStringList(JsonNode? node)
    {
        if (node is not JsonArray array) return new List<string>();

        return array
            .Select(item => EveXString(item))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryReadEveXRect(JsonObject? source, out TriffViewRect rect)
    {
        rect = new TriffViewRect();
        if (source == null) return false;

        foreach (var (_, node) in source)
        {
            if (TryParseEveXRect(node as JsonObject, out rect)) return true;
        }

        return false;
    }

    private static bool TryParseEveXRect(JsonObject? source, out TriffViewRect rect)
    {
        rect = new TriffViewRect();
        if (source == null) return false;

        var width = EveXInt(source["width"], 0);
        var height = EveXInt(source["height"], 0);
        if (width <= 0 || height <= 0) return false;

        rect = new TriffViewRect
        {
            X = EveXInt(source["x"], 0),
            Y = EveXInt(source["y"], 0),
            Width = width,
            Height = height,
        };
        return true;
    }

    private static string CleanImportedColor(string value, string fallback)
    {
        var clean = (value ?? "").Trim();
        if (clean.Length == 0) return fallback;
        if (clean.StartsWith("#", StringComparison.Ordinal)) return clean;
        if (clean.Length == 6 && clean.All(Uri.IsHexDigit)) return $"#{clean}";
        return ColorNameToHex(clean, fallback);
    }

    private void DeleteProfile(string? profileId)
    {
        if (Settings.Profiles.Count <= 1 || string.IsNullOrWhiteSpace(profileId)) return;
        var removed = Settings.Profiles.RemoveAll(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (removed <= 0) return;
        Settings.SelectedProfileId = Settings.Profiles[0].Id;
        Settings.Save();
        Refresh();
    }

    private void PostState(bool force = false)
    {
        var profile = Settings.ActiveProfile();
        var payload = new
        {
            type = "triffview:state",
            enabled = Settings.Enabled,
            hotkeysSuspended = Settings.HotkeysSuspended,
            settingsWindowAlwaysOnTop = Settings.SettingsWindowAlwaysOnTop,
            guideCompleted = Settings.GuideCompleted,
            guideVersion = Settings.GuideVersion,
            selectedProfileId = Settings.SelectedProfileId,
            profiles = Settings.Profiles.Select(profileItem => new
            {
                id = profileItem.Id,
                name = profileItem.Name,
            }).ToArray(),
            profile = profile.ToState(),
            clients = _clients.Select(client => new
            {
                handle = client.Handle.ToString("X"),
                title = client.Title,
                characterName = client.CharacterName,
                previewLabel = profile.PreviewLabelFor(client),
                processId = client.ProcessId,
                minimized = client.IsMinimized,
                foreground = client.IsForeground,
                key = client.StableKey,
            }).ToArray(),
            alerts = Settings.Alerts.ToState(),
            alertHistory = _alerts.History.Select(alert => alert.ToState()).ToArray(),
            hotkeyFailures = _overlay.HotkeyFailures,
            dwmAvailable = _overlay.DwmAvailable,
        };

        var json = JsonSerializer.Serialize(payload);
        if (!force && string.Equals(json, _lastPostedStateJson, StringComparison.Ordinal)) return;
        _lastPostedStateJson = json;
        _postToHud(payload);
    }

    private void MarkActiveClient(nint activeHandle)
    {
        if (_clients.Count == 0) return;

        _clients = _clients
            .Select(client => client with { IsForeground = client.Handle == activeHandle })
            .ToArray();
        _lastClientStateSignature = ClientStateSignature(_clients, activeHandle);

        _overlay.MarkActiveClient(activeHandle);
        ScheduleSwitchStatePost();
    }

    private void ScheduleSwitchStatePost()
    {
        _switchStateTimer.Stop();
        _switchStateTimer.Start();
    }

    private bool ActivateWindow(EveClientWindow client, TriffViewProfile profile)
    {
        var activated = TriffViewNativeMethods.SetForegroundWindow(client.Handle);
        TriffViewNativeMethods.SetFocus(client.Handle);

        if (profile.AlwaysMaximizeClients)
        {
            TriffViewNativeMethods.ShowWindowAsync(client.Handle, TriffViewNativeMethods.SwMaximize);
            activated |= TriffViewNativeMethods.SetForegroundWindow(client.Handle);
            return activated || TriffViewNativeMethods.GetForegroundWindow() == client.Handle;
        }

        if (TriffViewNativeMethods.IsIconic(client.Handle))
        {
            TriffViewNativeMethods.ShowWindowAsync(client.Handle, TriffViewNativeMethods.SwRestore);
            activated |= TriffViewNativeMethods.SetForegroundWindow(client.Handle);
        }

        return activated || TriffViewNativeMethods.GetForegroundWindow() == client.Handle;
    }

    private void PostError(string action, string message)
    {
        _postToHud(new
        {
            type = "triffview:error",
            action,
            message,
        });
    }

    private static int ClampInt(JsonNode? node, int min, int max, int fallback)
    {
        var value = node?.GetValue<int>() ?? fallback;
        return Math.Max(min, Math.Min(max, value));
    }

    private static double ClampDouble(JsonNode? node, double min, double max, double fallback)
    {
        var value = node?.GetValue<double>() ?? fallback;
        return Math.Max(min, Math.Min(max, value));
    }

    private static string CleanColor(JsonNode? node, string fallback)
    {
        var color = node?.GetValue<string>()?.Trim() ?? "";
        if (color.Length == 7 && color[0] == '#') return color;
        if (color.Length == 9 && color[0] == '#') return color;
        return fallback;
    }

    private static List<string> SplitLines(string? value)
    {
        return (value ?? "")
            .Split(new[] { "\r\n", "\n", "\r", "," }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string> ParseStringMap(JsonObject? value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value == null) return result;

        foreach (var (key, node) in value)
        {
            var cleanKey = key.Trim();
            var cleanValue = node?.GetValue<string>()?.Trim() ?? "";
            if (cleanKey.Length == 0 || cleanValue.Length == 0) continue;
            result[cleanKey] = cleanValue;
        }

        return result;
    }

    private static List<TriffViewHotkeyBinding> ParseDirectHotkeys(string? value)
    {
        return (value ?? "")
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => new TriffViewHotkeyBinding
            {
                CharacterName = parts[0],
                Gestures = CleanGestureList(new[] { parts[1] }),
                Enabled = true,
            })
            .Where(binding => binding.Gestures.Count > 0)
            .ToList();
    }

    private static List<TriffViewCycleGroup> ParseCycleGroups(string? value)
    {
        var groups = new List<TriffViewCycleGroup>();
        foreach (var line in (value ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) continue;
            var name = parts[0].Length > 0 ? parts[0] : "Cycle group";
            groups.Add(new TriffViewCycleGroup
            {
                Id = TriffViewCycleGroup.IdFromName(name),
                Name = name,
                ForwardGestures = CleanGestureList(new[] { parts[1] }),
                BackwardGestures = CleanGestureList(new[] { parts[2] }),
                Characters = parts.Length >= 4 ? SplitLines(parts[3]) : new List<string>(),
                Enabled = true,
            });
        }

        return groups;
    }

    private static List<TriffViewCycleGroup> ImportEveXCycleGroups(JsonObject? sourceGroups)
    {
        if (sourceGroups == null) return new List<TriffViewCycleGroup>();

        var groups = new List<TriffViewCycleGroup>();
        foreach (var (groupName, node) in sourceGroups)
        {
            if (node is not JsonObject sourceGroup) continue;

            var group = new TriffViewCycleGroup
            {
                Name = string.IsNullOrWhiteSpace(groupName) ? "Imported Group" : groupName.Trim(),
                ForwardGestures = CleanGestureList(new[] { NormalizeEveXGesture(EveXString(sourceGroup["ForwardsHotkey"])) }),
                BackwardGestures = CleanGestureList(new[] { NormalizeEveXGesture(EveXString(sourceGroup["BackwardsHotkey"])) }),
                Characters = EveXStringList(sourceGroup["Characters"])
                    .Select(NormalizeEveWindowTitle)
                    .Where(name => name.Length > 0)
                    .ToList(),
                Enabled = true,
            };
            group.Normalize();
            if (group.ForwardGestures.Count > 0 || group.BackwardGestures.Count > 0 || group.Characters.Count > 0)
            {
                groups.Add(group);
            }
        }

        return groups;
    }
}

internal sealed record TriffViewPreviewAlert(
    int SeverityRank,
    string Color,
    int Thickness,
    int DurationMs,
    int PulseCount
);

internal sealed class ActivePreviewAlert
{
    public ActivePreviewAlert(int severityRank, string color, int thickness, int durationMs, int pulseCount, DateTime startedUtc, DateTime expiresUtc)
    {
        SeverityRank = severityRank;
        Color = color;
        Thickness = thickness;
        DurationMs = durationMs;
        PulseCount = pulseCount;
        StartedUtc = startedUtc;
        ExpiresUtc = expiresUtc;
    }

    public int SeverityRank { get; }
    public string Color { get; }
    public int Thickness { get; }
    public int DurationMs { get; }
    public int PulseCount { get; }
    public DateTime StartedUtc { get; }
    public DateTime ExpiresUtc { get; set; }
}

internal sealed class TriffViewOverlayForm : Forms.Form
{
    private const int ResizeHitSize = 16;
    private const int DragThreshold = 4;
    private readonly Dictionary<nint, PreviewState> _previews = new();
    private readonly Dictionary<int, TriffViewHotkeyCommand> _hotkeys = new();
    private readonly Forms.Timer _alertTimer = new() { Interval = 80 };
    private readonly TriffViewLabelOverlayForm _labelOverlay = new();
    private string _hotkeySignature = "";
    private string _windowRegionSignature = "";
    private int _nextHotkeyId = 3000;
    private PreviewState? _mousePreview;
    private MouseMode _mouseMode = MouseMode.None;
    private Point _mouseDownPoint;
    private Rectangle _mouseStartRect;
    private TriffViewProfile _profile = TriffViewProfile.CreateDefault("Default");
    private nint _foreground;
    private Rectangle _virtualDesktop;
    private IReadOnlyList<EveClientWindow> _clients = Array.Empty<EveClientWindow>();
    private bool? _appliedTopmost;
    private bool _suppressLabelOverlay;

    public event Action<EveClientWindow>? ActivateRequested;
    public event Action<EveClientWindow>? MinimizeRequested;
    public event Action<string, TriffViewRect>? PreviewLayoutChanged;
    public event Action<TriffViewHotkeyCommand>? HotkeyPressed;

    public bool AllowTopmost { get; set; } = true;
    public bool DwmAvailable { get; private set; } = true;
    public IReadOnlyList<string> HotkeyFailures { get; private set; } = Array.Empty<string>();

    public TriffViewOverlayForm()
    {
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        BackColor = Color.FromArgb(5, 7, 11);
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9, FontStyle.Regular);
        _labelOverlay.Owner = this;
        _alertTimer.Tick += (_, _) => TickAlertFlashes();
        SizeToVirtualDesktop();
        UpdateWindowRegion(forceEmpty: true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= TriffViewNativeMethods.WsExToolWindow | TriffViewNativeMethods.WsExNoActivate;
            if (AllowTopmost) cp.ExStyle |= TriffViewNativeMethods.WsExTopmost;
            return cp;
        }
    }

    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == TriffViewNativeMethods.WmHotkey && _hotkeys.TryGetValue(m.WParam.ToInt32(), out var command))
        {
            HotkeyPressed?.Invoke(command);
            return;
        }

        base.WndProc(ref m);
    }

    public void SizeToVirtualDesktop()
    {
        _virtualDesktop = ScreenGeometry.VirtualDesktopPixels();
        if (Bounds != _virtualDesktop)
        {
            SetBounds(_virtualDesktop.Left, _virtualDesktop.Top, _virtualDesktop.Width, _virtualDesktop.Height);
        }

        _labelOverlay.SetVirtualDesktop(_virtualDesktop);
    }

    public void ApplyTopmostPolicy(bool force = false)
    {
        if (!force && _appliedTopmost == AllowTopmost)
        {
            return;
        }

        _appliedTopmost = AllowTopmost;
        TopMost = AllowTopmost;
        TriffViewNativeMethods.SetWindowPos(
            Handle,
            AllowTopmost ? TriffViewNativeMethods.HwndTopmost : TriffViewNativeMethods.HwndNotTopmost,
            0,
            0,
            0,
            0,
            TriffViewNativeMethods.SwpNoMove | TriffViewNativeMethods.SwpNoSize | TriffViewNativeMethods.SwpNoActivate
        );
        _labelOverlay.AllowTopmost = AllowTopmost;
        _labelOverlay.ApplyTopmostPolicy(force);
    }

    public void SetClients(IReadOnlyList<EveClientWindow> clients, TriffViewProfile profile, nint foreground, bool suppressLostFocusHide = false, nint activeHandle = default)
    {
        _clients = clients;
        _profile = profile;
        _foreground = foreground;
        SizeToVirtualDesktop();

        var highlightHandle = activeHandle != nint.Zero ? activeHandle : foreground;
        DwmAvailable = TriffViewNativeMethods.DwmIsCompositionEnabled(out var compositionEnabled) == 0 && compositionEnabled;
        var desiredHandles = clients
            .Where(client => !(profile.HideActivePreview && client.Handle == highlightHandle))
            .Select(client => client.Handle)
            .ToHashSet();

        foreach (var handle in _previews.Keys.Where(handle => !desiredHandles.Contains(handle)).ToArray())
        {
            _previews[handle].Thumbnail.Dispose();
            _previews.Remove(handle);
        }

        var visibleIndex = 0;
        foreach (var client in clients)
        {
            if (profile.HideActivePreview && client.Handle == highlightHandle) continue;

            if (!_previews.TryGetValue(client.Handle, out var state))
            {
                state = new PreviewState(client, CreateThumbnail(client.Handle));
                _previews[client.Handle] = state;
            }

            state.Client = client;
            state.FrameRect = ResolveFrameRect(client, visibleIndex++);
            state.Active = client.Handle == highlightHandle;
            state.Visible = DwmAvailable;
            UpdateThumbnail(state);
        }

        var shouldHideForLostFocus = profile.HideOnLostFocus && !suppressLostFocusHide && clients.All(client => client.Handle != foreground);
        Opacity = shouldHideForLostFocus ? 0 : Math.Max(0.2, Math.Min(1, profile.Opacity));
        _suppressLabelOverlay = shouldHideForLostFocus;
        UpdateWindowRegion(shouldHideForLostFocus);
        RefreshLabelOverlay();
        Invalidate();
    }

    public IReadOnlyDictionary<string, TriffViewRect> CurrentPreviewLayouts()
    {
        return _previews.Values.ToDictionary(
            state => state.Client.StableKey,
            state => TriffViewRect.FromRectangle(state.FrameRect),
            StringComparer.OrdinalIgnoreCase
        );
    }

    public void MarkActiveClient(nint activeHandle)
    {
        if (activeHandle == nint.Zero) return;
        _foreground = activeHandle;
        _clients = _clients
            .Select(client => client with { IsForeground = client.Handle == activeHandle })
            .ToArray();
        var restoreFromLostFocus = _suppressLabelOverlay;
        if (restoreFromLostFocus)
        {
            Opacity = Math.Max(0.2, Math.Min(1, _profile.Opacity));
            _suppressLabelOverlay = false;
        }

        if (_profile.HideActivePreview)
        {
            SyncHiddenActivePreview(activeHandle);
            return;
        }

        foreach (var state in _previews.Values)
        {
            var active = state.Client.Handle == activeHandle;
            if (state.Active == active && state.Client.IsForeground == active) continue;

            state.Active = active;
            state.Client = state.Client with { IsForeground = active };
            Invalidate(ToClientRect(state.FrameRect));
        }

        if (restoreFromLostFocus)
        {
            UpdateWindowRegion();
            RefreshLabelOverlay();
        }
    }

    public void SyncClientStates(IReadOnlyList<EveClientWindow> clients, nint foreground, nint activeHandle)
    {
        _clients = clients;
        _foreground = foreground;

        if (_profile.HideActivePreview)
        {
            SyncHiddenActivePreview(activeHandle);
            return;
        }

        var clientsByHandle = clients.ToDictionary(client => client.Handle);
        foreach (var state in _previews.Values)
        {
            if (!clientsByHandle.TryGetValue(state.Client.Handle, out var client)) continue;
            var active = state.Client.Handle == activeHandle;
            var changed = state.Active != active || state.Client != client;
            state.Client = client;
            state.Active = active;
            if (changed) Invalidate(ToClientRect(state.FrameRect));
        }

        var shouldHideForLostFocus = _profile.HideOnLostFocus && clients.All(client => client.Handle != foreground);
        if (_suppressLabelOverlay == shouldHideForLostFocus) return;

        Opacity = shouldHideForLostFocus ? 0 : Math.Max(0.2, Math.Min(1, _profile.Opacity));
        _suppressLabelOverlay = shouldHideForLostFocus;
        UpdateWindowRegion(shouldHideForLostFocus);
        RefreshLabelOverlay();
    }

    private void SyncHiddenActivePreview(nint activeHandle)
    {
        foreach (var handle in _previews.Keys.Where(handle => handle == activeHandle).ToArray())
        {
            _previews[handle].Thumbnail.Dispose();
            _previews.Remove(handle);
        }

        var visibleIndex = 0;
        foreach (var client in _clients)
        {
            if (client.Handle == activeHandle) continue;

            if (!_previews.TryGetValue(client.Handle, out var state))
            {
                state = new PreviewState(client, CreateThumbnail(client.Handle))
                {
                    FrameRect = ResolveFrameRect(client, visibleIndex),
                };
                _previews[client.Handle] = state;
            }

            state.Client = client;
            state.Active = false;
            state.Visible = DwmAvailable;
            UpdateThumbnail(state);
            visibleIndex++;
        }

        UpdateWindowRegion();
        RefreshLabelOverlay();
        Invalidate();
    }

    public void ShowAlert(string characterName, TriffViewPreviewAlert alert)
    {
        if (string.IsNullOrWhiteSpace(characterName)) return;
        var now = DateTime.UtcNow;
        var matched = false;

        foreach (var state in _previews.Values)
        {
            if (!MatchesAlertTarget(state, characterName)) continue;
            state.SetAlert(alert, now);
            matched = true;
        }

        if (!matched) return;
        UpdateAlertTimer();
        Invalidate();
    }

    public void ConfigureHotkeys(TriffViewProfile profile, IReadOnlyList<EveClientWindow> clients, bool suspended)
    {
        var signature = HotkeySignature(profile, clients, suspended);
        if (string.Equals(signature, _hotkeySignature, StringComparison.Ordinal)) return;

        UnregisterHotkeys();
        _hotkeySignature = signature;
        if (suspended) return;

        var failures = new List<string>();
        var directHotkeyGroups = profile.DirectHotkeys
            .Where(binding => binding.Enabled
                && !string.IsNullOrWhiteSpace(binding.CharacterName)
                && binding.Gestures.Count > 0)
            .Where(binding => clients.Any(client => string.Equals(client.CharacterName, binding.CharacterName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(client.StableKey, binding.CharacterName, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(binding => binding.Gestures.Select(gesture => new
            {
                binding.CharacterName,
                Gesture = gesture,
            }))
            .GroupBy(binding => binding.Gesture, StringComparer.OrdinalIgnoreCase);

        foreach (var group in directHotkeyGroups)
        {
            var characterNames = group
                .Select(binding => binding.CharacterName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            RegisterHotkey(
                group.Key,
                new TriffViewHotkeyCommand(TriffViewHotkeyKind.Direct, "", "", 0, characterNames),
                failures
            );
        }

        foreach (var group in profile.ActiveCycleGroups())
        {
            if (group.Characters.Count == 0 && profile.CharacterOrder.Count == 0) continue;
            foreach (var gesture in group.ForwardGestures)
            {
                RegisterHotkey(gesture, new TriffViewHotkeyCommand(TriffViewHotkeyKind.Cycle, "", group.Id, 1), failures);
            }

            foreach (var gesture in group.BackwardGestures)
            {
                RegisterHotkey(gesture, new TriffViewHotkeyCommand(TriffViewHotkeyKind.Cycle, "", group.Id, -1), failures);
            }
        }

        HotkeyFailures = failures;
    }

    public void ClearThumbnails()
    {
        foreach (var state in _previews.Values)
        {
            state.Thumbnail.Dispose();
        }

        _previews.Clear();
        _windowRegionSignature = "";
        _alertTimer.Stop();
        _labelOverlay.SetItems(Array.Empty<TriffViewLabelOverlayItem>());
        UpdateWindowRegion();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnregisterHotkeys();
            ClearThumbnails();
            _alertTimer.Dispose();
            _labelOverlay.Dispose();
            Region?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            RefreshLabelOverlay();
        }
        else
        {
            _labelOverlay.SetItems(Array.Empty<TriffViewLabelOverlayItem>());
        }
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        foreach (var state in _previews.Values)
        {
            if (!state.Visible) continue;
            DrawPreviewChrome(e.Graphics, state);
        }
    }

    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        var state = HitPreview(e.Location);
        if (state == null) return;

        _mousePreview = state;
        _mouseDownPoint = e.Location;
        _mouseStartRect = state.FrameRect;
        Capture = true;

        if (e.Button == Forms.MouseButtons.Middle)
        {
            MinimizeRequested?.Invoke(state.Client);
            _mouseMode = MouseMode.None;
            return;
        }

        var absolutePoint = new Point(e.Location.X + _virtualDesktop.Left, e.Location.Y + _virtualDesktop.Top);
        if (!_profile.LockPreviews && HitResizeHandle(state.FrameRect, absolutePoint))
        {
            _mouseMode = MouseMode.Resize;
            return;
        }

        _mouseMode = e.Button == Forms.MouseButtons.Right && !_profile.LockPreviews ? MouseMode.Move : MouseMode.PendingClick;
    }

    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        if (_mousePreview == null || _mouseMode == MouseMode.None) return;

        var deltaX = e.Location.X - _mouseDownPoint.X;
        var deltaY = e.Location.Y - _mouseDownPoint.Y;
        if (_mouseMode == MouseMode.PendingClick && Math.Abs(deltaX) + Math.Abs(deltaY) > DragThreshold)
        {
            _mouseMode = _profile.LockPreviews ? MouseMode.None : MouseMode.Move;
        }

        if (_mouseMode == MouseMode.Move)
        {
            var next = _mouseStartRect;
            next.Offset(deltaX, deltaY);
            _mousePreview.FrameRect = Snap(next, _mousePreview);
            UpdateThumbnail(_mousePreview);
            UpdateWindowRegion();
            RefreshLabelOverlay();
            Invalidate();
        }
        else if (_mouseMode == MouseMode.Resize)
        {
            var next = _mouseStartRect;
            next.Width = Math.Max(120, next.Width + deltaX);
            next.Height = Math.Max(90, next.Height + deltaY);
            _mousePreview.FrameRect = Snap(next, _mousePreview);
            UpdateThumbnail(_mousePreview);
            UpdateWindowRegion();
            RefreshLabelOverlay();
            Invalidate();
        }
    }

    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        if (_mousePreview == null) return;

        var preview = _mousePreview;
        var mode = _mouseMode;
        Capture = false;
        _mousePreview = null;
        _mouseMode = MouseMode.None;

        if (mode is MouseMode.Move or MouseMode.Resize)
        {
            PreviewLayoutChanged?.Invoke(preview.Client.StableKey, TriffViewRect.FromRectangle(preview.FrameRect));
            return;
        }

        if (mode == MouseMode.PendingClick && e.Button == Forms.MouseButtons.Left)
        {
            ActivateRequested?.Invoke(preview.Client);
        }
    }

    private DwmThumbnail CreateThumbnail(nint source)
    {
        try
        {
            return new DwmThumbnail(Handle, source);
        }
        catch
        {
            DwmAvailable = false;
            return DwmThumbnail.Empty;
        }
    }

    private Rectangle ResolveFrameRect(EveClientWindow client, int index)
    {
        if (_profile.PreviewLayouts.TryGetValue(client.StableKey, out var saved) && saved.IsUsable)
        {
            return saved.ToRectangle();
        }

        if (string.IsNullOrWhiteSpace(client.CharacterName)
            && _profile.PreviewLayouts.TryGetValue(client.Title, out var titleSaved)
            && titleSaved.IsUsable)
        {
            var rect = titleSaved.ToRectangle();
            var sameTitleIndex = _clients
                .TakeWhile(item => item.Handle != client.Handle)
                .Count(item => string.Equals(item.Title, client.Title, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(item.CharacterName));
            if (sameTitleIndex > 0)
            {
                rect.Y += sameTitleIndex * (rect.Height + 10);
            }

            return ClampToVirtualDesktop(rect);
        }

        var primary = ScreenGeometry.PrimaryScreenPixels();
        var width = Math.Max(120, _profile.PreviewWidth);
        var height = Math.Max(90, _profile.PreviewHeight);
        var x = primary.Right - width - 18;
        var y = primary.Top + 82 + index * (height + 10);
        if (y + height > primary.Bottom - 18)
        {
            var column = (index * (height + 10)) / Math.Max(1, primary.Height - 120);
            var row = index % Math.Max(1, (primary.Height - 120) / Math.Max(1, height + 10));
            x = primary.Right - width - 18 - column * (width + 10);
            y = primary.Top + 82 + row * (height + 10);
        }

        return new Rectangle(x, y, width, height);
    }

    private void UpdateThumbnail(PreviewState state)
    {
        var thumbRect = ThumbnailRect(state.FrameRect);
        thumbRect.Offset(-_virtualDesktop.Left, -_virtualDesktop.Top);
        state.Thumbnail.Update(thumbRect, 255, state.Visible);
    }

    private void RefreshLabelOverlay()
    {
        if (!Visible
            || _suppressLabelOverlay
            || !_profile.ShowLabels
            || !_profile.LabelBackgroundTransparent)
        {
            _labelOverlay.SetItems(Array.Empty<TriffViewLabelOverlayItem>());
            return;
        }

        var items = _previews.Values
            .Where(state => state.Visible)
            .Select(state => new TriffViewLabelOverlayItem(
                ToClientRect(state.FrameRect),
                _profile.PreviewLabelFor(state.Client),
                ColorFromString(_profile.LabelTextColor, Color.FromArgb(217, 226, 238)),
                Math.Max(8, Math.Min(32, _profile.LabelFontSize)),
                LabelPosition(),
                Math.Max(1, _profile.BorderThickness)
            ))
            .ToArray();

        _labelOverlay.SetItems(items);
    }

    private Rectangle ThumbnailRect(Rectangle frameRect)
    {
        var border = Math.Max(1, _profile.BorderThickness);
        var labelHeight = ReservedLabelHeight();
        var labelPosition = LabelPosition();
        var topLabelHeight = labelPosition == "top" ? labelHeight : 0;
        var bottomLabelHeight = labelPosition == "bottom" ? labelHeight : 0;
        return new Rectangle(
            frameRect.Left + border,
            frameRect.Top + topLabelHeight + border,
            Math.Max(1, frameRect.Width - border * 2),
            Math.Max(1, frameRect.Height - topLabelHeight - bottomLabelHeight - border * 2)
        );
    }

    private int ReservedLabelHeight()
    {
        if (!_profile.ShowLabels || _profile.LabelBackgroundTransparent) return 0;
        return Math.Max(24, Math.Min(32, _profile.LabelFontSize) + 12);
    }

    private string LabelPosition()
    {
        var position = (_profile.LabelPosition ?? "top").Trim().ToLowerInvariant();
        if (!_profile.LabelBackgroundTransparent && position == "center") return "top";
        return position is "top" or "bottom" or "center" ? position : "top";
    }

    private Rectangle ToClientRect(Rectangle rect)
    {
        var next = rect;
        next.Offset(-_virtualDesktop.Left, -_virtualDesktop.Top);
        return next;
    }

    private void DrawPreviewChrome(Graphics graphics, PreviewState state)
    {
        var frame = ToClientRect(state.FrameRect);
        var borderThickness = state.Active && _profile.ShowActiveHighlight ? Math.Max(1, _profile.BorderThickness) : 1;
        var labelHeight = ReservedLabelHeight();
        var labelPosition = LabelPosition();
        var borderColor = state.Active && _profile.ShowActiveHighlight
            ? ColorFromString(_profile.ActiveBorderColor, Color.FromArgb(83, 182, 255))
            : ColorFromString(_profile.InactiveBorderColor, Color.FromArgb(115, 123, 140));

        if (!state.Active && !_profile.ShowInactiveBorders) borderColor = Color.Transparent;

        using var framePath = RoundedRect(frame, 6);
        using var borderPen = new Pen(borderColor, borderThickness)
        {
            Alignment = PenAlignment.Inset,
        };
        if (borderColor.A > 0)
        {
            graphics.DrawPath(borderPen, framePath);
        }

        if (_profile.ShowLabels && labelHeight > 0)
        {
            var labelTop = labelPosition == "bottom" ? frame.Bottom - labelHeight - 1 : frame.Top + 1;
            var labelRect = new Rectangle(frame.Left + 1, labelTop, Math.Max(1, frame.Width - 2), labelHeight);
            using var labelBrush = new SolidBrush(ColorFromString(_profile.LabelBackgroundColor, Color.FromArgb(210, 11, 17, 29)));
            graphics.FillRectangle(labelBrush, labelRect);

            var text = _profile.PreviewLabelFor(state.Client);
            using var labelFont = new Font(Font.FontFamily, Math.Max(8, Math.Min(32, _profile.LabelFontSize)), FontStyle.Regular, GraphicsUnit.Point);
            using var textBrush = new SolidBrush(ColorFromString(_profile.LabelTextColor, Color.FromArgb(217, 226, 238)));
            using var format = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                LineAlignment = StringAlignment.Center,
                Alignment = StringAlignment.Near,
            };
            var textRect = new RectangleF(labelRect.Left + 8, labelRect.Top, Math.Max(1, labelRect.Width - 36), labelRect.Height);
            graphics.DrawString(text, labelFont, textBrush, textRect, format);
        }

        var activeAlert = state.ActiveAlert(DateTime.UtcNow);
        if (activeAlert != null)
        {
            DrawAlertBorder(graphics, frame, activeAlert);
        }

        var handle = new Rectangle(frame.Right - ResizeHitSize, frame.Bottom - ResizeHitSize, ResizeHitSize - 3, ResizeHitSize - 3);
        using var handlePen = new Pen(Color.FromArgb(180, 217, 226, 238), 1);
        graphics.DrawLine(handlePen, handle.Right - 8, handle.Bottom - 2, handle.Right - 2, handle.Bottom - 8);
        graphics.DrawLine(handlePen, handle.Right - 13, handle.Bottom - 2, handle.Right - 2, handle.Bottom - 13);
    }

    private void DrawAlertBorder(Graphics graphics, Rectangle frame, ActivePreviewAlert alert)
    {
        var baseColor = ColorFromString(alert.Color, Color.FromArgb(255, 59, 59));
        var elapsed = Math.Max(0, (DateTime.UtcNow - alert.StartedUtc).TotalMilliseconds);
        var progress = Math.Min(1, elapsed / Math.Max(1, alert.DurationMs));
        var wave = (Math.Sin(progress * alert.PulseCount * Math.PI * 2) + 1) / 2;
        var alpha = (int)Math.Max(90, Math.Min(255, 110 + wave * 145));
        using var path = RoundedRect(frame, 6);
        using var pen = new Pen(Color.FromArgb(alpha, baseColor), Math.Max(1, alert.Thickness))
        {
            Alignment = PenAlignment.Inset,
            LineJoin = LineJoin.Round,
        };
        graphics.DrawPath(pen, path);
    }

    private void TickAlertFlashes()
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var state in _previews.Values)
        {
            removed |= state.ClearExpiredAlert(now);
        }

        var anyActive = _previews.Values.Any(state => state.ActiveAlert(now) != null);
        if (!anyActive) _alertTimer.Stop();
        if (removed || anyActive) Invalidate();
    }

    private void UpdateAlertTimer()
    {
        var anyActive = _previews.Values.Any(state => state.ActiveAlert(DateTime.UtcNow) != null);
        if (anyActive && !_alertTimer.Enabled) _alertTimer.Start();
        if (!anyActive && _alertTimer.Enabled) _alertTimer.Stop();
    }

    private static bool MatchesAlertTarget(PreviewState state, string characterName)
    {
        return string.Equals(state.Client.CharacterName, characterName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Client.StableKey, characterName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Client.Title, characterName, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateWindowRegion(bool forceEmpty = false)
    {
        var signature = forceEmpty
            ? "empty"
            : string.Join(";", _previews.Values
                .Where(state => state.Visible)
                .Select(state => $"{state.Client.Handle:X}:{state.FrameRect.X},{state.FrameRect.Y},{state.FrameRect.Width},{state.FrameRect.Height}"));
        if (string.Equals(signature, _windowRegionSignature, StringComparison.Ordinal)) return;
        _windowRegionSignature = signature;

        var region = new Region();
        region.MakeEmpty();

        foreach (var state in _previews.Values.Where(state => state.Visible && !forceEmpty))
        {
            region.Union(ToClientRect(state.FrameRect));
        }

        var oldRegion = Region;
        Region = region;
        oldRegion?.Dispose();
    }

    private PreviewState? HitPreview(Point clientPoint)
    {
        var absolute = new Point(clientPoint.X + _virtualDesktop.Left, clientPoint.Y + _virtualDesktop.Top);
        return _previews.Values
            .Where(state => state.Visible && state.FrameRect.Contains(absolute))
            .OrderByDescending(state => state.Active)
            .FirstOrDefault();
    }

    private static bool HitResizeHandle(Rectangle frame, Point clientPoint)
    {
        return clientPoint.X >= frame.Right - ResizeHitSize
            && clientPoint.X <= frame.Right
            && clientPoint.Y >= frame.Bottom - ResizeHitSize
            && clientPoint.Y <= frame.Bottom;
    }

    private Rectangle Snap(Rectangle rect, PreviewState moving)
    {
        rect = ClampToVirtualDesktop(rect);
        if (!_profile.SnapEnabled || _profile.SnapDistance <= 0) return rect;

        var snap = Math.Max(0, _profile.SnapDistance);
        foreach (var other in _previews.Values.Where(state => state != moving && state.Visible))
        {
            rect = SnapAxis(rect, other.FrameRect, snap);
        }

        var screen = _virtualDesktop;
        rect = SnapToLine(rect, screen.Left, Edge.Left, snap);
        rect = SnapToLine(rect, screen.Right, Edge.Right, snap);
        rect = SnapToLine(rect, screen.Top, Edge.Top, snap);
        rect = SnapToLine(rect, screen.Bottom, Edge.Bottom, snap);
        return ClampToVirtualDesktop(rect);
    }

    private Rectangle ClampToVirtualDesktop(Rectangle rect)
    {
        if (rect.Left < _virtualDesktop.Left) rect.X = _virtualDesktop.Left;
        if (rect.Top < _virtualDesktop.Top) rect.Y = _virtualDesktop.Top;
        if (rect.Right > _virtualDesktop.Right) rect.X = _virtualDesktop.Right - rect.Width;
        if (rect.Bottom > _virtualDesktop.Bottom) rect.Y = _virtualDesktop.Bottom - rect.Height;
        return rect;
    }

    private static Rectangle SnapAxis(Rectangle rect, Rectangle other, int snap)
    {
        rect = SnapToLine(rect, other.Left, Edge.Left, snap);
        rect = SnapToLine(rect, other.Right, Edge.Right, snap);
        rect = SnapToLine(rect, other.Top, Edge.Top, snap);
        rect = SnapToLine(rect, other.Bottom, Edge.Bottom, snap);
        return rect;
    }

    private static Rectangle SnapToLine(Rectangle rect, int line, Edge edge, int snap)
    {
        if (edge == Edge.Left && Math.Abs(rect.Left - line) <= snap) rect.X = line;
        if (edge == Edge.Right && Math.Abs(rect.Right - line) <= snap) rect.X = line - rect.Width;
        if (edge == Edge.Top && Math.Abs(rect.Top - line) <= snap) rect.Y = line;
        if (edge == Edge.Bottom && Math.Abs(rect.Bottom - line) <= snap) rect.Y = line - rect.Height;
        return rect;
    }

    private void RegisterHotkey(string gesture, TriffViewHotkeyCommand command, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return;

        if (!HotkeyGesture.TryParse(gesture, out var modifiers, out var key))
        {
            failures.Add($"{gesture}: unsupported hotkey");
            return;
        }

        var id = _nextHotkeyId++;
        if (!TriffViewNativeMethods.RegisterHotKey(Handle, id, modifiers, key))
        {
            var error = Marshal.GetLastWin32Error();
            failures.Add($"{gesture}: could not register hotkey (Windows error {error})");
            return;
        }

        _hotkeys[id] = command;
    }

    private void UnregisterHotkeys()
    {
        foreach (var id in _hotkeys.Keys.ToArray())
        {
            TriffViewNativeMethods.UnregisterHotKey(Handle, id);
        }

        _hotkeys.Clear();
        _nextHotkeyId = 3000;
        HotkeyFailures = Array.Empty<string>();
    }

    private static string HotkeySignature(TriffViewProfile profile, IReadOnlyList<EveClientWindow> clients, bool suspended)
    {
        if (suspended) return $"suspended:{profile.Id}";
        var direct = string.Join(";", profile.DirectHotkeys.Select(binding => $"{binding.Enabled}:{binding.CharacterName}:{string.Join(",", binding.Gestures)}"));
        var cycles = string.Join(";", profile.CycleGroups.Select(group => $"{group.Enabled}:{group.Id}:{string.Join(",", group.ForwardGestures)}:{string.Join(",", group.BackwardGestures)}:{string.Join(",", group.Characters)}"));
        var characterOrder = string.Join(",", profile.CharacterOrder);
        var clientKeys = string.Join(";", clients.Select(client => client.StableKey));
        return $"{profile.Id}|{profile.SelectedCycleGroupId}|{direct}|{cycles}|{characterOrder}|{clientKeys}";
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color ColorFromString(string value, Color fallback)
    {
        if (value.Length == 9 && value[0] == '#'
            && int.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var alpha)
            && int.TryParse(value.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var red)
            && int.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var green)
            && int.TryParse(value.AsSpan(7, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
        {
            return Color.FromArgb(alpha, red, green, blue);
        }

        try
        {
            return ColorTranslator.FromHtml(value);
        }
        catch
        {
            return fallback;
        }
    }

    private sealed class PreviewState
    {
        public PreviewState(EveClientWindow client, DwmThumbnail thumbnail)
        {
            Client = client;
            Thumbnail = thumbnail;
        }

        public EveClientWindow Client { get; set; }
        public DwmThumbnail Thumbnail { get; }
        public Rectangle FrameRect { get; set; }
        public bool Active { get; set; }
        public bool Visible { get; set; }
        private ActivePreviewAlert? Alert { get; set; }

        public void SetAlert(TriffViewPreviewAlert alert, DateTime now)
        {
            if (Alert != null && Alert.ExpiresUtc > now && Alert.SeverityRank > alert.SeverityRank)
            {
                Alert.ExpiresUtc = now.AddMilliseconds(Math.Max(1, alert.DurationMs));
                return;
            }

            Alert = new ActivePreviewAlert(
                alert.SeverityRank,
                alert.Color,
                alert.Thickness,
                alert.DurationMs,
                alert.PulseCount,
                now,
                now.AddMilliseconds(Math.Max(1, alert.DurationMs))
            );
        }

        public ActivePreviewAlert? ActiveAlert(DateTime now) => Alert != null && Alert.ExpiresUtc > now ? Alert : null;

        public bool ClearExpiredAlert(DateTime now)
        {
            if (Alert == null || Alert.ExpiresUtc > now) return false;
            Alert = null;
            return true;
        }
    }

    private enum MouseMode
    {
        None,
        PendingClick,
        Move,
        Resize,
    }

    private enum Edge
    {
        Left,
        Right,
        Top,
        Bottom,
    }
}

internal sealed record TriffViewLabelOverlayItem(
    Rectangle Frame,
    string Text,
    Color TextColor,
    int FontSize,
    string Position,
    int BorderThickness);

internal sealed class TriffViewLabelOverlayForm : Forms.Form
{
    private static readonly Color TransparentBackColor = Color.FromArgb(1, 2, 3);
    private IReadOnlyList<TriffViewLabelOverlayItem> _items = Array.Empty<TriffViewLabelOverlayItem>();
    private bool? _appliedTopmost;

    public TriffViewLabelOverlayForm()
    {
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        BackColor = TransparentBackColor;
        TransparencyKey = TransparentBackColor;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9, FontStyle.Regular);
    }

    public bool AllowTopmost { get; set; } = true;

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= TriffViewNativeMethods.WsExToolWindow
                | TriffViewNativeMethods.WsExNoActivate
                | TriffViewNativeMethods.WsExTransparent
                | TriffViewNativeMethods.WsExLayered;
            if (AllowTopmost) cp.ExStyle |= TriffViewNativeMethods.WsExTopmost;
            return cp;
        }
    }

    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == TriffViewNativeMethods.WmNcHitTest)
        {
            m.Result = TriffViewNativeMethods.HtTransparent;
            return;
        }

        base.WndProc(ref m);
    }

    public void SetVirtualDesktop(Rectangle virtualDesktop)
    {
        if (Bounds != virtualDesktop)
        {
            SetBounds(virtualDesktop.Left, virtualDesktop.Top, virtualDesktop.Width, virtualDesktop.Height);
        }
    }

    public void ApplyTopmostPolicy(bool force = false)
    {
        if (!force && _appliedTopmost == AllowTopmost) return;
        _appliedTopmost = AllowTopmost;
        TopMost = AllowTopmost;
        if (!IsHandleCreated) return;

        TriffViewNativeMethods.SetWindowPos(
            Handle,
            AllowTopmost ? TriffViewNativeMethods.HwndTopmost : TriffViewNativeMethods.HwndNotTopmost,
            0,
            0,
            0,
            0,
            TriffViewNativeMethods.SwpNoMove | TriffViewNativeMethods.SwpNoSize | TriffViewNativeMethods.SwpNoActivate
        );
    }

    public void SetItems(IReadOnlyList<TriffViewLabelOverlayItem> items)
    {
        _items = items;
        if (_items.Count == 0)
        {
            if (Visible) Hide();
            return;
        }

        if (!Visible)
        {
            Show();
            ApplyTopmostPolicy(force: true);
        }
        else
        {
            ApplyTopmostPolicy();
        }

        Invalidate();
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.Clear(TransparentBackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        foreach (var item in _items)
        {
            DrawItem(e.Graphics, item);
        }
    }

    private void DrawItem(Graphics graphics, TriffViewLabelOverlayItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Text)) return;

        var fontSize = Math.Max(8, Math.Min(32, item.FontSize));
        using var labelFont = new Font(Font.FontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(item.TextColor);
        using var shadowBrush = new SolidBrush(Color.FromArgb(205, 0, 0, 0));
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Center,
        };

        var textHeight = Math.Max(18, (int)Math.Ceiling(labelFont.GetHeight(graphics)) + 8);
        var inset = Math.Max(6, item.BorderThickness + 6);
        var labelTop = item.Position switch
        {
            "bottom" => item.Frame.Bottom - inset - textHeight,
            "center" => item.Frame.Top + (item.Frame.Height - textHeight) / 2,
            _ => item.Frame.Top + inset,
        };
        var labelRect = new RectangleF(
            item.Frame.Left + inset,
            labelTop,
            Math.Max(1, item.Frame.Width - inset * 2),
            textHeight
        );

        var shadowRect = labelRect;
        shadowRect.Offset(1, 1);
        graphics.DrawString(item.Text, labelFont, shadowBrush, shadowRect, format);
        graphics.DrawString(item.Text, labelFont, textBrush, labelRect, format);
    }
}

internal sealed class EveWindowTracker
{
    private readonly Dictionary<uint, string> _processNames = new();

    public IReadOnlyList<EveClientWindow> GetClients(nint foreground)
    {
        var clients = new List<EveClientWindow>();
        TriffViewNativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == nint.Zero || !TriffViewNativeMethods.IsWindowVisible(hwnd)) return true;
            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            TriffViewNativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;
            if (!LooksLikeEve(title, ProcessName(processId))) return true;

            clients.Add(new EveClientWindow(
                hwnd,
                title,
                CharacterNameFromTitle(title),
                processId,
                TriffViewNativeMethods.IsIconic(hwnd),
                hwnd == foreground
            ));
            return true;
        }, nint.Zero);

        return clients;
    }

    private string ProcessName(uint processId)
    {
        if (_processNames.TryGetValue(processId, out var cached)) return cached;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            cached = process.ProcessName;
        }
        catch
        {
            cached = "";
        }

        _processNames[processId] = cached;
        return cached;
    }

    private static string GetWindowTitle(nint hwnd)
    {
        var length = TriffViewNativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var builder = new StringBuilder(length + 1);
        TriffViewNativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static bool LooksLikeEve(string title, string processName)
    {
        if (title.Contains("Launcher", StringComparison.OrdinalIgnoreCase)) return false;
        if (processName.Contains("launcher", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsEveClientProcess(processName)) return false;

        return title.Length > 2;
    }

    private static bool IsEveClientProcess(string processName)
    {
        return processName.Equals("exefile", StringComparison.OrdinalIgnoreCase);
    }

    private static string CharacterNameFromTitle(string title)
    {
        if (title.StartsWith("EVE - ", StringComparison.OrdinalIgnoreCase))
        {
            return title[6..].Trim();
        }

        if (title.EndsWith(" - EVE", StringComparison.OrdinalIgnoreCase))
        {
            return title[..^6].Trim();
        }

        return title.Equals("EVE", StringComparison.OrdinalIgnoreCase) ? "" : title.Trim();
    }
}

internal sealed record EveClientWindow(
    nint Handle,
    string Title,
    string CharacterName,
    uint ProcessId,
    bool IsMinimized,
    bool IsForeground)
{
    public string StableKey => string.IsNullOrWhiteSpace(CharacterName) ? Handle.ToString("X") : CharacterName;
}

internal sealed class DwmThumbnail : IDisposable
{
    public static DwmThumbnail Empty { get; } = new();

    private nint _handle;
    private bool _disposed;
    private bool _hasLastUpdate;
    private Rectangle _lastDestination;
    private byte _lastOpacity;
    private bool _lastVisible;

    private DwmThumbnail()
    {
    }

    public DwmThumbnail(nint destination, nint source)
    {
        var result = TriffViewNativeMethods.DwmRegisterThumbnail(destination, source, out _handle);
        if (result != 0 || _handle == nint.Zero)
        {
            throw new InvalidOperationException($"DwmRegisterThumbnail failed: 0x{result:X8}");
        }
    }

    public void Update(Rectangle destination, byte opacity, bool visible)
    {
        if (_disposed || _handle == nint.Zero) return;
        if (_hasLastUpdate && _lastDestination == destination && _lastOpacity == opacity && _lastVisible == visible)
        {
            return;
        }

        var properties = new TriffViewNativeMethods.DwmThumbnailProperties
        {
            Flags = TriffViewNativeMethods.DwmTnpRectDestination
                | TriffViewNativeMethods.DwmTnpVisible
                | TriffViewNativeMethods.DwmTnpOpacity
                | TriffViewNativeMethods.DwmTnpSourceClientAreaOnly,
            Destination = TriffViewNativeMethods.NativeRect.FromRectangle(destination),
            Opacity = opacity,
            Visible = visible,
            SourceClientAreaOnly = true,
        };

        if (TriffViewNativeMethods.DwmUpdateThumbnailProperties(_handle, ref properties) == 0)
        {
            _hasLastUpdate = true;
            _lastDestination = destination;
            _lastOpacity = opacity;
            _lastVisible = visible;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != nint.Zero)
        {
            TriffViewNativeMethods.DwmUnregisterThumbnail(_handle);
            _handle = nint.Zero;
        }
    }
}

internal sealed class TriffViewSettings
{
    public const string CurrentGuideVersion = "triffview-guide-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public bool Enabled { get; set; }
    public bool HotkeysSuspended { get; set; }
    public bool SettingsWindowAlwaysOnTop { get; set; } = true;
    public bool GuideCompleted { get; set; }
    public string GuideVersion { get; set; } = "";
    public string SelectedProfileId { get; set; } = "default";
    public TriffAlertsSettings Alerts { get; set; } = TriffAlertsSettings.CreateDefault();
    public List<TriffViewProfile> Profiles { get; set; } = new() { TriffViewProfile.CreateDefault("Default") };

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TriffHud", "triffview-settings.json");

    public static TriffViewSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return FromJson(File.ReadAllText(SettingsPath));
            }
        }
        catch
        {
            // Corrupt settings should not stop the HUD from launching.
        }

        var defaults = new TriffViewSettings();
        defaults.Normalize();
        return defaults;
    }

    public static TriffViewSettings FromJson(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root == null || (root["profiles"] is not JsonArray && root["Profiles"] is not JsonArray))
        {
            throw new InvalidDataException("The selected file is not a TriffView settings backup.");
        }

        var settings = JsonSerializer.Deserialize<TriffViewSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("The selected file could not be read as a TriffView settings backup.");
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, ToJson());
    }

    public string ToJson()
    {
        Normalize();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public TriffViewProfile ActiveProfile()
    {
        Normalize();
        return Profiles.First(profile => string.Equals(profile.Id, SelectedProfileId, StringComparison.OrdinalIgnoreCase));
    }

    public TriffViewProfile ActiveProfileFast()
    {
        return Profiles.FirstOrDefault(profile => string.Equals(profile.Id, SelectedProfileId, StringComparison.OrdinalIgnoreCase))
            ?? ActiveProfile();
    }

    private void Normalize()
    {
        GuideVersion = GuideVersion?.Trim() ?? "";
        if (!GuideCompleted) GuideVersion = "";
        Alerts ??= TriffAlertsSettings.CreateDefault();
        Alerts.Normalize();
        if (Profiles.Count == 0) Profiles.Add(TriffViewProfile.CreateDefault("Default"));
        foreach (var profile in Profiles) profile.Normalize();
        if (Profiles.All(profile => !string.Equals(profile.Id, SelectedProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedProfileId = Profiles[0].Id;
        }
    }
}

internal sealed class TriffViewProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "Default";
    public int PreviewWidth { get; set; } = 320;
    public int PreviewHeight { get; set; } = 204;
    public double Opacity { get; set; } = 1;
    public bool ShowLabels { get; set; } = true;
    public bool ShowActiveHighlight { get; set; } = true;
    public bool ShowInactiveBorders { get; set; } = true;
    public bool LockPreviews { get; set; }
    public int BorderThickness { get; set; } = 2;
    public bool SnapEnabled { get; set; } = true;
    public int SnapDistance { get; set; } = 18;
    public bool HideActivePreview { get; set; }
    public bool HideOnLostFocus { get; set; }
    public bool MinimizeInactiveClients { get; set; }
    public bool AlwaysMaximizeClients { get; set; }
    public bool AutoRestoreClientLayouts { get; set; }
    public bool HotkeysRequireEveForeground { get; set; } = true;
    public string ActiveBorderColor { get; set; } = "#53B6FF";
    public string InactiveBorderColor { get; set; } = "#737B8C";
    public string LabelTextColor { get; set; } = "#D9E2EE";
    public string LabelBackgroundColor { get; set; } = "#D20B111D";
    public bool LabelBackgroundTransparent { get; set; }
    public string LabelPosition { get; set; } = "top";
    public int LabelFontSize { get; set; } = 9;
    public List<string> CharacterOrder { get; set; } = new();
    public List<string> HiddenClients { get; set; } = new();
    public List<string> NeverMinimizeClients { get; set; } = new();
    public string SelectedCycleGroupId { get; set; } = "";
    public Dictionary<string, string> PreviewLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ClientColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TriffViewRect> PreviewLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TriffViewClientPlacement> ClientPlacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TriffViewHotkeyBinding> DirectHotkeys { get; set; } = new();
    public List<TriffViewCycleGroup> CycleGroups { get; set; } = new();

    public static TriffViewProfile CreateDefault(string name)
    {
        return new TriffViewProfile
        {
            Id = string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ? "default" : Guid.NewGuid().ToString("N"),
            Name = name,
        };
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(Name)) Name = "Profile";
        PreviewWidth = Math.Max(120, PreviewWidth);
        PreviewHeight = Math.Max(90, PreviewHeight);
        Opacity = Math.Max(0.2, Math.Min(1, Opacity));
        BorderThickness = Math.Max(1, Math.Min(16, BorderThickness));
        LabelFontSize = Math.Max(8, Math.Min(32, LabelFontSize));
        LabelPosition = NormalizeLabelPosition(LabelPosition, LabelBackgroundTransparent);
        SnapDistance = Math.Max(0, Math.Min(80, SnapDistance));
        CharacterOrder = CleanList(CharacterOrder);
        HiddenClients = CleanList(HiddenClients);
        NeverMinimizeClients = CleanList(NeverMinimizeClients);
        DirectHotkeys ??= new List<TriffViewHotkeyBinding>();
        CycleGroups ??= new List<TriffViewCycleGroup>();
        foreach (var binding in DirectHotkeys) binding.Normalize();
        foreach (var group in CycleGroups) group.Normalize();
        DirectHotkeys = DirectHotkeys
            .Where(binding => binding.CharacterName.Length > 0 && binding.Gestures.Count > 0)
            .ToList();
        if (CycleGroups.Count > 0 && CycleGroups.All(group => !string.Equals(group.Id, SelectedCycleGroupId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedCycleGroupId = CycleGroups[0].Id;
        }
        if (CycleGroups.Count == 0)
        {
            SelectedCycleGroupId = "";
        }
        PreviewLabels = CleanMap(PreviewLabels);
        PreviewLayouts ??= new Dictionary<string, TriffViewRect>(StringComparer.OrdinalIgnoreCase);
        ClientPlacements ??= new Dictionary<string, TriffViewClientPlacement>(StringComparer.OrdinalIgnoreCase);
        ClientColors ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public object ToState()
    {
        return new
        {
            id = Id,
            name = Name,
            previewWidth = PreviewWidth,
            previewHeight = PreviewHeight,
            opacity = Opacity,
            showLabels = ShowLabels,
            showActiveHighlight = ShowActiveHighlight,
            showInactiveBorders = ShowInactiveBorders,
            lockPreviews = LockPreviews,
            borderThickness = BorderThickness,
            snapEnabled = SnapEnabled,
            snapDistance = SnapDistance,
            hideActivePreview = HideActivePreview,
            hideOnLostFocus = HideOnLostFocus,
            minimizeInactiveClients = MinimizeInactiveClients,
            alwaysMaximizeClients = AlwaysMaximizeClients,
            autoRestoreClientLayouts = AutoRestoreClientLayouts,
            hotkeysRequireEveForeground = HotkeysRequireEveForeground,
            activeBorderColor = ActiveBorderColor,
            inactiveBorderColor = InactiveBorderColor,
            labelTextColor = LabelTextColor,
            labelBackgroundColor = LabelBackgroundColor,
            labelBackgroundTransparent = LabelBackgroundTransparent,
            labelPosition = LabelPosition,
            labelFontSize = LabelFontSize,
            characterOrderText = string.Join(Environment.NewLine, CharacterOrder),
            hiddenClientsText = string.Join(Environment.NewLine, HiddenClients),
            neverMinimizeClientsText = string.Join(Environment.NewLine, NeverMinimizeClients),
            selectedCycleGroupId = SelectedCycleGroupId,
            previewLabels = PreviewLabels,
            directHotkeys = DirectHotkeys.Select(binding => new
            {
                characterName = binding.CharacterName,
                gesture = binding.Gesture,
                gestures = binding.Gestures,
                enabled = binding.Enabled,
            }).ToArray(),
            cycleGroups = CycleGroups.Select(group => new
            {
                id = group.Id,
                name = group.Name,
                forwardGesture = group.ForwardGesture,
                backwardGesture = group.BackwardGesture,
                forwardGestures = group.ForwardGestures,
                backwardGestures = group.BackwardGestures,
                charactersText = string.Join(Environment.NewLine, group.Characters),
                enabled = group.Enabled,
            }).ToArray(),
            directHotkeysText = string.Join(Environment.NewLine, DirectHotkeys.Select(binding => $"{binding.CharacterName} = {string.Join(", ", binding.Gestures)}")),
            cycleGroupsText = string.Join(Environment.NewLine, CycleGroups.Select(group => $"{group.Name}|{string.Join(", ", group.ForwardGestures)}|{string.Join(", ", group.BackwardGestures)}|{string.Join(",", group.Characters)}")),
        };
    }

    public IEnumerable<TriffViewCycleGroup> ActiveCycleGroups()
    {
        Normalize();
        var active = CycleGroups.FirstOrDefault(group => group.Enabled
            && string.Equals(group.Id, SelectedCycleGroupId, StringComparison.OrdinalIgnoreCase));
        return active != null ? new[] { active } : CycleGroups.Where(group => group.Enabled).Take(1);
    }

    public string PreviewLabelFor(EveClientWindow client)
    {
        if (PreviewLabels.TryGetValue(client.StableKey, out var stableLabel) && !string.IsNullOrWhiteSpace(stableLabel))
        {
            return stableLabel;
        }

        if (!string.IsNullOrWhiteSpace(client.CharacterName)
            && PreviewLabels.TryGetValue(client.CharacterName, out var characterLabel)
            && !string.IsNullOrWhiteSpace(characterLabel))
        {
            return characterLabel;
        }

        if (!string.IsNullOrWhiteSpace(client.Title)
            && PreviewLabels.TryGetValue(client.Title, out var titleLabel)
            && !string.IsNullOrWhiteSpace(titleLabel))
        {
            return titleLabel;
        }

        return string.IsNullOrWhiteSpace(client.CharacterName) ? client.Title : client.CharacterName;
    }

    private static string NormalizeLabelPosition(string? value, bool transparent)
    {
        var clean = (value ?? "top").Trim().ToLowerInvariant();
        if (!transparent && clean == "center") return "top";
        return clean is "top" or "bottom" or "center" ? clean : "top";
    }

    private static List<string> CleanList(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string> CleanMap(IDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values == null) return result;

        foreach (var (key, value) in values)
        {
            var cleanKey = key.Trim();
            var cleanValue = value?.Trim() ?? "";
            if (cleanKey.Length == 0 || cleanValue.Length == 0) continue;
            result[cleanKey] = cleanValue;
        }

        return result;
    }
}

internal sealed class TriffViewRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool IsUsable => Width >= 80 && Height >= 60;

    public Rectangle ToRectangle() => new(X, Y, Width, Height);

    public static TriffViewRect FromRectangle(Rectangle rect)
    {
        return new TriffViewRect
        {
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
        };
    }
}

internal sealed class TriffViewClientPlacement
{
    public string Title { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

internal sealed class TriffViewHotkeyBinding
{
    public string CharacterName { get; set; } = "";
    public string Gesture { get; set; } = "";
    public List<string> Gestures { get; set; } = new();
    public bool Enabled { get; set; } = true;

    public void Normalize()
    {
        CharacterName = CharacterName.Trim();
        Gestures ??= new List<string>();
        Gestures = CleanGestureList(Gestures.Count > 0 ? Gestures : new[] { Gesture });
        Gesture = Gestures.FirstOrDefault() ?? "";
    }

    private static List<string> CleanGestureList(IEnumerable<string>? gestures)
    {
        return (gestures ?? Array.Empty<string>())
            .SelectMany(gesture => (gesture ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(gesture => gesture.Trim().Replace("Ctrl+", "Control+", StringComparison.OrdinalIgnoreCase))
            .Where(gesture => gesture.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}

internal sealed class TriffViewCycleGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Cycle";
    public List<string> Characters { get; set; } = new();
    public string ForwardGesture { get; set; } = "";
    public string BackwardGesture { get; set; } = "";
    public List<string> ForwardGestures { get; set; } = new();
    public List<string> BackwardGestures { get; set; } = new();
    public bool Enabled { get; set; } = true;

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Name)) Name = "Cycle";
        Id = IdFromName(Name);
        ForwardGestures ??= new List<string>();
        BackwardGestures ??= new List<string>();
        ForwardGestures = CleanGestureList(ForwardGestures.Count > 0 ? ForwardGestures : new[] { ForwardGesture });
        BackwardGestures = CleanGestureList(BackwardGestures.Count > 0 ? BackwardGestures : new[] { BackwardGesture });
        ForwardGesture = ForwardGestures.FirstOrDefault() ?? "";
        BackwardGesture = BackwardGestures.FirstOrDefault() ?? "";
        Characters = Characters
            .Select(character => character.Trim())
            .Where(character => character.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string IdFromName(string name)
    {
        var clean = new string((name ?? "")
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        clean = clean.Trim('-');
        return string.IsNullOrWhiteSpace(clean) ? Guid.NewGuid().ToString("N") : clean;
    }

    private static List<string> CleanGestureList(IEnumerable<string>? gestures)
    {
        return (gestures ?? Array.Empty<string>())
            .SelectMany(gesture => (gesture ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(gesture => gesture.Trim().Replace("Ctrl+", "Control+", StringComparison.OrdinalIgnoreCase))
            .Where(gesture => gesture.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal sealed class TriffViewHotkeyCommand
{
    public TriffViewHotkeyCommand(
        TriffViewHotkeyKind kind,
        string characterName,
        string groupId,
        int direction,
        IReadOnlyList<string>? characterNames = null)
    {
        Kind = kind;
        CharacterName = characterName;
        GroupId = groupId;
        Direction = direction;
        CharacterNames = characterNames ?? Array.Empty<string>();
    }

    public TriffViewHotkeyKind Kind { get; }
    public string CharacterName { get; }
    public string GroupId { get; }
    public int Direction { get; }
    public IReadOnlyList<string> CharacterNames { get; }
}

internal enum TriffViewHotkeyKind
{
    Direct,
    Cycle,
}

internal static class HotkeyGesture
{
    public static bool TryParse(string gesture, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        foreach (var part in parts.Take(parts.Length - 1))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= TriffViewNativeMethods.ModControl;
                    break;
                case "alt":
                    modifiers |= TriffViewNativeMethods.ModAlt;
                    break;
                case "shift":
                    modifiers |= TriffViewNativeMethods.ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= TriffViewNativeMethods.ModWin;
                    break;
                case "norepeat":
                    modifiers |= TriffViewNativeMethods.ModNoRepeat;
                    break;
                default:
                    return false;
            }
        }

        var keyName = parts[^1].Trim();
        switch (keyName.ToLowerInvariant())
        {
            case "`":
            case "~":
            case "grave":
            case "backquote":
            case "tilde":
            case "oem3":
            case "oemtilde":
                key = (uint)Forms.Keys.Oemtilde;
                return true;
            case "plus":
                key = (uint)Forms.Keys.Oemplus;
                return true;
            case "minus":
                key = (uint)Forms.Keys.OemMinus;
                return true;
            case "comma":
                key = (uint)Forms.Keys.Oemcomma;
                return true;
            case "period":
                key = (uint)Forms.Keys.OemPeriod;
                return true;
            case "[":
            case "openbracket":
            case "openbrackets":
            case "leftbracket":
            case "leftbrackets":
            case "oemopenbrackets":
                key = (uint)Forms.Keys.OemOpenBrackets;
                return true;
            case "]":
            case "closebracket":
            case "closebrackets":
            case "rightbracket":
            case "rightbrackets":
            case "oemclosebrackets":
                key = (uint)Forms.Keys.OemCloseBrackets;
                return true;
            case "cancel":
                key = 0x03;
                return true;
            case "pause":
                key = (uint)Forms.Keys.Pause;
                return true;
            case "capslock":
            case "capital":
                key = (uint)Forms.Keys.CapsLock;
                return true;
            case "printscreen":
            case "snapshot":
                key = (uint)Forms.Keys.PrintScreen;
                return true;
            case "apps":
            case "menu":
            case "contextmenu":
                key = (uint)Forms.Keys.Apps;
                return true;
            case "sleep":
                key = (uint)Forms.Keys.Sleep;
                return true;
            case "numpadmultiply":
            case "multiply":
                key = (uint)Forms.Keys.Multiply;
                return true;
            case "numpadadd":
            case "add":
                key = (uint)Forms.Keys.Add;
                return true;
            case "numpadseparator":
            case "separator":
                key = (uint)Forms.Keys.Separator;
                return true;
            case "numpadsubtract":
            case "subtract":
                key = (uint)Forms.Keys.Subtract;
                return true;
            case "numpaddecimal":
            case "decimal":
                key = (uint)Forms.Keys.Decimal;
                return true;
            case "numpaddivide":
            case "divide":
                key = (uint)Forms.Keys.Divide;
                return true;
            case "numlock":
                key = (uint)Forms.Keys.NumLock;
                return true;
            case "scrolllock":
                key = (uint)Forms.Keys.Scroll;
                return true;
            case "browserback":
                key = (uint)Forms.Keys.BrowserBack;
                return true;
            case "browserforward":
                key = (uint)Forms.Keys.BrowserForward;
                return true;
            case "browserrefresh":
                key = (uint)Forms.Keys.BrowserRefresh;
                return true;
            case "browserstop":
                key = (uint)Forms.Keys.BrowserStop;
                return true;
            case "browsersearch":
                key = (uint)Forms.Keys.BrowserSearch;
                return true;
            case "browserfavorites":
                key = (uint)Forms.Keys.BrowserFavorites;
                return true;
            case "browserhome":
                key = (uint)Forms.Keys.BrowserHome;
                return true;
            case "volumemute":
                key = (uint)Forms.Keys.VolumeMute;
                return true;
            case "volumedown":
                key = (uint)Forms.Keys.VolumeDown;
                return true;
            case "volumeup":
                key = (uint)Forms.Keys.VolumeUp;
                return true;
            case "medianexttrack":
                key = (uint)Forms.Keys.MediaNextTrack;
                return true;
            case "mediaprevioustrack":
                key = (uint)Forms.Keys.MediaPreviousTrack;
                return true;
            case "mediastop":
                key = (uint)Forms.Keys.MediaStop;
                return true;
            case "mediaplaypause":
                key = (uint)Forms.Keys.MediaPlayPause;
                return true;
            case "launchmail":
                key = (uint)Forms.Keys.LaunchMail;
                return true;
            case "selectmedia":
                key = (uint)Forms.Keys.SelectMedia;
                return true;
            case "launchapplication1":
                key = (uint)Forms.Keys.LaunchApplication1;
                return true;
            case "launchapplication2":
                key = (uint)Forms.Keys.LaunchApplication2;
                return true;
            case ";":
            case "semicolon":
            case "oemsemicolon":
                key = (uint)Forms.Keys.OemSemicolon;
                return true;
            case "/":
            case "slash":
            case "question":
            case "oemquestion":
                key = (uint)Forms.Keys.OemQuestion;
                return true;
            case "\\":
            case "backslash":
            case "pipe":
            case "oempipe":
                key = (uint)Forms.Keys.OemPipe;
                return true;
            case "'":
            case "\"":
            case "quote":
            case "quotes":
            case "apostrophe":
            case "oemquotes":
                key = (uint)Forms.Keys.OemQuotes;
                return true;
            case "oem8":
                key = (uint)Forms.Keys.Oem8;
                return true;
            case "oem102":
            case "oembackslash":
                key = (uint)Forms.Keys.OemBackslash;
                return true;
        }

        if (TryParseVirtualKeyLiteral(keyName, out key)) return true;

        if (keyName.Length == 1)
        {
            var c = char.ToUpperInvariant(keyName[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = (uint)c;
                return true;
            }
        }

        if (Enum.TryParse<Forms.Keys>(keyName, true, out var parsed))
        {
            key = (uint)parsed;
            return key != 0;
        }

        return false;
    }

    private static bool TryParseVirtualKeyLiteral(string keyName, out uint key)
    {
        key = 0;
        var value = keyName.Trim();
        var hasVirtualKeyPrefix = false;
        var isHex = false;

        if (value.StartsWith("VK_", StringComparison.OrdinalIgnoreCase))
        {
            value = value[3..];
            hasVirtualKeyPrefix = true;
        }
        else if (value.StartsWith("VK", StringComparison.OrdinalIgnoreCase) && value.Length > 2)
        {
            value = value[2..];
            hasVirtualKeyPrefix = true;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            hasVirtualKeyPrefix = true;
            isHex = true;
        }

        if (!hasVirtualKeyPrefix || value.Length == 0) return false;
        if (!uint.TryParse(
                value,
                isHex || HasHexLetter(value) ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)) return false;

        if (parsed == 0 || parsed > 0xFE) return false;
        key = parsed;
        return true;
    }

    private static bool HasHexLetter(string value)
    {
        foreach (var c in value)
        {
            if (c is >= 'A' and <= 'F' or >= 'a' and <= 'f') return true;
        }

        return false;
    }
}

internal static class TriffViewNativeMethods
{
    public const int WsExTopmost = 0x00000008;
    public const int WsExTransparent = 0x00000020;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExLayered = 0x00080000;
    public const int WmClose = 0x0010;
    public const int WmNcHitTest = 0x0084;
    public const int WmHotkey = 0x0312;
    public const int WmSysCommand = 0x0112;
    public static readonly nint ScMinimize = new(0xF020);
    public static readonly nint HtTransparent = new(-1);
    public const int SwRestore = 9;
    public const int SwMinimize = 6;
    public const int SwMaximize = 3;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;
    public const int DwmTnpRectDestination = 0x00000001;
    public const int DwmTnpRectSource = 0x00000002;
    public const int DwmTnpOpacity = 0x00000004;
    public const int DwmTnpVisible = 0x00000008;
    public const int DwmTnpSourceClientAreaOnly = 0x00000010;
    public static readonly nint HwndTop = new(0);
    public static readonly nint HwndTopmost = new(-1);
    public static readonly nint HwndNotTopmost = new(-2);

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindowAsync(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(nint hwnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowPlacement", SetLastError = true)]
    private static extern bool GetWindowPlacementNative(nint hwnd, ref WindowPlacement placement);

    public static bool GetWindowPlacement(nint hwnd, out WindowPlacement placement)
    {
        placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>(),
        };
        return GetWindowPlacementNative(hwnd, ref placement);
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(nint hwnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public static bool IsKeyDown(uint virtualKey)
    {
        return (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;
    }

    public static bool IsAnyControlDown()
    {
        return IsKeyDown((uint)Forms.Keys.ControlKey)
            || IsKeyDown((uint)Forms.Keys.LControlKey)
            || IsKeyDown((uint)Forms.Keys.RControlKey);
    }

    public static bool IsAnyAltDown()
    {
        return IsKeyDown((uint)Forms.Keys.Menu)
            || IsKeyDown((uint)Forms.Keys.LMenu)
            || IsKeyDown((uint)Forms.Keys.RMenu);
    }

    public static bool IsAnyShiftDown()
    {
        return IsKeyDown((uint)Forms.Keys.ShiftKey)
            || IsKeyDown((uint)Forms.Keys.LShiftKey)
            || IsKeyDown((uint)Forms.Keys.RShiftKey);
    }

    public static bool IsAnyWinDown()
    {
        return IsKeyDown((uint)Forms.Keys.LWin)
            || IsKeyDown((uint)Forms.Keys.RWin);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmRegisterThumbnail(nint destinationWindow, nint sourceWindow, out nint thumbnail);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmUnregisterThumbnail(nint thumbnail);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DwmThumbnailProperties properties);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public static NativeRect FromRectangle(Rectangle rect)
        {
            return new NativeRect
            {
                Left = rect.Left,
                Top = rect.Top,
                Right = rect.Right,
                Bottom = rect.Bottom,
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DwmThumbnailProperties
    {
        public int Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Visible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool SourceClientAreaOnly;
    }
}
