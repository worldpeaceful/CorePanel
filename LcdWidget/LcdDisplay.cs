using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LcdWidget;

public sealed class LcdDisplay : FrameworkElement
{
    private string text = " --";
    public string Text { get => text; set { text = value; InvalidateVisual(); } }
    public static string Format(double? value) => value.HasValue && double.IsFinite(value.Value)
        ? Math.Clamp((int)Math.Round(value.Value, MidpointRounding.AwayFromZero), 0, 999).ToString(CultureInfo.InvariantCulture).PadLeft(3)
        : " --";
    private static readonly Brush On = Frozen("#BEC6C4"), Off = Frozen("#1B201F");
    // a,b,c,d,e,f,g clockwise from top; beveled polygons, no font dependency.
    private static readonly string[] Masks = { "abcdef", "bc", "abdeg", "abcdg", "bcfg", "acdfg", "acdefg", "abc", "abcdefg", "abcdfg" };
    private static readonly Geometry[] Segments = {
        Polygon(6,0, 30,0, 34,4, 29,8, 8,8, 3,4),
        Polygon(34,6, 37,9, 35,27, 30,31, 27,27, 29,11),
        Polygon(30,34, 34,37, 32,55, 27,59, 24,54, 26,39),
        Polygon(5,57, 24,57, 28,61, 23,65, 2,65, 0,61),
        Polygon(1,35, 5,32, 9,37, 7,53, 2,58, 0,54),
        Polygon(5,7, 9,10, 7,27, 3,31, 0,27, 2,11),
        Polygon(7,28, 26,28, 30,32, 25,36, 6,36, 2,32)
    };
    public static Brush Frozen(string color) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; b.Freeze(); return b; }
    private static Geometry Polygon(params double[] xy)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) {
            c.BeginFigure(new Point(xy[0], xy[1]), true, true);
            for (int i = 2; i < xy.Length; i += 2) c.LineTo(new Point(xy[i], xy[i + 1]), true, false);
        }
        g.Freeze(); return g;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double total = 0;
        foreach (char c in text) total += c == '.' ? 12 : 43;
        double scale = Math.Min(ActualWidth / (total - 6), ActualHeight / 65.0);
        dc.PushTransform(new ScaleTransform(scale, scale));
        double offset = 0;
        for (int i = 0; i < text.Length; i++) {
            var ch = text[i];
            if (ch == '.') { dc.DrawEllipse(On, null, new Point(offset + 3, 61), 3, 3); offset += 12; continue; }
            var mask = ch is >= '0' and <= '9' ? Masks[ch - '0'] : ch == '-' ? "g" : "";
            dc.PushTransform(new TranslateTransform(offset, 0));
            for (int j = 0; j < 7; j++) dc.DrawGeometry(mask.Contains((char)('a' + j)) ? On : Off, null, Segments[j]);
            dc.Pop();
            offset += 43;
        }
        dc.Pop();
    }
}
