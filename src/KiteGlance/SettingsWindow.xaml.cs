using System.Windows;
using System.Windows.Input;
using KiteGlance.Interop;
using KiteGlance.Services;
using KiteGlance.State;

namespace KiteGlance;

public partial class SettingsWindow : Window
{
    private readonly CredentialVault _vault = new();

    /// <summary>
    /// Auto-refresh cadence in minutes, 0 for manual-only. Seeded from the
    /// caller's state and read back after the dialog returns true.
    /// </summary>
    public int RefreshIntervalMinutes { get; private set; }

    /// <summary>Which palette to paint with. Read back after the dialog
    /// returns true, the same way the interval is.</summary>
    public ThemeMode Theme { get; private set; }

    public SettingsWindow() : this(5, ThemeMode.System) { }

    public SettingsWindow(int refreshIntervalMinutes, ThemeMode theme)
    {
        InitializeComponent();

        RefreshIntervalMinutes = refreshIntervalMinutes;
        Theme = theme;

        DragBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        CloseButton.Click += (_, _) => { DialogResult = false; Close(); };
        SaveButton.Click += (_, _) => Save();

        // The API key is OK to show back -- it is already visible in the
        // browser's address bar during the Kite redirect. The API secret is
        // not: it is the second half of the credentials and never leaves the
        // vault unless the user is actively editing it. Decrypting it on
        // every open would put cleartext on screen for anyone walking past.
        // Leave the field empty and treat empty-on-save as "keep existing".
        var (key, _) = _vault.GetCredentials();
        KeyBox.Text = key ?? "";
        SecretBox.Password = "";

        SelectInterval(refreshIntervalMinutes);
        ThemeBox.SelectedValue = theme.ToString();
    }

    /// <summary>
    /// Picks the item matching the stored interval. An interval that is not one
    /// of the offered choices (hand-edited state.json) falls back to the 5-minute
    /// default rather than leaving the box blank.
    /// </summary>
    private void SelectInterval(int minutes)
    {
        foreach (var obj in IntervalBox.Items)
        {
            if (obj is System.Windows.Controls.ComboBoxItem item &&
                item.Tag is string tag &&
                int.TryParse(tag, out var value) &&
                value == minutes)
            {
                IntervalBox.SelectedItem = item;
                return;
            }
        }

        IntervalBox.SelectedValue = "5";
    }

    private int SelectedInterval()
    {
        if (IntervalBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out var value))
        {
            return value;
        }
        return RefreshIntervalMinutes;
    }

    private ThemeMode SelectedTheme()
    {
        if (ThemeBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is string tag &&
            Enum.TryParse<ThemeMode>(tag, out var mode))
        {
            return mode;
        }
        return Theme;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (!WindowMaterial.Apply(this))
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0E, 0x0F, 0x11));
        }
    }

    private void Save()
    {
        var key = KeyBox.Text.Trim();
        var secretInput = SecretBox.Password;

        if (string.IsNullOrEmpty(key))
        {
            ErrorText.Text = "The API key is required.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // The secret field starts empty on purpose: we never decrypt it just
        // to show it back. An empty field means "keep what is already stored";
        // a non-empty field means "replace it". Without this branch, the first
        // Save after a successful setup would silently wipe the secret.
        string secret;
        if (string.IsNullOrEmpty(secretInput))
        {
            var (_, existing) = _vault.GetCredentials();
            if (string.IsNullOrEmpty(existing))
            {
                ErrorText.Text = "The API secret is required.";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            secret = existing;
        }
        else
        {
            secret = secretInput.Trim();
        }

        try
        {
            _vault.SaveCredentials(key, secret);
            RefreshIntervalMinutes = SelectedInterval();
            Theme = SelectedTheme();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
