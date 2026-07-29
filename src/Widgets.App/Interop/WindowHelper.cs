using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Widgets.App.Models;
using Windows.Graphics;

namespace Widgets.App.Interop;

/// <summary>
/// Window-level policy for widget hosts: chrome, z-order, transparency and monitor geometry.
/// All coordinates here are physical (device) pixels on the virtual screen.
/// </summary>
internal static class WindowHelper
{
    public static IntPtr GetHwnd(Window w) => WinRT.Interop.WindowNative.GetWindowHandle(w);

    /// <summary>
    /// Makes a window behave like desktop furniture: no taskbar button, no Alt-Tab entry,
    /// never steals focus, and square corners so the widget's own corner radius is the only one visible.
    /// </summary>
    public static void ApplyWidgetChrome(IntPtr hwnd)
    {
        try
        {
            var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            ex &= ~Win32.WS_EX_APPWINDOW;
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

            // OverlappedPresenter keeps a resize frame even with SetBorderAndTitleBar(false, false),
            // which leaves a ~3px invisible border on each edge: the client area ends up smaller than
            // the window, so content gets clipped and anything positioned from the window origin
            // (the wallpaper layer, the rounded region) is offset. A plain popup has no frame, so
            // client and window rects coincide.
            var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
            style &= ~(Win32.WS_CAPTION | Win32.WS_THICKFRAME | Win32.WS_SYSMENU
                       | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX | Win32.WS_BORDER | Win32.WS_DLGFRAME);
            style |= Win32.WS_POPUP;
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style));

