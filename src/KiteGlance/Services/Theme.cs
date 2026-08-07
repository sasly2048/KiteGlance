using System.Windows;
using KiteGlance.State;

// UseWindowsForms + UseWPF puts both Applications in scope. This is WPF code.
using Application = System.Windows.Application;

namespace KiteGlance.Services;

/// <summary>
/// Swaps the palette dictionary underneath the running app.
///
/// Only the palette is themed. Themes/Dark.xaml and Themes/Light.xaml define
/// the same keys, and every usage site is a DynamicResource, so replacing the
/// merged dictionary repaints the window without rebuilding it -- no restart,
/// no flicker, and no second copy of any style.
///
/// The two files must stay key-for-key identical. A key present in one and
/// missing from the other resolves to nothing after a switch: silently
/// transparent text, and no error to tell you why.
/// </summary>
public static class Theme
{
    private const string DarkPath = "Themes/Dark.xaml";
    private const string LightPath = "Themes/Light.xaml";

    /// <summary>The theme actually showing, once System has been resolved.</summary>
    public static bool IsLight { get; private set; }

    public static void Apply(ThemeMode mode)
    {
        var light = mode switch
        {
            ThemeMode.Light => true,
            ThemeMode.Dark => false,
            _ => WindowsPrefersLight()
        };

        IsLight = light;

        var app = Application.Current;
        if (app is null) return;

        var dictionaries = app.Resources.MergedDictionaries;

        var next = new ResourceDictionary
        {
            Source = new Uri(light ? LightPath : DarkPath, UriKind.Relative)
        };

        // The palette is merged first, ahead of anything else that might be
        // added later, so replacing index 0 replaces exactly the palette.
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = next;
        }
        else
        {
            dictionaries.Add(next);
        }
    }

    /// <summary>
    /// Windows' app theme, from the same registry value the Settings app
    /// writes. AppsUseLightTheme is 0 for dark and 1 for light; a missing value
    /// means an edition or build that never had the setting, where dark is the
    /// safer guess for a widget designed dark-first.
    /// </summary>
    public static bool WindowsPrefersLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }
}
