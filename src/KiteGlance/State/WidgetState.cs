using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiteGlance.State;

public enum PinMode
{
    /// <summary>Ordinary window. Falls behind whatever you click next.</summary>
    Normal,

    /// <summary>Floats above every app. Useful while actively trading.</summary>
    AlwaysOnTop,

    /// <summary>
    /// Pinned under every app, over the wallpaper: bottom-most z-order,
    /// out of Alt+Tab. What a desktop widget should be. The default.
    /// </summary>
    Desktop
}

public enum ThemeMode
{
    /// <summary>Follows the Windows app theme, and changes with it. The default.</summary>
    System,

    Dark,
    Light
}

public enum BackdropMode
{
    /// <summary>Dawn, day, dusk, night -- follows the clock. The default.</summary>
    TimeOfDay,

    /// <summary>Cycles through the whole set every few hours.</summary>
    Rotate,

    /// <summary>One fixed backdrop (the day graphite).</summary>
    Static,

    /// <summary>An image of the user's choosing.</summary>
    Custom
}

/// <summary>
/// Where you put the widget, how you left it, and how you like it pinned.
///
/// A widget that forgets its position after every restart is not a widget --
/// it's a window that happens to be small. Nothing else on the desktop behaves
/// that way, and the omission does more damage to the sense of "first-party"
/// than any amount of gradient work can repair.
/// </summary>
public sealed class WidgetState
{
    [JsonIgnore]
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KiteGlance", "state.json");

    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Expanded { get; set; }
    public string Tab { get; set; } = "stocks";
    public PinMode Pin { get; set; } = PinMode.Desktop;
    public BackdropMode Backdrop { get; set; } = BackdropMode.TimeOfDay;

    /// <summary>
    /// Which palette to paint with. Defaults to following Windows, so a user
    /// who has never opened Settings gets the theme they already asked the OS
    /// for rather than whichever one we happened to build first.
    ///
    /// The enum is declared here, not beside the Theme service: this file is
    /// compiled into the plain-net8.0 test assembly, and anything reaching into
    /// System.Windows from here would drag WPF onto it.
    /// </summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Absolute path of the user-chosen image (Custom mode only).
    /// Points inside %APPDATA%\KiteGlance, where we copy the picked file, so
    /// the backdrop survives the original being moved or deleted.</summary>
    public string? CustomBackdropPath { get; set; }

    /// <summary>
    /// Known accounts, in the order they were added. Empty means the original
    /// single-account layout: credentials at the root of %APPDATA%\KiteGlance
    /// and no account switcher in the menu. Adding a second account migrates
    /// the existing vault into the list rather than moving any files.
    /// </summary>
    public List<AccountRef> Accounts { get; set; } = new();

    /// <summary>Id of the account currently displayed, or null for the legacy
    /// single-account vault.</summary>
    public string? ActiveAccountId { get; set; }

    /// <summary>
    /// Minutes between automatic refreshes during market hours. 0 disables
    /// auto-refresh entirely, leaving manual R / tray refresh. Clamped on read,
    /// so a hand-edited file cannot set a value that hammers the Kite API.
    /// </summary>
    public int RefreshIntervalMinutes { get; set; } = 5;

    /// <summary>Smallest interval we will honour, to stay well inside Kite's
    /// rate limits even if the file says otherwise.</summary>
    public const int MinRefreshIntervalMinutes = 1;
    public const int MaxRefreshIntervalMinutes = 60;

    /// <summary>The stored interval, bounded. 0 stays 0 (disabled).</summary>
    [JsonIgnore]
    public int EffectiveRefreshIntervalMinutes =>
        RefreshIntervalMinutes <= 0
            ? 0
            : Math.Clamp(RefreshIntervalMinutes, MinRefreshIntervalMinutes, MaxRefreshIntervalMinutes);

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static WidgetState Load()
    {
        try
        {
            if (!File.Exists(Path_)) return new WidgetState();

            var json = File.ReadAllText(Path_);
            return JsonSerializer.Deserialize<WidgetState>(json, Opts) ?? new WidgetState();
        }
        catch
        {
            return new WidgetState();
        }
    }

