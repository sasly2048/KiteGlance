using KiteGlance.State;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// Covers the persisted-state rules that have no UI: the auto-refresh interval
/// bounds, and pulling a stored window position back onto a display that
/// actually exists.
/// </summary>
public class WidgetStateTests
{
    // -- Refresh interval ------------------------------------------------

    [Fact]
    public void Default_refresh_interval_is_five_minutes()
    {
        Assert.Equal(5, new WidgetState().EffectiveRefreshIntervalMinutes);
    }

    [Fact]
    public void Zero_disables_auto_refresh()
    {
        var state = new WidgetState { RefreshIntervalMinutes = 0 };
        Assert.Equal(0, state.EffectiveRefreshIntervalMinutes);
    }

    [Fact]
    public void Negative_interval_is_treated_as_disabled_not_as_a_timer()
    {
        var state = new WidgetState { RefreshIntervalMinutes = -5 };
        Assert.Equal(0, state.EffectiveRefreshIntervalMinutes);
    }

    /// <summary>
    /// A hand-edited state.json must not be able to set a sub-minute cadence
    /// that would hammer the Kite API and trip its rate limit.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(60, 60)]
    [InlineData(600, 60)]      // clamped down to the ceiling
    public void Interval_is_clamped_to_a_sane_range(int stored, int expected)
    {
        var state = new WidgetState { RefreshIntervalMinutes = stored };
        Assert.Equal(expected, state.EffectiveRefreshIntervalMinutes);
    }

    // -- Position clamping -----------------------------------------------

    private const double ScreenLeft = 0;
    private const double ScreenTop = 0;
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1080;
    private const double WindowWidth = 372;
    private const double WindowHeight = 256;

    private static void Clamp(WidgetState s) =>
        s.ClampToVisibleArea(ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight,
                             WindowWidth, WindowHeight);

    [Fact]
    public void A_position_already_on_screen_is_left_alone()
    {
        var state = new WidgetState { Left = 400, Top = 300 };
        Clamp(state);

        Assert.Equal(400, state.Left);
        Assert.Equal(300, state.Top);
    }

    /// <summary>
    /// The monitor the widget was parked on has been unplugged. Without this
    /// the app runs at coordinates no display covers -- invisible, with no way
    /// back short of deleting state.json.
    /// </summary>
    [Fact]
    public void A_position_on_a_detached_monitor_is_pulled_back()
    {
        var state = new WidgetState { Left = 5000, Top = 4000 };
        Clamp(state);

        Assert.True(state.Left < ScreenWidth, $"Left {state.Left} still off-screen");
        Assert.True(state.Top < ScreenHeight, $"Top {state.Top} still off-screen");
    }

    [Fact]
    public void Far_negative_coordinates_are_pulled_back()
    {
        var state = new WidgetState { Left = -9000, Top = -9000 };
        Clamp(state);

        Assert.True(state.Left is > -WindowWidth);
        Assert.True(state.Top >= ScreenTop);
    }

    /// <summary>NaN reaches WPF layout and throws; it must be discarded.</summary>
    [Fact]
    public void Non_finite_coordinates_are_discarded()
    {
        var state = new WidgetState { Left = double.NaN, Top = 100 };
        Clamp(state);

        Assert.Null(state.Left);
        Assert.Null(state.Top);
    }

    [Fact]
    public void Infinite_coordinates_are_discarded()
    {
        var state = new WidgetState { Left = 100, Top = double.PositiveInfinity };
        Clamp(state);

        Assert.Null(state.Left);
        Assert.Null(state.Top);
    }

    [Fact]
    public void An_unset_position_stays_unset()
    {
        var state = new WidgetState();
        Clamp(state);

        Assert.Null(state.Left);
        Assert.Null(state.Top);
    }

    // -- Accounts --------------------------------------------------------

    /// <summary>
    /// An existing single-account install has no Accounts entries, and must
    /// keep reading the original root vault rather than being pointed at a
    /// per-account folder that does not exist.
    /// </summary>
    [Fact]
    public void With_no_accounts_the_legacy_root_vault_is_used()
    {
        var state = new WidgetState();

        Assert.Null(state.ResolvedAccountId);
        Assert.False(state.HasMultipleAccounts);
    }

    [Fact]
    public void Adding_an_account_records_it()
    {
        var state = new WidgetState();

        Assert.True(state.UpsertAccount("AB1234", "Ada"));
        Assert.Single(state.Accounts);
        Assert.Equal("Ada", state.Accounts[0].Name);
    }

    [Fact]
    public void Re_adding_the_same_account_does_not_duplicate_it()
    {
        var state = new WidgetState();
        state.UpsertAccount("AB1234", "Ada");

        Assert.False(state.UpsertAccount("AB1234", "Ada"));
        Assert.Single(state.Accounts);
    }

    [Fact]
    public void A_renamed_account_updates_in_place()
    {
        var state = new WidgetState();
        state.UpsertAccount("AB1234", "Ada");

        Assert.True(state.UpsertAccount("AB1234", "Ada L."));
        Assert.Single(state.Accounts);
        Assert.Equal("Ada L.", state.Accounts[0].Name);
    }

    [Fact]
    public void An_empty_id_is_rejected()
    {
        var state = new WidgetState();

        Assert.False(state.UpsertAccount("", "Nobody"));
        Assert.Empty(state.Accounts);
    }

    [Fact]
    public void The_active_account_resolves_when_it_exists()
    {
        var state = new WidgetState();
        state.UpsertAccount("AB1234", "Ada");
        state.UpsertAccount("CD5678", "Grace");
        state.ActiveAccountId = "CD5678";

        Assert.Equal("CD5678", state.ResolvedAccountId);
        Assert.True(state.HasMultipleAccounts);
    }

    /// <summary>
    /// Deleting an account folder by hand must not leave the widget pointing
    /// at an id that no longer resolves.
    /// </summary>
    [Fact]
    public void A_stale_active_id_falls_back_to_the_first_account()
    {
        var state = new WidgetState();
        state.UpsertAccount("AB1234", "Ada");
        state.ActiveAccountId = "GONE999";

        Assert.Equal("AB1234", state.ResolvedAccountId);
    }
}
