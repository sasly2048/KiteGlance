using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KiteGlance.Interop;
using KiteGlance.Motion;
using KiteGlance.Services;
using KiteGlance.State;
using KiteGlance.ViewModels;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace KiteGlance;

public partial class MainWindow : Window
{
    // Geometry. Strict 4pt grid.
    private const double CompactH = 256;
    private const double RowH = 58;
    private const double PaneChrome = 76;
    private const double MaxPaneH = 336;
    private const double TrackW = 332;
    private const double Centre = TrackW / 2;

    // A return has to run this far before the bar saturates. 50% is deliberate:
    // most portfolios live well inside it, so the bar spends its life in the
    // expressive range rather than pinned to the end.
    private const double FullScale = 0.50;

    // Read from the palette rather than baked in. As static readonly fields
    // these kept their dark-theme values for the life of the process, so every
    // gradient and alpha-blend built from them survived a theme switch
    // unchanged. Properties, so a switch is picked up on the next render.
    private static Color Green => PaletteColor("Green", 0x32, 0xD7, 0x4B);
    private static Color Red => PaletteColor("Red", 0xFF, 0x45, 0x3A);

    private static Color PaletteColor(string key, byte r, byte g, byte b)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is SolidColorBrush brush)
            return brush.Color;

        // The dark values, as a fallback for the designer and for tests that
        // run without an Application.
        return Color.FromRgb(r, g, b);
    }

    private static readonly CultureInfo IN = new("en-IN");

    // Declaration order matters: _state is read to pick the account these two
    // are opened against, so it must be initialised first.
    private readonly WidgetState _state = WidgetState.Load();
    private KiteService _kite;
    private CredentialVault _vault;
    private readonly ObservableCollection<HoldingViewModel> _rows = new();
    private PriceHistoryService _history;
    private bool _backfilled;
    private System.Timers.Timer? _autoRefreshTimer;

    private PortfolioData? _portfolio;
    private DateTime _syncedAt;
    private bool _open;
    private bool _regionDirty;
    private bool _firstPaint = true;
    private DateTime _lastManual = DateTime.MinValue;
    private Action? _overlayAction;
    private Debounce? _saver;
    private Storyboard? _breath;
    private System.Windows.Threading.DispatcherTimer? _ticker;

    // Stored as a field so the SystemParameters handler can be removed on
    // shutdown. The widget's Closing is cancelled (Hide, not exit), so Closed
    // never fires during the app's lifetime; cleanup is driven by App.OnExit
    // through a static callback registered in the constructor.
    private System.ComponentModel.PropertyChangedEventHandler? _onStaticPropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        Live.Add(this);

        // Opened against whichever account is active. With no accounts
        // configured this resolves to null and reads the original root vault,
        // so an existing install behaves exactly as before.
        var accountId = _state.ResolvedAccountId;
        _kite = new KiteService(accountId);
        _vault = new CredentialVault(accountId: accountId);
        _history = new PriceHistoryService(accountId: accountId);

        HoldingsList.ItemsSource = _rows;

        DragBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        ToggleButton.Click += (_, _) => Toggle();
        MenuButton.Click += (_, _) => ShowMenu();
        StocksTab.Click += (_, _) => SwitchTab("stocks");
        FundsTab.Click += (_, _) => SwitchTab("funds");

        HoldingsList.PreviewMouseLeftButtonUp += RowClicked;
        HoldingsList.PreviewKeyDown += RowKeyDown;

        PreviewKeyDown += OnKey;
        LocationChanged += (_, _) => QueueSave();

        Restore();
        ShowSkeleton();
        ApplyBackdrop(instant: true);
        ApplyHighContrast();

        // High contrast can be switched on mid-session (Left Alt + Left Shift +
        // Print Screen), so it is watched rather than read once at startup.
        _onStaticPropertyChanged = (_, e) =>
        {
            if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
            Dispatcher.Invoke(ApplyHighContrast);
        };
        SystemParameters.StaticPropertyChanged += _onStaticPropertyChanged;

        // Windows' own light/dark switch. WPF predates that setting and does
        // not surface it, so it arrives as a General preference change and the
        // registry has to be re-read. This matters most for the default mode:
        // a user who never opens Settings is exactly the one who would
        // otherwise watch Windows go light while the widget stayed dark.
        // Released by ReleaseOnExit -- the widget's Closing is cancelled, so
        // Closed never fires during normal shutdown.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

        // The sync label ages in place: "just now" becomes "2m ago" without
        // needing a refresh to make it true. The same tick re-evaluates the
        // backdrop, so dusk arrives on time without its own timer.
        _ticker = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _ticker.Tick += (_, _) =>
        {
            PaintSyncLabel();
            ApplyBackdrop();
        };
        _ticker.Start();

        // Auto-refresh during market hours, at the interval the user chose in
        // Settings. Built here and rebuilt on change, so both paths share one
        // definition. The same refresh that pulls today's prices also catches
        // a 401, so no separate hourly session check is needed -- a redundant
        // /user/profile round-trip every hour would double the API calls
        // without changing the outcome.
        ApplyRefreshInterval();
    }

    // ==== Placement =====================================================

    private void Restore()
    {
        var wa = SystemParameters.WorkArea;

        // Pull a saved position back onto a display that still exists rather
        // than only accepting or rejecting it: a monitor that moved in the
        // virtual desktop leaves coordinates that are wrong but recoverable.
        _state.ClampToVisibleArea(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight,
            Width,
            Height);

        if (_state.Left is { } l && _state.Top is { } t && OnScreen(l, t))
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = Math.Round(wa.Right - Width - 24);
            Top = Math.Round(wa.Top + 24);
        }

        // Pin is applied in OnSourceInitialized -- Desktop glue needs an HWND.
    }

    /// <summary>Undock a laptop and a naive restore hurls the widget into the void.</summary>
    private static bool OnScreen(double l, double t)
    {
        double left = SystemParameters.VirtualScreenLeft;
        double top = SystemParameters.VirtualScreenTop;
        double width = SystemParameters.VirtualScreenWidth;
        double height = SystemParameters.VirtualScreenHeight;
        return l >= left - 40 && l <= left + width + 80 && t >= top - 10 && t <= top + height + 60;
    }
    public PinMode Pin
    {
        get => _state.Pin;
        set
        {
            if (_state.Pin == value) return;

            // Leaving Desktop mode needs an explicit unglue first.
            if (_state.Pin == PinMode.Desktop && value != PinMode.Desktop)
                DesktopPin.Unglue(this);

            _state.Pin = value;
            _state.Save();
            ApplyPin();
        }
    }

    private void ApplyPin()
    {
        switch (_state.Pin)
        {
            case PinMode.Desktop:
                Topmost = false;
                if (!DesktopPin.Glue(this))
                {
                    // Shell replaced or WorkerW not found: degrade honestly
                    // to a normal window rather than pretending.
                    _state.Pin = PinMode.Normal;
                    _state.Save();
                }
                break;

            case PinMode.AlwaysOnTop:
                Topmost = true;
                break;

            default:
                Topmost = false;
                break;
        }
    }

    private void QueueSave()
    {
        _state.Left = Left;
        _state.Top = Top;
        _saver ??= new Debounce(TimeSpan.FromMilliseconds(600), () => _state.Save());
        _saver.Poke();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // The surface is now painted by us -- no system backdrop. DWM still
        // provides corners, dark frame and shadow for top-level modes.
        WindowMaterial.Apply(this, acrylic: false);
        ApplyPin();

        // While glued to the desktop, the expand/collapse spring changes Height
        // every frame and each SizeChanged would rebuild a GDI region and force
        // a redraw. Coalesce: mark dirty here, do the actual rebuild once per
        // render tick, and only while a rebuild is pending. (No-op on the
        // bottom-most pin path, which keeps DWM corners.)
        SizeChanged += (_, _) =>
        {
            if (_state.Pin != PinMode.Desktop || _regionDirty) return;
            _regionDirty = true;
            CompositionTarget.Rendering += RebuildRegionOnce;
        };

        // Win+D minimizes bottom-most windows (the one thing the WorkerW
        // trick did better). Restore immediately: the widget blinks for a
        // frame instead of vanishing until the user hunts for the tray icon.
        StateChanged += (_, _) =>
        {
            if (_state.Pin == PinMode.Desktop && WindowState == WindowState.Minimized)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_state.Pin != PinMode.Desktop) return;
                    Show();
                    WindowState = WindowState.Normal;
                    DesktopPin.Glue(this);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        };

        // Animate the CONTENT: with AllowsTransparency=false the window surface
        // belongs to DWM, so Window.Opacity is inert.
        if (Content is FrameworkElement root)
        {
            root.Opacity = 0;
            var lift = new TranslateTransform(0, 12);
            root.RenderTransform = lift;

            root.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(300)
            });

            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(660),
                EasingFunction = SpringEase.Gentle()
            });
        }
    }

    /// <summary>
    /// Rebuild the desktop-glue corner region at most once per render frame,
    /// no matter how many SizeChanged events the height spring raised in
    /// between. Unhooks itself immediately so it costs nothing while idle.
    /// </summary>
    private void RebuildRegionOnce(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= RebuildRegionOnce;
        _regionDirty = false;

        if (_state.Pin == PinMode.Desktop)
            DesktopPin.ApplyCornerRegion(this);
    }

    // ==== Backdrop ======================================================

    private string? _backdropCurrent;

    /// <summary>
    /// Ensure the right backdrop is showing for the current mode and hour.
    /// Called at startup and on every ticker tick; does nothing unless the
    /// answer has actually changed, so the 20-second cadence costs nothing.
    /// A change crossfades over ~1.2s -- dusk should arrive the way it does
    /// outside, not like a slide projector.
    /// </summary>
    /// <summary>
    /// Repaints when Windows' own theme changes, but only while the user has
    /// asked us to follow it. Someone who explicitly picked Dark or Light has
    /// overridden the OS, and quietly switching out from under them would
    /// discard that choice.
    /// </summary>
    private void OnSystemPreferenceChanged(
        object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        if (_state.Theme != ThemeMode.System) return;

        Dispatcher.Invoke(() =>
        {
            // General fires for several unrelated preferences, so this lands
            // more often than the theme actually changes. Bail unless the
            // resolved theme really differs, or every stray notification would
            // rebuild the palette and reload a backdrop image.
            if (Services.Theme.IsLight == Services.Theme.WindowsPrefersLight()) return;

            Services.Theme.Apply(ThemeMode.System);
            _backdropCurrent = null;
            ApplyBackdrop(instant: true);

            Log.Info("Followed Windows theme change to {Theme}",
                Services.Theme.IsLight ? "Light" : "Dark");
        });
    }

    /// <summary>
    /// High contrast is a request for legibility over decoration, so in that
    /// mode the decoration comes off: the photographic backdrop, the grain
    /// dither, the accent wash and the list's fade mask all sit on top of the
    /// contrast the mode guarantees, and each one erodes it.
    ///
    /// What is left is the plain surface with the palette's own foregrounds --
    /// deliberately duller than the widget is meant to look, which is the
    /// correct trade when the user has asked the OS for exactly that.
    /// </summary>
    private void ApplyHighContrast()
    {
        var on = SystemParameters.HighContrast;

        Grain.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        if (on)
        {
            BackdropFront.Fill = null;
            BackdropBack.Fill = null;
            BackdropScrim.Opacity = 0;
            Wash.Opacity = 0;

            // Force the next ApplyBackdrop to redraw rather than short-circuit
            // on an unchanged path, for when the user switches back.
            _backdropCurrent = null;
        }
        else
        {
            ApplyBackdrop(instant: true);
        }

        // The mask fades the top and bottom rows to transparent. That is a
        // legibility cost paid for a soft edge, which is the wrong trade here.
        HoldingsList.SetValue(HighContrastProperty, on);
    }

    /// <summary>
    /// Read by the holdings list template to drop its edge fade. An attached
    /// flag rather than a second style, so the one template stays the single
    /// description of the list.
    /// </summary>
    public static readonly DependencyProperty HighContrastProperty =
        DependencyProperty.RegisterAttached(
            "HighContrast", typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false));

    public static bool GetHighContrast(DependencyObject o) =>
        (bool)o.GetValue(HighContrastProperty);

    public static void SetHighContrast(DependencyObject o, bool value) =>
        o.SetValue(HighContrastProperty, value);

    private void ApplyBackdrop(bool instant = false)
    {
        // A backdrop image would sit under the text and defeat the point.
        if (SystemParameters.HighContrast) return;

        string path;
        var custom = false;

        if (_state.Backdrop == BackdropMode.Custom
            && !string.IsNullOrEmpty(_state.CustomBackdropPath)
            && System.IO.File.Exists(_state.CustomBackdropPath))
        {
            path = _state.CustomBackdropPath;
            custom = true;
        }
        else
        {
            // Custom selected but the file is gone: fall back honestly.
            path = BackdropService.Pick(
                _state.Backdrop == BackdropMode.Custom ? BackdropMode.Static : _state.Backdrop,
                DateTime.Now);
        }

        if (path == _backdropCurrent) return;
        _backdropCurrent = path;

        var brush = MakeBackdropBrush(path, custom);
        if (brush is null) return;

        // The scrim only earns its keep over user images; built-ins were
        // tuned dark by hand.
        var scrimTo = custom ? 1.0 : 0.0;

        if (instant)
        {
            BackdropFront.Fill = brush;
            BackdropFront.Opacity = 1;
            BackdropScrim.Opacity = scrimTo;
            return;
        }

        // Crossfade: old image moves to the back layer at full opacity, new
        // image fades in over it on the front layer.
        BackdropBack.Fill = BackdropFront.Fill;
        BackdropBack.Opacity = 1;
        BackdropFront.Fill = brush;
        BackdropFront.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        BackdropFront.BeginAnimation(OpacityProperty, fade);

        BackdropScrim.BeginAnimation(OpacityProperty,
            new DoubleAnimation(scrimTo, TimeSpan.FromMilliseconds(1200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    /// <summary>
    /// An ImageBrush for a built-in (pack URI) or user file (absolute path).
    /// Returns null -- and logs -- if the image cannot be decoded, rather than
    /// letting a corrupt file take the window down.
    /// </summary>
    private static ImageBrush? MakeBackdropBrush(string path, bool custom)
    {
        try
        {
            var uri = custom
                ? new Uri(path, UriKind.Absolute)
                : new Uri($"pack://application:,,,/{path}", UriKind.Absolute);

            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            // Decode at the widget's scale, not the file's: a 12 MP photo
            // should cost a few hundred KB of VRAM here, not fifty.
            bmp.DecodePixelWidth = 800;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            return new ImageBrush(bmp)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Top
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Backdrop load failed: {Path}", path);
            return null;
        }
    }

    // ==== States ========================================================

    private void ShowSkeleton()
    {
        Skeleton.Visibility = Visibility.Visible;
        Summary.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Cross-fade skeleton to content. The bones sit exactly where the numbers
    /// will land, so nothing reflows when data arrives -- that absence of shift
    /// is what reads as "solid".
    /// </summary>
    private void ShowSummary()
    {
        if (Summary.Visibility == Visibility.Visible) return;

        Summary.Visibility = Visibility.Visible;
        Summary.Opacity = 0;

        Summary.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(340)
        });

        var out_ = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(200)
        };
        out_.Completed += (_, _) => Skeleton.Visibility = Visibility.Collapsed;
        Skeleton.BeginAnimation(OpacityProperty, out_);
    }

    // ==== Boot / auth ===================================================

    public async Task BootAsync()
    {
        var (key, secret) = _vault.GetCredentials();

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
        {
            ShowOverlay("Connect Kite",
                "Add your Kite Connect API key and secret to begin.",
                "Set up", OpenSettings);
            return;
        }

        if (!await _kite.IsAuthenticatedAsync())
        {
            ShowLogin();
            return;
        }

        // The profile call above tells us which Kite user these credentials
        // belong to; record it so the account can appear in the switcher.
        RememberActiveAccount();

        HideOverlay();
        await RefreshAsync();

        if (_state.Expanded && _portfolio is not null) Toggle();
    }

    private void ShowLogin(string? why = null) =>
        ShowOverlay("Sign in",
            why ?? "Kite sessions reset each morning. Sign in to sync today's portfolio.",
            "Sign in with Kite", async () => await SignInAsync());

    private void ShowOverlay(string title, string body, string action, Action onClick)
    {
        Skeleton.Visibility = Visibility.Collapsed;

        OverlayTitle.Text = title;
        OverlayBody.Text = body;
        OverlayButton.Content = action;

        OverlayButton.Click -= OverlayClick;
        _overlayAction = onClick;
        OverlayButton.Click += OverlayClick;
        OverlayButton.IsEnabled = true;

        if (_open) Toggle();

        Overlay.Opacity = 0;
        Overlay.Visibility = Visibility.Visible;
        Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(240)
        });
    }

    private void OverlayClick(object s, RoutedEventArgs e) => _overlayAction?.Invoke();

    private void HideOverlay()
    {
        if (Overlay.Visibility != Visibility.Visible) return;

        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(240) };
        fade.Completed += (_, _) => Overlay.Visibility = Visibility.Collapsed;
        Overlay.BeginAnimation(OpacityProperty, fade);
    }

    private async Task SignInAsync()
    {
        // Sending the browser to Kite without an API key produces an error page
        // that blames nothing in particular. Say what is actually missing, and
        // offer the screen that fixes it.
        if (!_kite.HasApiKey)
        {
            ShowOverlay("Connect Kite",
                "No API key is stored for this account. Add your Kite Connect "
                + "API key and secret to sign in.",
                "Open settings", OpenSettings);
            return;
        }

        OverlayButton.IsEnabled = false;
        OverlayButton.Content = "Waiting for Kite...";

        try
        {
            var token = await LoginServer.CaptureRequestTokenAsync(_kite.LoginUrl);
            await _kite.AuthenticateAsync(token);
            HideOverlay();
            ShowSkeleton();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            OverlayBody.Text = ex.Message;
            OverlayButton.IsEnabled = true;
            OverlayButton.Content = "Try again";
        }
    }

    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_state.RefreshIntervalMinutes, _state.Theme) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _kite.ReloadCredentials();

            // Persist and apply the chosen cadence without a restart.
            if (dlg.RefreshIntervalMinutes != _state.RefreshIntervalMinutes)
            {
                _state.RefreshIntervalMinutes = dlg.RefreshIntervalMinutes;
                _state.Save();
                ApplyRefreshInterval();
            }

            if (dlg.Theme != _state.Theme)
            {
                _state.Theme = dlg.Theme;
                _state.Save();

                // Swapping the palette dictionary repaints everything bound
                // with DynamicResource; the backdrop is painted from code, so
                // it has to be told separately.
                Services.Theme.Apply(_state.Theme);
                _backdropCurrent = null;
                ApplyBackdrop(instant: true);

                Log.Info("Theme changed to {Theme}", _state.Theme);
            }

            ShowSkeleton();

            // Back through the boot gate rather than straight to a refresh.
            // Credentials may have just been entered for the first time, in
            // which case there is no session yet and a refresh would fail; boot
            // decides between "set up", "sign in" and "load the portfolio".
            _ = BootAsync();
        }
    }

    // ==== Refresh =======================================================

    public async Task RefreshAsync(bool manual = false)
    {
        if (manual)
        {
            if ((DateTime.Now - _lastManual).TotalSeconds < 60)
            {
                Flash("Just synced a moment ago");
                return;
            }
            _lastManual = DateTime.Now;
        }

        try
        {
            _portfolio = await _kite.GetPortfolioAsync();

            // _syncedAt marks the LAST SUCCESSFUL refresh -- not the last
            // attempt. The catch blocks below leave it untouched so the
            // "stale Xm ago" label still points at the last good numbers, and
            // PaintSyncLabel falls back to "can't reach Kite" when we have
            // never once succeeded. UpdatePriceHistoryAsync is best-effort
            // and does not affect this.
            _syncedAt = DateTime.Now;

            await UpdatePriceHistoryAsync(_portfolio);

            Render();
            ShowSummary();
            PaintSyncLabel();
            HideOverlay();

            if (manual) Flash("Synced");
        }
        catch (KiteAuthException)
        {
            ShowLogin("Your session expired for the day. Sign in again.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);

            StopBreathing();
            LiveDot.Fill = (Brush)FindResource("Amber");

            // Amber is the whole message here, and colour alone is not a message.
            System.Windows.Automation.AutomationProperties.SetName(
                LiveStatus, "Cannot reach Kite");

            // Stale, not blank. The last-known numbers are still the truest
            // thing on screen; say when they were true and leave them up.
            SyncLabel.Text = _syncedAt == default
                ? "can't reach Kite"
                : "stale " + Ago(_syncedAt);

            if (_syncedAt == default) ShowSummary();
            if (manual) Flash("Couldn't reach Kite");
        }
    }

    /// <summary>
    /// Feeds this refresh's prices into the sparkline history, and -- once per
    /// run, for equities only -- tries to backfill real candles from Kite so a
    /// subscribed user does not have to wait days for a line.
    ///
    /// Deliberately best-effort throughout. A sparkline is decoration; nothing
    /// here may delay or fail a refresh that has already produced good numbers.
    /// </summary>
    private async Task UpdatePriceHistoryAsync(PortfolioData portfolio)
    {
        try
        {
            foreach (var h in portfolio.Holdings)
            {
                if (h.AwaitingPrice) continue;   // a cost-basis stand-in is not a price
                _history.Record(h.Symbol, h.LastPrice);
            }

            // Drop symbols no longer held, so the file tracks the portfolio
            // rather than growing for the life of the install.
            _history.Retain(portfolio.Holdings.Select(h => h.Symbol));

            if (!_backfilled)
            {
                _backfilled = true;
                await BackfillHistoryAsync(portfolio);
            }

            _history.Save();
        }
        catch (KiteAuthException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("Price history update failed ({Error}); sparklines may be missing",
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// Asks Kite for real daily candles for holdings whose local series is thin.
    ///
    /// Runs once per app run, not per refresh: the answer barely changes
    /// intraday, the endpoint is rate-limited, and on the overwhelmingly common
    /// unsubscribed account every call is a 403. The first failure stops the
    /// loop so an unsubscribed user with thirty holdings makes one wasted
    /// request rather than thirty.
    /// </summary>
    private async Task BackfillHistoryAsync(PortfolioData portfolio)
    {
        foreach (var h in portfolio.Holdings)
        {
            if (h.InstrumentToken is not { } token) continue;   // funds have none
            if (_history.IsComplete(h.Symbol)) continue;

            var closes = await _kite.GetDailyClosesAsync(token, PriceHistoryService.MaxPoints);

            // Null means no subscription (or the endpoint is unhappy). Either
            // way it will be null for every other holding too.
            if (closes is null) return;

            _history.Seed(h.Symbol, closes);
        }
    }

    /// <summary>
    /// "just now" / "4m ago" / "at 3:30 pm". Humans do not think in timestamps
    /// until enough time has passed that the timestamp is the shorter answer.
    /// </summary>
    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;

        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m ago";
        if (d.TotalHours < 6) return (int)d.TotalHours + "h ago";

        return "at " + t.ToString("h:mm tt", IN).ToLowerInvariant();
    }

    private void PaintSyncLabel()
    {
        if (_syncedAt == default) return;

        var live = MarketOpen();

        // When AMFI was unreachable, fund NAVs are Kite's stale settlement
        // figures. Say so, the same way we flag a stale portfolio sync, rather
        // than showing numbers that quietly disagree with Coin.
        var navHint = _kite.UsingStaleFundNavs ? "  \u00B7  fund NAVs delayed" : "";

        if (live)
        {
            SyncLabel.Text = Ago(_syncedAt) + navHint;
            LiveDot.Fill = (Brush)FindResource("Blue");
            StartBreathing();
        }
        else
        {
            SyncLabel.Text = "closed" + navHint;
            LiveDot.Fill = (Brush)FindResource("Label4");
            StopBreathing();
        }
    }

    /// <summary>
    /// A halo swells out of the dot and dissolves, once every 2.4s, while the
    /// core dips and recovers. It is a heartbeat: unhurried, and it only exists
    /// while the market is actually open, so it always means something.
    /// </summary>
    private void StartBreathing()
    {
        if (_breath is not null) return;

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var dur = TimeSpan.FromSeconds(2.4);

        var halo = new DoubleAnimationUsingKeyFrames { Duration = dur };
        halo.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0)));
        halo.KeyFrames.Add(new EasingDoubleKeyFrame(0.28, KeyTime.FromPercent(0.12),
            new SineEase { EasingMode = EasingMode.EaseOut }));
        halo.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(0.55),
            new SineEase { EasingMode = EasingMode.EaseIn }));
        halo.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(1)));
        Storyboard.SetTarget(halo, LiveHalo);
        Storyboard.SetTargetProperty(halo, new PropertyPath(OpacityProperty));
        sb.Children.Add(halo);

        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var grow = new DoubleAnimationUsingKeyFrames { Duration = dur };
            grow.KeyFrames.Add(new LinearDoubleKeyFrame(0.45, KeyTime.FromPercent(0)));
            grow.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.55),
                new SineEase { EasingMode = EasingMode.EaseOut }));
            grow.KeyFrames.Add(new LinearDoubleKeyFrame(0.45, KeyTime.FromPercent(1)));
            Storyboard.SetTarget(grow, HaloScale);
            Storyboard.SetTargetProperty(grow,
                new PropertyPath("(ScaleTransform." + axis + ")"));
            sb.Children.Add(grow);
        }

        var core = new DoubleAnimationUsingKeyFrames { Duration = dur };
        core.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        core.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromPercent(0.3),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        core.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.62),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        Storyboard.SetTarget(core, LiveDot);
        Storyboard.SetTargetProperty(core, new PropertyPath(OpacityProperty));
        sb.Children.Add(core);

        _breath = sb;
        sb.Begin();

        // The pulse IS the "market is open" signal, and a pulse cannot be heard.
        // Say it, in the one place that already knows.
        System.Windows.Automation.AutomationProperties.SetName(LiveStatus, "Market open");
    }

    private void StopBreathing()
    {
        if (_breath is null) return;

        _breath.Stop();
        _breath = null;

        LiveHalo.Opacity = 0;
        LiveDot.Opacity = 1;

        System.Windows.Automation.AutomationProperties.SetName(LiveStatus, "Market closed");
    }

    public static bool MarketOpen()
    {
        var ist = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            DateTime.UtcNow, "India Standard Time");

        if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;

        var m = ist.Hour * 60 + ist.Minute;
        return m >= 9 * 60 + 15 && m <= 15 * 60 + 30;
    }

    // ==== Render ========================================================

    private void Render()
    {
        if (_portfolio is null) return;

        var stocks = _portfolio.Holdings.Where(h => !h.IsMutualFund).ToList();
        var funds = _portfolio.Holdings.Where(h => h.IsMutualFund).ToList();

        StocksCount.Text = stocks.Count > 0 ? stocks.Count.ToString() : "";
        FundsCount.Text = funds.Count > 0 ? funds.Count.ToString() : "";

        var isFunds = _state.Tab == "funds";
        var set = isFunds ? funds : stocks;

        // All three come straight from Kite's own arithmetic. Invested is
        // qty * avg (which Coin agrees with), P&L is Kite's own pnl field, and
        // current is DERIVED from those two -- never recomputed as
        // qty * last_price, which disagrees with Coin when Kite's NAV
        // timestamp differs from the one their P&L was struck against.
        var invested = set.Sum(h => h.Invested);
        var overall = set.Sum(h => h.Pnl);
        var current = invested + overall;

        // Stocks move intraday, so "today" is the live fact. Fund NAVs settle
        // once daily, so "today" would be a fiction.
        var heroVal = isFunds ? overall : _portfolio.DayPnl;
        var heroPct = isFunds
            ? (invested > 0 ? overall / invested * 100 : 0)
            : _portfolio.DayPnlPct;

        HeroLabel.Text = isFunds ? "Overall" : "Today";

        var up = heroVal >= 0;
        var accent = up ? Green : Red;
        var accentBrush = Frozen(new SolidColorBrush(accent));

        HeroValue.Foreground = accentBrush;
        HeroPct.Foreground = accentBrush;
        Arrow.Fill = accentBrush;
        PctChip.Background = Frozen(new SolidColorBrush(
            Color.FromArgb(0x1F, accent.R, accent.G, accent.B)));

        // The arrow ROTATES rather than being swapped for a different glyph.
        // Same object, new orientation -- that is what makes it feel physical.
        ArrowRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = up ? 0 : 180,
            Duration = TimeSpan.FromMilliseconds(460),
            EasingFunction = SpringEase.Layout()
        });

        WashStop.Color = Color.FromArgb(0x2E, accent.R, accent.G, accent.B);
        Wash.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(700)
        });

        // EVERY number now moves by the same law. v5 animated the hero and let
        // these three snap, which quietly announced the hero's count-up as
        // decoration rather than physics.
        var animate = !_firstPaint;

        Numeral.Set(HeroValue, heroVal,
            v => (v < 0 ? Money.MINUS : "") + Money.Rupees(Math.Abs(v)),
            720, animate);

        Numeral.Set(HeroPct, Math.Abs(heroPct),
            v => v.ToString("0.00", IN) + "%", 720, animate);

        Numeral.Set(InvestedText, invested, Money.Rupees, 640, animate);
        Numeral.Set(CurrentText, current, Money.Rupees, 640, animate);

        if (isFunds)
        {
            Numeral.Reset(OverallText);
            OverallText.Text = set.Count + (set.Count == 1 ? " fund" : " funds");
            OverallText.Foreground = (Brush)FindResource("Label3");
        }
        else
        {
            Numeral.Set(OverallText, overall, Money.Signed, 640, animate);
            OverallText.Foreground = Frozen(new SolidColorBrush(overall >= 0 ? Green : Red));
        }

        if (animate) PulseHero();

        // The two halves of the hero are one sentence, so they are announced as
        // one. Numeral.Set animates the visible digits over ~700ms; reading the
        // mid-count value would be wrong, so this states the settled figure.
        System.Windows.Automation.AutomationProperties.SetName(Hero,
            (isFunds ? "Funds today, " : "Today, ")
            + (heroVal >= 0 ? "up " : "down ")
            + Money.Rupees(Math.Abs(heroVal))
            + ", " + Math.Abs(heroPct).ToString("0.00", IN) + " percent");

        DrawDelta(invested, current);

        _rows.Clear();
        foreach (var h in set.OrderByDescending(h => Math.Abs(h.Pnl)))
        {
            _rows.Add(new HoldingViewModel
            {
                RawSymbol = h.Symbol,
                Qty = h.Qty,
                AvgPrice = h.AvgPrice,
                LastPrice = h.LastPrice,
                AwaitingPrice = h.AwaitingPrice,
                ApiPnl = h.ApiPnl,
                History = _history.Series(h.Symbol)
            });
        }

        var empty = _rows.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = isFunds ? "No funds held" : "No stocks held";
        EmptySub.Text = isFunds
            ? "Coin holdings will appear here"
            : "Equity holdings will appear here";

        _firstPaint = false;

        if (_open)
        {
            Resize();
            StaggerIn();
        }
    }

    /// <summary>
    /// Break-even at the centre. Gains run right in green, losses left in red,
    /// scaled by the size of the move -- and coloured by THAT fact, not by the
    /// hero. v4 painted a gain red because the hero happened to be down on the
    /// day, which is the worst thing a portfolio chart can do.
    /// </summary>
    private void DrawDelta(decimal invested, decimal current)
    {
        if (invested <= 0)
        {
            DeltaFill.Width = 0;
            return;
        }

        var ret = (double)(current / invested) - 1.0;
        var mag = Math.Min(Math.Abs(ret) / FullScale, 1.0);
        var half = mag * Centre;
        var up = ret >= 0;

        DeltaFill.Background = Frozen(new SolidColorBrush(up ? Green : Red));

        var spring = SpringEase.Gentle();
        var dur = TimeSpan.FromMilliseconds(800);

        DeltaFill.BeginAnimation(WidthProperty, new DoubleAnimation
        {
            To = Math.Max(half, 2),
            Duration = dur,
            EasingFunction = spring
        });

        DeltaFill.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation
        {
            To = up ? Centre : Centre - half,
            Duration = dur,
            EasingFunction = spring
        });
    }

    private void PulseHero()
    {
        HeroScale.BeginAnimation(ScaleTransform.ScaleXProperty, Pop());
        HeroScale.BeginAnimation(ScaleTransform.ScaleYProperty, Pop());

        static DoubleAnimationUsingKeyFrames Pop() => new()
        {
            Duration = TimeSpan.FromMilliseconds(520),
            KeyFrames =
            {
                new EasingDoubleKeyFrame(1.016,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))),
                new EasingDoubleKeyFrame(1.0,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)),
                    SpringEase.Layout())
            }
        };
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    // ==== Toast =========================================================

    private void Flash(string message)
    {
        ToastText.Text = message;

        var show = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200)
        };

        var rise = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(520),
            EasingFunction = SpringEase.Layout()
        };

        Toast.BeginAnimation(OpacityProperty, show);
        ToastLift.BeginAnimation(TranslateTransform.YProperty, rise);

        var hide = new DoubleAnimation
        {
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(1700),
            Duration = TimeSpan.FromMilliseconds(340)
        };
        hide.Completed += (_, _) => ToastLift.Y = 8;

        Toast.BeginAnimation(OpacityProperty, hide);
    }

    // ==== Expand / collapse =============================================

    private double PaneHeight()
    {
        var rows = Math.Max(_rows.Count, 1);
        return Math.Min(PaneChrome + rows * RowH, MaxPaneH);
    }

    private void Resize()
    {
        if (!_open) return;

        BeginAnimation(HeightProperty, new DoubleAnimation
        {
            To = CompactH + PaneHeight(),
            Duration = TimeSpan.FromMilliseconds(440),
            EasingFunction = SpringEase.Layout()
        });
    }

    private void Toggle()
    {
        _open = !_open;
        _state.Expanded = _open;
        _state.Save();

        ToggleText.Text = _open ? "Hide" : "Holdings";

        // The visible label is the state; the announced one is the action, which
        // is what a button should promise.
        System.Windows.Automation.AutomationProperties.SetName(
            ToggleButton, _open ? "Hide holdings" : "Show holdings");
        System.Windows.Automation.AutomationProperties.SetHelpText(
            ToggleButton,
            _open ? "Space or Enter to collapse the holdings list"
                  : "Space or Enter to expand the holdings list");

        if (_open)
        {
            Pane.Visibility = Visibility.Visible;
            Pane.Opacity = 0;
        }
        else
        {
            StaggerOut();
        }

        var h = new DoubleAnimation
        {
            To = _open ? CompactH + PaneHeight() : CompactH,
            // Collapse begins a beat late, so the rows are already leaving
            // before the window starts closing on them.
            BeginTime = TimeSpan.FromMilliseconds(_open ? 0 : 90),
            Duration = TimeSpan.FromMilliseconds(_open ? 640 : 480),
            EasingFunction = _open ? SpringEase.Layout() : SpringEase.Gentle()
        };
        if (!_open)
            h.Completed += (_, _) => Pane.Visibility = Visibility.Collapsed;

        BeginAnimation(HeightProperty, h);

        ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = _open ? 180 : 0,
            Duration = TimeSpan.FromMilliseconds(540),
            EasingFunction = SpringEase.Layout()
        });

        Pane.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = _open ? 1 : 0,
            BeginTime = TimeSpan.FromMilliseconds(_open ? 80 : 120),
            Duration = TimeSpan.FromMilliseconds(_open ? 280 : 160)
        });

        if (_open)
            Dispatcher.BeginInvoke(StaggerIn,
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Rows arrive 26ms apart: one gesture, not a slideshow.</summary>
    private void StaggerIn()
    {
        HoldingsList.UpdateLayout();

        for (var i = 0; i < HoldingsList.Items.Count; i++)
        {
            if (Container(i) is not { } row) continue;

            var slide = new TranslateTransform(0, 10);
            row.RenderTransform = slide;
            row.Opacity = 0;

            var delay = TimeSpan.FromMilliseconds(i * 26);

            row.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 1,
                BeginTime = delay,
                Duration = TimeSpan.FromMilliseconds(240)
            });

            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                To = 0,
                BeginTime = delay,
                Duration = TimeSpan.FromMilliseconds(520),
                EasingFunction = SpringEase.Gentle()
            });
        }
    }

    /// <summary>
    /// And they leave the same way, from the bottom up -- as if the last row in
    /// is the first row out. v5's rows staggered in beautifully and then simply
    /// evaporated. Motion that only works in one direction is half a system.
    /// </summary>
    private void StaggerOut()
    {
        var n = HoldingsList.Items.Count;

        for (var i = 0; i < n; i++)
        {
            if (Container(i) is not { } row) continue;

            var slide = row.RenderTransform as TranslateTransform;
            if (slide is null)
            {
                slide = new TranslateTransform();
                row.RenderTransform = slide;
            }

            var delay = TimeSpan.FromMilliseconds((n - 1 - i) * 14);

            row.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 0,
                BeginTime = delay,
                Duration = TimeSpan.FromMilliseconds(130)
            });

            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                To = 6,
                BeginTime = delay,
                Duration = TimeSpan.FromMilliseconds(180)
            });
        }
    }

    private FrameworkElement? Container(int i) =>
        HoldingsList.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;

    // ==== Lifecycle =====================================================

    /// <summary>
    /// Every window registers itself so App.OnExit can release static
    /// subscriptions (SystemParameters, SystemEvents) and stop timers that
    /// the suppressed Closing handler would never release. The window's
    /// Closing is cancelled, so Closed never fires during normal shutdown.
    /// </summary>
    private static readonly List<MainWindow> Live = new();

    public static void ShutdownAll()
    {
        // Called by App.OnExit. The widget suppresses Closing (Hide, not
        // exit) so its OnClosed would never run, which left two subscriptions
        // and a couple of timers on the books for the process's lifetime.
        for (var i = Live.Count - 1; i >= 0; i--)
        {
            Live[i].ReleaseOnExit();
            Live.RemoveAt(i);
        }
    }

    private void ReleaseOnExit()
    {
        if (_onStaticPropertyChanged is not null)
        {
            SystemParameters.StaticPropertyChanged -= _onStaticPropertyChanged;
            _onStaticPropertyChanged = null;
        }

        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;

        _ticker?.Stop();
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer?.Dispose();
    }

    private void SwitchTab(string tab)
    {
        if (_state.Tab == tab) return;
        _state.Tab = tab;
        _state.Save();

        var on = (Brush)FindResource("Label");
        var off = (Brush)FindResource("Label3");

        StocksTab.Foreground = tab == "stocks" ? on : off;
        FundsTab.Foreground = tab == "funds" ? on : off;

        // Which tab is showing is carried entirely by colour and the underline
        // position. Neither survives to a screen reader, and these are Buttons
        // rather than a TabControl, so there is no selection state to inherit.
        System.Windows.Automation.AutomationProperties.SetItemStatus(
            StocksTab, tab == "stocks" ? "selected" : "not selected");
        System.Windows.Automation.AutomationProperties.SetItemStatus(
            FundsTab, tab == "funds" ? "selected" : "not selected");

        var target = tab == "stocks" ? StocksTab : FundsTab;
        target.UpdateLayout();
        var x = target.TranslatePoint(new Point(0, 0), TabRow).X;

        var spring = SpringEase.Layout();
        var dur = TimeSpan.FromMilliseconds(480);

        // The underline GLIDES and resizes to the tab it lands on. It does not cut.
        Underline.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation
        {
            To = x,
            Duration = dur,
            EasingFunction = spring
        });

        Underline.BeginAnimation(WidthProperty, new DoubleAnimation
        {
            To = target.ActualWidth,
            Duration = dur,
            EasingFunction = spring
        });

        // Different tab means different money. Don't tween from the old set's
        // numbers to the new set's -- that would animate a transition between
        // two unrelated facts.
        Numeral.Reset(HeroValue, HeroPct, InvestedText, CurrentText, OverallText);
        _firstPaint = true;

        Render();

        _firstPaint = false;
    }

    // ==== Row interaction ===============================================

    private void RowClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;

        var row = FindRow(src);
        if (row?.DataContext is HoldingViewModel vm) Copy(vm);
    }

    /// <summary>
    /// Enter copies the focused row, the keyboard counterpart of clicking it.
    /// Rows are ListBoxItems, so arrow keys already move between them; without
    /// this, reaching a row by keyboard led nowhere.
    ///
    /// Handled here rather than in OnKey because this must only fire when a row
    /// actually holds focus, and the window-level handler cannot see that
    /// without reaching back into the list.
    /// </summary>
    private void RowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if (e.OriginalSource is not DependencyObject src) return;

        var row = FindRow(src);
        if (row?.DataContext is not HoldingViewModel vm) return;

        Copy(vm);

        // Stops OnKey seeing the same press and toggling the pane shut on top
        // of the copy.
        e.Handled = true;
    }

    private void Copy(HoldingViewModel vm)
    {
        try
        {
            System.Windows.Clipboard.SetText(vm.RawSymbol);
            Flash("Copied " + vm.Symbol);
        }
        catch
        {
            // Clipboard can be locked by another process. Not worth a crash.
        }
    }

    private static FrameworkElement? FindRow(DependencyObject node)
    {
        while (node is not null)
        {
            if (node is ContentPresenter cp) return cp;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    // ==== Keyboard ======================================================

    /// <summary>
    /// Window-level shortcuts. These fire wherever focus happens to be, which
    /// is what makes them shortcuts -- and is also why each one has to check
    /// that it is not stealing a key its focused control needs.
    /// </summary>
    private async void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when _open:
                Toggle();
                e.Handled = true;
                break;

            // Space and Enter are how a focused button is pressed. Swallowing
            // them here meant activating the menu button ALSO collapsed the
            // holdings pane -- one keystroke, two unrelated effects. Each label
            // carries its own `when` so the guard applies to both: in C#, a
            // fall-through `case Key.Space:` into `case Key.Enter when ...` is
            // bound to the new label without re-evaluating the guard, so
            // Space would have toggled even with a TextBox focused.
            case Key.Space when !FocusIsOnAControl():
            case Key.Enter when !FocusIsOnAControl():
                Toggle();
                e.Handled = true;
                break;

            case Key.R when !FocusIsOnAControl():
                await RefreshAsync(manual: true);
                e.Handled = true;
                break;

            // Tab was unconditionally consumed to flip between Stocks and
            // Funds, which killed focus traversal outright and made the focus
            // rings unreachable by the only input that can show them. The tab
            // switch keeps Tab only while focus sits on the tab row itself,
            // where "next tab" is the obvious reading; everywhere else Tab
            // moves focus, as it must.
            case Key.Tab when FocusIsOnTabRow():
                SwitchTab(_state.Tab == "stocks" ? "funds" : "stocks");
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// True when a focusable control owns the keyboard, and so owns keys like
    /// Space and Enter. The window itself holding focus is not a control.
    /// </summary>
    private static bool FocusIsOnAControl() =>
        System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Control;

    private bool FocusIsOnTabRow()
    {
        var focused = System.Windows.Input.Keyboard.FocusedElement;
        return focused == StocksTab || focused == FundsTab;
    }

    // ==== Menu ==========================================================

    private void ShowMenu()
    {
        var menu = (ContextMenu)FindResource("WidgetMenu");
        menu.PlacementTarget = MenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = -168;
        menu.VerticalOffset = 6;

        // Menu items live inside a resource, so no generated fields exist.
        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi) continue;

            if (mi.Tag is string tag)
                mi.IsChecked = tag == _state.Pin.ToString();

            // The Background submenu: check the active backdrop mode.
            if (mi.Header is "Background")
                foreach (var sub in mi.Items)
                    if (sub is MenuItem smi && smi.Tag is string stag)
                        smi.IsChecked = stag == _state.Backdrop.ToString();
        }
        menu.IsOpen = true;
    }

    private async void MenuRefresh(object s, RoutedEventArgs e) => await RefreshAsync(manual: true);
    private void MenuSettings(object s, RoutedEventArgs e) => OpenSettings();
    private void MenuPinMode(object s, RoutedEventArgs e)
    {
        if (s is MenuItem { Tag: string tag } && Enum.TryParse<PinMode>(tag, out var mode))
            Pin = mode;
    }

    private void MenuBackdropMode(object s, RoutedEventArgs e)
    {
        if (s is MenuItem { Tag: string tag } && Enum.TryParse<BackdropMode>(tag, out var mode))
        {
            _state.Backdrop = mode;
            _state.Save();
            ApplyBackdrop();
        }
    }

    private void MenuBackdropCustom(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a background image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // Copy into our AppData so the backdrop survives the original
            // being moved, renamed, or living on a USB stick.
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KiteGlance");
            System.IO.Directory.CreateDirectory(dir);

            var dest = System.IO.Path.Combine(dir,
                "custom-backdrop" + System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant());
            System.IO.File.Copy(dlg.FileName, dest, overwrite: true);

            _state.CustomBackdropPath = dest;
            _state.Backdrop = BackdropMode.Custom;
            _state.Save();
            ApplyBackdrop();
        }
        catch (Exception ex)
        {
            Log.Error("Custom backdrop copy failed", ex);
            Flash("Couldn't use that image");
        }
    }

    private void MenuHide(object s, RoutedEventArgs e) => Hide();
    private void MenuQuit(object s, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();
}

