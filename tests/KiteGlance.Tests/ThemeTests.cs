using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// The theme dictionaries are read as text rather than loaded as WPF resources:
/// this assembly is plain net8.0 and cannot reference PresentationFramework.
/// That is enough to protect the invariant that actually bites.
/// </summary>
public class ThemeTests
{
    private static string ThemeDir()
    {
        // Walk up from the test binary to the repository root.
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "KiteGlance", "Themes");
            if (Directory.Exists(candidate)) return candidate;

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate src/KiteGlance/Themes");
    }

    private static List<string> KeysIn(string file)
    {
        var text = File.ReadAllText(Path.Combine(ThemeDir(), file));
        var keys = new List<string>();

        foreach (Match m in Regex.Matches(text, "x:Key=\"([^\"]+)\""))
        {
            keys.Add(m.Groups[1].Value);
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>
    /// Every key must exist in both files.
    ///
    /// This is the failure mode worth a test: a key defined in one theme and
    /// missing from the other resolves to nothing after a switch. WPF raises no
    /// error for an unresolved DynamicResource -- the brush is simply absent, so
    /// the text turns invisible and nothing says why.
    /// </summary>
    [Fact]
    public void Both_themes_define_exactly_the_same_keys()
    {
        var dark = KeysIn("Dark.xaml");
        var light = KeysIn("Light.xaml");

        Assert.NotEmpty(dark);
        Assert.Equal(dark, light);
    }

    /// <summary>
    /// Guards against a palette key being added to App.xaml (where it would
    /// resolve identically in both themes, looking fine) instead of to the two
    /// theme files, which is how a theme-blind colour creeps back in.
    /// </summary>
    [Fact]
    public void The_palette_keys_are_all_present()
    {
        var expected = new[]
        {
            "Green", "Red", "Blue", "Amber",
            "Label", "Label2", "Label3", "Label4",
            "Fill", "Separator", "Track",
            "Surface", "BackdropTint", "Scrim", "TopSheen", "Grain",
        };

        var dark = KeysIn("Dark.xaml");

        foreach (var key in expected) Assert.Contains(key, dark);
    }
}
