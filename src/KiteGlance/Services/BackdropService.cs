using KiteGlance.State;

namespace KiteGlance.Services;

/// <summary>
/// Decides which backdrop image the widget should be showing right now,
/// given the mode and the clock. Pure decision logic -- no WPF, no file IO --
/// so the time boundaries can be unit tested like the P&L math is.
///
/// The set is eight pre-rendered mesh gradients (see Assets/), two per phase
/// of the day. Rendering them at build time keeps the app free of a raster
/// pipeline while giving each phase a distinct early and late look.
/// </summary>
public static class BackdropService
{
    public const string Dawn = "Assets/backdrop-dawn.png";
    public const string Sunrise = "Assets/backdrop-sunrise.png";
    public const string Day = "Assets/backdrop-day.png";
    public const string Noon = "Assets/backdrop-noon.png";
    public const string Dusk = "Assets/backdrop-dusk.png";
    public const string Evening = "Assets/backdrop-evening.png";
    public const string Night = "Assets/backdrop-night.png";
    public const string Midnight = "Assets/backdrop-midnight.png";

    /// <summary>All built-in backdrops, in day order.</summary>
    public static readonly string[] Set =
    {
        Dawn, Sunrise, Day, Noon, Dusk, Evening, Night, Midnight
    };

    /// <summary>
    /// The built-in image for a moment in time, per mode. Custom mode is not
    /// answered here -- the caller uses the user's file instead.
    /// </summary>
    public static string Pick(BackdropMode mode, DateTime now) => mode switch
    {
        BackdropMode.TimeOfDay => ByHour(now),
        BackdropMode.Rotate => ByRotation(now),
        _ => Day
    };

    /// <summary>
    /// Dawn 05-08, day 08-17, dusk 17-20, night 20-05. The boundaries follow
    /// the sky, not the market: the widget lives on the desktop all day, and
    /// its light should agree with the window next to it.
    /// </summary>
    private static string ByHour(DateTime now) => now.Hour switch
    {
        >= 5 and < 7 => Dawn,
        >= 7 and < 8 => Sunrise,
        >= 8 and < 13 => Day,
        >= 13 and < 17 => Noon,
        >= 17 and < 19 => Dusk,
        >= 19 and < 20 => Evening,
        >= 20 or < 1 => Night,
        _ => Midnight
    };

    /// <summary>
    /// A steady walk through the set, one step every three hours, anchored to
    /// the calendar so every launch on the same afternoon shows the same
    /// image -- rotation should feel like a slow clock, not a slot machine.
    /// </summary>
    private static string ByRotation(DateTime now)
    {
        var slot = (now.DayOfYear * 8 + now.Hour / 3) % Set.Length;
        return Set[slot];
    }
}