    public void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path_)!;
            Directory.CreateDirectory(dir);

            // Write-then-replace. A direct WriteAllText that is interrupted --
            // power loss, or the process being killed mid-write, and Save runs
            // on every window move -- leaves truncated JSON, which Load then
            // discards, silently resetting position, tab, pin mode and
            // backdrop. Replacing an already-complete temp file is atomic.
            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));

            if (File.Exists(Path_))
            {
                File.Replace(tmp, Path_, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, Path_);
            }
        }
        catch
        {
            // Losing window position is not worth crashing over.
        }
    }

    /// <summary>
    /// Forces the saved position back onto a screen that currently exists.
    /// Unplugging the monitor the widget was parked on would otherwise restore
    /// it to coordinates no display covers -- the app runs, is invisible, and
    /// offers no way back. Also rejects NaN/Infinity from a hand-edited file,
    /// which would throw inside WPF layout.
    /// </summary>
    public void ClampToVisibleArea(double virtualLeft, double virtualTop,
                                   double virtualWidth, double virtualHeight,
                                   double windowWidth, double windowHeight)
    {
        if (Left is null || Top is null) return;

        if (!IsUsable(Left.Value) || !IsUsable(Top.Value))
        {
            Left = null;
            Top = null;
            return;
        }

        // Keep at least a strip of the widget on screen so it can be grabbed.
        const double margin = 48;
        var maxLeft = virtualLeft + virtualWidth - margin;
        var maxTop = virtualTop + virtualHeight - margin;
        var minLeft = virtualLeft - (windowWidth - margin);
        var minTop = virtualTop;

        Left = Math.Clamp(Left.Value, minLeft, maxLeft);
        Top = Math.Clamp(Top.Value, minTop, maxTop);
    }

    private static bool IsUsable(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    // -- Accounts --------------------------------------------------------

    /// <summary>True once more than one account exists, which is when the
    /// switcher is worth showing at all.</summary>
    [JsonIgnore]
    public bool HasMultipleAccounts => Accounts.Count > 1;

    /// <summary>
    /// The account to read credentials for, or null for the original vault at
    /// the root of %APPDATA%\KiteGlance.
    ///
    /// Null is returned whenever the resolved account has no vault directory of
    /// its own. That case is not hypothetical: signing in on a single-account
    /// install records the Kite user id here purely to label the account, and
    /// treating a non-empty list as proof that account folders exist pointed
    /// the app at an empty directory. It then read no API key, built a login
    /// URL with an empty api_key, and Kite answered with an error page -- the
    /// credentials were never lost, just no longer being looked at.
    ///
    /// Also covers an account folder deleted by hand.
    /// </summary>
    [JsonIgnore]
    public string? ResolvedAccountId
    {
        get
        {
            if (Accounts.Count == 0) return null;

            var chosen = Accounts[0].Id;

            foreach (var a in Accounts)
            {
                if (a.Id == ActiveAccountId)
                {
                    chosen = a.Id;
                    break;
                }
            }

            return HasVault(chosen) ? chosen : null;
        }
    }

    /// <summary>
    /// True when this account has its own credential file. Injectable so the
    /// rule can be tested without writing to the real %APPDATA%.
    /// </summary>
    [JsonIgnore]
    public Func<string, bool>? VaultProbe { get; set; }

    private bool HasVault(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return false;
        if (VaultProbe is not null) return VaultProbe(accountId);

        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KiteGlance", "accounts", Sanitize(accountId), "vault.bin");

            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Mirrors CredentialVault's own sanitizer. Duplicated rather than shared
    /// because this file is compiled into the plain-net8.0 test assembly and
    /// must not depend on the service layer.
    /// </summary>
    private static string Sanitize(string accountId)
    {
        var sb = new System.Text.StringBuilder(accountId.Length);
        foreach (var c in accountId)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return sb.Length == 0 ? "default" : sb.ToString();
    }

    /// <summary>
    /// Records an account, or refreshes the display name of one already known.
    /// Returns true when the list changed and the caller should save.
    /// </summary>
    public bool UpsertAccount(string id, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        foreach (var existing in Accounts)
        {
            if (existing.Id != id) continue;

            if (!string.IsNullOrWhiteSpace(displayName) && existing.Name != displayName)
            {
                existing.Name = displayName;
                return true;
            }
            return false;
        }

        Accounts.Add(new AccountRef { Id = id, Name = displayName ?? id });
        return true;
    }
}

/// <summary>
/// The account list handed to the tray menu: a copy of the stored accounts
/// plus whichever is active. A named type rather than a tuple so the
/// pre-flight validator can see the method's return type.
/// </summary>
public sealed class AccountsView
{
    public AccountsView(List<AccountRef> accounts, string? activeId)
    {
        Accounts = accounts;
        ActiveId = activeId;
    }

    public List<AccountRef> Accounts { get; }
    public string? ActiveId { get; }
}

/// <summary>
/// A Zerodha login the widget knows about. Only the id and a display name are
/// stored here -- credentials live in that account's own encrypted vault, never
/// in this plain-JSON file.
/// </summary>
public sealed class AccountRef
{
    /// <summary>Kite user id, e.g. "AB1234". Also the vault folder name.</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable label for the menu; defaults to the id.</summary>
    public string Name { get; set; } = "";
}
