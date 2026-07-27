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
/// intact -- blackout is impossible), and a WndProc hook forces every z-order
/// change to land at HWND_BOTTOM. Click it, drag it, open apps over it: it
/// stays glued under everything. WS_EX_TOOLWINDOW keeps it out of Alt+Tab.
///
/// Trade-offs vs WorkerW, stated honestly:
///   - Win+D / "show desktop" minimizes it (WorkerW survived that). We listen
///     for that and quietly restore, so in practice it blinks rather than
///     vanishes.
///   - It exists on one virtual desktop at a time.
/// The WorkerW path is kept, opt-in, for setups where it renders correctly:
/// set KITEGLANCE_WORKERW=1.
/// </summary>
public static class DesktopPin
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const uint SWP_NOZORDER = 0x0004;

    private static readonly IntPtr HWND_BOTTOM = new(1);

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

    /// <summary>Opt-in escape hatch to the legacy WorkerW reparenting.</summary>
    public static bool UseWorkerW =>
        Environment.GetEnvironmentVariable("KITEGLANCE_WORKERW") == "1";

    private static HwndSourceHook? _hook;
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

        // Land at the bottom now...
        window.Topmost = false;
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
            0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/ | 0x0010 /*NOACTIVATE*/);

        // ...and stay there: every future z-order change is redirected to
        // HWND_BOTTOM before the window manager acts on it. This is what
        // makes clicking the widget not raise it.
        if (_hook is null)
        {
            _hook = KeepAtBottom;
            source.AddHook(_hook);
        }

        return true;
    }

    private static IntPtr KeepAtBottom(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Show Desktop, including four-finger touchpad gestures, sends a
        // minimize command to ordinary top-level windows. Ignore it while
        // desktop-pinned; explicit quit remains available from the tray.
        if (msg == WM_SYSCOMMAND
            && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
        {
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/ | 0x0010 /*NOACTIVATE*/);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Back to a normal top-level window.</summary>
    public static void Unglue(Window window)
    {
        var source = SourceFromWindow(window);
        if (source is null) return;

        var hwnd = source.Handle;

        if (_hook is not null)
        {
            source.RemoveHook(_hook);
            _hook = null;
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
}
