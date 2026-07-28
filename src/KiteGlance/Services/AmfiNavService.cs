using System.IO;
using System.Net.Http;
using KiteGlance.Services;

namespace KiteGlance.Services;

/// <summary>
/// Live mutual fund NAVs from AMFI, the industry body every AMC files with by
/// law.
///
/// Why this exists: Kite's /mf/holdings endpoint returns a `last_price` that
/// disagrees with what Coin's Portfolio Analytics tab displays -- for FoFs by
/// a percent or more. Kite's own two systems disagree because the holdings
/// endpoint returns a stale settlement NAV while Coin fetches live NAVs from
/// a different feed. Nothing on the Kite API bridges that.
///
/// AMFI publishes NAVAll.txt daily -- semicolon-delimited, free, no auth,
/// keyed by ISIN. Kite happens to use ISIN as the tradingsymbol for MFs, so
/// they join cleanly. This service overrides Kite's stale NAV with AMFI's
/// authoritative one for every fund it can match.
///
///   Line format: schemeCode;ISIN growth;ISIN reinvest;name;nav;date
///
/// The file is cached in memory for the whole trading day -- AMFI publishes
/// once, around 11 PM IST -- so we fetch it once per app-day rather than once
/// per portfolio refresh.
/// </summary>
public sealed class AmfiNavService
{
    private const string Url = "https://www.amfiindia.com/spages/NAVAll.txt";

    private readonly HttpClient _http;
    private Dictionary<string, decimal>? _cache;
    private DateTime _cachedOn;

    private static readonly string CachePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KiteGlance", "amfi-nav.txt");

    /// <summary>
    /// True once the current NAV table came from AMFI (live). False when the
    /// only data we could get was Kite's own stale NAVs, i.e. AMFI has never
    /// been reachable this run and no disk cache existed. Lets the UI be honest
    /// about which NAV source is in play, the same way sync-staleness is shown.
    /// </summary>
    public bool HasLiveNavs { get; private set; }

    public AmfiNavService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        // AMFI's server occasionally rejects requests without a UA.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KiteGlance/1.0");
    }

    /// <summary>
    /// Look up an ISIN's current NAV. Returns null if AMFI hasn't published
    /// this ISIN today, or if the fetch failed and there is no prior cache to
    /// fall back to. Never throws.
    /// </summary>
    public async Task<decimal?> GetNavAsync(string isin)
    {
        if (string.IsNullOrWhiteSpace(isin)) return null;

        var table = await EnsureCacheAsync();
        return table is not null && table.TryGetValue(isin, out var nav) ? nav : null;
    }

    /// <summary>
    /// The whole NAV table, keyed by ISIN. Null if no fetch has ever
    /// succeeded. Never throws.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, decimal>?> GetNavsAsync()
        => await EnsureCacheAsync();

    private async Task<Dictionary<string, decimal>?> EnsureCacheAsync()
    {
        // AMFI's file updates once a day, ~11pm IST. Refetching more often is
        // pointless and rude to their server. Cache stays valid for the
        // Indian calendar day.
        if (_cache is not null && IsSameIstDay(_cachedOn, DateTime.UtcNow))
            return _cache;

        // Disk cache: survives restarts, so a cold start paints immediately
        // from a same-IST-day file instead of blocking on a ~3 MB download.
        var (diskText, diskTime) = ReadDiskCache();
        if (diskText is not null && IsSameIstDay(diskTime, DateTime.UtcNow))
        {
            _cache = Parse(diskText);
            _cachedOn = diskTime;
            HasLiveNavs = true;
            return _cache;
        }

        try
        {
            var text = await _http.GetStringAsync(Url);
            _cache = Parse(text);
            _cachedOn = DateTime.UtcNow;
            HasLiveNavs = true;
            WriteDiskCache(text);
            return _cache;
        }
        catch (Exception ex)
        {
            // Network failed. Prefer a stale disk cache (a day-old NAV still
            // beats Kite's holdings-endpoint NAV) over the in-memory one, then
            // fall back to whatever is in memory. Return null only if we have
            // never once succeeded -- callers then use Kite's own NAV.
            if (_cache is null && diskText is not null)
            {
                _cache = Parse(diskText);
                _cachedOn = diskTime;
                HasLiveNavs = true;
                Log.Warn($"AMFI fetch failed ({ex.GetType().Name}); using disk cache from {diskTime:yyyy-MM-dd}");
            }
            else if (_cache is null)
            {
                Log.Warn($"AMFI fetch failed ({ex.GetType().Name}); no cache, falling back to Kite NAVs");
            }

            return _cache;
        }
    }

    private static (string? Text, DateTime WriteUtc) ReadDiskCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return (null, default);
            var text = File.ReadAllText(CachePath);
            return (text, File.GetLastWriteTimeUtc(CachePath));
        }
        catch
        {
            return (null, default);
        }
    }

    private static void WriteDiskCache(string text)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, text);
        }
        catch
        {
            // A missing disk cache only costs a re-download; never fatal.
        }
    }

    /// <summary>
    /// Parse NAVAll.txt. Skips headers, section titles, AMC names, and rows
    /// where the NAV is "N.A." or the ISIN is missing.
    /// </summary>
    internal static Dictionary<string, decimal> Parse(string text)
    {
        var map = new Dictionary<string, decimal>(capacity: 20_000);

        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            // Data rows have five semicolons. Section headers and AMC names
            // have none. Cheap prefilter.
            if (line.Length < 20 || line.IndexOf(';') < 0) continue;
            if (line.StartsWith("Scheme Code", StringComparison.Ordinal)) continue;

            var parts = line.Split(';');
            if (parts.Length < 5) continue;

            // Two ISIN columns: growth (index 1) and reinvestment (index 2).
            // Kite's MF tradingsymbol can be either, so we index both.
            var navText = parts[4].Trim();
            if (!decimal.TryParse(navText, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var nav)
                || nav <= 0)
                continue;

            var isinGrowth = parts[1].Trim();
            var isinReinvest = parts[2].Trim();

            if (isinGrowth.Length == 12) map[isinGrowth] = nav;
            if (isinReinvest.Length == 12) map[isinReinvest] = nav;
        }

        return map;
    }

    private static bool IsSameIstDay(DateTime aUtc, DateTime bUtc)
    {
        var offset = TimeSpan.FromMinutes(330);   // IST = UTC+5:30
        return (aUtc + offset).Date == (bUtc + offset).Date;
    }
}
