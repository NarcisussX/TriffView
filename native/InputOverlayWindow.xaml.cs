using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TriffView;

public partial class InputOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);
    private const int WmNchittest = 0x0084;
    private const int Htclient = 1;
    private const int Httransparent = -1;

    private readonly MainWindow _hud;
    private readonly List<Rect> _inputRegions = new();
    private readonly DispatcherTimer _hitTestTimer;
    private HwndSource? _source;
    private nint _hwnd;
    private bool _isPassThrough;

    public InputOverlayWindow(MainWindow hud)
    {
        _hud = hud;
        _hitTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(24) };
        _hitTestTimer.Tick += (_, _) => UpdatePassThroughForCursor();
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static nint GetWindowLongPtr(nint hwnd, int index)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);
    }

    private static nint SetWindowLongPtr(nint hwnd, int index, nint value)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, value.ToInt32());
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SizeToVirtualScreen();
        InputSurface.Focus();
        _hitTestTimer.Start();
        UpdatePassThroughForCursor();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _hitTestTimer.Stop();
        base.OnClosed(e);
    }

    public void SizeToVirtualScreen()
    {
        ScreenGeometry.ApplyWindowDeviceBounds(this, _hwnd, ScreenGeometry.VirtualDesktopPixels());
    }

    public void BringToTop(bool topmost)
    {
        if (_hwnd == nint.Zero) return;

        SetWindowPos(
            _hwnd,
            topmost ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged
        );
    }

    public void SetInputRegions(IReadOnlyList<Rect> regions)
    {
        _inputRegions.Clear();
        _inputRegions.AddRange(regions);
        UpdatePassThroughForCursor();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmNchittest) return nint.Zero;

        var screenPoint = new System.Windows.Point(GetSignedLoWord(lParam), GetSignedHiWord(lParam));
        var point = PointFromScreen(screenPoint);
        handled = true;
        return IsInputPoint(point) ? Htclient : Httransparent;
    }

    private bool IsInputPoint(System.Windows.Point point)
    {
        if (Mouse.Captured != null) return true;
        return _inputRegions.Any(region => region.Contains(point));
    }

    private void UpdatePassThroughForCursor()
    {
        if (_hwnd == nint.Zero) return;
        if (!GetCursorPos(out var cursor)) return;

        var point = PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        SetPassThrough(!IsInputPoint(point));
    }

    private void SetPassThrough(bool enabled)
    {
        if (_isPassThrough == enabled || _hwnd == nint.Zero) return;

        _isPassThrough = enabled;
        var style = GetWindowLongPtr(_hwnd, GwlExStyle);
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(_hwnd, GwlExStyle, style);
        SetWindowPos(
            _hwnd,
            nint.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged
        );
        if (enabled)
        {
            _hud.ForwardPointerToHud("pointerleave", Mouse.GetPosition(this), 0, 0);
        }
    }

    private void OnInputSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        InputSurface.Focus();
        InputSurface.CaptureMouse();
        _hud.ForwardPointerToHud("pointerdown", e.GetPosition(this), MouseButtonToDomButton(e.ChangedButton), 0, e.ClickCount);
        e.Handled = true;
    }

    private void OnInputSurfaceMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hud.ForwardPointerToHud("pointermove", e.GetPosition(this), 0, 0);
        e.Handled = true;
    }

    private void OnInputSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        _hud.ForwardPointerToHud("pointerup", e.GetPosition(this), MouseButtonToDomButton(e.ChangedButton), 0, e.ClickCount);
        InputSurface.ReleaseMouseCapture();
        UpdatePassThroughForCursor();
        e.Handled = true;
    }

    private void OnInputSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _hud.ForwardPointerToHud("wheel", e.GetPosition(this), 0, -e.Delta);
        e.Handled = true;
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        _hud.ForwardTextToHud(e.Text);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var windows = Keyboard.Modifiers.HasFlag(ModifierKeys.Windows);
        var gestureKey = KeyToGestureKey(key);
        var domKey = KeyToDomKey(key) ?? gestureKey;

        if (control && key == Key.A)
        {
            _hud.ForwardKeyToHud("SelectAll", BuildGesture(gestureKey, control, alt, shift, windows), control, alt, shift, windows);
            e.Handled = true;
            return;
        }

        if (control && key == Key.C)
        {
            _hud.ForwardKeyToHud("Copy", BuildGesture(gestureKey, control, alt, shift, windows), control, alt, shift, windows);
            e.Handled = true;
            return;
        }

        if (control && key == Key.X)
        {
            _hud.ForwardKeyToHud("Cut", BuildGesture(gestureKey, control, alt, shift, windows), control, alt, shift, windows);
            e.Handled = true;
            return;
        }

        if (control && key == Key.V)
        {
            _hud.ForwardKeyToHud("Paste", BuildGesture(gestureKey, control, alt, shift, windows), control, alt, shift, windows);
            e.Handled = true;
            return;
        }

        if (!IsModifierOnly(key) && domKey != null)
        {
            _hud.ForwardKeyToHud(domKey, BuildGesture(gestureKey, control, alt, shift, windows), control, alt, shift, windows);
        }

        if (domKey == null || !IsEditingKey(domKey)) return;

        e.Handled = true;
    }

    private static int MouseButtonToDomButton(MouseButton button)
    {
        return button switch
        {
            MouseButton.Right => 2,
            MouseButton.Middle => 1,
            _ => 0,
        };
    }

    private static string? KeyToDomKey(Key key)
    {
        return key switch
        {
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Return => "Enter",
            Key.Escape => "Escape",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Tab => "Tab",
            _ => null,
        };
    }

    private static bool IsEditingKey(string key)
    {
        return key is "Backspace" or "Delete" or "Enter" or "Escape" or "ArrowLeft" or "ArrowRight" or "ArrowUp" or "ArrowDown" or "Tab";
    }

    private static bool IsModifierOnly(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;
    }

    private static string? KeyToGestureKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString();
        if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return $"NumPad{(int)(key - Key.NumPad0)}";
        if (key >= Key.F1 && key <= Key.F24) return key.ToString();

        return key switch
        {
            Key.Return => "Enter",
            Key.Escape => "Escape",
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Oem3 => "Tilde",
            Key.OemPlus => "Plus",
            Key.OemMinus => "Minus",
            Key.OemComma => "Comma",
            Key.OemPeriod => "Period",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            _ => VirtualKeyToGestureKey(KeyInterop.VirtualKeyFromKey(key)),
        };
    }

    private static string? VirtualKeyToGestureKey(int virtualKey)
    {
        return virtualKey switch
        {
            0x03 => "Cancel",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x2C => "PrintScreen",
            0x5D => "Apps",
            0x5F => "Sleep",
            0x6A => "NumPadMultiply",
            0x6B => "NumPadAdd",
            0x6C => "NumPadSeparator",
            0x6D => "NumPadSubtract",
            0x6E => "NumPadDecimal",
            0x6F => "NumPadDivide",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0xA6 => "BrowserBack",
            0xA7 => "BrowserForward",
            0xA8 => "BrowserRefresh",
            0xA9 => "BrowserStop",
            0xAA => "BrowserSearch",
            0xAB => "BrowserFavorites",
            0xAC => "BrowserHome",
            0xAD => "VolumeMute",
            0xAE => "VolumeDown",
            0xAF => "VolumeUp",
            0xB0 => "MediaNextTrack",
            0xB1 => "MediaPreviousTrack",
            0xB2 => "MediaStop",
            0xB3 => "MediaPlayPause",
            0xB4 => "LaunchMail",
            0xB5 => "SelectMedia",
            0xB6 => "LaunchApplication1",
            0xB7 => "LaunchApplication2",
            0xBA => "Semicolon",
            0xBF => "Slash",
            0xDB => "[",
            0xDC => "Backslash",
            0xDD => "]",
            0xDE => "Quote",
            0xDF => "OEM8",
            0xE2 => "OEM102",
            > 0 and <= 0xFE => $"VK_0x{virtualKey:X2}",
            _ => null,
        };
    }

    private static string? BuildGesture(string? key, bool control, bool alt, bool shift, bool windows)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var parts = new List<string>();
        if (control) parts.Add("Control");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (windows) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }

    private static int GetSignedLoWord(nint value)
    {
        return unchecked((short)((long)value & 0xffff));
    }

    private static int GetSignedHiWord(nint value)
    {
        return unchecked((short)(((long)value >> 16) & 0xffff));
    }
}
