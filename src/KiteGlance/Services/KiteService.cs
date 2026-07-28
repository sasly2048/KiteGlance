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

public class KiteService
{
    private const string BaseUrl = "https://api.kite.trade";

    private readonly HttpClient _http = new();
    private readonly CredentialVault _vault = new();
    private readonly AmfiNavService _amfi = new();

    // Hourly timer, manual refresh and boot can all fire at once. Without a
    // gate they interleave: doubled work, and two Dump() appends producing
    // garbled diagnostics. One refresh at a time.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private string _apiKey;
    private string? _accessToken;

    /// <summary>True when the last portfolio came back on Kite's stale MF
    /// NAVs because AMFI could not be reached. Surfaced so the UI can say so.</summary>
    public bool UsingStaleFundNavs { get; private set; }

    public KiteService()
    {
        _apiKey = _vault.GetApiKey() ?? "";
    }

    public void ReloadCredentials() => _apiKey = _vault.GetApiKey() ?? "";

    public string LoginUrl =>
        $"https://kite.zerodha.com/connect/login?v=3&api_key={Uri.EscapeDataString(_apiKey)}";

    // -- Auth ------------------------------------------------------

    public async Task<bool> IsAuthenticatedAsync()
    {
        _accessToken = _vault.GetAccessToken();
        if (string.IsNullOrEmpty(_accessToken)) return false;

        try
        {
            await GetAsync<UserProfileDto>("/user/profile");
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
        var payload = await res.Content.ReadFromJsonAsync<KiteResponse<SessionData>>();

        if (!res.IsSuccessStatusCode || payload?.Data?.AccessToken is null)
            throw new Exception(payload?.Message ?? "Login failed. Check your API secret.");

        _accessToken = payload.Data.AccessToken;
        _vault.SaveAccessToken(_accessToken);
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
        _accessToken ??= _vault.GetAccessToken();
        if (string.IsNullOrEmpty(_accessToken))
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
            Log.Warn($"MF holdings fetch failed ({ex.GetType().Name}); showing equity only");
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
            dayPnl += qty * change;
            equityCurrent += qty * last;

            all.Add(new Holding
            {
                Symbol = string.IsNullOrWhiteSpace(h.TradingSymbol)
                    ? "Unnamed holding"
                    : h.TradingSymbol,
                Qty = qty,
                AvgPrice = h.AveragePrice,
                LastPrice = last,
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
            catch { /* fall back to Kite's NAVs */ }
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
        req.Headers.Add("Authorization", $"token {_apiKey}:{_accessToken}");

        var res = await _http.SendAsync(req);

        var raw = await res.Content.ReadAsStringAsync();
        Dump(path, raw);

        var payload = System.Text.Json.JsonSerializer.Deserialize<KiteResponse<T>>(raw);

        if (!res.IsSuccessStatusCode)
        {
            if (payload?.ErrorType == "TokenException")
            {
                _accessToken = null;
                _vault.ClearAccessToken();
                throw new KiteAuthException(payload.Message ?? "Session expired");
            }
            throw new Exception(payload?.Message ?? $"Kite request failed ({(int)res.StatusCode})");
        }

        return payload is null ? default : payload.Data;
    }

    /// <summary>
    /// Kite returns last_price = 0 for units it has not yet priced -- a fund
    /// ordered but not allotted, or a NAV that has not published today.
    ///
    /// Reading that as "the asset is worth nothing" is how you invent a 100%
    /// loss out of thin air and poison the portfolio total. The honest read is
    /// "unknown, so hold it at cost": P&L of zero, and the row says so.
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
    public decimal LastPrice { get; set; }
    public bool IsMutualFund { get; set; }

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

public class UserProfileDto
{
    [JsonPropertyName("user_id")] public string? UserId { get; set; }
    [JsonPropertyName("user_name")] public string? UserName { get; set; }
}
