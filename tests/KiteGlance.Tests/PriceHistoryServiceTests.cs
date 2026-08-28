using System;
using System.Collections.Generic;
using System.IO;
using KiteGlance.Services;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// The rolling price series behind the row sparklines. Everything here is
/// best-effort by design -- a corrupt file must degrade to "no sparkline",
/// never to an exception -- so the failure paths are tested as carefully as
/// the happy one.
/// </summary>
public class PriceHistoryServiceTests : IDisposable
{
    private readonly string _dir;

    public PriceHistoryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"KiteGlanceHistory_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private PriceHistoryService New(string? accountId = null) =>
        new(baseDirectory: _dir, accountId: accountId);

    [Fact]
    public void Series_is_null_until_enough_points_exist()
    {
        var history = New();

        history.Record("INFY", 100);
        Assert.Null(history.Series("INFY"));

        history.Record("INFY", 101);
        Assert.Null(history.Series("INFY"));

        // Three points is the first shape that can bend rather than merely
        // slope, which is where a line starts saying something.
        history.Record("INFY", 102);
        Assert.NotNull(history.Series("INFY"));
    }

    [Fact]
    public void Repeated_identical_prices_are_not_recorded()
    {
        var history = New();

        history.Record("INFY", 100);
        history.Record("INFY", 100);
        history.Record("INFY", 100);

        // Otherwise a weekend of five-minute refreshes at an unchanged price
        // flushes every real point out of the buffer and the line goes flat
        // exactly when the market reopens.
        Assert.Null(history.Series("INFY"));
    }

    [Fact]
    public void Series_is_capped_and_keeps_the_newest_points()
    {
        var history = New();

        for (var i = 1; i <= PriceHistoryService.MaxPoints + 15; i++)
        {
            history.Record("INFY", i);
        }

        var series = history.Series("INFY");

        Assert.NotNull(series);
        Assert.Equal(PriceHistoryService.MaxPoints, series!.Count);
        Assert.Equal(PriceHistoryService.MaxPoints + 15, series[^1]);
    }

    [Fact]
    public void Zero_and_negative_prices_are_ignored()
    {
        var history = New();

        history.Record("INFY", 0);
        history.Record("INFY", -5);
        history.Record("INFY", 100);
        history.Record("INFY", 101);
        history.Record("INFY", 102);

        var series = history.Series("INFY");

        Assert.NotNull(series);
        Assert.Equal(3, series!.Count);
        Assert.DoesNotContain(0m, series);
    }

    [Fact]
    public void Points_survive_a_save_and_reload()
    {
        var first = New();
        first.Record("INFY", 100);
        first.Record("INFY", 101);
        first.Record("INFY", 102);
        first.Save();

        var reopened = New();
        var series = reopened.Series("INFY");

        Assert.NotNull(series);
        Assert.Equal(new[] { 100m, 101m, 102m }, series!);
    }

    [Fact]
    public void Retain_forgets_symbols_no_longer_held()
    {
        var history = New();

        foreach (var price in new decimal[] { 100, 101, 102 })
        {
            history.Record("INFY", price);
            history.Record("TCS", price);
        }

        history.Retain(new[] { "INFY" });

        Assert.NotNull(history.Series("INFY"));
        Assert.Null(history.Series("TCS"));
    }

    [Fact]
    public void Seed_replaces_a_series_and_trims_to_the_cap()
    {
        var history = New();
        history.Record("INFY", 999);

        var closes = new List<decimal>();
        for (var i = 1; i <= PriceHistoryService.MaxPoints + 5; i++) closes.Add(i);

        history.Seed("INFY", closes);
        var series = history.Series("INFY");

        Assert.NotNull(series);
        Assert.Equal(PriceHistoryService.MaxPoints, series!.Count);
        Assert.DoesNotContain(999m, series);
        Assert.Equal(PriceHistoryService.MaxPoints + 5, series[^1]);
    }

    [Fact]
    public void IsComplete_is_true_only_at_the_cap()
    {
        var history = New();

        history.Record("INFY", 1);
        Assert.False(history.IsComplete("INFY"));

        for (var i = 2; i <= PriceHistoryService.MaxPoints; i++) history.Record("INFY", i);
        Assert.True(history.IsComplete("INFY"));
    }

