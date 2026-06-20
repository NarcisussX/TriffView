using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TriffView;

internal static class ScreenGeometry
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static Drawing.Rectangle VirtualDesktopPixels()
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) return Forms.SystemInformation.VirtualScreen;

        var bounds = screens[0].Bounds;
        for (var index = 1; index < screens.Length; index++)
        {
            bounds = Drawing.Rectangle.Union(bounds, screens[index].Bounds);
        }

        return bounds;
    }

    public static Drawing.Rectangle PrimaryScreenPixels()
    {
        return Forms.Screen.PrimaryScreen?.Bounds ?? Forms.SystemInformation.VirtualScreen;
    }

    public static Rect PrimaryScreenDips(Visual visual)
    {
        return DeviceRectToDips(visual, PrimaryScreenPixels());
    }

    public static Rect VirtualDesktopDips(Visual visual)
    {
        return DeviceRectToDips(visual, VirtualDesktopPixels());
    }

    public static IReadOnlyList<ScreenDipInfo> ScreensDips(Visual visual)
    {
        return Forms.Screen.AllScreens
            .Select((screen, index) => new ScreenDipInfo(
                screen.DeviceName,
                screen.Primary ? "Primary" : $"Monitor {index + 1}",
                screen.Primary,
                DeviceRectToDips(visual, screen.Bounds)
            ))
            .ToList();
    }

    public static Rect ApplyWindowDeviceBounds(Window window, nint hwnd, Drawing.Rectangle bounds)
    {
        var dips = DeviceRectToDips(window, bounds);
        window.Left = dips.Left;
        window.Top = dips.Top;
        window.Width = dips.Width;
        window.Height = dips.Height;

        if (hwnd != nint.Zero)
        {
            SetWindowPos(
                hwnd,
                nint.Zero,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                SwpNoZOrder | SwpNoActivate | SwpFrameChanged
            );
        }

        return dips;
    }

    private static Rect DeviceRectToDips(Visual visual, Drawing.Rectangle bounds)
    {
        var transform = PresentationSource.FromVisual(visual)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(bounds.Left, bounds.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(bounds.Right, bounds.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
    public sealed record ScreenDipInfo(string DeviceName, string Label, bool Primary, Rect Bounds);
