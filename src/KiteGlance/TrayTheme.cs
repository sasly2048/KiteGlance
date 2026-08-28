using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// UseWindowsForms + UseWPF means BOTH worlds are in scope via implicit usings,
// and they collide on a dozen names. This file draws with GDI+, so every
// ambiguous name is pinned to System.Drawing. Without these, the compiler
// reports six CS0104s and the cause is entirely non-obvious.
using Brush = System.Drawing.Brush;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using Pen = System.Drawing.Pen;
using Point = System.Drawing.Point;
using PointF = System.Drawing.PointF;
using Rectangle = System.Drawing.Rectangle;
using SolidBrush = System.Drawing.SolidBrush;

namespace KiteGlance;

/// <summary>
/// WinForms' ToolStrip renders a grey slab with a gradient margin strip, and no
/// amount of colour-table fiddling gets it to stop. So take the pen: draw the
/// background, the border, the highlight, and the check mark ourselves.
///
/// This is the difference between a widget that has a dark menu and a widget
/// where nothing gives away that two UI frameworks are in the room.
/// </summary>
internal sealed class TrayTheme : ToolStripRenderer
{
    // This menu is drawn with GDI+, not WPF, so it cannot read the WPF palette
    // and keeps its own pair of themes. Properties rather than fields: the tray
    // menu is rebuilt on every open, and a field would hold whichever theme was
    // current when the class was first touched.
    private static bool Light => Services.Theme.IsLight;

    private static Color Surface => Light
        ? Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0xF2, 0x1A, 0x1C, 0x20);

    private static Color Edge => Light
        ? Color.FromArgb(0xD8, 0xD8, 0xDC)
        : Color.FromArgb(0x2A, 0x2F, 0x36);

    private static Color Hover => Light
        ? Color.FromArgb(0xEC, 0xEC, 0xF0)
        : Color.FromArgb(0x26, 0x2B, 0x32);

    private static Color Text => Light
        ? Color.FromArgb(0x1A, 0x1C, 0x20)
        : Color.FromArgb(0xF2, 0xF4, 0xF5);

    private static Color Dim => Light
        ? Color.FromArgb(0x8A, 0x8A, 0x8E)
        : Color.FromArgb(0x8A, 0x90, 0x99);

    private static Color Rule => Light
        ? Color.FromArgb(0xE3, 0xE3, 0xE7)
        : Color.FromArgb(0x25, 0x2A, 0x30);

    private static Color Accent => Light
        ? Color.FromArgb(0x00, 0x7A, 0xFF)
        : Color.FromArgb(0x0A, 0x84, 0xFF);

    // Constructed once and reused for every item's text render. A new Font
    // per call (the old code) was a hot-path allocation, since each menu
    // item re-renders on hover and on theme switch.
    private static readonly Font ItemFont = new("Segoe UI", 9.25f, FontStyle.Regular);

    public static void Apply(ToolStripDropDownMenu menu)
    {
        menu.Renderer = new TrayTheme();
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        menu.ShowImageMargin = false;
        menu.Padding = new Padding(0, 5, 0, 5);
        menu.DropShadowEnabled = true;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new Rectangle(Point.Empty, e.AffectedBounds.Size);
        r.Width -= 1;
        r.Height -= 1;

        using var path = Rounded(r, 9);
        using var fill = new SolidBrush(Surface);
        e.Graphics.FillPath(fill, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new Rectangle(Point.Empty, e.AffectedBounds.Size);
        r.Width -= 1;
        r.Height -= 1;

        using var path = Rounded(r, 9);
        using var pen = new Pen(Edge);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new Rectangle(4, 0, e.Item.Width - 8, e.Item.Height);
        using var path = Rounded(r, 6);
        using var fill = new SolidBrush(Hover);
        e.Graphics.FillPath(fill, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Dim;
        e.TextFont = ItemFont;

        // Leave room for the check column so labels don't jump when toggled.
        e.TextRectangle = new Rectangle(
            e.TextRectangle.X + 22, e.TextRectangle.Y,
            e.TextRectangle.Width, e.TextRectangle.Height);

        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var cx = 14;
        var cy = e.Item.Height / 2;

        using var pen = new Pen(Accent, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(cx - 4, cy),
            new PointF(cx - 1, cy + 3),
            new PointF(cx + 5, cy - 4)
        });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Rule);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }
}
