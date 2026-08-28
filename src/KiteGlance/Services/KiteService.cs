using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using KiteGlance.Services;

namespace KiteGlance.Services;

public class KiteAuthException : Exception
{
    public KiteAuthException(string message) : base(message) { }
}

public class KiteService : IDisposable
{
    private const string BaseUrl = "https://api.kite.trade";

    private readonly HttpClient _http = new();
    private readonly CredentialVault _vault;
    private readonly AmfiNavService _amfi = new();

    /// <summary>Which stored account this client reads credentials for; null
    /// for the original single-account vault.</summary>
    public string? AccountId { get; }

    // Hourly timer, manual refresh and boot can all fire at once. Without a
    // gate they interleave: doubled work, and two Dump() appends producing
    // garbled diagnostics. One refresh at a time.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private string _apiKey;
    private string? _accessToken;

    // `_accessToken` is written from the hourly session check, from login, and
    // from the 401 path inside GetAsync -- three callers on different threads.
    // Without this, a boot-time auth check could overwrite a token that login
    // had just obtained, or resurrect one that GetAsync had just cleared,
    // surfacing as an intermittent "Session expired" right after signing in.
    private readonly object _tokenGate = new();

    private string? AccessToken
    {
        get { lock (_tokenGate) return _accessToken; }
        set { lock (_tokenGate) _accessToken = value; }
    }

    /// <summary>True when the last portfolio came back on Kite's stale MF
    /// NAVs because AMFI could not be reached. Surfaced so the UI can say so.</summary>
    public bool UsingStaleFundNavs { get; private set; }

    public KiteService(string? accountId = null)
    {
        AccountId = accountId;
        _vault = new CredentialVault(accountId: accountId);
        _apiKey = _vault.GetApiKey() ?? "";
    }

    public void ReloadCredentials() => _apiKey = _vault.GetApiKey() ?? "";

    /// <summary>
    /// Drops the session token here and in the vault, then reports it as an
    /// auth failure. Single place so the in-memory copy and the stored copy can
    /// never disagree about whether the user is signed in.
    /// </summary>
    private KiteAuthException ClearAccessToken(string message)
    {
        AccessToken = null;
        _vault.ClearAccessToken();
        return new KiteAuthException(message);
    }

    /// <summary>True once an API key is available to build a login URL with.</summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>
    /// Kite's hosted login page for this app.
    ///
    /// Throws rather than returning a URL with an empty api_key. Kite answers
    /// that with an unexplained error page in the user's browser, which reads
    /// as "login is broken" and gives no hint that the real problem is a
    /// credential the app could not read.
    /// </summary>
    public string LoginUrl =>
        HasApiKey
            ? $"https://kite.zerodha.com/connect/login?v=3&api_key={Uri.EscapeDataString(_apiKey)}"
            : throw new InvalidOperationException(
                "No API key stored for this account. Add your Kite Connect "
                + "API key and secret in Settings, then sign in.");

    // -- Auth ------------------------------------------------------

    /// <summary>
    /// Kite user id and display name of the signed-in account, captured from
    /// the profile call that the auth check already makes. Null until a
    /// successful check. Multi-account uses this to label and key an account
    /// without a second round trip.
    /// </summary>
    public string? UserId { get; private set; }
    public string? UserName { get; private set; }

    public async Task<bool> IsAuthenticatedAsync()
    {
        AccessToken = _vault.GetAccessToken();
        if (string.IsNullOrEmpty(AccessToken)) return false;

        try
        {
            var profile = await GetAsync<UserProfileDto>("/user/profile");
            if (profile is null)
            {
                // A 200 with no data is not a session. Kite's error responses
                // still return 200, with status:"error" and data:null. The
                // old code treated the absence of a thrown exception as
                // proof of authentication, which kept the widget pointed at
                // a token the API no longer recognises.
                Log.Warn("/user/profile returned no data; session may be invalid");
                return false;
            }
            UserId = profile.UserId;
            UserName = profile.UserName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task AuthenticateAsync(string requestToken)
    {
        var apiSecret = _vault.GetApiSecret()
            ?? throw new Exception("API secret is missing. Re-enter it in Settings.");

        var checksum = Checksum(_apiKey, requestToken, apiSecret);

        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/session/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["api_key"] = _apiKey,
                ["request_token"] = requestToken,
                ["checksum"] = checksum
            })
        };
        req.Headers.Add("X-Kite-Version", "3");

