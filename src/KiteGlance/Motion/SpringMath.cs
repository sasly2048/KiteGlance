using System;

namespace KiteGlance.Motion;

/// <summary>
/// Pure damped-harmonic-oscillator math, extracted from the
/// WPF-coupled <c>SpringEase</c> class so the test target can
/// exercise it without pulling WPF into the net8.0 build.
///
/// The two implementations are kept in lockstep: a change to
/// the math here must be mirrored in <c>SpringEase.EaseInCore</c>,
/// and vice versa. The
/// <c>SpringEase.Ease(double, double, double, double)</c> thin
/// wrapper exists for the test target only; the WPF-coupled class
/// inlines the same math in <c>EaseInCore</c> so it does not have
/// to call into a non-WPF sibling.
/// </summary>
public static class SpringMath
{
    /// <summary>
    /// The eased value for a damped harmonic oscillator at time
    /// <paramref name="t"/>. Returns a value in [0, 1] for the
    /// under/critically/over-damped regimes the widget uses.
    /// </summary>
    /// <param name="t">Normalised time, where 0 is the start of
    /// the animation and large values are the settled state.</param>
    /// <param name="stiffness">How hard the spring pulls. Higher
    /// = faster arrival.</param>
    /// <param name="damping">How much the system resists. Below
    /// 2*sqrt(stiffness*mass) the system overshoots.</param>
    /// <param name="mass">Inertia. Clamped to a tiny positive
    /// value to avoid division by zero.</param>
    public static double Ease(double t, double stiffness, double damping, double mass)
    {
        var m = Math.Max(mass, 0.0001);
        var w0 = Math.Sqrt(stiffness / m);                       // natural frequency
        var zeta = damping / (2 * Math.Sqrt(stiffness * m));     // damping ratio

        double x;

        if (zeta < 1)
        {
            // Underdamped: overshoots, then rings down. This is the good one.
            var wd = w0 * Math.Sqrt(1 - zeta * zeta);            // damped frequency
            x = Math.Exp(-zeta * w0 * t) *
                (Math.Cos(wd * t) + zeta * w0 / wd * Math.Sin(wd * t));
        }
        else
        {
            // Critically damped: fastest approach with no overshoot.
            x = Math.Exp(-w0 * t) * (1 + w0 * t);
        }

        return 1 - x;
    }
}