/// <summary>Fires once, after things go quiet.</summary>
internal sealed class Debounce
{
    private readonly System.Windows.Threading.DispatcherTimer _t;

    public Debounce(TimeSpan delay, Action then)
    {
        _t = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        _t.Tick += (_, _) =>
        {
            _t.Stop();
            then();
        };
    }

    public void Poke()
    {
        _t.Stop();
        _t.Start();
    }
}

/// <summary>Cleanup for timers when the window closes.</summary>
public partial class MainWindow
{
    // ==== Accounts ======================================================

    /// <summary>
    /// The stored accounts and which one is active, for the tray menu. Copied
    /// out rather than handing over the live list, since the menu is built on
    /// the WinForms side.
    /// </summary>
    public AccountsView AccountsSnapshot()
    {
        var list = new List<AccountRef>();
        foreach (var a in _state.Accounts)
        {
            list.Add(new AccountRef
            {
                Id = a.Id,
                Name = string.IsNullOrWhiteSpace(a.Name) ? a.Id : a.Name
            });
        }
        return new AccountsView(list, _state.ResolvedAccountId);
    }

    /// <summary>
    /// Records the signed-in account so it can appear in the switcher. Called
    /// after a successful auth, when KiteService has the profile in hand.
    /// </summary>
    /// <summary>
    /// Records who the current credentials belong to, so the account can be
    /// named in the switcher.
    ///
    /// This is labelling only. On a single-account install the credentials stay
    /// in the root vault and are still read from there -- ResolvedAccountId
    /// only points at accounts\&lt;id&gt;\ once that folder actually holds a
    /// vault. Getting that wrong is what broke sign-in: merely learning the
    /// user id was enough to send the app looking in an empty directory.
    /// </summary>
    private void RememberActiveAccount()
    {
        var id = _kite.UserId;
        if (string.IsNullOrWhiteSpace(id)) return;

        var changed = _state.UpsertAccount(id, _kite.UserName);

        if (_state.ActiveAccountId != id && _kite.AccountId == id)
        {
            _state.ActiveAccountId = id;
            changed = true;
        }

        if (changed) _state.Save();
    }

