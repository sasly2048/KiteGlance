using System.Windows;
using System.Windows.Input;
using KiteGlance.Interop;
using KiteGlance.Services;

namespace KiteGlance;

public partial class SettingsWindow : Window
{
    private readonly CredentialVault _vault = new();

    /// <summary>
    /// Auto-refresh cadence in minutes, 0 for manual-only. Seeded from the
    /// caller's state and read back after the dialog returns true.
    /// </summary>
    public int RefreshIntervalMinutes { get; private set; }

    public SettingsWindow() : this(5) { }

    public SettingsWindow(int refreshIntervalMinutes)
    {
        InitializeComponent();

        RefreshIntervalMinutes = refreshIntervalMinutes;

        DragBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        CloseButton.Click += (_, _) => { DialogResult = false; Close(); };
        SaveButton.Click += (_, _) => Save();

        var (key, secret) = _vault.GetCredentials();
        KeyBox.Text = key ?? "";
        SecretBox.Password = secret ?? "";

        SelectInterval(refreshIntervalMinutes);
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
        var secret = SecretBox.Password.Trim();

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
        {
            ErrorText.Text = "Both the API key and secret are required.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            _vault.SaveCredentials(key, secret);
            RefreshIntervalMinutes = SelectedInterval();
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
