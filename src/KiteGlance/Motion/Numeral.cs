using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;

namespace KiteGlance.Motion;

/// <summary>
/// Every number in the widget moves by the same law.
///
/// v5 animated the hero and let Invested, Current and Overall snap. That
/// inconsistency is subliminal but corrosive: it tells you the hero's count-up
/// is decoration bolted on, rather than physics the interface obeys. Once every
/// numeral tweens on the same curve, the motion stops reading as an effect and
/// starts reading as a property of the material.
///
/// One global rendering hook drives all live tweens, rather than each TextBlock
/// subscribing its own -- WPF raises CompositionTarget.Rendering once per frame
/// regardless, and N handlers on one event is N times the delegate dispatch for
/// no gain.
/// </summary>
public static class Numeral
{
    private sealed class Tween
    {
        public decimal From;
        public decimal To;
        public double Elapsed;
        public double Duration;
        public Func<decimal, string> Format = _ => "";
    }

    private static readonly ConditionalWeakTable<TextBlock, Tween> Live = new();
    private static readonly List<TextBlock> Active = new();

    private static bool _hooked;
    private static TimeSpan _last;

    /// <summary>
    /// Quartic ease-out. Leaves fast, arrives patient.
    ///
    /// A count-up is one of the few places a spring is wrong: overshoot would
    /// mean rendering a number you never held, then correcting it. Money should
    /// never overshoot. So the numbers ease and the *layout* springs -- two
    /// different laws, each honest about what it is describing.
    /// </summary>
    private static double Ease(double t) => 1 - Math.Pow(1 - t, 4);

    public static void Set(
        TextBlock target,
        decimal value,
        Func<decimal, string> format,
        double ms = 680,
        bool animate = true)
    {
        if (!animate)
        {
            Live.Remove(target);
            Active.Remove(target);
            target.Text = format(value);
            return;
        }

        var from = Live.TryGetValue(target, out var prev)
            ? Lerp(prev)          // retarget mid-flight from where it actually is
            : value;              // first paint: no tween, just land

        if (!Live.TryGetValue(target, out _))
        {
            target.Text = format(value);
            Live.Add(target, new Tween
            {
                From = value,
                To = value,
                Elapsed = ms,
                Duration = ms,
                Format = format
            });
            return;
        }

        Live.Remove(target);
        Live.Add(target, new Tween
        {
            From = from,
            To = value,
            Elapsed = 0,
            Duration = ms,
            Format = format
        });

        if (!Active.Contains(target)) Active.Add(target);
        Hook();
    }

    /// <summary>Where a tween currently sits, so a retarget doesn't jump.</summary>
    private static decimal Lerp(Tween t)
    {
        var p = t.Duration <= 0 ? 1 : Math.Min(t.Elapsed / t.Duration, 1);
        return t.From + (t.To - t.From) * (decimal)Ease(p);
    }

    private static void Hook()
    {
        if (_hooked) return;

        _hooked = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs r) return;

        var dt = _last == TimeSpan.Zero ? 16 : (r.RenderingTime - _last).TotalMilliseconds;
        _last = r.RenderingTime;

        // Guard against a stalled frame (dragged window, sleeping laptop)
        // teleporting every tween to its endpoint.
        dt = Math.Clamp(dt, 0, 64);

        for (var i = Active.Count - 1; i >= 0; i--)
        {
            var tb = Active[i];

            if (!Live.TryGetValue(tb, out var tw))
            {
                Active.RemoveAt(i);
                continue;
            }

            tw.Elapsed += dt;

            var p = Math.Min(tw.Elapsed / tw.Duration, 1);
            var v = tw.From + (tw.To - tw.From) * (decimal)Ease(p);

            tb.Text = tw.Format(v);

            if (p >= 1)
            {
                tb.Text = tw.Format(tw.To);
                tw.From = tw.To;
                Active.RemoveAt(i);
            }
        }

        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }
    }

    /// <summary>
    /// Forget cached values, e.g. when switching tabs.
    ///
    /// Marshalled onto the dispatcher. `Active` is walked by index inside
    /// <see cref="OnFrame"/>, which runs on the UI thread; mutating it from any
    /// other thread mid-frame shifts the indices under that loop and throws
    /// ArgumentOutOfRangeException on the dispatcher, taking the app down.
    /// TextBlock is thread-affine anyway, so this is where it has to happen.
    /// </summary>
    public static void Reset(params TextBlock[] targets)
    {
        if (targets.Length == 0) return;

        var dispatcher = targets[0].Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Reset(targets));
            return;
        }

        foreach (var t in targets)
        {
            Live.Remove(t);
            Active.Remove(t);
        }
    }
}
