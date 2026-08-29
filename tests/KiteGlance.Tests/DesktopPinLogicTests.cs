using System;
using KiteGlance.Interop;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// The desktop-pin WndProc hook has to recognise every Win32 message that
/// can lead to a WS_MINIMIZE state, because the Win11 four-finger
/// swipe-down gesture (and the Win+D hotkey) uses paths that bypass the
/// classic SC_MINIMIZE route. These tests pin the decision logic so a
/// future change to the thresholds or opcode table is caught immediately.
/// </summary>
public class DesktopPinLogicTests
{
    // ---- IsMinimizeSysCommand ------------------------------------------

    [Fact]
    public void IsMinimizeSysCommand_recognises_SC_MINIMIZE()
    {
        var wParam = new IntPtr(0xF020);
        Assert.True(DesktopPinLogic.IsMinimizeSysCommand(wParam));
    }

    [Fact]
    public void IsMinimizeSysCommand_recognises_SC_MAXIMIZE()
    {
        // Maximise also counts: it removes the widget from the bottom of
        // the z-order the same way a minimise does, and the user-visible
        // result is "the widget is no longer where I left it".
        var wParam = new IntPtr(0xF030);
        Assert.True(DesktopPinLogic.IsMinimizeSysCommand(wParam));
    }

    [Fact]
    public void IsMinimizeSysCommand_recognises_SC_DESKTOP_from_Win11_gesture()
    {
        // SC_DESKTOP is the undocumented code that Windows 11's
        // four-finger-swipe-down broadcasts to every top-level window. It
        // is the actual reason the widget disappears in the user's
        // report, so the test guards this against accidental removal.
        var wParam = new IntPtr(0xF130);
        Assert.True(DesktopPinLogic.IsMinimizeSysCommand(wParam));
    }

    [Fact]
    public void IsMinimizeSysCommand_masks_off_modifier_bits()
    {
        // The low 4 bits of wParam are modifier flags (MK_CONTROL etc.).
        // 0xF020 | 0x0008 = 0xF028 (SC_MINIMIZE with MK_SHIFT). The
        // heuristic must still match.
        var wParam = new IntPtr(0xF028);
        Assert.True(DesktopPinLogic.IsMinimizeSysCommand(wParam));
    }

    [Fact]
    public void IsMinimizeSysCommand_rejects_SC_RESTORE()
    {
        // SC_RESTORE is the opposite of minimise; it means "come back to
        // normal". Re-asserting HWND_BOTTOM is fine, but this is not a
        // minimise-class command and should not be handled as one.
        var wParam = new IntPtr(0xF120);
        Assert.False(DesktopPinLogic.IsMinimizeSysCommand(wParam));
    }

    [Fact]
    public void IsMinimizeSysCommand_rejects_unrelated_opcodes()
    {
        Assert.False(DesktopPinLogic.IsMinimizeSysCommand(new IntPtr(0xF060)));  // SC_CLOSE
        Assert.False(DesktopPinLogic.IsMinimizeSysCommand(new IntPtr(0xF010)));  // SC_MOVE
        Assert.False(DesktopPinLogic.IsMinimizeSysCommand(new IntPtr(0xF000)));  // SC_SIZE
        Assert.False(DesktopPinLogic.IsMinimizeSysCommand(IntPtr.Zero));         // bogus
    }

    // ---- IsSizeMinimized -----------------------------------------------

    [Fact]
    public void IsSizeMinimized_recognises_SIZE_MINIMIZED()
    {
        var wParam = new IntPtr(1);  // SIZE_MINIMIZED
        Assert.True(DesktopPinLogic.IsSizeMinimized(wParam));
    }

    [Fact]
    public void IsSizeMinimized_rejects_SIZE_RESTORED()
    {
        var wParam = new IntPtr(0);  // SIZE_RESTORED
        Assert.False(DesktopPinLogic.IsSizeMinimized(wParam));
    }

    [Fact]
    public void IsSizeMinimized_rejects_SIZE_MAXIMIZED()
    {
        // Maximised is a different visual state. The user can still see
        // the widget; the z-order is what we care about, not the size.
        var wParam = new IntPtr(2);  // SIZE_MAXIMIZED
        Assert.False(DesktopPinLogic.IsSizeMinimized(wParam));
    }

