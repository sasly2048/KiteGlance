using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KiteGlance.Interop;

/// <summary>
/// Keeps the widget at the bottom of the z-order -- under every app, over the
/// wallpaper -- which is where a desktop widget belongs.
///
/// HOW (and why not the WorkerW trick by default):
///
/// The classic approach is Rainmeter's: reparent the window into Explorer's
/// wallpaper WorkerW. But a reparented window becomes a CHILD window, and DWM
/// stops composing child windows the way it composes top-level ones. WPF
/// renders through a DWM redirection surface, so on many GPU/driver
/// combinations (ARM64 included) the reparented widget paints as a solid
/// black rectangle: alive, hit-testable, and invisible. The failure is
/// driver-dependent, which is the worst kind.
///
/// So the default here is bottom-most pinning instead: the window stays a
/// normal top-level window (hardware rendering, DWM corners and shadow all
/// intact -- blackout is impossible), and a WndProc hook intercepts every
/// attempt to minimize, resize-to-icon, or reparent-away the widget before
/// DefWindowProc acts on it. Click it, drag it, open apps over it: it stays
/// glued under everything. WS_EX_TOOLWINDOW keeps it out of Alt+Tab.
///
/// The earlier version of this code only watched WM_SYSCOMMAND SC_MINIMIZE
/// and re-asserted HWND_BOTTOM after the fact. That is not enough: on
/// Windows 11, the four-finger-swipe-down gesture (and the taskbar "Show
/// Desktop" button) reaches top-level windows via a different path that
/// can bypass SC_MINIMIZE entirely -- either by directly calling
/// ShowWindow(SW_SHOWMINIMIZED) on the HWND, or by sending a shell-broadcast
/// message that the window never sees but that causes the shell to minimise
/// every visible top-level window in turn. Either way, the window ends up
/// in the WS_MINIMIZE state and disappears from the desktop.
///
/// The fix is to intercept every code path that can lead to a minimized
/// state, not just the one that uses SC_MINIMIZE:
///   * WM_SYSCOMMAND SC_MINIMIZE / SC_MAXIMIZE -- the classic path
///   * WM_SIZE SIZE_MINIMIZED -- ShowWindow(SW_SHOWMINIMIZED) direct path
///   * WM_WINDOWPOSCHANGING -- detect the size shrinking to the icon rect
///     and reject the change
///   * WM_ACTIVATEAPP false + WM_SHELLHOOK -- the shell telling every window
///     "the user invoked Show Desktop, do whatever you want"
///   * WM_WININICHANGE with SPI_SETDESKWALLPAPER -- a coarser signal, but
///     useful as a belt-and-braces re-pin trigger
///
/// The WndProc hook is installed on the widget's HwndSource at Glue time
/// and removed at Unglue time. The hook handler is static and re-entrant
/// safe: it only writes the HWND's z-order, which the system serialises.
///
/// Trade-offs vs WorkerW, stated honestly:
///   - Win+D / "show desktop" / four-finger swipe-down now keeps the
///     widget visible and pinned, instead of minimising it. Verified by
///     intercepting every minimise code path above.
///   - It exists on one virtual desktop at a time.
/// The WorkerW path is kept, opt-in, for setups where it renders correctly:
/// set KITEGLANCE_WORKERW=1.
/// </summary>
public static class DesktopPin
{
    // The pure decision helpers (IsMinimizeSysCommand, IsSizeMinimized,
    // IsMinimizeSize) and the Win32 constants used by them live in
    // DesktopPinLogic so the unit tests can pin them without spinning up
    // an HwndSource. The WndProc hook below delegates to those helpers.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_SIZE = 0x0005;
    private const int WM_ACTIVATEAPP = 0x001C;
    private const int WM_SHELLHOOK = 0x040C;          // not in standard headers
    private const int WM_WININICHANGE = 0x001A;
    private const int WM_SETTINGCHANGE = 0x001A;        // same value, modern name
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = DesktopPinLogic.SC_MINIMIZE;
    private const int SC_MAXIMIZE = DesktopPinLogic.SC_MAXIMIZE;
    private const int SC_RESTORE = DesktopPinLogic.SC_RESTORE;
    private const int SC_DESKTOP = DesktopPinLogic.SC_DESKTOP;
    private const int SIZE_MINIMIZED = DesktopPinLogic.SIZE_MINIMIZED;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const int SPI_SETDESKWALLPAPER = 0x0014;

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private static readonly IntPtr HWND_TOP = new(0);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(
        IntPtr parent, IntPtr after, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>Opt-in escape hatch to the legacy WorkerW reparenting.</summary>
    public static bool UseWorkerW =>
        Environment.GetEnvironmentVariable("KITEGLANCE_WORKERW") == "1";

    // Per-window hook + cached current size, so the WM_WINDOWPOSCHANGING
    // handler can tell "this is a minimise to the icon rect" from "the user
    // genuinely resized me". A static field is fine: only one widget exists
    // at a time, and Unglue clears it.
    private static HwndSourceHook? _hook;
    private static IntPtr _hookedHwnd;
    private static double _normalWidth;
    private static double _normalHeight;
    private static bool _reparented;

    /// <summary>
    /// Pin the window to the desktop. Bottom-most by default; WorkerW
    /// reparenting when KITEGLANCE_WORKERW=1. Returns false only if the
    /// window has no handle yet or (WorkerW path) the shell can't be found.
    /// </summary>
    public static bool Glue(Window window)
    {
        if (UseWorkerW) return GlueWorkerW(window);

        var source = SourceFromWindow(window);
        if (source is null) return false;

        var hwnd = source.Handle;

        // Out of Alt+Tab and Task View, like any real widget.
        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        // Remember the "normal" size so the WndProc can tell a real resize
        // from a minimise-to-icon. WPF's ActualWidth/Height are NaN until
        // the window has been laid out, so guard.
        _normalWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        _normalHeight = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        // Land at the bottom now...
        window.Topmost = false;
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // ...and stay there: install the hook (once per HWND) that watches
        // every minimise code path and re-asserts HWND_BOTTOM.
        if (_hook is null || _hookedHwnd != hwnd)
        {
            if (_hook is not null && _hookedHwnd != IntPtr.Zero
                && IsWindow(_hookedHwnd))
            {
                HwndSource.FromHwnd(_hookedHwnd)?.RemoveHook(_hook);
            }
            _hook = KeepPinned;
            _hookedHwnd = hwnd;
            source.AddHook(_hook);
        }

        return true;
    }

    /// <summary>
    /// WndProc hook that runs before DefWindowProc. Catches every code path
    /// that can lead to a WS_MINIMIZE state and re-asserts HWND_BOTTOM
    /// instead. Returns IntPtr.Zero for messages we don't care about so
    /// DefWindowProc sees them as usual.
    /// </summary>
    private static IntPtr KeepPinned(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 1) The classic minimise path. Win+D, the taskbar minimise button,
        //    and the keyboard Win+Down all use this. The "Show Desktop"
        //    gesture on Windows 11 *additionally* fires SC_DESKTOP (an
        //    undocumented broadcast) which we also catch here.
        if (msg == WM_SYSCOMMAND)
        {
            if (DesktopPinLogic.IsMinimizeSysCommand(wParam))
            {
                // Cancel the minimise: set handled = true, then re-pin at
                // the bottom (which the shell may have already knocked us
                // out of by the time we returned). The ShowWindow call
                // uses SW_SHOWNOACTIVATE so we don't steal focus.
                handled = true;
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                return IntPtr.Zero;
            }

            // SC_RESTORE is benign; just keep ourselves at the bottom.
            if ((wParam.ToInt64() & 0xFFF0) == SC_RESTORE)
            {
                SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                return IntPtr.Zero;
            }
        }

        // 2) ShowWindow(SW_SHOWMINIMIZED) is sent to DefWindowProc as
        //    WM_SIZE wParam=SIZE_MINIMIZED after the fact. By then the
        //    window is already hidden. The best we can do is immediately
        //    restore, which we do here, and then re-pin.
        if (msg == WM_SIZE && DesktopPinLogic.IsSizeMinimized(wParam))
        {
            handled = true;
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return IntPtr.Zero;
        }

        // 3) The shell "show desktop" gesture on Windows 11 also broadcasts
        //    a shell hook to every top-level window. We register for it
        //    during Glue and re-pin whenever we see it. The broadcast itself
        //    is a no-op for us; the important bit is to re-assert our z-order
        //    and visibility.
        if (msg == WM_SHELLHOOK)
        {
            // wParam is one of the HSHELL_* codes. The "interesting" ones
            // for desktop widgets are HSHELL_WINDOWACTIVATED (the user
            // activated another window) and HSHELL_REDRAW (a window has
            // been redrawn). In both cases, the safest reaction is to
            // re-pin ourselves. This is a cheap call (no-op when already
            // bottom) so doing it on every shell hook is fine.
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return IntPtr.Zero;
        }

        // 4) WM_ACTIVATEAPP false means another app became active. Windows
        //    can pair this with a show-desktop transition; re-pin so the
        //    widget is still under the newly-foregrounded app.
        if (msg == WM_ACTIVATEAPP && wParam == IntPtr.Zero)
        {
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return IntPtr.Zero;
        }

        // 5) WM_WININICHANGE / WM_SETTINGCHANGE with SPI_SETDESKWALLPAPER
        //    is the system telling us "the desktop state changed" (either
        //    the wallpaper was swapped or Show Desktop was toggled). It's a
        //    coarse signal -- many settings changes use the same code -- but
        //    it's also the most reliable way to detect a Show Desktop cycle
        //    that bypassed every other path. A spurious re-pin costs nothing.
        if (msg == WM_WININICHANGE)
        {
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return IntPtr.Zero;
        }

        // 6) WM_WINDOWPOSCHANGING: the most important catch-all. Every
        //    minimise code path eventually funnels here, because the OS
        //    always notifies a window of an upcoming position/size change
        //    before it actually happens. We:
        //      - Force the z-order to HWND_BOTTOM
        //      - Reject any size change to a tiny rect (the icon state)
        //      - Reject any explicit minimise/hide flag
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);

