using System.IO;
using System.Text.Json;

namespace KiteGlance.Services;

/// <summary>
/// A short rolling price series per holding, kept only so the rows can draw a
/// sparkline.
///
/// Two sources, in order of preference:
///
///   1. Kite's /instruments/historical endpoint, which returns real daily
///      candles. It is accurate and instantly complete -- and it is gated
///      behind a paid Historical Data subscription, so for most users it
///      answers 403. It also does not cover mutual funds at all.
///
///   2. Whatever prices we have already seen. Every refresh already fetches
///      last_price for every holding; appending it to a per-symbol ring buffer
///      costs one small file write and no extra API calls. A subscription-less
///      user sees the line fill in over days instead of seeing an error, and
///      funds get a line the historical API could never have given them.
///
/// The file is deliberately not treated as precious. It is a drawing aid: if
/// it is corrupt, unreadable, or missing, the sparkline is simply absent and
/// nothing else in the app notices.
/// </summary>
public sealed class PriceHistoryService
{
    /// <summary>
    /// Points kept per symbol. Forty is what fits legibly in a 44px-wide
    /// sparkline -- beyond that neighbouring points share a pixel column and
    /// the extra data buys nothing but file size.
    /// </summary>
    public const int MaxPoints = 40;

    /// <summary>
    /// Minimum points before a line is worth drawing. Two points is a straight
    /// segment that implies a trend from a single observation; three is the
    /// first shape that can actually bend.
    /// </summary>
    public const int MinPoints = 3;

    private readonly string _path;
    private Dictionary<string, List<decimal>> _series;
    private bool _dirty;

    /// <summary>
    /// Opens the history for one account. Scoped the same way the vault is:
    /// two accounts holding the same ticker are two different positions bought
    /// at different times, and pooling their prices would draw each a line the
    /// other's trading made.
    /// </summary>
    public PriceHistoryService(string? baseDirectory = null, string? accountId = null)
    {
        _path = Path.Combine(
            CredentialVault.AccountDirectory(baseDirectory, accountId),
            "history.json");

        _series = Load();
    }

    /// <summary>
    /// Records this refresh's price for a symbol, dropping the oldest point
    /// once the series is full.
    ///
    /// Consecutive identical prices are skipped. A holding refreshed every five
    /// minutes over a closed weekend would otherwise flush every real point out
    /// of the buffer and leave a flat line that says nothing.
    /// </summary>
    public void Record(string symbol, decimal price)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        if (price <= 0) return;

        var key = symbol.Trim();

        if (!_series.TryGetValue(key, out var points))
        {
            points = new List<decimal>();
            _series[key] = points;
        }

        if (points.Count > 0 && points[^1] == price) return;

        points.Add(price);
        while (points.Count > MaxPoints) points.RemoveAt(0);

        _dirty = true;
    }

    /// <summary>
    /// The stored series for a symbol, oldest first, or null when too little
    /// has been seen to draw anything honest.
    /// </summary>
    public IReadOnlyList<decimal>? Series(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        return _series.TryGetValue(symbol.Trim(), out var points)
               && points.Count >= MinPoints
            ? points
            : null;
    }

    /// <summary>
    /// Replaces a symbol's series wholesale, for when a real historical feed
    /// gave us actual candles. Trimmed to the same cap so a subscribed and an
    /// unsubscribed user's sparklines are the same shape of thing.
    /// </summary>
    public void Seed(string symbol, IReadOnlyList<decimal> closes)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        if (closes.Count == 0) return;

        var points = new List<decimal>(Math.Min(closes.Count, MaxPoints));
        var start = Math.Max(0, closes.Count - MaxPoints);

        for (var i = start; i < closes.Count; i++)
        {
            if (closes[i] > 0) points.Add(closes[i]);
        }

        if (points.Count == 0) return;

        _series[symbol.Trim()] = points;
        _dirty = true;
    }

    /// <summary>True once a symbol holds a full-length series, i.e. there is
    /// nothing a historical fetch could usefully add.</summary>
    public bool IsComplete(string symbol) =>
        !string.IsNullOrWhiteSpace(symbol)
        && _series.TryGetValue(symbol.Trim(), out var points)
        && points.Count >= MaxPoints;

    /// <summary>
    /// Forgets symbols no longer held. Without this the file grows forever with
    /// every stock the user has ever owned.
    /// </summary>
    public void Retain(IEnumerable<string> symbols)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in symbols)
        {
            if (!string.IsNullOrWhiteSpace(s)) keep.Add(s.Trim());
        }

        // Materialise the key list first: removing from a dictionary while
        // enumerating it throws.
        var drop = new List<string>();
        foreach (var key in _series.Keys)
        {
            if (!keep.Contains(key)) drop.Add(key);
        }

        foreach (var key in drop)
        {
            _series.Remove(key);
            _dirty = true;
        }
    }

    /// <summary>
    /// Persists, but only when something changed. Refreshes outside market
    /// hours record no new price, and rewriting an identical file every five
    /// minutes is pure disk churn.
    /// </summary>
    public void Save()
    {
        if (!_dirty) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Same write-then-replace as WidgetState: a truncated file here is
            // silently discarded on load, throwing away days of accumulated
            // points for nothing.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_series));

            if (File.Exists(_path))
                File.Replace(tmp, _path, destinationBackupFileName: null);
            else
                File.Move(tmp, _path);

            _dirty = false;
        }
        catch (Exception ex)
        {
            Log.Warn("Price history save failed ({Error}); sparklines will restart",
                ex.GetType().Name);
        }
    }

    private Dictionary<string, List<decimal>> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, List<decimal>>(StringComparer.Ordinal);

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<decimal>>>(json);

            if (loaded is null) return new Dictionary<string, List<decimal>>(StringComparer.Ordinal);

            // A hand-edited or half-written file can carry over-long series or
            // nulls. Normalise on the way in rather than letting the renderer
            // discover it.
            var clean = new Dictionary<string, List<decimal>>(StringComparer.Ordinal);
            foreach (var pair in loaded)
            {
                if (pair.Value is null || pair.Value.Count == 0) continue;

                var points = pair.Value;
                if (points.Count > MaxPoints)
                    points = points.GetRange(points.Count - MaxPoints, MaxPoints);

                clean[pair.Key] = points;
            }

            return clean;
        }
        catch
        {
            // A sparkline is not worth a crash, nor worth a log line on every
            // launch of a fresh install.
            return new Dictionary<string, List<decimal>>(StringComparer.Ordinal);
        }
    }
}
