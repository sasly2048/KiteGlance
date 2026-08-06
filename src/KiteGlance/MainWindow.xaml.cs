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

    private static readonly Color Green = Color.FromRgb(0x32, 0xD7, 0x4B);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    private static readonly CultureInfo IN = new("en-IN");

    private readonly KiteService _kite = new();
    private readonly CredentialVault _vault = new();
    private readonly ObservableCollection<HoldingViewModel> _rows = new();
    private readonly WidgetState _state = WidgetState.Load();
    private System.Timers.Timer? _sessionCheckTimer;
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

    public MainWindow()
    {
        InitializeComponent();

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

        PreviewKeyDown += OnKey;
        LocationChanged += (_, _) => QueueSave();

        Restore();
        ShowSkeleton();
        ApplyBackdrop(instant: true);

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

        // Session expiry check: Kite sessions expire daily. Check every hour
        // during market hours to minimize API calls while catching expirations.
        // This runs independently of the auto-refresh timer below.
        _sessionCheckTimer = new System.Timers.Timer(TimeSpan.FromHours(1).TotalMilliseconds)
        {
            AutoReset = true,
            Enabled = true
        };
        _sessionCheckTimer.Elapsed += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () => await CheckSessionAsync());
        };
        _sessionCheckTimer.Start();

        // Auto-refresh during market hours, at the interval the user chose in
        // Settings. Built here and rebuilt on change, so both paths share one
        // definition.
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
    private void ApplyBackdrop(bool instant = false)
    {
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
            Log.Error($"Backdrop load failed: {path}", ex);
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
        var dlg = new SettingsWindow(_state.RefreshIntervalMinutes) { Owner = this };
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

            ShowSkeleton();
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Periodic session check: if authenticated but no portfolio loaded, refresh.
    /// If auth expired, show login overlay without waiting for user action.
    /// </summary>
    private async Task CheckSessionAsync()
    {
        // Skip if market is closed - sessions don't matter then
        if (!MarketOpen()) return;

        var (key, secret) = _vault.GetCredentials();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
            return; // Not configured yet

        var isAuthenticated = await _kite.IsAuthenticatedAsync();

        if (!isAuthenticated)
        {
            // Session expired - show login prompt
            await Dispatcher.InvokeAsync(() => ShowLogin("Your session expired. Sign in again."));
            return;
        }

        // Still authenticated but no data? Auto-refresh silently
        if (_portfolio == null && Overlay.Visibility != Visibility.Visible)
        {
            await RefreshAsync();
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
            _syncedAt = DateTime.Now;

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
    }

    private void StopBreathing()
    {
        if (_breath is null) return;

        _breath.Stop();
        _breath = null;

        LiveHalo.Opacity = 0;
        LiveDot.Opacity = 1;
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
                ApiPnl = h.ApiPnl
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

    private void SwitchTab(string tab)
    {
        if (_state.Tab == tab) return;
        _state.Tab = tab;
        _state.Save();

        var on = (Brush)FindResource("Label");
        var off = (Brush)FindResource("Label3");

        StocksTab.Foreground = tab == "stocks" ? on : off;
        FundsTab.Foreground = tab == "funds" ? on : off;

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
        if (row?.DataContext is not HoldingViewModel vm) return;

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

    private async void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when _open:
                Toggle();
                e.Handled = true;
                break;

            case Key.Space:
            case Key.Enter:
                Toggle();
                e.Handled = true;
                break;

            case Key.R:
                await RefreshAsync(manual: true);
                e.Handled = true;
                break;

            case Key.Tab:
                SwitchTab(_state.Tab == "stocks" ? "funds" : "stocks");
                e.Handled = true;
                break;
        }
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
    protected override void OnClosed(EventArgs e)
    {
        _ticker?.Stop();
        _sessionCheckTimer?.Stop();
        _sessionCheckTimer?.Dispose();
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Reads the auto-refresh interval from WidgetState. Returns 0 to disable,
    /// or a positive integer (minutes) for the refresh cadence. Default: 5 min.
    /// </summary>
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

        Log.Info($"Auto-refresh every {minutes} minute(s)");
    }
}



