using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TriffView.EveSettings;
using TriffView.Preview;
using TriffView.TriffFleets;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace TriffView;

internal static class TriffToolsGuiTheme
{
    public const string Id = "triff-tools";
    public const string Name = "Triff.Tools";
    public const int DwmCaptionColor = 0x00140D09;
    public const int DwmBorderColor = 0x00231B13;
    public const int DwmTextColor = 0x00EEE2D9;
    public static readonly Drawing.Color Background = Drawing.Color.FromArgb(5, 7, 11);
    public static readonly Drawing.Color Text = Drawing.Color.FromArgb(217, 226, 238);
    public static readonly Drawing.Color Accent = Drawing.Color.FromArgb(83, 182, 255);
    public static readonly Drawing.Color Border = Drawing.Color.FromArgb(48, 54, 64);
    public static readonly Drawing.Color PanelBorder = Drawing.Color.FromArgb(44, 47, 54);
    public static readonly Drawing.Color PanelDark = Drawing.Color.FromArgb(7, 9, 16);
    public static readonly Drawing.Color TrayImageMargin = Drawing.Color.FromArgb(8, 13, 20);
    public static readonly Drawing.Color TraySelected = Drawing.Color.FromArgb(13, 34, 53);
    public static readonly Drawing.Color TrayPressed = Drawing.Color.FromArgb(11, 35, 53);
}

internal sealed class GuiThemePalette
{
    public string Id { get; init; } = TriffToolsGuiTheme.Id;
    public string Name { get; init; } = TriffToolsGuiTheme.Name;
    public Drawing.Color Background { get; init; } = TriffToolsGuiTheme.Background;
    public Drawing.Color Text { get; init; } = TriffToolsGuiTheme.Text;
    public Drawing.Color Accent { get; init; } = TriffToolsGuiTheme.Accent;
    public Drawing.Color Border { get; init; } = TriffToolsGuiTheme.Border;
    public Drawing.Color PanelBorder { get; init; } = TriffToolsGuiTheme.PanelBorder;
    public Drawing.Color PanelDark { get; init; } = TriffToolsGuiTheme.PanelDark;
    public Drawing.Color TrayImageMargin { get; init; } = TriffToolsGuiTheme.TrayImageMargin;
    public Drawing.Color TraySelected { get; init; } = TriffToolsGuiTheme.TraySelected;
    public Drawing.Color TrayPressed { get; init; } = TriffToolsGuiTheme.TrayPressed;
    public Drawing.Color Caption { get; init; } = Drawing.Color.FromArgb(9, 13, 20);
    public Drawing.Color WindowBorder { get; init; } = Drawing.Color.FromArgb(19, 27, 35);
    public Drawing.Color TitleText { get; init; } = TriffToolsGuiTheme.Text;

    public int DwmCaptionColor => ToColorRef(Caption);
    public int DwmBorderColor => ToColorRef(WindowBorder);
    public int DwmTextColor => ToColorRef(TitleText);

    public static GuiThemePalette TriffTools { get; } = new();

    public static GuiThemePalette FromMessage(JsonObject? theme, GuiThemePalette fallback)
    {
        if (theme == null) return fallback;
        return new GuiThemePalette
        {
            Id = ReadString(theme, "id", fallback.Id),
            Name = ReadString(theme, "name", fallback.Name),
            Background = ReadColor(theme, "background", fallback.Background),
            Text = ReadColor(theme, "text", fallback.Text),
            Accent = ReadColor(theme, "accent", fallback.Accent),
            Border = ReadColor(theme, "border", fallback.Border),
            PanelBorder = ReadColor(theme, "panelBorder", fallback.PanelBorder),
            PanelDark = ReadColor(theme, "panelDark", fallback.PanelDark),
            TrayImageMargin = ReadColor(theme, "trayImageMargin", fallback.TrayImageMargin),
            TraySelected = ReadColor(theme, "traySelected", fallback.TraySelected),
            TrayPressed = ReadColor(theme, "trayPressed", fallback.TrayPressed),
            Caption = ReadColor(theme, "caption", fallback.Caption),
            WindowBorder = ReadColor(theme, "windowBorder", fallback.WindowBorder),
            TitleText = ReadColor(theme, "titleText", fallback.TitleText),
        };
    }

