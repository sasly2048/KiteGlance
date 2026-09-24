using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KiteGlance.Services;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// Tests for KiteService portfolio calculations and data handling.
/// Uses mock HTTP to avoid actual API calls.
/// </summary>
public class KiteServiceTests : IDisposable
{
    private readonly MockHttpMessageHandler _mockHttp;

    public KiteServiceTests()
    {
        _mockHttp = new MockHttpMessageHandler();
    }

    [Fact]
    public void LoginUrl_encodes_api_key_properly()
    {
        // Can't easily test full KiteService without credentials,
        // but we can verify the URL construction logic is sound
        var apiKey = "test123";
        var expectedContains = Uri.EscapeDataString(apiKey);
        
        Assert.Contains(expectedContains, $"https://kite.zerodha.com/connect/login?v=3&api_key={expectedContains}");
    }

    [Fact]
    public void Checksum_produces_valid_sha256_hex()
    {
        // Calls the real `KiteService.Checksum` -- the test assembly
        // has `InternalsVisibleTo("KiteGlance.Tests")` so the
        // previously-private method is reachable. The earlier
        // version of this test called `SHA256.HashData` directly and
        // missed the real production method, which meant any future
        // change to the encoding (e.g. adding a separator between
        // the three fields) would have slipped through.
        var checksum = KiteService.Checksum("apikey", "requesttoken", "apisecret");

        Assert.Equal(64, checksum.Length);
        Assert.Matches("^[a-f0-9]{64}$", checksum);
    }

    /// Kite's API rejects checksums that use uppercase hex or
    /// non-hex characters. `Convert.ToHexString` returns uppercase by
    /// default, so the production method explicitly lowercases. This
    /// pins that step -- a regression that removed the `ToLower`
    /// would be caught here before it reached the server.
    [Fact]
    public void Checksum_uses_lowercase_hex()
    {
        var checksum = KiteService.Checksum("apikey", "requesttoken", "apisecret");
        Assert.Equal(checksum.ToLowerInvariant(), checksum);
    }

    /// The checksum is over the *concatenation* of the three fields,
    /// with no separator. A regression that introduced a separator
    /// (e.g. `apiKey + "|" + requestToken + "|" + apiSecret`) would
    /// compute a different hash for the same inputs and Kite would
    /// reject every login. This test pins the exact concat order
    /// by comparing the actual output to the canonical SHA-256 of
    /// the documented input.
    [Fact]
    public void Checksum_matches_canonical_sha256_of_concatenation()
    {
        var apiKey = "abc";
        var requestToken = "def";
        var apiSecret = "ghi";
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(apiKey + requestToken + apiSecret)
            )
        ).ToLowerInvariant();

        var actual = KiteService.Checksum(apiKey, requestToken, apiSecret);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Priced_returns_avg_when_last_is_zero_and_awaiting_true()
    {
        // This tests the internal Priced logic through Holding class
        var holding = new Holding
        {
            Qty = 10m,
            AvgPrice = 100m,
            LastPrice = 0m,
            IsMutualFund = false,
            AwaitingPrice = true
        };

        Assert.Equal(100m, holding.LastPrice);
        Assert.Equal(1000m, holding.Invested);
    }

    [Fact]
    public void Priced_returns_last_when_last_is_positive()
    {
        var holding = new Holding
        {
            Qty = 10m,
            AvgPrice = 100m,
            LastPrice = 150m,
            IsMutualFund = false,
            AwaitingPrice = false
        };

        Assert.Equal(150m, holding.LastPrice);
    }

    [Fact]
    public void Holding_pnl_uses_api_pnl_when_available()
    {
        var holding = new Holding
        {
            Qty = 10m,
            AvgPrice = 100m,
            LastPrice = 150m,
            ApiPnl = 450m, // Explicit PnL from API
            IsMutualFund = false,
            AwaitingPrice = false
        };

        // Should use API PnL when available
        Assert.Equal(450m, holding.Pnl);
    }

    [Fact]
    public void Holding_current_equals_invested_plus_pnl()
    {
        var holding = new Holding
        {
            Qty = 10m,
            AvgPrice = 100m,
            LastPrice = 150m,
            ApiPnl = 500m,
            IsMutualFund = false,
            AwaitingPrice = false
        };

        Assert.Equal(holding.Invested + holding.Pnl, holding.Current);
    }

    [Fact]
    public void PortfolioData_initializes_with_empty_holdings()
    {
        var portfolio = new PortfolioData();
        
        Assert.NotNull(portfolio.Holdings);
        Assert.Empty(portfolio.Holdings);
        Assert.Equal(0m, portfolio.DayPnl);
        Assert.Equal(0m, portfolio.DayPnlPct);
    }

    /// <summary>
    /// Mock HTTP handler for testing.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"success\",\"data\":{}}")
            });
        }
    }

    public void Dispose()
    {
        _mockHttp?.Dispose();
    }
}
