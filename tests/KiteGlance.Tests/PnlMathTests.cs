using KiteGlance.Services;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// Regression tests for the P&L arithmetic. These encode the three bugs this
/// project actually shipped and then fixed, so they can never come back:
///
///   1. Kite's MF pnl: 0 being trusted as a real zero (flatlined the widget).
///   2. current recomputed as qty * last_price drifting from invested + pnl.
///   3. unpriced holdings (last_price: 0) producing a fake loss.
///
/// The numbers are from a real portfolio, cross-checked against what Zerodha
/// Coin displayed at the same moment.
/// </summary>
public class PnlMathTests
{
    // Tolerance: the widget rounds to whole rupees for display, so anything
    // within a rupee is a match at the level that matters to a user. Most
    // assertions are far tighter than this.
    private const decimal Eps = 0.01m;

    // ---- The pnl: 0 trap -------------------------------------------------

    [Fact]
    public void KiteReportedPnl_is_false_for_literal_zero()
    {
        // The whole first-shipped bug in one assertion: a zero is not a report.
        Assert.False(PnlMath.KiteReportedPnl(0m));
    }

    [Fact]
    public void KiteReportedPnl_is_false_for_null()
    {
        Assert.False(PnlMath.KiteReportedPnl(null));
    }

    [Fact]
    public void KiteReportedPnl_is_true_for_a_real_figure()
    {
        Assert.True(PnlMath.KiteReportedPnl(6.82m));
        Assert.True(PnlMath.KiteReportedPnl(-109.72m));
    }

    [Fact]
    public void Pnl_ignores_a_zero_from_kite_and_computes_from_nav()
    {
        // Gold FoF: Kite sends pnl: 0, but the NAV clearly shows a loss.
        // The computed figure -- not the zero -- must win.
        var qty = 1749.91m / 47.019013m;
        var pnl = PnlMath.Pnl(qty, 47.019013m, 44.0707m, apiPnl: 0m, awaitingPrice: false);

        Assert.True(pnl < -100m, $"expected a real loss, got {pnl}");
        Assert.Equal(-109.72m, pnl, precision: 0);   // matches Coin to the rupee
    }

    // ---- current stays consistent with pnl -------------------------------

    [Fact]
    public void Current_equals_invested_plus_pnl_when_kite_reports_pnl()
    {
        // Equity RBA: Kite reports pnl 6.82 directly.
        const decimal qty = 1m, avg = 63.48m, last = 70.30m, api = 6.82m;

        var invested = PnlMath.Invested(qty, avg);
        var pnl = PnlMath.Pnl(qty, avg, last, api, awaitingPrice: false);
        var current = PnlMath.Current(qty, avg, last, api, awaitingPrice: false);

        Assert.Equal(invested + pnl, current, precision: 4);
    }

    [Fact]
    public void Current_is_qty_times_last_when_computing_from_nav()
    {
        // Flexi Cap: no trustworthy pnl from Kite, so current is qty * NAV.
        var qty = 200.45m / 2109.971684m;
        var current = PnlMath.Current(qty, 2109.971684m, 2241.1970m, apiPnl: 0m, awaitingPrice: false);

        Assert.Equal(qty * 2241.1970m, current, precision: 4);
        Assert.Equal(212.91m, current, precision: 0);   // matches Coin
    }

    // ---- unpriced holdings held at cost ----------------------------------

    [Fact]
    public void Awaiting_price_holding_shows_zero_pnl_and_current_at_cost()
    {
        // Zerodha Life Cycle 2036: last_price 0 in the API -> not yet priced.
        const decimal qty = 10m, avg = 10m;

        var pnl = PnlMath.Pnl(qty, avg, lastPrice: 0m, apiPnl: 0m, awaitingPrice: true);
        var current = PnlMath.Current(qty, avg, lastPrice: 0m, apiPnl: 0m, awaitingPrice: true);

        Assert.Equal(0m, pnl);
        Assert.Equal(100m, current);          // held at cost, not shown as -100
    }

    // ---- the whole portfolio reconciles to Coin --------------------------

    [Theory]
    // fund                 avg            navLive       investedCoin  coinPnl
    [InlineData("Flexi",    2109.971684, 2241.1970,   200.45,     12.46)]
    [InlineData("Nippon",   4856.546124, 4993.2106,   199.12,      5.60)]
    [InlineData("Silver",     36.294558,   35.8509,  4649.92,    -56.84)]
    [InlineData("Gold",       47.019013,   44.0707,  1749.91,   -109.72)]
    [InlineData("Nifty50",   230.967546,  236.5114,   200.02,      4.80)]
    public void Each_fund_matches_coin_within_a_rupee(
        string _, decimal avg, decimal navLive, decimal investedCoin, decimal coinPnl)
    {
        // Reconstruct quantity the way the app has it (exact, from the API).
        var qty = investedCoin / avg;

        var pnl = PnlMath.Pnl(qty, avg, navLive, apiPnl: 0m, awaitingPrice: false);

        Assert.True(System.Math.Abs(pnl - coinPnl) <= 1.0m,
            $"fund P&L {pnl} should be within a rupee of Coin's {coinPnl}");
    }

