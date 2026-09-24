using KiteGlance.Motion;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// Tests for the damped-harmonic-oscillator easing math. The WPF
/// <c>SpringEase</c> class delegates to a pure static
/// <see cref="SpringMath.Ease(double, double, double, double)"/>
/// so the math is testable on the Linux CI runner. The visual
/// overshoot/settle behaviour the preset table promises is what
/// these tests pin.
/// </summary>
public class SpringEaseTests
{
    /// At t=0 the spring has not moved: the eased value is 0.
    /// A regression that returned 1 here would mean the spring
    /// "arrives" before the animation starts.
    [Fact]
    public void Ease_at_zero_is_zero()
    {
        Assert.Equal(0.0, SpringMath.Ease(0, 170, 20, 1), precision: 6);
    }

    /// Underdamped springs (zeta < 1) overshoot: the eased value
    /// crosses 1.0 at some t > 0 before ringing back down. The
    /// default `Layout` preset is underdamped (zeta ≈ 0.76), so
    /// this is the case the widget's tab switches and pane opens
    /// hit.
    [Fact]
    public void Ease_underdamped_overshoots_unity()
    {
        var overshot = false;
        for (var t = 0.0; t <= 2.0; t += 0.01)
        {
            if (SpringMath.Ease(t, 170, 20, 1) > 1.0)
            {
                overshot = true;
                break;
            }
        }
        Assert.True(overshot, "Layout preset should overshoot 1.0 -- the 'arrives fast, overshoots by a hair' is the whole point");
    }

    /// At t=infinity the spring has fully settled: the eased value
    /// is 1.0. We use a large t rather than `double.PositiveInfinity`
    /// because `Math.Exp` of a very large negative is zero, and a
    /// large finite t gives the same answer without the infinity
    /// arithmetic.
    [Fact]
    public void Ease_at_large_t_is_one()
    {
        Assert.Equal(1.0, SpringMath.Ease(50, 170, 20, 1), precision: 6);
    }

    /// Critically damped (zeta == 1) does NOT overshoot. The
    /// math: x = exp(-w0*t) * (1 + w0*t), which is monotonically
    /// increasing toward 1. With zeta exactly 1 the system has
    /// the fastest approach without overshoot.
    [Fact]
    public void Ease_critically_damped_does_not_overshoot()
    {
        // damping = 2 * sqrt(stiffness * mass) gives zeta = 1.
        // Stiffness=170, mass=1, so damping = 2*sqrt(170) ≈ 26.08.
        var damping = 2 * System.Math.Sqrt(170 * 1);
        var maxValue = 0.0;
        for (var t = 0.0; t <= 2.0; t += 0.01)
        {
            var v = SpringMath.Ease(t, 170, damping, 1);
            if (v > maxValue) maxValue = v;
        }
        // Critically damped: 1 - exp(-w0*t) * (1 + w0*t). The
        // function is monotonically increasing in t (its
        // derivative is positive), so the maximum is at t=2.
        // We do not assert "maxValue == 1.0" because the spring
        // has not fully settled by t=2; we assert the *qualitative*
        // property that it never went above 1.0.
        Assert.True(maxValue <= 1.0,
            $"Critically-damped eased value should not exceed 1.0; got {maxValue}");
    }

    /// Overdamped (zeta > 1) also does not overshoot. The
    /// implementation picks the `else` branch when zeta >= 1, so
    /// this exercises the same branch as critically damped but
    /// with a different zeta. The expected behaviour is the same:
    /// monotonic toward 1.0, no overshoot.
    [Fact]
    public void Ease_overdamped_does_not_overshoot()
    {
        // damping = 4 * sqrt(170) -> zeta = 2, well into the
        // overdamped regime.
        var damping = 4 * System.Math.Sqrt(170 * 1);
        var maxValue = 0.0;
        for (var t = 0.0; t <= 2.0; t += 0.01)
        {
            var v = SpringMath.Ease(t, 170, damping, 1);
            if (v > maxValue) maxValue = v;
        }
        Assert.True(maxValue <= 1.0,
            $"Overdamped eased value should not exceed 1.0; got {maxValue}");
    }

    /// The math clamps the mass to a tiny positive number to
    /// avoid division by zero. `Ease(..., 0)` (mass=0) should not
    /// throw; the `Math.Max(mass, 0.0001)` clamp keeps the formula
    /// stable.
    [Fact]
    public void Ease_handles_zero_mass_without_throwing()
    {
        // Should not throw; the result is well-defined because
        // the formula clamps mass to 0.0001. The specific value
        // is not asserted -- only the absence of an exception.
        _ = SpringMath.Ease(0.5, 170, 20, 0);
        _ = SpringMath.Ease(0.5, 170, 20, -1);
    }
}