    private static string ReadString(JsonObject theme, string key, string fallback)
    {
        return theme[key]?.GetValue<string>()?.Trim() is { Length: > 0 } value ? value : fallback;
    }

    private static Drawing.Color ReadColor(JsonObject theme, string key, Drawing.Color fallback)
    {
        var value = theme[key]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (value.StartsWith("#", StringComparison.Ordinal)) value = value[1..];
        if (value.Length != 6) return fallback;
        return int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? Drawing.Color.FromArgb((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff)
            : fallback;
    }

    private static int ToColorRef(Drawing.Color color)
    {
        return color.R | (color.G << 8) | (color.B << 16);
    }
}

public partial class MainWindow : Window
{
    private const string VirtualHostName = "app.triffview.local";
    private const string EmbeddedOverlayResourceName = "TriffView.Assets.overlay-dist.zip";
    private static readonly JsonSerializerOptions WebMessageJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20h1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private readonly string[] _args;
    private readonly TriffViewUpdateChecker _updateChecker;
    private readonly System.Windows.Threading.DispatcherTimer _conflictTimer;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayIconImage;
    private Forms.ToolStripMenuItem? _showHideItem;
    private Forms.ToolStripMenuItem? _triffViewEnabledItem;
    private Forms.ToolStripMenuItem? _triffViewHotkeysItem;
    private TriffViewController? _triffView;
    private EveSettingsController? _eveSettings;
    private TriffFleetsController? _triffFleets;
    private InputOverlayWindow? _inputOverlay;
    private GuiThemePalette _guiTheme = GuiThemePalette.TriffTools;
    private TriffViewUpdateSnapshot _updateSnapshot;
    private bool _settingsAlwaysOnTop = true;
    private bool _isClosing;
    private bool _conflictWarningShown;
    private bool _updateCheckInFlight;
    private bool _updateBalloonShown;

    public MainWindow(string[] args)
    {
        _args = args;
        _updateChecker = new TriffViewUpdateChecker();
        _updateSnapshot = TriffViewUpdateSnapshot.Idle(_updateChecker.CurrentVersion);
        _conflictTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _conflictTimer.Tick += (_, _) => CheckRuntimeTriffHudConflict();
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkWindowChrome();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int attributeValue, int attributeSize);

    private void ApplyDarkWindowChrome()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;

        var enabled = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20h1, ref enabled, sizeof(int));

