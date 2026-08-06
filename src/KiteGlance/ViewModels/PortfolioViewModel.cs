using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

// WinForms is referenced alongside WPF for the tray icon, and it defines its
// own Point. Alias every ambiguous name to the WPF one, as the brushes above
// already do, so the wrong type cannot be picked up silently.
using Point = System.Windows.Point;
using PointCollection = System.Windows.Media.PointCollection;
using Visibility = System.Windows.Visibility;

namespace KiteGlance.ViewModels;

public class HoldingViewModel
{
    private static readonly Brush Up =
        Frozen(new SolidColorBrush(Color.FromRgb(0x32, 0xD7, 0x4B)));   // systemGreen

    private static readonly Brush Down =
        Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));   // systemRed

    private static readonly Brush Muted =
        Frozen(new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)));

    /// <summary>Sparkline box, in device-independent pixels. The window is a
    /// fixed 372 wide, so this is a budget, not a preference.</summary>
    public const double SparkWidth = 44;
    public const double SparkHeight = 16;

    public string RawSymbol { get; set; } = "";
    public decimal Qty { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal LastPrice { get; set; }
    public bool AwaitingPrice { get; set; }
    public decimal? ApiPnl { get; set; }

    public string Symbol => Money.PrettyName(RawSymbol);

    // All P&L arithmetic delegates to the one pure, unit-tested implementation
    // in KiteGlance.Services.PnlMath, so the row viewmodel can never drift from
    // what the service computes. See PnlMath for why pnl: 0 is not trusted.
    public decimal Invested => Services.PnlMath.Invested(Qty, AvgPrice);

    public decimal Pnl =>
        Services.PnlMath.Pnl(Qty, AvgPrice, LastPrice, ApiPnl, AwaitingPrice);

    public decimal Current =>
        Services.PnlMath.Current(Qty, AvgPrice, LastPrice, ApiPnl, AwaitingPrice);

    public decimal PnlPct => Services.PnlMath.PnlPct(Pnl, Invested);

    public string PnlDisplay => AwaitingPrice
        ? "--"
        : Money.Signed(Pnl);

    public string ReturnDisplay => AwaitingPrice
        ? "not priced yet"
        : Money.Percent(PnlPct);

    public string InvestedDisplay => Money.Rupees(Invested);
    public string CurrentDisplay => Money.Rupees(Current);

    public Brush PnlColor => AwaitingPrice
        ? Muted
        : (Pnl >= 0 ? Up : Down);

    // -- Sparkline -------------------------------------------------------

    /// <summary>
    /// Recent prices for this holding, oldest first. Null when too few points
    /// have been collected to draw an honest line.
    /// </summary>
    public IReadOnlyList<decimal>? History { get; set; }

    private PointCollection? _spark;
    private bool _sparkBuilt;

    /// <summary>
    /// The price series mapped into the sparkline box, or null when there is
    /// nothing to draw. Built once and cached: the row template binds it and
    /// WPF may ask more than once, and re-normalising on every read would show
    /// up as jitter during the entrance stagger.
    ///
    /// Scaling is per-row, against that row's own min and max. A shared scale
    /// across the whole list would flatten every line but the most volatile
    /// one; the question a sparkline answers is "which way has THIS moved",
    /// not "how does its volatility compare to the others".
    /// </summary>
    public PointCollection? Spark
    {
        get
        {
            if (_sparkBuilt) return _spark;
            _sparkBuilt = true;

            var series = History;
            if (series is null || series.Count < 2) return _spark = null;

            decimal lo = series[0], hi = series[0];
            foreach (var v in series)
            {
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }

            var points = new PointCollection(series.Count);
            var span = (double)(hi - lo);
            var stepX = SparkWidth / (series.Count - 1);

            // A dead-flat series has no range to scale against. Draw it down
            // the middle rather than dividing by zero or hiding the row's line
            // entirely -- "it did not move" is real information.
            var flat = span <= 0;

            for (var i = 0; i < series.Count; i++)
            {
                var y = flat
                    ? SparkHeight / 2
                    : SparkHeight - (double)(series[i] - lo) / span * SparkHeight;

                points.Add(new Point(i * stepX, y));
            }

            points.Freeze();
            return _spark = points;
        }
    }

    /// <summary>Hidden until there is a line, so a fresh install shows empty
    /// space rather than a stub or a placeholder.</summary>
    public Visibility SparkVisibility =>
        Spark is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// The line follows the direction of the series itself, not the row's P&L.
    /// A holding can be up overall while the last few days ran down, and
    /// colouring the line by total P&L would contradict its own shape.
    /// </summary>
    public Brush SparkColor
    {
        get
        {
            var series = History;
            if (series is null || series.Count < 2) return Muted;

            return series[^1] >= series[0] ? Up : Down;
        }
    }

    /// <summary>
    /// The row shows a cleaned-up name and rounded figures. The tooltip shows
    /// the truth: the real ticker, the exact quantity, the precise average.
    /// Compression is for glanceability; the underlying facts stay reachable.
    /// </summary>
    public string Tip =>
        RawSymbol + "\n"
        + Qty.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("en-IN"))
        + " units at " + Money.Exact(AvgPrice)
        + (AwaitingPrice
            ? "\nNot priced by Kite yet - held at cost"
            : "\nNow " + Money.Exact(LastPrice))
        + "\n\nClick to copy";

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Formatting rules, in one place, because a number rendered two different
/// ways in one window is the loudest tell that nobody was paying attention.
/// </summary>
public static class Money
{
    public const string RS = "\u20B9";
    public const string MINUS = "\u2212";     // true minus, not a hyphen

