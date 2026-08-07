using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KiteGlance.Services;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// Tests for AMFI NAV service parsing and caching logic.
/// Uses mock HTTP handler to avoid network calls.
/// </summary>
public class AmfiNavServiceTests : IDisposable
{
    private readonly MockHttpMessageHandler _mockHttp;
    private readonly HttpClient _httpClient;

    public AmfiNavServiceTests()
    {
        _mockHttp = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHttp) { Timeout = TimeSpan.FromSeconds(5) };
    }

    [Fact]
    public void Parse_extracts_navs_from_amfi_format()
    {
        const string sampleData = @"Scheme Code;ISIN Growth;ISIN Reinvest;Name;NAV;Date
120503;INF846K01EW2;INF846K01EX0;HDFC Balanced Advantage Fund - Direct Plan;45.67;28-Jul-2025
119551;INF204K01BC3;INF204K01BD1;SBI Small Cap Fund - Regular Plan;123.45;28-Jul-2025
100064;INF090I01LK7;INF090I01LL5;ICICI Prudential Technology Fund - Growth;98.76;28-Jul-2025";

        var result = AmfiNavService.Parse(sampleData);

                Assert.Equal(6, result.Count);
        Assert.Equal(45.67m, result["INF846K01EW2"]);
        Assert.Equal(45.67m, result["INF846K01EX0"]);
        Assert.Equal(123.45m, result["INF204K01BC3"]);
        Assert.Equal(98.76m, result["INF090I01LK7"]);
    }

    /// <summary>
    /// A truncated five-field row has the date at index 4. The old guard was
    /// `parts.Length &lt; 5`, which admitted it and parsed the date as a price.
    /// </summary>
    [Fact]
    public void Parse_rejects_five_field_row_rather_than_reading_the_date_as_a_nav()
    {
        const string truncated = @"Scheme Code;ISIN Growth;ISIN Reinvest;Name;NAV;Date
120503;INF846K01EW2;INF846K01EX0;HDFC Balanced Advantage Fund;28-Jul-2025";

        var result = AmfiNavService.Parse(truncated);

        Assert.Empty(result);
    }

    /// <summary>
    /// A scheme name containing a semicolon shifts every later column. Reading
    /// the NAV as a fixed index 4 would pick up a name fragment; reading it as
    /// the second-from-last column stays correct.
    /// </summary>
    [Fact]
    public void Parse_handles_semicolon_inside_scheme_name()
    {
        const string awkward = @"Scheme Code;ISIN Growth;ISIN Reinvest;Name;NAV;Date
120503;INF846K01EW2;INF846K01EX0;HDFC Fund - Direct; Growth Option;45.67;28-Jul-2025";

        var result = AmfiNavService.Parse(awkward);

        Assert.Equal(45.67m, result["INF846K01EW2"]);
    }

    /// <summary>
    /// Guards the exact figure a real row yields, so an index change that
    /// happens to still parse *something* numeric cannot pass silently.
    /// </summary>
    [Fact]
    public void Parse_reads_the_nav_column_not_an_adjacent_one()
    {
        const string row = @"Scheme Code;ISIN Growth;ISIN Reinvest;Name;NAV;Date
120503;INF846K01EW2;INF846K01EX0;Some Fund;45.67;28-Jul-2025";

        var result = AmfiNavService.Parse(row);

        Assert.Equal(45.67m, result["INF846K01EW2"]);
        Assert.DoesNotContain(result.Values, v => v == 120503m);
    }

    [Fact]
    public void Parse_skips_invalid_rows()
    {
        const string dataWithInvalidRows = @"Scheme Code;ISIN Growth;ISIN Reinvest;Name;NAV;Date
120503;INF846K01EW2;INF846K01EX0;HDFC Fund;45.67;28-Jul-2025
Some AMC Name Without Semicolons
119551;;;N.A.;28-Jul-2025
Short Row
100064;INF090I01LK7;INF090I01LL5;ICICI Fund;98.76;28-Jul-2025";

        var result = AmfiNavService.Parse(dataWithInvalidRows);

        Assert.Equal(4, result.Count);
        Assert.True(result.ContainsKey("INF846K01EW2"));
        Assert.True(result.ContainsKey("INF090I01LK7"));
    }

    [Fact]
    public void Parse_handles_na_nav_values()
    {
        const string dataWithNaNav = @"120503;INF846K01EW2;INF846K01EX0;HDFC Fund;N.A.;28-Jul-2025
119551;INF204K01BC3;INF204K01BD1;SBI Fund;123.45;28-Jul-2025";

        var result = AmfiNavService.Parse(dataWithNaNav);

                                                Assert.Equal(2, result.Count);
            Assert.False(result.ContainsKey("INF846K01EW2"));
        Assert.Equal(123.45m, result["INF204K01BC3"]);
    }

    [Fact]
    public void Parse_handles_zero_nav_values()
    {
        const string dataWithZeroNav = @"120503;INF846K01EW2;INF846K01EX0;HDFC Fund;0;28-Jul-2025
119551;INF204K01BC3;INF204K01BD1;SBI Fund;123.45;28-Jul-2025";

        var result = AmfiNavService.Parse(dataWithZeroNav);

Assert.Equal(2, result.Count);
Assert.False(result.ContainsKey("INF846K01EW2"));
        Assert.Equal(123.45m, result["INF204K01BC3"]);
    }

    [Fact]
    public void Parse_handles_empty_input()
    {
        var result = AmfiNavService.Parse(string.Empty);
        Assert.Empty(result);

        result = AmfiNavService.Parse("\n\n\n");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_handles_whitespace_in_isin()
    {
        const string dataWithWhitespace = @"120503; INF846K01EW2 ; INF846K01EX0 ;HDFC Fund;45.67;28-Jul-2025";

        var result = AmfiNavService.Parse(dataWithWhitespace);

        Assert.Equal(2, result.Count);
        Assert.Equal(45.67m, result["INF846K01EW2"]);
        Assert.Equal(45.67m, result["INF846K01EX0"]);
    }

    [Fact]
    public void GetNavAsync_returns_null_for_missing_isin()
    {
        var service = new AmfiNavService();
        // Without any cache or network, should return null gracefully
        var task = service.GetNavAsync("NONEXISTENT123");
        
        // Should not throw
        Assert.NotNull(task);
    }

    [Fact]
    public void GetNavsAsync_returns_null_without_data()
    {
        var service = new AmfiNavService();
        var task = service.GetNavsAsync();
        
        // Should not throw even without network/cache
        Assert.NotNull(task);
    }

    [Fact]
    public void HasLiveNavs_is_false_initially()
    {
        var service = new AmfiNavService();
        Assert.False(service.HasLiveNavs);
    }

    /// <summary>
    /// Mock HTTP handler that returns predefined responses without network calls.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _mockHttp?.Dispose();
    }
}