        var caption = _guiTheme.DwmCaptionColor;
        var border = _guiTheme.DwmBorderColor;
        var text = _guiTheme.DwmTextColor;
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeTriffView();
        InitializeEveSettings();
        InitializeTriffFleets();
        InitializeTray();
        await InitializeWebViewAsync();
        InitializeInputOverlay();
        ShowSettings();
        _conflictTimer.Start();
        _ = CheckForUpdatesAsync(manual: false);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Cleanup();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosing)
        {
            e.Cancel = true;
            HideSettings();
            return;
        }

        base.OnClosing(e);
    }

    private void OnStateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            UpdateControlState();
            PostAppEvent(new { type = "visibility", visible = false });
            return;
        }

        ApplySettingsAlwaysOnTop(_settingsAlwaysOnTop);
        UpdateControlState();
        PostAppEvent(new { type = "visibility", visible = true });
    }

    private void InitializeTriffView()
    {
        _triffView = new TriffViewController(
            Dispatcher,
            PostAppEvent,
            () => Dispatcher.InvokeAsync(BringSettingsAbovePreviews),
            alwaysOnTop => Dispatcher.InvokeAsync(() => ApplySettingsAlwaysOnTop(alwaysOnTop))
        );
        _triffView.Start();
    }

    private void InitializeEveSettings()
    {
        _eveSettings = new EveSettingsController(Dispatcher, PostAppEvent);
    }

    private void InitializeTriffFleets()
    {
        _triffFleets = new TriffFleetsController(Dispatcher, PostAppEvent);
    }

    private void InitializeTray()
    {
        _trayIconImage ??= LoadAppIcon();
        _showHideItem = new Forms.ToolStripMenuItem("Hide Settings", null, (_, _) => ToggleSettings());
        _triffViewEnabledItem = new Forms.ToolStripMenuItem("Enable TriffView", null, (_, _) => ToggleTriffView());
        _triffViewHotkeysItem = new Forms.ToolStripMenuItem("Suspend TriffView hotkeys", null, (_, _) => ToggleTriffViewHotkeys());
        var openTriffViewItem = new Forms.ToolStripMenuItem("Open TriffView settings", null, (_, _) => OpenTool("triffview"));
        var openEveSettingsItem = new Forms.ToolStripMenuItem("Open EVE Settings", null, (_, _) => OpenTool("eve-settings"));
        var openFleetManagerItem = new Forms.ToolStripMenuItem("Open Fleet Manager", null, (_, _) => OpenTool("fleet-manager"));
        var savePreviewItem = new Forms.ToolStripMenuItem("Save preview positions", null, (_, _) => PostTriffViewNativeCommand("save-preview-layout"));
        var saveClientsItem = new Forms.ToolStripMenuItem("Save EVE client positions", null, (_, _) => PostTriffViewNativeCommand("save-client-layouts"));
        var restoreClientsItem = new Forms.ToolStripMenuItem("Restore EVE client positions", null, (_, _) => PostTriffViewNativeCommand("restore-client-layouts"));
        var checkUpdatesItem = new Forms.ToolStripMenuItem("Check for updates", null, (_, _) => _ = CheckForUpdatesAsync(manual: true));
        var reloadItem = new Forms.ToolStripMenuItem("Reload UI", null, (_, _) => AppWebView.Reload());
        var quitItem = new Forms.ToolStripMenuItem("Quit", null, (_, _) => Quit());

        var menu = CreateTrayMenu(_guiTheme);
        menu.Opened += (_, _) => _triffView?.SetNativeMenuOpen(true);
        menu.Closed += (_, _) =>
        {
            _triffView?.SetNativeMenuOpen(false);
            BringSettingsAbovePreviews();
        };
        menu.Items.Add(_showHideItem);
        menu.Items.Add(openTriffViewItem);
        menu.Items.Add(openEveSettingsItem);
        menu.Items.Add(openFleetManagerItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_triffViewEnabledItem);
        menu.Items.Add(_triffViewHotkeysItem);
        menu.Items.Add(savePreviewItem);
        menu.Items.Add(saveClientsItem);
        menu.Items.Add(restoreClientsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(checkUpdatesItem);
        menu.Items.Add(reloadItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "TriffView",
            Icon = _trayIconImage ?? Drawing.SystemIcons.Application,
            Visible = true,
        };
        _trayMenu = menu;
        _trayIcon.MouseUp += OnTrayMouseUp;
        _trayIcon.DoubleClick += (_, _) => OpenTool("triffview");
        _trayIcon.BalloonTipClicked += (_, _) => OpenUpdateRelease();
        UpdateControlState();
    }

    private static Forms.ContextMenuStrip CreateTrayMenu(GuiThemePalette theme)
    {
        var menu = new Forms.ContextMenuStrip
        {
            BackColor = theme.Background,
            ForeColor = theme.Text,
            Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point),
            Renderer = new Forms.ToolStripProfessionalRenderer(new TriffViewMenuColorTable(theme)),
            ShowImageMargin = true,
            ShowCheckMargin = false,
        };
        menu.Padding = new Forms.Padding(1);
        return menu;
    }

    private void OnTrayMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            ShowSettings();
            return;
        }

        if (e.Button != Forms.MouseButtons.Right || _trayMenu == null) return;

        UpdateControlState();
        ApplyTrayMenuTheme(_trayMenu, _guiTheme);

        var cursor = Forms.Cursor.Position;
        var workingArea = Forms.Screen.FromPoint(cursor).WorkingArea;
        var preferredSize = _trayMenu.GetPreferredSize(Drawing.Size.Empty);
        var x = Math.Max(workingArea.Left, Math.Min(cursor.X, workingArea.Right - preferredSize.Width));
        var y = cursor.Y;
        if (y + preferredSize.Height > workingArea.Bottom)
        {
            y = workingArea.Bottom - preferredSize.Height;
        }
        y = Math.Max(workingArea.Top, y);

        _trayMenu.Show(new Drawing.Point(x, y));
    }

    private static void ApplyTrayMenuTheme(Forms.ToolStripDropDown menu, GuiThemePalette theme)
    {
        menu.BackColor = theme.Background;
        menu.ForeColor = theme.Text;
        menu.Renderer = new Forms.ToolStripProfessionalRenderer(new TriffViewMenuColorTable(theme));
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.BackColor = theme.Background;
            item.ForeColor = theme.Text;
            item.Font = new Drawing.Font("Segoe UI", 9f, item.Font?.Style ?? Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);
        }
    }

    private static Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/TriffView.ico", UriKind.Absolute));
            if (resource?.Stream == null) return null;

            using var stream = resource.Stream;
            using var icon = new Drawing.Icon(stream);
            return (Drawing.Icon)icon.Clone();
        }
        catch
        {
            return null;
        }
    }

    private void InitializeInputOverlay()
    {
        _inputOverlay ??= new InputOverlayWindow(this);
    }

    private async Task InitializeWebViewAsync()
    {
        AppWebView.DefaultBackgroundColor = _guiTheme.Background;

        var webViewUserDataFolder = GetTriffViewLocalAppDataPath("WebView2");
        Directory.CreateDirectory(webViewUserDataFolder);
        var webViewEnvironment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: webViewUserDataFolder
        );
        await AppWebView.EnsureCoreWebView2Async(webViewEnvironment);

        AppWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        AppWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        AppWebView.CoreWebView2.ScriptDialogOpening += OnScriptDialogOpening;
        AppWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        AppWebView.CoreWebView2.NavigationCompleted += (_, _) => PostAppSettings();

        var devUrl = GetDevUrl();
        if (!string.IsNullOrWhiteSpace(devUrl))
        {
            AppWebView.Source = new Uri(devUrl);
            return;
        }

        var distFolder = FindOverlayDistFolder();
        if (distFolder == null)
        {
            AppWebView.NavigateToString(MissingOverlayHtml());
            return;
        }

        AppWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            distFolder,
            CoreWebView2HostResourceAccessKind.Allow
        );
        AppWebView.Source = new Uri($"https://{VirtualHostName}/index.html");
    }

    private void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        var message = string.IsNullOrWhiteSpace(e.Message) ? "Confirm this action?" : e.Message;

        switch (e.Kind)
        {
            case CoreWebView2ScriptDialogKind.Alert:
                System.Windows.MessageBox.Show(this, message, "TriffView", MessageBoxButton.OK, MessageBoxImage.Information);
                e.Accept();
                break;
            case CoreWebView2ScriptDialogKind.Confirm:
            case CoreWebView2ScriptDialogKind.Beforeunload:
                if (System.Windows.MessageBox.Show(this, message, "TriffView", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                {
                    e.Accept();
                }
                break;
            case CoreWebView2ScriptDialogKind.Prompt:
                if (System.Windows.MessageBox.Show(this, message, "TriffView", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
                {
                    e.Accept();
                }
                break;
        }
    }

    private string? GetDevUrl()
    {
        var envUrl = Environment.GetEnvironmentVariable("TRIFFVIEW_DEV_URL");
        if (!string.IsNullOrWhiteSpace(envUrl)) return envUrl;
        return _args.Any(arg => string.Equals(arg, "--dev", StringComparison.OrdinalIgnoreCase))
            ? "http://localhost:5178"
            : null;
    }

    private static string? FindOverlayDistFolder()
    {
        return FindLooseOverlayDistFolder() ?? ExtractEmbeddedOverlayDist();
    }

    private static string? FindLooseOverlayDistFolder()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "app", "dist");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
            current = current.Parent;
        }

        return null;
    }

    private static string? ExtractEmbeddedOverlayDist()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var resource = assembly.GetManifestResourceStream(EmbeddedOverlayResourceName);
            if (resource == null) return null;

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var zipBytes = buffer.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(zipBytes))[..16].ToLowerInvariant();

            var extractRoot = GetTriffViewLocalAppDataPath("ui", hash);
            var markerPath = Path.Combine(extractRoot, ".extract-complete");
            var indexPath = Path.Combine(extractRoot, "index.html");
            if (File.Exists(markerPath) && File.Exists(indexPath)) return extractRoot;

            if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);
            Directory.CreateDirectory(extractRoot);

            using var archiveStream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            var rootWithSeparator = Path.GetFullPath(extractRoot + Path.DirectorySeparatorChar);

            foreach (var entry in archive.Entries)
            {
                var targetPath = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
                if (!targetPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Unsafe overlay bundle entry: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                var targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDirectory)) Directory.CreateDirectory(targetDirectory);
                entry.ExtractToFile(targetPath, overwrite: true);
            }

            File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
            return File.Exists(indexPath) ? extractRoot : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to extract embedded TriffView UI: {ex}");
            return null;
        }
    }

    internal static string GetTriffViewLocalAppDataPath(params string[] segments)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(
            string.IsNullOrWhiteSpace(localAppData) ? Path.GetTempPath() : localAppData,
            "TriffView"
        );

        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        return path;
    }

    private static string MissingOverlayHtml()
    {
        return $"""
        <!doctype html>
        <html>
          <body style="margin:0;background:{CssColor(TriffToolsGuiTheme.Background)};color:{CssColor(TriffToolsGuiTheme.Accent)};font-family:Segoe UI;">
            <div style="position:absolute;top:32px;left:32px;border:1px solid {CssColor(TriffToolsGuiTheme.PanelBorder)};background:{CssColor(TriffToolsGuiTheme.PanelDark)};padding:12px;">
              <div style="font-size:12px;font-weight:800;text-transform:uppercase;">TriffView</div>
              <div style="margin-top:8px;color:{CssColor(TriffToolsGuiTheme.Text)};font-size:11px;">Build the UI first: npm.cmd run build from TriffView/app.</div>
            </div>
          </body>
        </html>
        """;
    }

    private static string CssColor(Drawing.Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    internal void ForwardPointerToHud(string type, System.Windows.Point point, int button, int deltaY, int clickCount = 1)
    {
        if (!IsVisible || AppWebView.CoreWebView2 == null) return;
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight) return;

        var payload = new
        {
            type,
            x = point.X,
            y = point.Y,
            button,
            deltaY,
            clickCount,
        };
        var json = JsonSerializer.Serialize(payload);
        _ = AppWebView.CoreWebView2.ExecuteScriptAsync($"window.triffHudDispatchInput && window.triffHudDispatchInput({json});");
    }

    internal void ForwardTextToHud(string text)
    {
        if (!IsVisible || AppWebView.CoreWebView2 == null) return;

        var json = JsonSerializer.Serialize(new { text });
        _ = AppWebView.CoreWebView2.ExecuteScriptAsync($"window.triffHudDispatchText && window.triffHudDispatchText({json});");
    }

    internal void ForwardKeyToHud(
        string key,
        string? gesture = null,
        bool control = false,
        bool alt = false,
        bool shift = false,
        bool windows = false
    )
    {
        if (!IsVisible || AppWebView.CoreWebView2 == null) return;

        var json = JsonSerializer.Serialize(new { key, gesture, control, alt, shift, windows });
        _ = AppWebView.CoreWebView2.ExecuteScriptAsync($"window.triffHudDispatchKey && window.triffHudDispatchKey({json});");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(e.WebMessageAsJson)?.AsObject();
        }
        catch
        {
            return;
        }

        var type = message?["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(type)) return;

        if (_triffView?.HandleWebMessage(type, message) == true)
        {
            UpdateControlState();
            return;
        }

        if (_eveSettings?.HandleWebMessage(type, message) == true)
        {
            return;
        }

        if (_triffFleets?.HandleWebMessage(type, message) == true)
        {
            return;
        }

        switch (type)
        {
            case "hide":
                HideSettings();
                break;
            case "open-external":
                OpenExternal(message?["url"]?.GetValue<string>());
                break;
            case "copy-text":
                CopyText(message?["text"]?.GetValue<string>());
                break;
            case "read-clipboard":
                ReadClipboard();
                break;
            case "input-regions":
                UpdateInputRegions(message);
                break;
            case "standalone:set-theme":
                ApplyStandaloneTheme(message?["theme"] as JsonObject);
                break;
            case "update:check":
                _ = CheckForUpdatesAsync(manual: true);
                break;
            case "update:open":
                OpenUpdateRelease();
                break;
            case "update:ignore-version":
                IgnoreUpdateVersion(message?["version"]?.GetValue<string>());
                break;
            case "settings:get":
            case "standalone:ready":
                PostAppSettings();
                PostUpdateState();
                break;
        }
    }

    private void ApplyStandaloneTheme(JsonObject? theme)
    {
        _guiTheme = GuiThemePalette.FromMessage(theme, _guiTheme);
        ApplyDarkWindowChrome();
        ApplyWindowBackground();
        AppWebView.DefaultBackgroundColor = _guiTheme.Background;
        if (_trayMenu != null)
        {
            ApplyTrayMenuTheme(_trayMenu, _guiTheme);
        }
    }

    private void ApplyWindowBackground()
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromRgb(_guiTheme.Background.R, _guiTheme.Background.G, _guiTheme.Background.B));
        Resources["TriffToolsBackgroundBrush"] = brush;
        Background = brush;
    }

    private void UpdateInputRegions(JsonObject? message)
    {
        var regions = new List<Rect>();
        if (message?["regions"] is JsonArray regionArray)
        {
            foreach (var node in regionArray)
            {
                if (node is not JsonObject region) continue;
                var x = region["x"]?.GetValue<double>() ?? 0;
                var y = region["y"]?.GetValue<double>() ?? 0;
                var width = region["width"]?.GetValue<double>() ?? 0;
                var height = region["height"]?.GetValue<double>() ?? 0;
                if (width <= 0 || height <= 0) continue;
                regions.Add(new Rect(x, y, width, height));
            }
        }

        _inputOverlay?.SetInputRegions(regions);
    }

    private void ToggleSettings()
    {
        if (SettingsWindowIsShown()) HideSettings();
        else ShowSettings();
    }

    private void ShowSettings()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        BringSettingsAbovePreviews();
        UpdateControlState();
        PostAppEvent(new { type = "visibility", visible = true });
    }

    private void HideSettings()
    {
        Hide();
        UpdateControlState();
        PostAppEvent(new { type = "visibility", visible = false });
    }

    private void OpenTool(string tool)
    {
        ShowSettings();
        PostAppEvent(new { type = "standalone:navigate", tool });
    }

    private void BringSettingsAbovePreviews()
    {
        if (!IsVisible) return;
        ApplySettingsAlwaysOnTop(_settingsAlwaysOnTop);
        if (!_settingsAlwaysOnTop) return;
    }

    private bool SettingsWindowIsShown()
    {
        return IsVisible && WindowState != WindowState.Minimized;
    }

    private void ApplySettingsAlwaysOnTop(bool alwaysOnTop)
    {
        _settingsAlwaysOnTop = alwaysOnTop;
        Topmost = alwaysOnTop;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero)
        {
            SetWindowPos(hwnd, alwaysOnTop ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
        }
    }

    private void ToggleTriffView()
    {
        if (_triffView == null) return;
        _triffView.SetEnabled(!_triffView.Settings.Enabled);
        UpdateControlState();
    }

    private void ToggleTriffViewHotkeys()
    {
        if (_triffView == null) return;
        _triffView.SetHotkeysSuspended(!_triffView.Settings.HotkeysSuspended);
        UpdateControlState();
    }

    private void PostTriffViewNativeCommand(string action)
    {
        switch (action)
        {
            case "save-preview-layout":
                _triffView?.HandleWebMessage("triffview:save-preview-layout", null);
                break;
            case "save-client-layouts":
                _triffView?.HandleWebMessage("triffview:save-client-layouts", null);
                break;
            case "restore-client-layouts":
                _triffView?.HandleWebMessage("triffview:restore-client-layouts", null);
                break;
        }

        UpdateControlState();
    }

    private void UpdateControlState()
    {
        if (_showHideItem != null) _showHideItem.Text = SettingsWindowIsShown() ? "Hide Settings" : "Show Settings";
        if (_triffViewEnabledItem != null)
        {
            var enabled = _triffView?.Settings.Enabled == true;
            _triffViewEnabledItem.Checked = false;
            _triffViewEnabledItem.Text = enabled ? "Disable TriffView" : "Enable TriffView";
        }
        if (_triffViewHotkeysItem != null)
        {
            var suspended = _triffView?.Settings.HotkeysSuspended == true;
            _triffViewHotkeysItem.Checked = false;
            _triffViewHotkeysItem.Enabled = _triffView?.Settings.Enabled == true;
            _triffViewHotkeysItem.Text = suspended ? "Resume TriffView hotkeys" : "Suspend TriffView hotkeys";
        }
    }

    private static void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }

    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            PostAppEvent(new { type = "clipboard-error", action = "copy" });
        }
    }

    private void ReadClipboard()
    {
        try
        {
            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
            PostAppEvent(new { type = "clipboard", text });
        }
        catch
        {
            PostAppEvent(new { type = "clipboard", text = "" });
        }
    }

    private void PostAppEvent(object message)
    {
        try
        {
            var json = JsonSerializer.Serialize(message, WebMessageJsonOptions);
            Dispatcher.InvokeAsync(() => AppWebView.CoreWebView2?.PostWebMessageAsJson(json));
        }
        catch
        {
            // The UI can miss a state event during startup/shutdown without breaking native control.
        }
    }

    private void PostAppSettings()
    {
        var virtualDesktop = ScreenGeometry.VirtualDesktopDips(this);
        var primaryScreen = ScreenGeometry.PrimaryScreenDips(this);
        var screens = ScreenGeometry.ScreensDips(this);

        PostAppEvent(new
        {
            type = "settings",
            opacity = 1,
            alwaysOnTop = _settingsAlwaysOnTop,
            screen = new
            {
                virtualDesktop = new
                {
                    width = virtualDesktop.Width,
                    height = virtualDesktop.Height,
                },
                primary = new
                {
                    x = primaryScreen.Left - virtualDesktop.Left,
                    y = primaryScreen.Top - virtualDesktop.Top,
                    width = primaryScreen.Width,
                    height = primaryScreen.Height,
                },
                commandBarMonitor = new
                {
                    x = primaryScreen.Left - virtualDesktop.Left,
                    y = primaryScreen.Top - virtualDesktop.Top,
                    width = primaryScreen.Width,
                    height = primaryScreen.Height,
                },
                monitors = screens.Select(screen => new
                {
                    id = screen.DeviceName,
                    label = screen.Label,
                    primary = screen.Primary,
                    selected = screen.Primary,
                    x = screen.Bounds.Left - virtualDesktop.Left,
                    y = screen.Bounds.Top - virtualDesktop.Top,
                    width = screen.Bounds.Width,
                    height = screen.Bounds.Height,
                }).ToArray(),
            },
        });
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckInFlight) return;

        _updateCheckInFlight = true;
        _updateSnapshot = TriffViewUpdateSnapshot.Checking(_updateChecker.CurrentVersion);
        if (manual) PostUpdateState();

        try
        {
            _updateSnapshot = await _updateChecker.CheckLatestAsync();
            PostUpdateState();

            if (string.Equals(_updateSnapshot.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                if (manual)
                {
                    System.Windows.MessageBox.Show(this, $"Could not check GitHub releases right now.\n\n{_updateSnapshot.Error}", "TriffView updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            if (_updateSnapshot.UpdateAvailable && !_updateSnapshot.Ignored)
            {
                if (manual)
                {
                    ShowSettings();
                }
                else
                {
                    ShowUpdateBalloonOnce();
                }
            }
            else if (manual)
            {
                var message = _updateSnapshot.Ignored
                    ? $"TriffView {_updateSnapshot.LatestTag} is available, but you chose to ignore this version."
                    : $"You're up to date. Current version: {_updateSnapshot.CurrentVersion}.";
                System.Windows.MessageBox.Show(this, message, "TriffView updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _updateSnapshot = TriffViewUpdateSnapshot.Failed(_updateChecker.CurrentVersion, ex.Message);
            PostUpdateState();
            if (manual)
            {
                System.Windows.MessageBox.Show(this, $"Could not check GitHub releases right now.\n\n{ex.Message}", "TriffView updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _updateCheckInFlight = false;
        }
    }

    private void ShowUpdateBalloonOnce()
    {
        if (_updateBalloonShown || _trayIcon == null) return;
        _updateBalloonShown = true;
        _trayIcon.ShowBalloonTip(
            8000,
            "TriffView update available",
            $"{_updateSnapshot.LatestTag} is available. Open TriffView settings to download it from GitHub.",
            Forms.ToolTipIcon.Info
        );
    }

    private void OpenUpdateRelease()
    {
        var url = !string.IsNullOrWhiteSpace(_updateSnapshot.ReleaseUrl)
            ? _updateSnapshot.ReleaseUrl
            : TriffViewUpdateChecker.ReleasesPageUrl;
        OpenExternal(url);
    }

    private void IgnoreUpdateVersion(string? version)
    {
        var versionToIgnore = string.IsNullOrWhiteSpace(version) ? _updateSnapshot.LatestVersion : version;
        _updateChecker.IgnoreVersion(versionToIgnore);
        if (string.Equals(_updateSnapshot.LatestVersion, versionToIgnore, StringComparison.OrdinalIgnoreCase))
        {
            _updateSnapshot = _updateSnapshot with { Ignored = true };
        }
        PostUpdateState();
    }

    private void PostUpdateState()
    {
        PostAppEvent(new
        {
            type = "update-state",
            update = new
            {
                status = _updateSnapshot.Status,
                currentVersion = _updateSnapshot.CurrentVersion,
                latestVersion = _updateSnapshot.LatestVersion,
                latestTag = _updateSnapshot.LatestTag,
                title = _updateSnapshot.Title,
                releaseUrl = _updateSnapshot.ReleaseUrl,
                publishedAt = _updateSnapshot.PublishedAt?.ToString("O"),
                updateAvailable = _updateSnapshot.UpdateAvailable,
                ignored = _updateSnapshot.Ignored,
                error = _updateSnapshot.Error,
            },
        });
    }

    private void CheckRuntimeTriffHudConflict()
    {
        if (_conflictWarningShown || !App.IsTriffHudRunning()) return;

        _conflictWarningShown = true;
        _triffView?.SetEnabled(false);
        _triffView?.SetHotkeysSuspended(true);
        UpdateControlState();

        System.Windows.MessageBox.Show(
            this,
            "TriffHUD is now running. Standalone TriffView disabled its previews and hotkeys so the two apps do not fight over EVE windows, DWM thumbnails, shared settings, or global hotkeys.\n\nQuit one app before enabling TriffView again.",
            "TriffView conflict",
            MessageBoxButton.OK,
            MessageBoxImage.Warning
        );
    }

    private void Quit()
    {
        _isClosing = true;
        Cleanup();
        System.Windows.Application.Current.Shutdown();
    }

    private void Cleanup()
    {
        _conflictTimer.Stop();

        if (_triffView != null)
        {
            _triffView.Dispose();
            _triffView = null;
        }

        if (_inputOverlay != null)
        {
            _inputOverlay.Close();
            _inputOverlay = null;
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_trayMenu != null)
        {
            _trayMenu.Dispose();
            _trayMenu = null;
        }

        if (_trayIconImage != null)
        {
            _trayIconImage.Dispose();
            _trayIconImage = null;
        }
    }
}

internal sealed class TriffViewMenuColorTable : Forms.ProfessionalColorTable
{
    private readonly GuiThemePalette _theme;

    public TriffViewMenuColorTable(GuiThemePalette theme)
    {
        _theme = theme;
    }

    public override Drawing.Color ToolStripDropDownBackground => _theme.Background;
    public override Drawing.Color ImageMarginGradientBegin => _theme.TrayImageMargin;
    public override Drawing.Color ImageMarginGradientMiddle => _theme.TrayImageMargin;
    public override Drawing.Color ImageMarginGradientEnd => _theme.TrayImageMargin;
    public override Drawing.Color MenuBorder => _theme.Border;
    public override Drawing.Color MenuItemBorder => _theme.Accent;
    public override Drawing.Color MenuItemSelected => _theme.TraySelected;
    public override Drawing.Color MenuItemSelectedGradientBegin => _theme.TraySelected;
    public override Drawing.Color MenuItemSelectedGradientEnd => _theme.TraySelected;
    public override Drawing.Color MenuItemPressedGradientBegin => _theme.TrayPressed;
    public override Drawing.Color MenuItemPressedGradientMiddle => _theme.TrayPressed;
    public override Drawing.Color MenuItemPressedGradientEnd => _theme.TrayPressed;
    public override Drawing.Color CheckBackground => _theme.TraySelected;
    public override Drawing.Color CheckSelectedBackground => _theme.TrayPressed;
    public override Drawing.Color CheckPressedBackground => _theme.TrayPressed;
    public override Drawing.Color SeparatorDark => _theme.Border;
    public override Drawing.Color SeparatorLight => _theme.Border;
}