    private static readonly CultureInfo IN = new("en-IN");

    /// <summary>
    /// Precision follows magnitude, the way a person would speak it.
    ///
    ///   0.94    -> Rs 0.94    (never "Rs 1" -- rounding a small move into
    ///                          meaninglessness is worse than showing nothing)
    ///   94.50   -> Rs 94.50
    ///   6,900   -> Rs 6,900   (paise are noise at this scale)
    ///   1.24 L  -> Rs 1.24L
    /// </summary>
    public static string Rupees(decimal v)
    {
        var a = Math.Abs(v);

        if (a >= 10_000_000) return RS + (v / 10_000_000).ToString("0.00", IN) + "Cr";
        if (a >= 100_000) return RS + (v / 100_000).ToString("0.00", IN) + "L";
        // AwayFromZero, not the default ToEven. Banker's rounding turns
        // Rs 2,500.50 into Rs 2,500 while Kite shows Rs 2,501 -- a visible Rs 1
        // disagreement on any exact half.
        if (a >= 1_000) return RS + Math.Round(v, MidpointRounding.AwayFromZero).ToString("N0", IN);

        return RS + v.ToString("0.##", IN);
    }

    /// <summary>Full precision, for tooltips. No compression, no rounding.</summary>
    public static string Exact(decimal v) => RS + v.ToString("N2", IN);

    public static string Signed(decimal v) =>
        (v >= 0 ? "+" : MINUS) + Rupees(Math.Abs(v));

    public static string Percent(decimal v) =>
        (v >= 0 ? "+" : MINUS) + Math.Abs(v).ToString("0.00", IN) + "%";

    /// <summary>
    /// "HDFC GOLD ETF FUND OF FUND - DIRECT PLAN" is a database key, not a name.
    /// Nobody shouts at you from their portfolio. Strip the plan boilerplate --
    /// you only hold one variant, so it carries no information -- and set it in
    /// title case with the acronyms left standing.
    /// </summary>
    public static string PrettyName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unnamed";

        var s = raw.Trim();

        // Plan boilerplate: true of every row, therefore says nothing.
        s = Regex.Replace(s,
            @"\s*[-\u2013]?\s*(DIRECT|REGULAR)\s+PLAN\b", "",
            RegexOptions.IgnoreCase);

        s = Regex.Replace(s,
            @"\s*[-\u2013]?\s*(GROWTH|IDCW|DIVIDEND)(\s+OPTION)?\b", "",
            RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"\bFUND OF FUND\b", "FoF", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*[-\u2013]\s*$", "");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();

        return TitleCase(s);
    }

    // Acronyms that must not be softened into Hdfc, Etf, Sbi.
    private static readonly HashSet<string> Keep = new(StringComparer.OrdinalIgnoreCase)
    {
        "HDFC", "ICICI", "SBI", "UTI", "ETF", "FOF", "NAV", "IT", "PSU", "FMCG",
        "NIFTY", "BSE", "NSE", "IDFC", "DSP", "PGIM", "LIC", "AMC", "REIT",
        "US", "UK", "GDP", "IPO", "ELSS", "NFO", "TATA", "L&T", "HSBC", "JM",
        "IDCW", "G-SEC", "SENSEX"
    };

    private static string TitleCase(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder(s.Length);

        foreach (var w in parts)
        {
            if (sb.Length > 0) sb.Append(' ');

            var bare = w.Trim('(', ')', '-', ',');

            if (Keep.Contains(bare) || bare.Length <= 2 && bare.All(char.IsUpper))
            {
                sb.Append(w.ToUpperInvariant());
            }
            else if (w.Length == 1)
            {
                sb.Append(char.ToUpperInvariant(w[0]));
            }
            else
            {
                sb.Append(char.ToUpperInvariant(w[0]));
                sb.Append(w[1..].ToLowerInvariant());
            }
        }

        return sb.ToString();
    }
}
