using System;
using System.Collections.Generic;

namespace KiteGlance.Services;

/// <summary>
/// Pure functions that turn Kite's holdings DTOs into the widget's
/// <see cref="Holding"/> shape. Lives outside <see cref="KiteService"/>
/// so the test target can exercise the rules without a mock HTTP
/// handler and so a regression in the arithmetic cannot hide behind
/// an HTTP fix-up. Mirrors the Mac <c>PortfolioAssembler</c> in
/// <c>KiteGlanceCore/Portfolio.swift</c> -- the two implementations
/// should produce byte-identical <see cref="PortfolioData"/> for the
/// same inputs.
/// </summary>
public static class PortfolioAssembler
{
    /// <summary>Empty-string fallback for an equity row whose
    /// <c>tradingSymbol</c> is missing. The Kite app sometimes returns
    /// `""` for a brand-new holding before it has been allotted.</summary>
    public const string UnnamedEquitySymbol = "Unnamed holding";

    /// <summary>Empty-string fallback for a fund row whose
    /// <c>fund_name</c> is missing. Kite uses blank names to mean
    /// "not allotted yet", not "the fund is called nothing".</summary>
    public const string AwaitingAllotmentSymbol = "Awaiting allotment";

    /// <summary>
    /// Resolves a single equity DTO into a <see cref="Holding"/>. Returns
    /// <c>null</c> when the holding should be dropped (e.g. zero
    /// quantity).
    /// </summary>
    public static Holding? AssembleEquity(HoldingDto h, out decimal dayPnl)
        => AssembleEquity(h, out dayPnl, out _);

    /// <summary>
    /// As <see cref="AssembleEquity(HoldingDto, out decimal)"/> but also emits
    /// <paramref name="settledPrevClose"/> -- the settled shares' value at
    /// yesterday's close. This is the correct denominator for the day-P&L
    /// percentage: it is on the same T1-excluded basis as <paramref name="dayPnl"/>,
    /// whereas the position's current value includes unsettled T1 shares.
    /// </summary>
    public static Holding? AssembleEquity(HoldingDto h, out decimal dayPnl, out decimal settledPrevClose)
    {
        dayPnl = 0m;
        settledPrevClose = 0m;
        var qty = h.Quantity + (h.T1Quantity ?? 0m);
        if (qty <= 0) return null;

        var last = Priced(h.LastPrice, h.AveragePrice, out var stale);
        dayPnl = PnlMath.DayPnl(
            h.Quantity, h.T1Quantity ?? 0m,
            h.LastPrice, h.ClosePrice ?? 0m,
            h.DayChange);
        // Only settled shares carry a yesterday-close; a positive close means
        // they were trading yesterday. T1 shares contribute neither to dayPnl
        // nor to this base, keeping the percentage's numerator and denominator
        // on one axis.
        if (h.Quantity > 0m && (h.ClosePrice ?? 0m) > 0m)
            settledPrevClose = h.Quantity * (h.ClosePrice ?? 0m);

        return new Holding
        {
            Symbol = string.IsNullOrWhiteSpace(h.TradingSymbol)
                ? UnnamedEquitySymbol
                : h.TradingSymbol,
            Qty = qty,
            AvgPrice = h.AveragePrice,
            LastPrice = last,
            InstrumentToken = h.InstrumentToken,
            IsMutualFund = false,
            AwaitingPrice = stale,
            ApiPnl = h.Pnl,
        };
    }

    /// <summary>
    /// Resolves a single mutual-fund DTO into a <see cref="Holding"/>.
    /// Returns <c>null</c> when the holding should be dropped.
    /// </summary>
    /// <param name="liveNavOverride">If the AMFI table contains an entry
    /// for this fund, pass it here. The Kite settlement NAV is replaced
    /// and the Kite pnl is dropped (it cannot annotate a live NAV).</param>
    public static Holding? AssembleFund(
        MFHoldingDto f,
        decimal? liveNavOverride)
    {
        if (f.Quantity <= 0) return null;

        var kiteNav = f.LastPrice;
        var apiPnl = (decimal?)f.Pnl;

        if (liveNavOverride is { } amfiNav && amfiNav > 0)
        {
            kiteNav = amfiNav;
            apiPnl = null;
        }

        var last = Priced(kiteNav, f.AveragePrice, out var stale);
        return new Holding
        {
            Symbol = string.IsNullOrWhiteSpace(f.FundName)
                ? AwaitingAllotmentSymbol
                : f.FundName,
            Qty = f.Quantity,
            AvgPrice = f.AveragePrice,
            LastPrice = last,
            IsMutualFund = true,
            AwaitingPrice = stale,
            ApiPnl = apiPnl,
        };
    }

    /// <summary>
    /// The "marked-to" rule: a holding that Kite has not priced is held
    /// at cost, not at zero. Stays in lockstep with the
    /// <c>Priced</c> method on <see cref="Holding"/> so a row's
    /// <c>LastPrice</c> can never disagree with its own
    /// <c>AwaitingPrice</c>.
    /// </summary>
    private static decimal Priced(decimal lastPrice, decimal avgPrice, out bool awaiting)
    {
        awaiting = lastPrice <= 0m;
        return awaiting ? avgPrice : lastPrice;
    }
}