            // The widget draws its own corner radius; the shell's rounding would clip it.
            var corner = Win32.DWMWCP_DONOTROUND;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(ApplyWidgetChrome));
        }
    }

    public static void SetClickThrough(IntPtr hwnd, bool enable)
    {
        try
        {
            var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            ex = enable ? ex | Win32.WS_EX_TRANSPARENT : ex & ~Win32.WS_EX_TRANSPARENT;
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(SetClickThrough));
        }
    }

    public static void ApplyZOrder(IntPtr hwnd, ZOrderMode mode)
    {
        try
        {
            var insertAfter = mode switch
            {
                ZOrderMode.Desktop => Win32.HWND_BOTTOM,
                ZOrderMode.TopMost => Win32.HWND_TOPMOST,
                _ => Win32.HWND_NOTOPMOST,
            };

            Win32.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(ApplyZOrder));
        }
    }

    /// <summary>
    /// Stops the window from painting an opaque fill behind the widget, so the area outside the
    /// widget's rounded corners blends into the desktop. The caller must also give the XAML root
    /// a transparent background.
    /// </summary>
    /// <remarks>
    /// A fully transparent window is not reachable here. The usual recipe — a transparent
    /// composition color brush assigned through <c>ICompositionSupportsSystemBackdrop</c> — does
    /// not work with Windows App SDK 1.8: the projected property takes the UWP
    /// <c>Windows.UI.Composition.CompositionBrush</c>, which the WinUI compositor cannot produce,
    /// and querying the native interface off the window returns E_NOINTERFACE. Measured behaviour
    /// of the alternatives: a null backdrop plus DWM blur-behind paints solid black, while desktop
    /// acrylic shows what is behind the window. Acrylic is therefore what we use; the DWM
    /// blur-behind region stays because it is what puts the window into per-pixel alpha mode.
    /// </remarks>
    public static void SetTransparentBackdrop(Window w)
    {
        try
        {
            var hwnd = GetHwnd(w);

            var region = Win32.CreateRectRgn(-2, -2, -1, -1);
            var blur = new Win32.DWM_BLURBEHIND
            {
                dwFlags = Win32.DWM_BB_ENABLE | Win32.DWM_BB_BLURREGION,
                fEnable = true,
                hRgnBlur = region,
            };
            Win32.DwmEnableBlurBehindWindow(hwnd, ref blur);
            Win32.DeleteObject(region);

            w.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(SetTransparentBackdrop));
        }
    }

    /// <summary>
    /// Picks the window-level backdrop for a theme. A WinUI 3 window with no system backdrop
    /// paints solid black, so there is never a null case here — "clear" is approximated with the
    /// thinnest acrylic, which lets the wallpaper through instead of showing a black rectangle.
    /// </summary>
    public static void ApplyBackdrop(Window w, BackdropMode mode)
    {
        try
        {
            if (mode == BackdropMode.Mica)
            {
                w.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                return;
            }

            var acrylic = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            w.SystemBackdrop = acrylic;
        }
        catch (Exception ex)
        {
            // Mica is unavailable on some Windows 10 builds; acrylic is the safe fallback.
            Crash.Log(ex, nameof(ApplyBackdrop));
            try
            {
                w.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            }
            catch
            {
                // Leave whatever backdrop the window already had rather than falling back to black.
            }
        }
    }

    /// <summary>
    /// Clips the window to a rounded rectangle. Without this the window stays a hard rectangle and
    /// the theme's corner radius is invisible — the backdrop keeps painting the square corners.
    /// </summary>
    public static void ApplyRoundedRegion(IntPtr hwnd, int width, int height, double cornerRadiusPx)
    {
        try
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            // A radius past half the shortest side degenerates into a stadium/ellipse.
            var radius = (int)Math.Round(Math.Clamp(cornerRadiusPx, 0, Math.Min(width, height) / 2.0));

            // CreateRoundRectRgn takes an exclusive right/bottom edge, hence the +1.
            var region = radius <= 0
                ? Win32.CreateRectRgn(0, 0, width + 1, height + 1)
                : Win32.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);

            // SetWindowRgn takes ownership of the region, so it must not be deleted here.
            Win32.SetWindowRgn(hwnd, region, true);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(ApplyRoundedRegion));
        }
    }

    public static double GetScale(IntPtr hwnd)
    {
        var dpi = Win32.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public static RectInt32 GetMonitorWorkArea(IntPtr hwnd)
    {
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        return GetWorkArea(monitor);
    }

    public static RectInt32 GetWorkAreaAt(int x, int y)
    {
        var monitor = Win32.MonitorFromPoint(new Win32.POINT { X = x, Y = y }, Win32.MONITOR_DEFAULTTONEAREST);
        return GetWorkArea(monitor);
    }

    /// <summary>Work area of the monitor that owns the origin of the virtual screen's primary display.</summary>
    public static RectInt32 GetPrimaryWorkArea()
    {
        var rect = default(Win32.RECT);
        if (Win32.SystemParametersInfo(Win32.SPI_GETWORKAREA, 0, ref rect, 0))
        {
            return ToRect(rect);
        }

        return new RectInt32(0, 0, Win32.GetSystemMetrics(Win32.SM_CXSCREEN), Win32.GetSystemMetrics(Win32.SM_CYSCREEN));
    }

    public static IReadOnlyList<RectInt32> GetAllMonitorBounds()
    {
        var result = new List<RectInt32>();

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref Win32.RECT bounds, IntPtr data) =>
        {
            var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfo(monitor, ref info))
            {
                result.Add(ToRect(info.rcWork));
            }

            return true;
        }, IntPtr.Zero);

        if (result.Count == 0)
        {
            result.Add(GetPrimaryWorkArea());
        }

        return result;
    }

    public static string? GetWallpaperPath()
    {
        try
        {
            var buffer = new StringBuilder(520);
            if (!Win32.SystemParametersInfo(Win32.SPI_GETDESKWALLPAPER, (uint)buffer.Capacity, buffer, 0))
            {
                return null;
            }

            var path = buffer.ToString();
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(GetWallpaperPath));
            return null;
        }
    }

    /// <summary>
    /// True while a game or full-screen video owns the foreground. The shell itself covers a
    /// whole monitor too, so Progman/WorkerW are excluded explicitly.
    /// </summary>
    public static bool IsForegroundFullscreen()
    {
        try
        {
            var fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Win32.GetShellWindow() || fg == Win32.GetDesktopWindow())
            {
                return false;
            }

            if (!Win32.IsWindowVisible(fg) || Win32.IsIconic(fg))
            {
                return false;
            }

            var cls = new StringBuilder(256);
            Win32.GetClassName(fg, cls, cls.Capacity);
            var name = cls.ToString();
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow")
            {
                return false;
            }

            if (!Win32.GetWindowRect(fg, out var rect))
            {
                return false;
            }

            var monitor = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST);
            var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (!Win32.GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            return rect.Left <= info.rcMonitor.Left
                && rect.Top <= info.rcMonitor.Top
                && rect.Right >= info.rcMonitor.Right
                && rect.Bottom >= info.rcMonitor.Bottom;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(IsForegroundFullscreen));
            return false;
        }
    }

    public static bool TryGetCursorPosition(out int x, out int y)
    {
        if (Win32.GetCursorPos(out var pt))
        {
            x = pt.X;
            y = pt.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    private static RectInt32 GetWorkArea(IntPtr monitor)
    {
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        return Win32.GetMonitorInfo(monitor, ref info) ? ToRect(info.rcWork) : GetPrimaryWorkArea();
    }

    private static RectInt32 ToRect(Win32.RECT r) => new(r.Left, r.Top, r.Width, r.Height);
}
