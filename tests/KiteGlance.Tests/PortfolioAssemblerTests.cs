using System.Collections.Generic;
using KiteGlance.Services;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// Tests for the pure portfolio-assembly rules that used to live
/// inline in <c>KiteService.FetchPortfolioAsync</c>. The audit
/// (1.5.0) flagged that the Windows side had no coverage for the
/// T1-exclusion, zero-close, blank-name, and AMFI-override rules
/// even though the Mac side had eight dedicated tests in
/// <c>PortfolioAssemblerTests.swift</c>. Both ports should now
/// produce byte-identical <see cref="PortfolioData"/> for the same
/// inputs.
/// </summary>
public class PortfolioAssemblerTests
{
    // ---- T1 exclusion (the day-PnL fix) -------------------------------

    /// The day-PnL fix: T1 stock was bought today and held no
    /// position at yesterday's close, so crediting it a full day's
    /// move inflates the day figure and makes the widget disagree
    /// with the Kite app. Only the settled `Quantity` counts.
    [Fact]
    public void AssembleEquity_excludes_T1_from_dayPnl()
    {
        var dto = new HoldingDto
        {
            TradingSymbol = "INFY",
            Quantity = 10,             // settled
            T1Quantity = 90,           // bought today
            AveragePrice = 100,
            LastPrice = 110,
            ClosePrice = 100,
        };

        var holding = PortfolioAssembler.AssembleEquity(dto, out var dayPnl);
        Assert.NotNull(holding);
        // 10 settled shares at +10 = 100. If T1 were included the
        // answer would be 1000, and the day-PnL figure would not
        // match Kite.
        Assert.Equal(100m, dayPnl);
    }

    /// The value of the position still counts every share -- you
    /// own the T1 stock either way. Only *today's move* excludes it.
    [Fact]
    public void AssembleEquity_holding_qty_still_includes_T1()
    {
        var dto = new HoldingDto
        {
            TradingSymbol = "INFY",
            Quantity = 10,
            T1Quantity = 90,
            AveragePrice = 100,
            LastPrice = 110,
            ClosePrice = 100,
        };

        var holding = PortfolioAssembler.AssembleEquity(dto, out _);
        Assert.NotNull(holding);
        Assert.Equal(100m, holding.Qty);
    }

    // ---- zero close ----------------------------------------------------

    /// A newly listed or unpriced holding reports close_price 0.
    /// Without the guard, `last - 0` books the entire position as
    /// today's gain.
    [Fact]
    public void AssembleEquity_zero_close_price_does_not_book_full_position()
    {
        var dto = new HoldingDto
        {
            TradingSymbol = "NEWLISTING",
            Quantity = 100,
            AveragePrice = 50,
            LastPrice = 500,
            ClosePrice = 0,
        };

        var holding = PortfolioAssembler.AssembleEquity(dto, out var dayPnl);
        Assert.NotNull(holding);
        Assert.Equal(0m, dayPnl);
    }

    /// Kite's own `day_change` figure takes precedence over the
    /// (last - close) computation. The settled-only rule is implicit
    /// here because Kite's figure already excludes T1.
    [Fact]
    public void AssembleEquity_kite_day_change_wins_when_present()
    {
        var dto = new HoldingDto
        {
            TradingSymbol = "INFY",
            Quantity = 10,
            AveragePrice = 100,
            LastPrice = 110,
            ClosePrice = 100,
            DayChange = 5m,
        };

        var holding = PortfolioAssembler.AssembleEquity(dto, out var dayPnl);
        Assert.NotNull(holding);
        Assert.Equal(50m, dayPnl);
    }

    // ---- awaiting price ------------------------------------------------

    /// An unpriced holding is held at cost rather than marked to
    /// zero. This is the "Priced returns avg when awaiting" rule.
    [Fact]
    public void AssembleEquity_unpriced_holding_held_at_cost()
    {
        var dto = new HoldingDto
        {
            TradingSymbol = "PENDING",
            Quantity = 10,
            AveragePrice = 100,
            LastPrice = 0,    // Kite has not priced this yet
        };

        var holding = PortfolioAssembler.AssembleEquity(dto, out _);
        Assert.NotNull(holding);
        Assert.Equal(100m, holding.AvgPrice);
        Assert.True(holding.AwaitingPrice);
        // The LastPrice is the post-assembly value -- because
        // `AwaitingPrice` is true, the getter returns `AvgPrice`.
        Assert.Equal(100m, holding.LastPrice);
    }

    // ---- blank symbols -------------------------------------------------

