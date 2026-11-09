using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace PatchDiff;

/// <summary>
/// Opts a WPF window into the dark title bar on Windows 10 1809+ / Windows 11.
/// WPF does not expose this natively; without it the OS paints a light title
/// bar (~31px, #F3F3F3) that clashes with the dark client area.
/// </summary>
public static class DarkTitleBar
{
    // DWMWINDOWATTRIBUTE::DWMWA_USE_IMMERSIVE_DARK_MODE.
    // Attribute 20 is the documented value (Windows 10 build 18963+ / Windows 11);
    // attribute 19 is the pre-18963 equivalent honored by earlier 1809+ builds.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_18963 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// Call after the window handle exists (e.g. OnSourceInitialized).
    /// Best-effort: silently does nothing on OS versions without the attribute.
    /// </summary>
    public static void Enable(Window window)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 1 = TRUE: use dark mode for the window's title bar and borders.
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_18963, ref on, sizeof(int));
        }
    }
}