        var res = await _http.SendAsync(req);

        // Same tolerant parse as GetAsync: a gateway error returns HTML, and
        // a raw JsonException here used to surface as a useless stack to the
        // user. Fall through to the status check below, which reports the
        // real failure (status code + Kite's own message if any).
        var raw = await res.Content.ReadAsStringAsync();
        KiteResponse<SessionData>? payload = null;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize<KiteResponse<SessionData>>(raw);
        }
        catch (System.Text.Json.JsonException) { /* leave payload null */ }

        if (!res.IsSuccessStatusCode || payload?.Data?.AccessToken is null)
            throw new Exception(payload?.Message ?? $"Login failed ({(int)res.StatusCode}). Check your API secret.");

        AccessToken = payload.Data.AccessToken;
        _vault.SaveAccessToken(payload.Data.AccessToken);
    }

    // -- Portfolio -------------------------------------------------

    public async Task<PortfolioData> GetPortfolioAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            return await FetchPortfolioAsync();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<PortfolioData> FetchPortfolioAsync()
    {
        if (string.IsNullOrEmpty(AccessToken))
        {
            AccessToken = _vault.GetAccessToken();
        }
        if (string.IsNullOrEmpty(AccessToken))
            throw new KiteAuthException("Not authenticated");

        var equity = await GetAsync<List<HoldingDto>>("/portfolio/holdings") ?? new();

        List<MFHoldingDto> funds;
        try
        {
            funds = await GetAsync<List<MFHoldingDto>>("/mf/holdings") ?? new();
        }
        catch (KiteAuthException) { throw; }
        catch (Exception ex)
        {
            // MF scope may simply not be enabled on the Kite app; that is a
            // normal, expected reason to have no funds. Log at Warn so it is
            // visible when diagnosing a "my funds are missing" report, without
            // treating it as an error.
            Log.Warn("MF holdings fetch failed ({Error}); showing equity only", ex.GetType().Name);
            funds = new();
        }

        var all = new List<Holding>();
        decimal dayPnl = 0, equityCurrent = 0;

        foreach (var h in equity)
        {
            var qty = h.Quantity + (h.T1Quantity ?? 0);
            if (qty <= 0) continue;

            var last = Priced(h.LastPrice, h.AveragePrice, out var stale);

            var change = h.DayChange
                ?? (h.ClosePrice is > 0 ? h.LastPrice - h.ClosePrice.Value : 0);

            // Day change applies to settled shares only. T1 stock was bought
            // today and held no position at yesterday's close, so attributing a
            // full day's move to it inflates (or deflates) the day figure and
            // makes it disagree with the Kite app. Holdings *value* below still
            // counts every share -- you own them either way.
            dayPnl += h.Quantity * change;
            equityCurrent += qty * last;

            all.Add(new Holding
            {
                Symbol = string.IsNullOrWhiteSpace(h.TradingSymbol)
                    ? "Unnamed holding"
                    : h.TradingSymbol,
                Qty = qty,
                AvgPrice = h.AveragePrice,
                LastPrice = last,
                InstrumentToken = h.InstrumentToken,
                IsMutualFund = false,
                AwaitingPrice = stale,
                ApiPnl = h.Pnl
            });
        }

        // Kite's /mf/holdings last_price is a stale settlement NAV -- verified
        // 1-3 percent away from the live NAV Coin itself displays. AMFI is the
        // official source both derive from, keyed by ISIN, which is exactly
        // what Kite uses as the MF tradingsymbol. Override wherever we can;
        // fall back to Kite's figure when AMFI is unreachable or lacks the
        // ISIN. On override, Kite's pnl (a literal 0 anyway) must not be
        // trusted, so it is dropped and P&L computes from the live NAV.
        IReadOnlyDictionary<string, decimal>? liveNavs = null;
        if (funds.Count > 0)
        {
            try { liveNavs = await _amfi.GetNavsAsync(); }
            catch (Exception ex)
            {
                // Fall back to Kite's NAVs, but leave a trace: a persistent AMFI
                // failure silently degrades every fund's valuation, and without
                // this the log showed nothing at all.
                Log.Warn("AMFI NAV lookup failed ({Error}); using Kite's settlement NAVs",
                    ex.GetType().Name);
            }
        }

        // If we hold funds but AMFI gave us nothing, the fund NAVs below are
        // Kite's stale settlement figures. Record it so the UI can be honest.
        UsingStaleFundNavs = funds.Count > 0 && !_amfi.HasLiveNavs;

        foreach (var f in funds)
        {
            if (f.Quantity <= 0) continue;

            var kiteNav = f.LastPrice;
            var apiPnl = f.Pnl;

            if (liveNavs is not null
                && !string.IsNullOrWhiteSpace(f.TradingSymbol)
                && liveNavs.TryGetValue(f.TradingSymbol.Trim(), out var amfiNav)
                && amfiNav > 0)
            {
                kiteNav = amfiNav;
                apiPnl = null;   // stale-NAV pnl cannot annotate a live NAV
            }

            var last = Priced(kiteNav, f.AveragePrice, out var stale);

            all.Add(new Holding
            {
                Symbol = string.IsNullOrWhiteSpace(f.FundName)
                    ? "Awaiting allotment"
                    : f.FundName,
                Qty = f.Quantity,
                AvgPrice = f.AveragePrice,
                LastPrice = last,
                IsMutualFund = true,
                AwaitingPrice = stale,
                ApiPnl = apiPnl
            });
        }

        var prevClose = equityCurrent - dayPnl;

        return new PortfolioData
        {
            DayPnl = dayPnl,
            DayPnlPct = prevClose > 0 ? dayPnl / prevClose * 100 : 0,
            Holdings = all
        };
    }

    // -- Plumbing --------------------------------------------------

    /// <summary>
    /// Set KITEGLANCE_DEBUG=1 to dump raw API responses to
    /// %APPDATA%\KiteGlance\api-dump.json. Field names in Kite's API have
    /// shifted across versions; this exists so we can read the truth instead
    /// of guessing at it.
    /// </summary>
    private static readonly bool Debugging =
        Environment.GetEnvironmentVariable("KITEGLANCE_DEBUG") == "1";

    private static readonly string DumpPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KiteGlance", "api-dump.json");

    /// <summary>
    /// The dump contains your user_id, email and full holdings in plaintext.
    /// It exists only to diagnose API shape, so on any NON-debug launch we
    /// delete a leftover from a previous debug session -- the sensitive file
    /// never outlives the debugging it was created for.
    /// </summary>
    static KiteService()
    {
        if (Debugging) return;

        try
        {
            if (System.IO.File.Exists(DumpPath))
                System.IO.File.Delete(DumpPath);
        }
        catch { /* best-effort cleanup */ }
    }

    private static void Dump(string path, string json)
    {
        if (!Debugging) return;

        try
        {
            System.IO.Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(DumpPath)!);
            System.IO.File.AppendAllText(DumpPath,
                $"\n===== {path}  {DateTime.Now:HH:mm:ss} =====\n{json}\n");
        }
        catch { /* diagnostics must never break the app */ }
    }

    private async Task<T?> GetAsync<T>(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        req.Headers.Add("X-Kite-Version", "3");
        req.Headers.Add("Authorization", $"token {_apiKey}:{AccessToken}");

        var res = await _http.SendAsync(req);

        var raw = await res.Content.ReadAsStringAsync();
        Dump(path, raw);

        // Tolerant parse. A proxy or gateway error returns HTML, not JSON, and
        // letting Deserialize throw here skipped the status check below -- so a
        // transient 502 surfaced as a JsonException that IsAuthenticatedAsync's
        // blanket catch read as "not authenticated", logging the user out.
        KiteResponse<T>? payload = null;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize<KiteResponse<T>>(raw);
        }
        catch (System.Text.Json.JsonException)
        {
            // Leave payload null; the status check reports the real failure.
        }

        if (!res.IsSuccessStatusCode)
        {
            if (payload?.ErrorType == "TokenException")
            {
                throw ClearAccessToken(payload.Message ?? "Session expired");
            }
            throw new Exception(payload?.Message ?? $"Kite request failed ({(int)res.StatusCode})");
        }

        if (payload is null)
        {
            throw new Exception($"Kite returned a response that could not be read ({(int)res.StatusCode})");
        }

        return payload.Data;
    }

    /// <summary>
    /// Daily closing prices for an instrument, oldest first, for the sparkline.
    ///
    /// This endpoint is part of Kite's paid Historical Data subscription. On an
    /// account without it, Kite answers 403 -- which is a normal state, not a
    /// fault, so this returns null rather than throwing and the caller falls
    /// back to locally-accumulated prices.
    ///
    /// Candles arrive as heterogeneous arrays rather than objects:
    ///
    ///   ["2024-01-01T09:15:00+0530", open, high, low, close, volume]
    ///
    /// so they cannot be mapped to a typed class. Index 4 is the close; taking
    /// any other slot yields a chart that looks entirely plausible and is wrong.
    /// </summary>
    public async Task<List<decimal>?> GetDailyClosesAsync(long instrumentToken, int days)
    {
        if (instrumentToken <= 0 || days <= 0) return null;

        // Ask for extra calendar days: weekends and holidays return no candle,
        // so a bare `days` window yields roughly five sessions in seven.
        var to = DateTime.Now.Date;
        var from = to.AddDays(-(days * 7 / 5 + 10));

        var path = $"/instruments/historical/{instrumentToken}/day"
                   + $"?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

        try
        {
            var data = await GetAsync<HistoricalDataDto>(path);
            if (data?.Candles is null) return null;

            var closes = new List<decimal>(data.Candles.Count);

            foreach (var candle in data.Candles)
            {
                if (candle.Count < 5) continue;
                if (candle[4].ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                if (!candle[4].TryGetDecimal(out var close)) continue;
                if (close > 0) closes.Add(close);
            }

            return closes.Count > 0 ? closes : null;
        }
        catch (KiteAuthException)
        {
            // A genuinely dead session must still surface; the caller's refresh
            // is already handling re-authentication.
            throw;
        }
        catch (Exception ex)
        {
            // Three outcomes share this branch:
            //   - 403: no Historical Data subscription (the common case)
            //   - 5xx / network: transient
            //   - any other HTTP failure
            // All collapse to null because the caller cannot act differently
            // on them: it will fall back to locally-accumulated prices either
            // way. The first null aborts the rest of the loop (see
            // BackfillHistoryAsync), so an unsubscribed account with 30
            // holdings wastes 1 call, not 30.
            //
            // The trade-off: a single transient error during this run is
            // remembered via _backfilled and not retried until next launch.
            // That is acceptable -- the Kite endpoint barely changes intraday
            // -- and the fallback is unaffected.
            Log.Info("Historical candles unavailable ({Error}); using local price history",
                ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Intraday 5-minute candles for an instrument, oldest first, for the
    /// sparkline when the user pays for the Historical Data subscription.
    ///
    /// Returns the raw candle list; <see cref="PriceHistoryService.SeedIntraday"/>
    /// down-samples it to the sparkline width. Null on any failure
    /// (subscription missing, transient error, malformed payload) so the
    /// caller can fall back to <see cref="GetDailyClosesAsync"/>.
    ///
    /// Kite caps this endpoint at 2000 records. A 5-minute window of one
    /// Indian trading day is ~75 candles, so 2 days fits comfortably.
    /// </summary>
    public async Task<List<decimal>?> GetIntradayClosesAsync(long instrumentToken, int intervalMinutes, int days)
    {
        if (instrumentToken <= 0 || days <= 0) return null;
        if (intervalMinutes is not (1 or 3 or 5 or 10 or 15 or 30 or 60)) return null;

        // Same calendar-day padding as the daily path: weekends and holidays
        // return no candle, so a bare `days` window under-fills.
        var to = DateTime.Now.Date;
        var from = to.AddDays(-(days * 7 / 5 + 2));

        var path = $"/instruments/historical/{instrumentToken}/minute"
                   + $"?interval={intervalMinutes}"
                   + $"&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

        try
        {
            var data = await GetAsync<HistoricalDataDto>(path);
            if (data?.Candles is null) return null;

            var closes = new List<decimal>(data.Candles.Count);

            foreach (var candle in data.Candles)
            {
                if (candle.Count < 5) continue;
                if (candle[4].ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                if (!candle[4].TryGetDecimal(out var close)) continue;
                if (close > 0) closes.Add(close);
            }

            return closes.Count > 0 ? closes : null;
        }
        catch (KiteAuthException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Same collapse as GetDailyClosesAsync: 403 (no subscription tier
            // that includes minute data), 5xx, malformed payload. The caller
            // can fall through to the daily endpoint and then to the local
            // accumulator, so null is the right answer.
            Log.Info("Intraday candles unavailable ({Error}); trying daily",
                ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Kite returns last_price = 0 for units they have not yet priced -- a fund
    /// ordered but not allotted, or a NAV that has not published today.
    ///
    /// Reading that as "the asset is worth nothing" is how you invent a 100%
    /// loss out of thin air and poison the portfolio total. The honest read is
    /// "unknown, so hold it cost": P&L of zero, and the row says so.
    /// </summary>
    private static decimal Priced(decimal last, decimal avg, out bool awaiting)
    {
        awaiting = last <= 0 && avg > 0;
        return awaiting ? avg : last;
    }

    private static string Checksum(string apiKey, string requestToken, string apiSecret)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey + requestToken + apiSecret));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Releases the HttpClient, the nested AMFI client, and the refresh gate.
    /// One instance lives for the app's lifetime today, so this is mostly a
    /// correctness guarantee -- but multi-account creates and drops services per
    /// account, and without this each switch would leak a socket handle.
    /// </summary>
    public void Dispose()
    {
        _http.Dispose();
        _amfi.Dispose();
        _refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }
}

// -- Models --------------------------------------------------------

public class PortfolioData
{
    public decimal DayPnl { get; set; }
    public decimal DayPnlPct { get; set; }
    public List<Holding> Holdings { get; set; } = new();
}

public class Holding
{
    public string Symbol { get; set; } = "";
    public decimal Qty { get; set; }
    public decimal AvgPrice { get; set; }

    private decimal _lastPrice;

    /// <summary>
    /// The last traded price. While awaiting a price update from Kite, this
    /// falls back to AvgPrice so the holding is valued at cost instead of
    /// showing zero.
    /// </summary>
    public decimal LastPrice
    {
        get => AwaitingPrice && _lastPrice == 0m ? AvgPrice : _lastPrice;
        set => _lastPrice = value;
    }

    public bool IsMutualFund { get; set; }

    /// <summary>
    /// Kite's instrument id, when there is one. Equity holdings carry it;
    /// mutual funds have no tradeable instrument and so leave it null.
    /// Used solely to request historical candles for the sparkline.
    /// </summary>
    public long? InstrumentToken { get; set; }

    /// <summary>Kite has not priced these units yet; held at cost.</summary>
    public bool AwaitingPrice { get; set; }

    /// <summary>
    /// Kite's own pnl figure from the API. This is the number the website
    /// shows. Recomputing (last - avg) * qty locally drifts from it, because
    /// Kite's average-price accounting (partial exits, corporate actions,
    /// rounding) is theirs, not ours. Trust the source.
    /// </summary>
    public decimal? ApiPnl { get; set; }

    public decimal Invested => PnlMath.Invested(Qty, AvgPrice);

    /// <summary>
    /// P&L for this holding. The arithmetic lives in <see cref="PnlMath"/> so
    /// it can be unit tested in isolation; see that class for why Kite's
    /// pnl: 0 is not trusted blindly. As a worked example:
    ///
    ///   HDFC Gold ETF FoF -- avg 47.02, NAV 44.0707, invested 1749.91
    ///     qty     = 1749.91 / 47.02  = 37.216
    ///     current = 37.216 * 44.0707 = 1640.18   (Coin: 1640.18)
    ///     pnl     = 1640.18 - 1749.91 = -109.73  (Coin: -109.72)
    /// </summary>
    public decimal Pnl => PnlMath.Pnl(Qty, AvgPrice, LastPrice, ApiPnl, AwaitingPrice);

    /// <summary>
    /// Current value, kept consistent with <see cref="Pnl"/> by construction.
    /// </summary>
    public decimal Current => PnlMath.Current(Qty, AvgPrice, LastPrice, ApiPnl, AwaitingPrice);
}

// -- DTOs ----------------------------------------------------------

public class KiteResponse<T>
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("error_type")] public string? ErrorType { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
}

public class SessionData
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("user_name")] public string? UserName { get; set; }
}

public class HoldingDto
{
    [JsonPropertyName("tradingsymbol")] public string TradingSymbol { get; set; } = "";

    /// <summary>
    /// Kite's numeric id for the instrument. Only used to ask the historical
    /// endpoint for candles; it is not shown anywhere.
    /// </summary>
    [JsonPropertyName("instrument_token")] public long? InstrumentToken { get; set; }

    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("t1_quantity")] public decimal? T1Quantity { get; set; }
    [JsonPropertyName("average_price")] public decimal AveragePrice { get; set; }
    [JsonPropertyName("last_price")] public decimal LastPrice { get; set; }
    [JsonPropertyName("close_price")] public decimal? ClosePrice { get; set; }
    [JsonPropertyName("day_change")] public decimal? DayChange { get; set; }
    [JsonPropertyName("pnl")] public decimal? Pnl { get; set; }
}

public class MFHoldingDto
{
    [JsonPropertyName("fund")] public string FundName { get; set; } = "";
    [JsonPropertyName("tradingsymbol")] public string? TradingSymbol { get; set; }
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("average_price")] public decimal AveragePrice { get; set; }
    [JsonPropertyName("last_price")] public decimal LastPrice { get; set; }

    /// <summary>
    /// Kite's own P&L. This is the number Coin shows. It is authoritative:
    /// their NAV timestamp and average-price accounting are theirs, and any
    /// local (last - avg) * qty recomputation drifts from it.
    /// </summary>
    [JsonPropertyName("pnl")] public decimal? Pnl { get; set; }
}

/// <summary>
/// The historical endpoint's payload. Candles are positional arrays, not
/// objects, so the element type stays JsonElement and the caller reads slot 4.
/// </summary>
public class HistoricalDataDto
{
    [JsonPropertyName("candles")]
    public List<List<System.Text.Json.JsonElement>>? Candles { get; set; }
}

public class UserProfileDto
{
    [JsonPropertyName("user_id")] public string? UserId { get; set; }
    [JsonPropertyName("user_name")] public string? UserName { get; set; }
}
