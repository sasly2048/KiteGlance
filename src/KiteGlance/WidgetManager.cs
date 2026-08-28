using System.Diagnostics;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using KiteGlance.State;
using Application = System.Windows.Application;

namespace KiteGlance;

/// <summary>Tray presence and start-with-Windows. Auto-refresh lives on the
/// widget itself, configurable from Settings, so there is no second clock
/// here firing on its own cadence.</summary>
public class WidgetManager : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "KiteGlance";

    private readonly MainWindow _widget;
    private readonly NotifyIcon _tray;

    public WidgetManager(MainWindow widget)
    {
        _widget = widget;

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Kite Glance",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _tray.DoubleClick += (_, _) => Reveal();

        // Closing the widget tucks it away; it doesn't kill the app.
        // Quit is an explicit choice, made from the tray.
        _widget.Closing += (_, e) =>
        {
            e.Cancel = true;
            _widget.Hide();
        };
    }

    /// <summary>
    /// The real mark, pulled from the packed resource. Falls back to the exe's
    /// own icon, then to a stock one, so the tray is never empty.
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is not null) return new System.Drawing.Icon(stream);
        }
        catch { /* fall through */ }

        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico is not null) return ico;
            }
        }
        catch { /* fall through */ }

        return System.Drawing.SystemIcons.Application;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        TrayTheme.Apply(menu);   // owner-drawn; no Win98 grey anywhere

        menu.Items.Add("Show widget", null, (_, _) => Reveal());

        menu.Items.Add("Refresh now", null, async (_, _) =>
            await _widget.Dispatcher.InvokeAsync(async () =>
                await _widget.RefreshAsync(manual: true)));

        menu.Items.Add(new ToolStripSeparator());

        // Three pin modes, mutually exclusive. Desktop is the default: glued
        // to the wallpaper layer, so it lives under your apps and on every
        // virtual desktop.
        var pinDesktop = new ToolStripMenuItem("Pin to desktop") { Tag = PinMode.Desktop };
        var pinTop = new ToolStripMenuItem("Always on top") { Tag = PinMode.AlwaysOnTop };
        var pinFree = new ToolStripMenuItem("Float freely") { Tag = PinMode.Normal };

        foreach (var item in new[] { pinDesktop, pinTop, pinFree })
        {
            item.Click += (s, _) =>
            {
                var mode = (PinMode)((ToolStripMenuItem)s!).Tag!;
                _widget.Dispatcher.Invoke(() => _widget.Pin = mode);
            };
            menu.Items.Add(item);
        }

        var startup = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsAutostartEnabled()
        };
        startup.Click += (s, _) => SetAutostart(((ToolStripMenuItem)s!).Checked);
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripSeparator());

        // Populated on Opening: accounts are added and renamed while the app
        // runs, so a list built once at construction would go stale.
        var accounts = new ToolStripMenuItem("Accounts");
        menu.Items.Add(accounts);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Kite Glance", null, (_, _) => Application.Current.Shutdown());

        // Reflect live state each time it opens, rather than trusting a snapshot
        // taken once at construction.
        menu.Opening += (_, _) =>
        {
            var mode = _widget.Dispatcher.Invoke(() => _widget.Pin);

            pinDesktop.Checked = mode == PinMode.Desktop;
            pinTop.Checked = mode == PinMode.AlwaysOnTop;
            pinFree.Checked = mode == PinMode.Normal;

            startup.Checked = IsAutostartEnabled();

            PopulateAccounts(accounts);
        };

        return menu;
    }

    /// <summary>
    /// Rebuilds the Accounts submenu: one checkable entry per stored account,
    /// then "Add account". A single-account install still sees the menu, so
    /// adding a second one is discoverable.
    /// </summary>
    private void PopulateAccounts(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();

        var view = _widget.Dispatcher.Invoke(() => _widget.AccountsSnapshot());
        var accounts = view.Accounts;

        foreach (var account in accounts)
        {
            var item = new ToolStripMenuItem(account.Name)
            {
                Checked = account.Id == view.ActiveId,
                Tag = account.Id
            };
            item.Click += async (s, _) =>
            {
                var target = (string?)((ToolStripMenuItem)s!).Tag;
                await _widget.Dispatcher.InvokeAsync(async () =>
                    await _widget.SwitchAccountAsync(target));
            };
            parent.DropDownItems.Add(item);
        }

        if (accounts.Count > 0)
        {
            parent.DropDownItems.Add(new ToolStripSeparator());
        }

        parent.DropDownItems.Add("Add account...", null, async (_, _) =>
            await _widget.Dispatcher.InvokeAsync(async () =>
                await _widget.AddAccountAsync()));
    }

    private void Reveal()
    {
        _widget.Show();
        _widget.WindowState = WindowState.Normal;
        _widget.Activate();
    }

    // ==== Start with Windows ============================================

    private static string? ExePath => Environment.ProcessPath;

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled && ExePath is { } path)
                key.SetValue(AppName, $"\"{path}\"");
            else
                key.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"autostart: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _tray.Visible = false;   // otherwise a ghost icon lingers until hover
        _tray.Dispose();
    }
}
