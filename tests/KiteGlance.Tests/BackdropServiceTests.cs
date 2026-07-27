using KiteGlance.Services;
using KiteGlance.State;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// The backdrop picker is pure clock logic, so its boundaries are locked in
/// here the same way the P&L rules are: dawn/day/dusk/night must switch at
/// exactly the documented hours, and rotation must be stable within a slot
/// (a widget that reshuffles its background on every launch feels broken).
/// </summary>
public class BackdropServiceTests
{
    [Theory]
    [InlineData(5, BackdropService.Dawn)]    // dawn opens at 05:00
    [InlineData(7, BackdropService.Sunrise)]
    [InlineData(8, BackdropService.Day)]     // day opens at 08:00
    [InlineData(12, BackdropService.Day)]
    [InlineData(16, BackdropService.Noon)]
    [InlineData(17, BackdropService.Dusk)]   // dusk opens at 17:00
    [InlineData(19, BackdropService.Evening)]
    [InlineData(20, BackdropService.Night)]  // night opens at 20:00
    [InlineData(23, BackdropService.Night)]
    [InlineData(0, BackdropService.Night)]
    [InlineData(4, BackdropService.Midnight)]   // still night at 04:59
    public void Time_of_day_boundaries_are_exact(int hour, string expected)
    {
        var moment = new DateTime(2026, 7, 16, hour, 30, 0);
        Assert.Equal(expected, BackdropService.Pick(BackdropMode.TimeOfDay, moment));
    }

    [Fact]
    public void Time_of_day_uses_the_late_day_variant()
    {
        var moment = new DateTime(2026, 7, 17, 13, 0, 0);
        Assert.Equal(BackdropService.Noon,
            BackdropService.Pick(BackdropMode.TimeOfDay, moment));
    }

    [Fact]
    public void Static_mode_is_always_the_day_graphite()
    {
        foreach (var hour in new[] { 0, 6, 12, 18, 23 })
        {
            var moment = new DateTime(2026, 7, 16, hour, 0, 0);
            Assert.Equal(BackdropService.Day, BackdropService.Pick(BackdropMode.Static, moment));
        }
    }

    [Fact]
    public void Rotation_is_stable_within_a_three_hour_slot()
    {
        // Two moments inside the same slot must agree: rotation is a slow
        // clock, not a slot machine.
        var a = new DateTime(2026, 7, 16, 9, 0, 0);
        var b = new DateTime(2026, 7, 16, 11, 59, 0);

        Assert.Equal(
            BackdropService.Pick(BackdropMode.Rotate, a),
            BackdropService.Pick(BackdropMode.Rotate, b));
    }

    [Fact]
    public void Rotation_advances_between_slots()
    {
        var a = new DateTime(2026, 7, 16, 9, 0, 0);
        var b = new DateTime(2026, 7, 16, 12, 0, 0);   // next slot

        Assert.NotEqual(
            BackdropService.Pick(BackdropMode.Rotate, a),
            BackdropService.Pick(BackdropMode.Rotate, b));
    }

    [Fact]
    public void Rotation_walks_the_whole_set_over_a_day()
    {
        // Eight slots a day over a four-image set: every image must appear.
        var seen = new HashSet<string>();
        for (var h = 0; h < 24; h += 3)
            seen.Add(BackdropService.Pick(BackdropMode.Rotate, new DateTime(2026, 7, 16, h, 0, 0)));

        Assert.Equal(BackdropService.Set.Length, seen.Count);
    }
}