    /// A blank `TradingSymbol` is Kite saying "not allotted yet", not
    /// a holding actually called nothing. The widget shows
    /// "Unnamed holding" so the user has *something* to identify the
    /// row by.
    [Fact]
    public void AssembleEquity_blank_symbol_becomes_placeholder()
    {
        var dto = new HoldingDto
        {
            TradingSymbol = "   ",
            Quantity = 1,
            AveragePrice = 1,
            LastPrice = 1,
        };

        var holding = PortfolioAssembler.AssembleEquity(dto, out _);
        Assert.NotNull(holding);
        Assert.Equal(PortfolioAssembler.UnnamedEquitySymbol, holding.Symbol);
    }

    /// A blank `FundName` is the same idea for funds. Without the
    /// placeholder, the row would be unclickable in the holdings
    /// list -- an empty string is not a useful label.
    [Fact]
    public void AssembleFund_blank_fundName_becomes_placeholder()
    {
        var f = new MFHoldingDto
        {
            FundName = "",
            TradingSymbol = "INF000000000",
            Quantity = 1,
            AveragePrice = 1,
            LastPrice = 1,
        };

        var holding = PortfolioAssembler.AssembleFund(f, liveNavOverride: null);
        Assert.NotNull(holding);
        Assert.Equal(PortfolioAssembler.AwaitingAllotmentSymbol, holding.Symbol);
    }

    // ---- zero quantity -------------------------------------------------

    /// A sold-out position (Quantity = 0) is dropped from the
    /// portfolio. Without this rule, every Kite refresh would carry
    /// a row for every symbol the user has ever held.
    [Fact]
    public void AssembleEquity_zero_quantity_is_dropped()
    {
        var dto = new HoldingDto
        {
            TradingSymbol = "SOLD",
            Quantity = 0,
            AveragePrice = 100,
            LastPrice = 110,
        };

        var holding = PortfolioAssembler.AssembleEquity(dto, out _);
        Assert.Null(holding);
    }

    [Fact]
    public void AssembleFund_zero_quantity_is_dropped()
    {
        var f = new MFHoldingDto
        {
            FundName = "Some Fund",
            TradingSymbol = "INF000000000",
            Quantity = 0,
            AveragePrice = 100,
            LastPrice = 110,
        };

        var holding = PortfolioAssembler.AssembleFund(f, liveNavOverride: null);
        Assert.Null(holding);
    }

    // ---- AMFI NAV override --------------------------------------------

    /// AMFI's live NAV replaces Kite's stale settlement figure, and
    /// Kite's P&L is dropped with it -- a P&L computed against a
    /// stale NAV cannot annotate a live one.
    [Fact]
    public void AssembleFund_amfi_override_replaces_kite_nav_and_drops_pnl()
    {
        var f = new MFHoldingDto
        {
            FundName = "Parag Parikh Flexi Cap",
            TradingSymbol = "INF879O01027",
            Quantity = 100,
            AveragePrice = 50,
            LastPrice = 55,        // Kite's stale settlement NAV
            Pnl = 500,
        };

        var holding = PortfolioAssembler.AssembleFund(f, liveNavOverride: 60m);
        Assert.NotNull(holding);
        Assert.Equal(60m, holding.LastPrice);   // AMFI NAV wins
        Assert.Null(holding.ApiPnl);           // Kite pnl dropped
        Assert.Equal(1000m, holding.Pnl);      // (60-50) * 100 recomputed
    }

    /// Kite's NAV is kept when AMFI lacks the ISIN.
    [Fact]
    public void AssembleFund_kite_nav_kept_when_amfi_lacks_isin()
    {
        var f = new MFHoldingDto
        {
            FundName = "Some Fund",
            TradingSymbol = "INF000000000",
            Quantity = 100,
            AveragePrice = 50,
            LastPrice = 55,
            Pnl = 500,
        };

        var holding = PortfolioAssembler.AssembleFund(f, liveNavOverride: null);
        Assert.NotNull(holding);
        Assert.Equal(55m, holding.LastPrice);
        Assert.Equal(500m, holding.ApiPnl);
    }

    // ---- empty-portfolio sanity ---------------------------------------

    /// The day-PnL-percentage divides by the previous close, not by
    /// the today's value, and must not divide by zero on an empty
    /// portfolio. Verified at the `PortfolioData` level, but the
    /// per-row assembly should not throw on empty input either.
    [Fact]
    public void AssembleEquity_empty_input_returns_zero_dayPnl()
    {
        var dto = new HoldingDto
        {
            TradingSymbol = "X",
            Quantity = 0,        // dropped -> null
            AveragePrice = 0,
            LastPrice = 0,
        };
        var holding = PortfolioAssembler.AssembleEquity(dto, out var dayPnl);
        Assert.Null(holding);
        Assert.Equal(0m, dayPnl);
    }
}