    [Fact]
    public void A_corrupt_file_loads_as_empty_rather_than_throwing()
    {
        var path = Path.Combine(_dir, "KiteGlance", "history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        var history = New();

        Assert.Null(history.Series("INFY"));

        // And it must still be usable afterwards -- a bad file is not a
        // permanent loss of the feature.
        history.Record("INFY", 100);
        history.Record("INFY", 101);
        history.Record("INFY", 102);
        Assert.NotNull(history.Series("INFY"));
    }

    [Fact]
    public void An_over_long_stored_series_is_trimmed_on_load()
    {
        var path = Path.Combine(_dir, "KiteGlance", "history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var points = new List<string>();
        for (var i = 0; i < PriceHistoryService.MaxPoints + 50; i++) points.Add(i.ToString());
        File.WriteAllText(path, "{\"INFY\":[" + string.Join(",", points) + "]}");

        var series = New().Series("INFY");

        Assert.NotNull(series);
        Assert.Equal(PriceHistoryService.MaxPoints, series!.Count);
    }

    [Fact]
    public void Accounts_keep_separate_histories()
    {
        var a = New("AB1234");
        var b = New("CD5678");

        foreach (var price in new decimal[] { 100, 101, 102 }) a.Record("INFY", price);
        a.Save();

        // Same ticker, different account: two different positions, bought at
        // different times. Pooling them would draw each a line the other's
        // trading made.
        Assert.Null(b.Series("INFY"));
    }

    // ---- Resample ------------------------------------------------------

    [Fact]
    public void Resample_passes_through_when_source_is_at_or_below_target()
    {
        var source = new List<decimal> { 10, 20, 30 };
        var result = PriceHistoryService.Resample(source, 5);
        Assert.Equal(new[] { 10m, 20m, 30m }, result);
    }

    [Fact]
    public void Resample_returns_exactly_target_count_for_a_dense_source()
    {
        // 1000 source points -> 40 targets; no off-by-one at either end.
        var source = new List<decimal>();
        for (var i = 0; i < 1000; i++) source.Add(i);
        var result = PriceHistoryService.Resample(source, 40);
        Assert.Equal(40, result.Count);
    }

    [Fact]
    public void Resample_picks_the_first_and_last_when_targets_are_two()
    {
        // With 2 targets we should see points from near the start and near
        // the end of the source, not the same one twice.
        var source = new List<decimal>();
        for (var i = 0; i < 100; i++) source.Add(i);
        var result = PriceHistoryService.Resample(source, 2);

        Assert.Equal(2, result.Count);
        Assert.NotEqual(result[0], result[1]);
        Assert.True(result[0] < result[1]);
    }

    [Fact]
    public void Resample_is_strictly_monotonic_when_source_is()
    {
        // A strictly increasing source should still be strictly increasing
        // after resampling, never the same point twice in a row.
        var source = new List<decimal>();
        for (var i = 0; i < 200; i++) source.Add(i);
        var result = PriceHistoryService.Resample(source, 50);

        for (var i = 1; i < result.Count; i++)
            Assert.True(result[i] > result[i - 1], $"non-monotonic at index {i}");
    }

    // ---- SeedIntraday + frozen series ---------------------------------

    [Fact]
    public void SeedIntraday_down_samples_to_the_cap()
    {
        var history = New();
        // 200 source points -> 40 in the series.
        var source = new List<decimal>();
        for (var i = 0; i < 200; i++) source.Add(100m + i);

        history.SeedIntraday("INFY", source);

        var series = history.Series("INFY");
        Assert.NotNull(series);
        Assert.Equal(PriceHistoryService.MaxPoints, series!.Count);

        // Resample picks midpoints of each target bucket. With 200 source
        // points downsampled to 40, the first picked point is index 2 (the
        // midpoint of the 0..4 bucket that the first target slot maps to),
        // and the last is index 197 (midpoint of the 195..199 bucket). The
        // chart is symmetric around the middle, not biased toward either
        // end.
        Assert.Equal(102m, series[0]);
        Assert.Equal(297m, series[^1]);
    }

    [Fact]
    public void SeedIntraday_with_a_short_source_keeps_every_point()
    {
        var history = New();
        var source = new List<decimal> { 100, 101, 102, 103, 104 };

        history.SeedIntraday("INFY", source);

        var series = history.Series("INFY");
        Assert.Equal(source, series);
    }

    [Fact]
    public void SeedIntraday_freezes_the_series_so_record_is_a_no_op()
    {
        var history = New();
        var source = new List<decimal>();
        for (var i = 0; i < 200; i++) source.Add(100m + i);

        history.SeedIntraday("INFY", source);
        var before = history.Series("INFY");
        Assert.NotNull(before);

        // Record must not append to a frozen series -- the latest 5-minute
        // candle is the rightmost point, and a single non-aligned latest
        // price would put a visible cliff on the chart.
        history.Record("INFY", 9999m);
        var after = history.Series("INFY");

        Assert.Equal(before, after);
    }

    [Fact]
    public void Seed_also_freezes_the_series()
    {
        // The daily backfill path calls Seed, not SeedIntraday. It must
        // freeze too, for the same reason: a single latest last_price must
        // not be appended on top of a carefully assembled daily series.
        var history = New();
        var source = new List<decimal>();
        for (var i = 0; i < PriceHistoryService.MaxPoints; i++) source.Add(100m + i);

        history.Seed("INFY", source);
        var before = history.Series("INFY");

        history.Record("INFY", 9999m);
        var after = history.Series("INFY");

        Assert.Equal(before, after);
    }

    [Fact]
    public void Frozen_status_is_reported_for_seeded_but_not_recorded_series()
    {
        var history = New();
        history.Record("INFY", 100);
        Assert.False(history.IsFrozen("INFY"));

        history.SeedIntraday("INFY", new List<decimal> { 100, 101, 102, 103, 104 });
        Assert.True(history.IsFrozen("INFY"));
    }

    [Fact]
    public void Retain_clears_frozen_status_for_removed_symbols()
    {
        var history = New();
        history.SeedIntraday("INFY", new List<decimal> { 100, 101, 102, 103, 104 });
        Assert.True(history.IsFrozen("INFY"));

        history.Retain(Array.Empty<string>());
        Assert.False(history.IsFrozen("INFY"));
    }

    [Fact]
    public void SeedIntraday_is_a_no_op_for_null_or_empty_source()
    {
        var history = New();

        history.SeedIntraday("INFY", null);
        Assert.Null(history.Series("INFY"));

        history.SeedIntraday("INFY", new List<decimal>());
        Assert.Null(history.Series("INFY"));
    }
}
