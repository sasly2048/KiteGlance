using System;

namespace KiteGlance.Interop;

/// <summary>
/// Pure decision helpers for the desktop-pin WndProc hook. Kept in a
/// separate file (and free of any WPF or UI dependency) so the unit tests
/// can pin the rules against accidental changes without spinning up an
/// HwndSource. The full WndProc logic lives in DesktopPin.cs; this file
/// holds only the predicates the hook calls.
/// </summary>
internal static class DesktopPinLogic
{
    /// <summary>
    /// WM_SYSCOMMAND SC_MINIMIZE is 0xF020. The low 4 bits of wParam are
    /// modifier bits (MK_CONTROL etc.) and must be masked off before the
    /// comparison. SC_MAXIMIZE (0xF030) and SC_DESKTOP (0xF130, the
    /// undocumented code that Windows 11's four-finger-swipe-down sends
    /// during a "Show Desktop" cycle) are also treated as minimisation
    /// attempts because the visible result is the same: the window goes
    /// away.
    /// </summary>
    internal const int SC_MINIMIZE = 0xF020;
    internal const int SC_MAXIMIZE = 0xF030;
    internal const int SC_RESTORE = 0xF120;
    internal const int SC_DESKTOP = 0xF130;

    /// <summary>wParam for WM_SIZE when the window has been minimised.</summary>
    internal const int SIZE_MINIMIZED = 1;

    /// <summary>WM_SYSCOMMAND opcode.</summary>
    internal const int WM_SYSCOMMAND = 0x0112;

    /// <summary>WM_SIZE opcode.</summary>
    internal const int WM_SIZE = 0x0005;

    /// <summary>WM_WINDOWPOSCHANGING opcode.</summary>
    internal const int WM_WINDOWPOSCHANGING = 0x0046;

    /// <summary>SetWindowPos flags (subset).</summary>
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;

    /// <summary>
    /// Heuristic for "is this WM_SYSCOMMAND wParam a minimise-class
    /// command?". Returns true for SC_MINIMIZE, SC_MAXIMIZE, and
    /// SC_DESKTOP. Returns false for SC_RESTORE, SC_MOVE, SC_SIZE, and
    /// any other opcode. The low 4 bits of wParam are modifier bits and
    /// are masked off before comparison.
    /// </summary>
    internal static bool IsMinimizeSysCommand(IntPtr wParam) =>
        (wParam.ToInt64() & 0xFFF0) is SC_MINIMIZE or SC_MAXIMIZE or SC_DESKTOP;

    /// <summary>
    /// Heuristic for "is this WM_SIZE wParam the minimised state?" -- true
    /// for SIZE_MINIMIZED (1). SIZE_RESTORED (0) and SIZE_MAXIMIZED (2) are
    /// not minimise states.
    /// </summary>
    internal static bool IsSizeMinimized(IntPtr wParam) =>
        wParam.ToInt32() == SIZE_MINIMIZED;

    /// <summary>
    /// Heuristic for "is this new size the icon rect a minimised window
    /// gets?". A rect with height &lt;= 32 AND width much smaller than the
    /// normal width is treated as an icon. A genuine user resize that
    /// produces such a tiny rect is impossible (the widget is
    /// ResizeMode=NoResize), so a false positive is not a risk.
    ///
    /// The thresholds are conservative on purpose: the icon title bar is
    /// ~28px on a default DPI, the normal widget is ~256px or larger. A
    /// height &lt;= 32 catches the icon and nothing else. The width
    /// comparison (must be at most 75% of normal) prevents a real
    /// compact-mode widget from being misclassified.
    /// </summary>
    internal static bool IsMinimizeSize(int newCx, int newCy, double normalCx, double normalCy)
    {
        if (newCy > 32) return false;                // icon title-bar is ~28px
        if (normalCy <= 0) return false;
        if (newCy > normalCy * 0.25) return false;    // not even close to icon
        if (newCx >= normalCx * 0.75) return false;  // not narrower than 3/4
        return true;
    }
}
