using System.Globalization;
using System.Linq;
using System.Threading;
using KiteGlance.Services;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// Covers the message-template renderer behind the structured-logging
/// overloads. The file-writing side is deliberately not tested here -- it is
/// best-effort by design and swallows its own failures.
/// </summary>
public class LogTests
{
    [Fact]
    public void Renders_a_hole_and_captures_the_property()
    {
        var (text, props) = Log.Render("Refreshed {Count} holdings", new object?[] { 12 });

        Assert.Equal("Refreshed 12 holdings", text);
        Assert.Single(props);
        Assert.Equal("Count", props[0].Key);
        Assert.Equal(12, props[0].Value);
    }

    [Fact]
    public void Renders_several_holes_in_order()
    {
        var (text, props) = Log.Render(
            "Refreshed {Count} holdings in {Ms}ms", new object?[] { 12, 340 });

        Assert.Equal("Refreshed 12 holdings in 340ms", text);
        Assert.Equal(new[] { "Count", "Ms" }, props.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void A_template_with_no_holes_is_returned_verbatim()
    {
        var (text, props) = Log.Render("Nothing to substitute", new object?[] { });

        Assert.Equal("Nothing to substitute", text);
        Assert.Empty(props);
    }

    /// <summary>
    /// A caller that passes too few arguments must still get a readable line;
    /// a logger that throws is worse than a slightly wrong log.
    /// </summary>
    [Fact]
    public void An_unmatched_hole_is_left_literal_rather_than_throwing()
    {
        var (text, props) = Log.Render("{A} and {B}", new object?[] { 1 });

        Assert.Equal("1 and {B}", text);
        Assert.Single(props);
    }

    [Fact]
    public void Extra_arguments_are_ignored()
    {
        var (text, _) = Log.Render("{A}", new object?[] { 1, 2, 3 });
        Assert.Equal("1", text);
    }

    [Fact]
    public void Null_renders_as_null_rather_than_an_empty_gap()
    {
        var (text, _) = Log.Render("value={V}", new object?[] { null });
        Assert.Equal("value=null", text);
    }

    [Fact]
    public void An_unclosed_brace_does_not_throw()
    {
        var (text, _) = Log.Render("broken {A", new object?[] { 1 });
        Assert.Equal("broken {A", text);
    }

    /// <summary>
    /// Numbers must format the same way regardless of the operator's locale --
    /// a comma decimal separator would corrupt a log meant to be machine-read.
    /// </summary>
    [Fact]
    public void Numbers_format_invariantly()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var (text, _) = Log.Render("nav={Nav}", new object?[] { 1234.56m });
            Assert.Equal("nav=1234.56", text);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
