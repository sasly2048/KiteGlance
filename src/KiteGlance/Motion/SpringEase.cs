using System.Windows;
using System.Windows.Media.Animation;

namespace KiteGlance.Motion;

/// <summary>
/// A real spring, not a bezier pretending to be one.
///
/// Solves the damped harmonic oscillator x'' + 2*zeta*w0*x' + w0^2*x = 0
/// analytically, so motion carries momentum: it arrives fast, overshoots by a
/// hair, and settles. That tiny overshoot is the entire difference between
/// "animated" and "alive".
///
///   Stiffness -> how hard it pulls toward the target
///   Damping   -> how much the system resists; below critical, it overshoots
///   Mass      -> inertia
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty StiffnessProperty =
        DependencyProperty.Register(nameof(Stiffness), typeof(double), typeof(SpringEase),
            new PropertyMetadata(170.0));

    public static readonly DependencyProperty DampingProperty =
        DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase),
            new PropertyMetadata(20.0));

    public static readonly DependencyProperty MassProperty =
        DependencyProperty.Register(nameof(Mass), typeof(double), typeof(SpringEase),
            new PropertyMetadata(1.0));

    public double Stiffness
    {
        get => (double)GetValue(StiffnessProperty);
        set => SetValue(StiffnessProperty, value);
    }

    public double Damping
    {
        get => (double)GetValue(DampingProperty);
        set => SetValue(DampingProperty, value);
    }

    public double Mass
    {
        get => (double)GetValue(MassProperty);
        set => SetValue(MassProperty, value);
    }

    protected override double EaseInCore(double t)
    {
        var m = Math.Max(Mass, 0.0001);
        var w0 = Math.Sqrt(Stiffness / m);                       // natural frequency
        var zeta = Damping / (2 * Math.Sqrt(Stiffness * m));     // damping ratio

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

    protected override Freezable CreateInstanceCore() => new SpringEase();

    // ---- Presets -------------------------------------------------------
    //
    // Frozen on first use. The old code allocated a new SpringEase per call,
    // which is harmless functionally but pointless: the parameters are
    // constant, the easing curve is identical, and a SpringEase is a
    // Freezable (a non-trivial object).

    /// <summary>Layout changes. Confident, a touch of overshoot.</summary>
    public static SpringEase Layout() => CachedLayout ??= Frozen(new SpringEase
    {
        Stiffness = 170,
        Damping = 20,
        EasingMode = EasingMode.EaseIn
    });

    /// <summary>Entrances. Softer landing, no visible bounce.</summary>
    public static SpringEase Gentle() => CachedGentle ??= Frozen(new SpringEase
    {
        Stiffness = 130,
        Damping = 22,
        EasingMode = EasingMode.EaseIn
    });

    /// <summary>Taps and toggles. Snappy, immediate.</summary>
    public static SpringEase Snap() => CachedSnap ??= Frozen(new SpringEase
    {
        Stiffness = 320,
        Damping = 24,
        EasingMode = EasingMode.EaseIn
    });

    private static SpringEase? CachedLayout;
    private static SpringEase? CachedGentle;
    private static SpringEase? CachedSnap;

    private static SpringEase Frozen(SpringEase s)
    {
        s.Freeze();
        return s;
    }
}
