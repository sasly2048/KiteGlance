namespace KiteGlance.Services;

/// <summary>
/// The profit-and-loss arithmetic for a single holding, extracted into one
/// pure, UI-free, side-effect-free place.
///
/// This lives on its own for two reasons. First, it is the most
/// consequential logic in the app -- three separate bugs in this project came
/// from getting it subtly wrong -- so it earns a home where it can be unit
/// tested in isolation, with no WPF or network in the way. Second, keeping it
/// pure means the exact same code runs in the widget and in the test suite;
/// the tests verify what actually ships, not a paraphrase of it.
///
/// Every method is static and deterministic: same inputs, same output, always.
/// </summary>
public static class PnlMath
{
    /// <summary>
    /// Whether Kite genuinely reported a P&L for this holding.
    ///
    /// The MF endpoint returns pnl: 0 -- a literal zero, not null -- for
    /// holdings it has not computed. Treating that as authoritative zeroes out
    /// real money: every row reads +Rs 0 and current collapses onto invested.
    /// A zero from an API that also reports a moving NAV is not a fact; it is
    /// an absence wearing a number's clothes. So a reported P&L counts only
    /// when it is present AND non-zero.
    /// </summary>
    public static bool KiteReportedPnl(decimal? apiPnl)
        => apiPnl is not null && apiPnl.Value != 0m;

    /// <summary>Amount put in: quantity times the average buy price.</summary>
    public static decimal Invested(decimal qty, decimal avgPrice)
        => qty * avgPrice;

    /// <summary>
    /// Profit or loss. Kite's own figure when it gave us one; otherwise
    /// (last - avg) * qty, which reproduces Coin's numbers exactly. When Kite
    /// has not priced the units yet, held at cost, so zero.
    /// </summary>
    public static decimal Pnl(
        decimal qty, decimal avgPrice, decimal lastPrice,
        decimal? apiPnl, bool awaitingPrice)
    {
        if (awaitingPrice) return 0m;
        if (KiteReportedPnl(apiPnl)) return apiPnl!.Value;
        return (lastPrice - avgPrice) * qty;
    }

    /// <summary>
    /// Current value, kept consistent with <see cref="Pnl"/> by construction so
    /// a row can never show a current value that contradicts its own P&L:
    /// when we take Kite's P&L, current is invested + that P&L; when we compute
    /// P&L from the NAV, current is qty * last_price.
    /// </summary>
    public static decimal Current(
        decimal qty, decimal avgPrice, decimal lastPrice,
        decimal? apiPnl, bool awaitingPrice)
    {
        if (awaitingPrice) return Invested(qty, avgPrice);
        if (KiteReportedPnl(apiPnl)) return Invested(qty, avgPrice) + apiPnl!.Value;
        return qty * lastPrice;
    }

    /// <summary>P&L as a percentage of invested. Zero when nothing is invested.</summary>
    public static decimal PnlPct(decimal pnl, decimal invested)
        => invested > 0m ? pnl / invested * 100m : 0m;

    /// <summary>
    /// A single holding's contribution to the day's P&L.
    ///
    /// T1 stock was bought today and held no position at yesterday's close,
    /// so crediting it a full day's move inflates the day figure and
    /// makes the widget disagree with the Kite app. Only the settled
    /// quantity counts. Kite's `day_change` is preferred when present
    /// because it is the value the app's own dashboard uses; the
    /// (last - close) computation is the fallback.
    /// </summary>
    public static decimal DayPnl(
        decimal quantity, decimal t1Quantity,
        decimal lastPrice, decimal closePrice,
        decimal? dayChange)
    {
        // Kite's own day-change figure applies to the settled quantity
        // already, so T1-exclusion is implicit.
        if (dayChange is { } change) return quantity * change;

        // A zero close means the security was not trading yesterday --
        // booking a full move against zero would credit the entire
        // position with "today's gain". Treat as no day-P&L.
        if (closePrice <= 0m) return 0m;

        // Per the Kite holdings API, `quantity` is the SETTLED quantity
        // (delivered to demat) and `t1Quantity` is a separate, additive count
        // of shares bought today that have not settled. Only settled shares
        // held a position at yesterday's close, so the day-P&L multiplier is
        // `quantity` directly. A whole position still in T1 is `quantity == 0`
        // -- t1Quantity is not part of this arithmetic beyond documenting why
        // the settled figure is the right one.
        if (quantity <= 0m) return 0m;

        return quantity * (lastPrice - closePrice);
    }
}