    /// <summary>
    /// Points the widget at a different stored account: new vault, new client,
    /// fresh portfolio. The old client is disposed so its sockets do not leak
    /// across switches.
    /// </summary>
    public async Task SwitchAccountAsync(string? accountId)
    {
        if (_state.ActiveAccountId == accountId) return;

        _state.ActiveAccountId = accountId;
        _state.Save();

        // Flush the outgoing account's points before repointing, or the switch
        // discards whatever this session had accumulated for it.
        _history.Save();

        _kite.Dispose();
        _kite = new KiteService(accountId);
        _vault = new CredentialVault(accountId: accountId);
        _history = new PriceHistoryService(accountId: accountId);
        _backfilled = false;

        Log.Info("Switched to account {AccountId}", accountId ?? "(default)");

        _portfolio = null;
        _rows.Clear();
        ShowSkeleton();

        // A different account means different credentials, so the sign-in state
        // has to be re-established rather than assumed.
        if (await _kite.IsAuthenticatedAsync())
        {
            await RefreshAsync();
        }
        else
        {
            ShowLogin("Sign in to this account to load its portfolio.");
        }
    }

    /// <summary>
    /// Adds an account. The vault folder is named for the Kite user id, which
    /// is only known after signing in, so credentials are first written to a
    /// staging folder and the directory is renamed once Kite tells us who it
    /// belongs to. Cancelling leaves the staging folder, which the next add
    /// reuses -- no orphan accumulates.
    /// </summary>
    public async Task AddAccountAsync()
    {
        const string staging = "pending";

        _kite.Dispose();
        _history.Save();

        _kite = new KiteService(staging);
        _vault = new CredentialVault(accountId: staging);
        _history = new PriceHistoryService(accountId: staging);
        _backfilled = false;
        _state.ActiveAccountId = staging;
        _state.Save();

        _portfolio = null;
        _rows.Clear();
        ShowSkeleton();

        OpenSettings();

        if (!await _kite.IsAuthenticatedAsync())
        {
            ShowLogin("Sign in to finish adding this account.");
            return;
        }

        PromoteStagedAccount(staging);
        await RefreshAsync();
    }

