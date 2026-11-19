using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LcdWidget;

public sealed record Skin(string Id, string Name, string Resource, string Unit, string Outline)
{
    public BitmapSource LoadImage()
    {
        var image = new BitmapImage(new Uri("pack://application:,,,/" + Resource));
        image.Freeze(); return image;
    }
    public Geometry CreateClip()
    {
        var geometry = Geometry.Parse(Outline).Clone();
        geometry.Transform = new ScaleTransform(480.0 / 1254, 480.0 / 1254);
        geometry.Freeze(); return geometry;
    }
}

public static class Skins
{
    // Vector silhouettes in the original 1254-square design coordinates.
    // The silhouette hides the exterior margins while keeping the inner panel opaque.
    public static IReadOnlyList<Skin> All { get; } = new[] {
        new Skin("red", "02 · 赤红（原版）", "Assets/Skins/red.png", "02",
            "M 3,26 L 25,3 114,3 128,14 206,7 231,14 279,14 307,24 423,24 438,28 944,28 961,17 1062,17 1095,4 1232,4 1250,23 1250,1227 1232,1247 27,1247 5,1228 Z"),
        new Skin("blue", "00 · 冰蓝", "Assets/Skins/blue.png", "00",
            "M 9,25 L 26,8 143,8 153,11 265,11 283,17 361,16 382,29 952,29 971,16 1056,17 1091,8 1223,8 1246,30 1246,1225 1227,1244 34,1244 8,1221 Z"),
        new Skin("purple", "01 · 紫绿", "Assets/Skins/purple.png", "01",
            "M 7,26 L 26,8 113,8 129,17 209,9 228,22 263,22 273,11 306,11 332,26 420,26 433,30 915,28 954,15 1054,16 1085,5 1231,5 1247,22 1247,1227 1227,1247 29,1247 5,1226 Z"),
        new Skin("pink", "08 · 樱粉", "Assets/Skins/pink.png", "08",
            "M 6,23 L 24,5 115,5 128,4 207,4 228,23 265,23 273,14 306,14 333,26 429,26 440,29 913,26 949,15 1059,15 1085,3 1232,3 1248,23 1248,1224 1227,1247 29,1247 5,1226 Z")
    };
    public static Skin Resolve(string? id) => All.FirstOrDefault(s => s.Id == id) ?? All[0];
}
