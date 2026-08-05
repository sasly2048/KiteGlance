using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace KiteGlance.ViewModels;

public class HoldingViewModel
{
    private static readonly Brush Up =
        Frozen(new SolidColorBrush(Color.FromRgb(0x32, 0xD7, 0x4B)));   // systemGreen

    private static readonly Brush Down =
        Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));   // systemRed

    private static readonly Brush Muted =
        Frozen(new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)));

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
