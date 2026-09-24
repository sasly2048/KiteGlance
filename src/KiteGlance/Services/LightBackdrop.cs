using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

// The project references both WinForms (System.Drawing) and WPF
// (System.Windows.Media); alias the WPF media types so Brush/Color are
// unambiguous, matching the aliasing pattern used elsewhere in the app.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace KiteGlance.Services;

/// <summary>
/// Runtime-generated light-mode backdrops.
///
/// The eight built-in dark backdrops are pre-rendered mesh-gradient PNGs tuned
/// for a dark surface; under the light theme they used to be shown as-is, so a
/// dark image sat under dark text. Rather than ship a second set of PNGs (a
/// raster pipeline the app deliberately avoids), the light variants are drawn
/// here as computed <see cref="Brush"/>es — two soft radial glows over a pale
/// base, one brush per phase, in the same day order as
/// <see cref="BackdropService.Set"/>. Every brush is frozen so it costs nothing
/// to reuse and is safe to hand to the render thread.
///
/// Pure WPF media construction, no file IO and no external dependency, so the
/// phase palettes can be unit-tested for luminance the way the P&amp;L math is.
/// </summary>
public static class LightBackdrop
{
    /// <summary>A light phase: a pale base plus two tinted radial glows whose
    /// hues echo the time of day (cool morning, warm midday, mauve dusk).</summary>
    private readonly record struct Phase(Color Base, Color GlowA, Color GlowB);

    // All bases sit high on the luminance scale (>= ~0.90) so dark foreground
    // text keeps WCAG-comfortable contrast; the glows are gentle so they read
    // as atmosphere, not decoration that fights the numbers.
    private static readonly IReadOnlyDictionary<string, Phase> Phases =
        new Dictionary<string, Phase>
        {
            [BackdropService.Dawn]     = new(Rgb(0xF3, 0xF1, 0xF6), Rgb(0xE7, 0xDD, 0xF0), Rgb(0xFB, 0xEC, 0xE4)),
            [BackdropService.Sunrise]  = new(Rgb(0xFB, 0xF3, 0xEC), Rgb(0xFD, 0xE7, 0xD4), Rgb(0xF7, 0xE9, 0xF2)),
            [BackdropService.Day]      = new(0xF6.Gray(), Rgb(0xE6, 0xEF, 0xFA), Rgb(0xFA, 0xF3, 0xE6)),
            [BackdropService.Noon]     = new(Rgb(0xF7, 0xF9, 0xFB), Rgb(0xDE, 0xEC, 0xFB), Rgb(0xEF, 0xF7, 0xEC)),
            [BackdropService.Dusk]     = new(Rgb(0xF6, 0xF1, 0xF3), Rgb(0xFB, 0xE3, 0xDE), Rgb(0xEA, 0xE1, 0xF3)),
            [BackdropService.Evening]  = new(Rgb(0xF2, 0xF0, 0xF5), Rgb(0xF0, 0xDF, 0xEC), Rgb(0xDE, 0xE4, 0xF4)),
            [BackdropService.Night]    = new(Rgb(0xEF, 0xF1, 0xF6), Rgb(0xDD, 0xE2, 0xF1), Rgb(0xE7, 0xE2, 0xF0)),
            [BackdropService.Midnight] = new(Rgb(0xEC, 0xEE, 0xF4), Rgb(0xD8, 0xDE, 0xEE), Rgb(0xE3, 0xDF, 0xEE)),
        };

    /// <summary>
    /// A frozen light backdrop brush for a built-in phase path, or null if the
    /// path is not a known built-in (custom images are never answered here).
    /// </summary>
    public static Brush? For(string phasePath)
    {
        if (!Phases.TryGetValue(phasePath, out var p)) return null;
        return Build(p);
    }

    private static Brush Build(Phase p)
    {
        // A pale flat base…
        var baseRect = new GeometryDrawing
        {
            Brush = new SolidColorBrush(p.Base),
            Geometry = new RectangleGeometry(new Rect(0, 0, 1, 1)),
        };

        // …with two soft radial glows offset to opposite corners so the light
        // reads as a gradient mesh rather than a single spotlight.
        var glowTop = new GeometryDrawing
        {
            Brush = Glow(p.GlowA, new Point(0.25, 0.15)),
            Geometry = new RectangleGeometry(new Rect(0, 0, 1, 1)),
        };
        var glowBottom = new GeometryDrawing
        {
            Brush = Glow(p.GlowB, new Point(0.85, 0.9)),
            Geometry = new RectangleGeometry(new Rect(0, 0, 1, 1)),
        };

        var group = new DrawingGroup();
        group.Children.Add(baseRect);
        group.Children.Add(glowTop);
        group.Children.Add(glowBottom);

        var brush = new DrawingBrush(group)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
        };
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush Glow(Color tint, Point center)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = center,
            Center = center,
            RadiusX = 0.9,
            RadiusY = 0.9,
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops = new GradientStopCollection
            {
                new GradientStop(WithAlpha(tint, 0xFF), 0.0),
                new GradientStop(WithAlpha(tint, 0x00), 1.0),
            },
        };
        brush.Freeze();
        return brush;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

file static class GrayExt
{
    /// <summary>A neutral gray base from a single channel value.</summary>
    public static System.Windows.Media.Color Gray(this int v) =>
        System.Windows.Media.Color.FromRgb((byte)v, (byte)v, (byte)v);
}