    /// <summary>
    /// Renames the staging vault to the real Kite user id now that it is known,
    /// and points the widget at it.
    /// </summary>
    private void PromoteStagedAccount(string staging)
    {
        var id = _kite.UserId;
        if (string.IsNullOrWhiteSpace(id) || id == staging) return;

        try
        {
            var root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KiteGlance", "accounts");

            var from = System.IO.Path.Combine(root, staging);
            var to = System.IO.Path.Combine(root, id);

            if (System.IO.Directory.Exists(from) && !System.IO.Directory.Exists(to))
            {
                System.IO.Directory.Move(from, to);
            }

            _kite.Dispose();
            _kite = new KiteService(id);
            _vault = new CredentialVault(accountId: id);

            // The history file lived inside the folder just renamed, so it has
            // moved with it; only the object needs repointing.
            _history = new PriceHistoryService(accountId: id);
        }
        catch (Exception ex)
        {
            // Keep the staging vault rather than losing the credentials just
            // entered; the account still works, it is just named "pending".
            Log.Error(ex, "Could not rename staged account to {AccountId}", id);
            return;
        }

        _state.UpsertAccount(id, _kite.UserName);
        _state.ActiveAccountId = id;
        _state.Save();
    }

    private int GetAutoRefreshIntervalMinutes() => _state.EffectiveRefreshIntervalMinutes;

    /// <summary>
    /// Rebuilds the auto-refresh timer after the interval changes in Settings,
    /// so a new cadence takes effect without restarting the app. An interval of
    /// 0 disables auto-refresh and leaves only manual refresh.
    /// </summary>
    public void ApplyRefreshInterval()
    {
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer?.Dispose();
        _autoRefreshTimer = null;

        var minutes = GetAutoRefreshIntervalMinutes();
        if (minutes <= 0)
        {
            Log.Info("Auto-refresh disabled by user setting");
            return;
        }

        _autoRefreshTimer = new System.Timers.Timer(minutes * 60_000)
        {
            AutoReset = true,
            Enabled = true
        };
        _autoRefreshTimer.Elapsed += async (_, _) =>
        {
            if (MarketOpen() && Overlay.Visibility != Visibility.Visible)
            {
                await Dispatcher.InvokeAsync(async () => await RefreshAsync(manual: false));
            }
        };

        Log.Info("Auto-refresh every {Minutes} minute(s)", minutes);
    }
}