    [Fact]
    public void IsSizeMinimized_rejects_SIZE_MAXSHOW()
    {
        var wParam = new IntPtr(3);  // SIZE_MAXSHOW
        Assert.False(DesktopPinLogic.IsSizeMinimized(wParam));
    }

    // ---- IsMinimizeSize -------------------------------------------------

    [Fact]
    public void IsMinimizeSize_rejects_a_typical_normal_size()
    {
        // A 372x256 widget is the compact view; that is the normal state
        // and must not be misclassified as an icon.
        Assert.False(DesktopPinLogic.IsMinimizeSize(372, 256, 372, 256));
    }

    [Fact]
    public void IsMinimizeSize_rejects_the_expanded_normal_size()
    {
        // When the holdings pane is open, the widget is ~592px tall.
        // Still not an icon.
        Assert.False(DesktopPinLogic.IsMinimizeSize(372, 592, 372, 256));
    }

    [Fact]
    public void IsMinimizeSize_recognises_a_typical_icon_rect()
    {
        // Windows minimises to ~160x28 by default. That is the canonical
        // "I'm being minimised" signal.
        Assert.True(DesktopPinLogic.IsMinimizeSize(160, 28, 372, 256));
    }

    [Fact]
    public void IsMinimizeSize_recognises_a_taller_icon_rect()
    {
        // High-DPI / accessibility settings can push the icon up to ~32px.
        Assert.True(DesktopPinLogic.IsMinimizeSize(180, 32, 372, 256));
    }

    [Fact]
    public void IsMinimizeSize_rejects_a_just_tall_enough_rect()
    {
        // Anything above 32 is the threshold for "definitely not an
        // icon". 33 is the smallest legitimate false.
        Assert.False(DesktopPinLogic.IsMinimizeSize(180, 33, 372, 256));
    }

    [Fact]
    public void IsMinimizeSize_rejects_an_icon_tall_but_normal_wide()
    {
        // Height looks like an icon, but width is full normal -- this
        // is a different layout, not a minimise.
        Assert.False(DesktopPinLogic.IsMinimizeSize(372, 28, 372, 256));
    }

    [Fact]
    public void IsMinimizeSize_rejects_a_zero_normal_size()
    {
        // Defensive: with no "normal" to compare against, we should not
        // classify any size as an icon. This would otherwise be a divide-
        // by-zero in a previous version of the code.
        Assert.False(DesktopPinLogic.IsMinimizeSize(160, 28, 0, 0));
    }

    [Fact]
    public void IsMinimizeSize_rejects_a_larger_than_quarter_normal_height()
    {
        // The "is this a quarter of the normal height" check is a second
        // filter. A 200x200 rect against a 372x256 normal is 78% of the
        // normal height -- not an icon, no matter what the width is.
        Assert.False(DesktopPinLogic.IsMinimizeSize(100, 200, 372, 256));
    }

    // ---- Edge cases on the wParam mask ----------------------------------

    [Fact]
    public void IsMinimizeSysCommand_handles_a_negative_wParam()
    {
        // IntPtr can be negative on 64-bit. ToInt64 should not throw, and
        // the masking should still work. (-1 has all bits set, so the
        // mask gives 0xFFF0, which doesn't match any known SC_).
        var wParam = new IntPtr(-1);
        Assert.False(DesktopPinLogic.IsMinimizeSysCommand(wParam));
    }

    [Fact]
    public void IsMinimizeSysCommand_handles_garbage_high_bits()
    {
        // wParam = 0xFFF0 is "no SC_ code, all modifier bits". The mask
        // (0xFFF0) strips the low 4 bits, so this should NOT match any
        // minimise command (0xFFF0 is not a valid SC_).
        //
        // Note: wParam is documented as 32 bits, and real values never
        // have garbage above bit 15. The mask 0xFFF0 is the SC_MASK
        // from Windows headers.
        var wParam = new IntPtr(0xFFF0);
        Assert.False(DesktopPinLogic.IsMinimizeSysCommand(wParam));
    }
}