    [Fact]
    public void Portfolio_total_reconciles_to_coin()
    {
        // Sum the five priced funds; expect Coin's -143.84 to the rupee.
        (decimal avg, decimal nav, decimal invested)[] funds =
        {
            (2109.971684m, 2241.1970m, 200.45m),
            (4856.546124m, 4993.2106m, 199.12m),
            (  36.294558m,   35.8509m, 4649.92m),
            (  47.019013m,   44.0707m, 1749.91m),
            ( 230.967546m,  236.5114m, 200.02m),
        };

        decimal total = 0m;
        foreach (var (avg, nav, invested) in funds)
        {
            var qty = invested / avg;
            total += PnlMath.Pnl(qty, avg, nav, apiPnl: 0m, awaitingPrice: false);
        }

        Assert.Equal(-143.84m, total, precision: 0);
    }

    // ---- percentage ------------------------------------------------------

    [Fact]
    public void PnlPct_is_zero_when_nothing_invested()
    {
        Assert.Equal(0m, PnlMath.PnlPct(pnl: 5m, invested: 0m));
    }

    [Fact]
    public void PnlPct_computes_against_invested()
    {
        // -143.84 / 7099.42 * 100 = -2.026..., which rounds to -2.03. (Coin
        // shows -2.02 because it divides by its own exact invested figure;
        // with two-decimal inputs the honest result is -2.03. We assert the
        // arithmetic, not Coin's rounding.)
        Assert.Equal(-2.03m, PnlMath.PnlPct(-143.84m, 7099.42m), precision: 2);
    }

    // ---- day P&L: T1 exclusion and the zero-close guard ----------------

    /// The day-P&L fix. T1 stock was bought today and held no position at
    /// yesterday's close, so crediting it a full day's move inflates the
    /// day figure and makes the widget disagree with the Kite app. Only
    /// the settled `quantity` counts.
    [Fact]
    public void DayPnl_excludes_T1_quantity()
    {
        // 10 settled shares at +10 = 100. If T1 were included the answer
        // would be 1000, and the day-P&L figure would not match Kite.
        var day = PnlMath.DayPnl(
            quantity: 10, t1Quantity: 90,
            lastPrice: 110, closePrice: 100,
            dayChange: null
        );
        Assert.Equal(100m, day);
    }

    /// When every share is T1 the position is brand new and has no
    /// yesterday-close to measure against. The day's move on those
    /// shares is irrelevant to the day-P&L figure.
    [Fact]
    public void DayPnl_is_zero_when_all_shares_are_T1()
    {
        // All shares still in T1 means nothing has settled: quantity == 0,
        // t1Quantity holds the unsettled count. (quantity:100,t1:100 is
        // impossible against the real API -- settled and T1 are disjoint.)
        var day = PnlMath.DayPnl(
            quantity: 0, t1Quantity: 100,
            lastPrice: 110, closePrice: 100,
            dayChange: null
        );
        Assert.Equal(0m, day);
    }

    /// A newly listed or unpriced holding reports `close_price: 0`.
    /// Without the guard, `last - 0` books the entire position as
    /// today's gain. The day figure is zero until Kite has a real
    /// previous close.
    [Fact]
    public void DayPnl_is_zero_when_close_price_is_zero()
    {
        var day = PnlMath.DayPnl(
            quantity: 100, t1Quantity: 0,
            lastPrice: 500, closePrice: 0,
            dayChange: null
        );
        Assert.Equal(0m, day);
    }

    /// Kite's own `day_change` figure takes precedence over the
    /// (last - close) computation. The settled-only rule is implicit
    /// here because Kite's figure already excludes T1.
    [Fact]
    public void DayPnl_uses_kite_day_change_when_present()
    {
        // 10 settled shares, Kite's day_change = 5, so 50. Falling
        // back to (110 - 100) * 10 = 100 would double-count and
        // disagree with the Kite dashboard.
        var day = PnlMath.DayPnl(
            quantity: 10, t1Quantity: 0,
            lastPrice: 110, closePrice: 100,
            dayChange: 5m
        );
        Assert.Equal(50m, day);
    }

    /// The day-P&L fix on a negative move. Same rule, opposite sign --
    /// a regression that special-cased the positive branch would pass
    /// the T1 test but still misreport losses.
    [Fact]
    public void DayPnl_excludes_T1_quantity_on_a_loss()
    {
        var day = PnlMath.DayPnl(
            quantity: 10, t1Quantity: 90,
            lastPrice: 90, closePrice: 100,
            dayChange: null
        );
        Assert.Equal(-100m, day);
    }
}