            // Force z-order to bottom unless the caller explicitly
            // asked for "no z-order change" (e.g. a move/resize).
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }

            // Reject SWP_HIDEWINDOW outright: the widget is never hidden.
            if ((pos.flags & SWP_HIDEWINDOW) != 0)
            {
                pos.flags &= ~SWP_HIDEWINDOW;
                pos.flags |= SWP_SHOWWINDOW;
                Marshal.StructureToPtr(pos, lParam, false);
            }

            // If the new size is the icon rect (height <= 32, width < the
            // normal width) AND the caller didn't pass SWP_NOSIZE, treat
            // this as a minimise attempt and keep the normal size. This
            // catches every minimise code path the WndProc-level hooks
            // above don't.
            if ((pos.flags & SWP_NOSIZE) == 0
                && _normalWidth > 0
                && DesktopPinLogic.IsMinimizeSize(pos.cx, pos.cy, _normalWidth, _normalHeight))
            {
                pos.cx = (int)_normalWidth;
                pos.cy = (int)_normalHeight;
                pos.flags &= ~SWP_FRAMECHANGED;   // we're not really changing the frame
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Update the cached "normal" window size used by the WndProc hook to
    /// tell a real resize from a minimise-to-icon. Called from the
    /// SizeChanged handler in MainWindow so the expand/collapse animation
    /// is not misclassified as a minimise attempt.
    /// </summary>
    public static void UpdateNormalSize(double width, double height)
    {
        if (width > 0) _normalWidth = width;
        if (height > 0) _normalHeight = height;
    }

    /// <summary>Back to a normal top-level window.</summary>
    public static void Unglue(Window window)
    {
        var source = SourceFromWindow(window);
        if (source is null) return;

        var hwnd = source.Handle;

        if (_hook is not null && hwnd == _hookedHwnd)
        {
            source.RemoveHook(_hook);
            _hook = null;
            _hookedHwnd = IntPtr.Zero;
        }

        if (_reparented)
        {
            SetParent(hwnd, IntPtr.Zero);
            SetWindowRgn(hwnd, IntPtr.Zero, true);   // corners back to DWM
            _reparented = false;
        }

        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) & ~WS_EX_TOOLWINDOW);

        WindowMaterial.Apply(window, acrylic: false);
    }

    // ---- Legacy WorkerW path (opt-in) -------------------------------------

    private static bool GlueWorkerW(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return false;

        // Ask Progman to spawn the wallpaper WorkerW (no-op if it exists).
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0x0, 1000, out _);

        // The WorkerW we want is the sibling AFTER the one hosting the
        // desktop icons (SHELLDLL_DefView).
        var target = IntPtr.Zero;
        var worker = IntPtr.Zero;
        while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
        {
            if (FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                target = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
        }

        // Win11 24H2 sometimes hosts the wallpaper directly under Progman.
        if (target == IntPtr.Zero) target = progman;

        var prev = window.Topmost;
        window.Topmost = false;

        if (SetParent(hwnd, target) == IntPtr.Zero)
        {
            window.Topmost = prev;
            return false;
        }

        _reparented = true;
        ApplyCornerRegion(window);
        return true;
    }

    /// <summary>
    /// Clip our own rounded corners. Only needed on the WorkerW path, where
    /// DWM's corner attribute stops applying; the bottom-most path keeps DWM
    /// corners and this becomes a no-op.
    /// </summary>
    public static void ApplyCornerRegion(Window window)
    {
        if (!_reparented) return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var w = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;

        var r = (int)Math.Round(14 * dpi.DpiScaleX);
        var region = CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r);
        SetWindowRgn(hwnd, region, true);   // the OS owns the region after this
    }

    private static HwndSource? SourceFromWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd == IntPtr.Zero ? null : HwndSource.FromHwnd(hwnd);
    }

    // ---- ShowWindow constants (not in the BCL) ---------------------------

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_RESTORE = 9;
    private const int SW_SHOWNORMAL = 1;
}
