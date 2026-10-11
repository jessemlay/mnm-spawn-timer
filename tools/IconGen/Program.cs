using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Draws the Spawn Timer icon (gold hourglass on a crimson medallion) at every Windows icon size and packs an .ico.
// Usage: dotnet run --project tools/IconGen -- Assets/SpawnTimer.ico <preview folder>
internal static class Program
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    [STAThread]
    private static void Main(string[] args)
    {
        var outIco = args[0];
        var previewDir = args[1];
        Directory.CreateDirectory(previewDir);

        var images = new List<(int Size, byte[] Png)>();
        foreach (var size in Sizes)
        {
            var png = Render(size);
            images.Add((size, png));
            File.WriteAllBytes(Path.Combine(previewDir, $"icon_{size}.png"), png);
        }
        WriteIco(outIco, images);
        Console.WriteLine($"wrote {outIco} ({new FileInfo(outIco).Length} bytes, {images.Count} sizes)");
        WriteContactSheet(Path.Combine(previewDir, "contact_sheet.png"), images);
    }

    /// <summary>Small sizes blown up 8x (nearest neighbour) on dark and light backgrounds, like a taskbar/tray.</summary>
    private static void WriteContactSheet(string path, List<(int Size, byte[] Png)> images)
    {
        var picks = new[] { 16, 20, 24, 32, 48 };
        const int scale = 8, pad = 12;
        var cell = 48 * scale + pad;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, picks.Length * cell, cell));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new Rect(0, cell, picks.Length * cell, cell));
            for (var i = 0; i < picks.Length; i++)
            {
                var png = images.Find(x => x.Size == picks[i]).Png;
                var src = BitmapFrame.Create(new MemoryStream(png));
                foreach (var row in new[] { 0, 1 })
                {
                    var rect = new Rect(i * cell + pad / 2, row * cell + pad / 2, picks[i] * scale, picks[i] * scale);
                    var group = new DrawingGroup();
                    RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
                    group.Children.Add(new ImageDrawing(src, rect));
                    dc.DrawDrawing(group);
                }
            }
        }
        var bmp = new RenderTargetBitmap(picks.Length * cell, cell * 2, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static byte[] Render(int size)
    {
        // Design space is 256x256; small sizes get thicker strokes and fewer details so they stay legible.
        var thick = size <= 24 ? 2.4 : size <= 48 ? 1.5 : 1.0;
        var detailed = size >= 48;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));

            var gold = new LinearGradientBrush(Color.FromRgb(0xFF, 0xE0, 0x8A), Color.FromRgb(0xB0, 0x7A, 0x1E), 90);
            var goldDark = new SolidColorBrush(Color.FromRgb(0x6E, 0x48, 0x10));
            var sand = new LinearGradientBrush(Color.FromRgb(0xFF, 0xD8, 0x7A), Color.FromRgb(0xE8, 0x7A, 0x14), 90);

            // Medallion: crimson radial fill, dark edge, gold rim.
            var face = new RadialGradientBrush(Color.FromRgb(0x8E, 0x1C, 0x22), Color.FromRgb(0x2A, 0x05, 0x08))
            {
                GradientOrigin = new Point(0.38, 0.32),
                Center = new Point(0.45, 0.42),
                RadiusX = 0.65,
                RadiusY = 0.65,
            };
            // Thinner rim at small sizes leaves more room for the hourglass.
            var faceR = size <= 32 ? 114 : 108;
            dc.DrawEllipse(goldDark, null, new Point(128, 128), 126, 126);
            dc.DrawEllipse(gold, null, new Point(128, 128), 121, 121);
            dc.DrawEllipse(face, new Pen(goldDark, 3), new Point(128, 128), faceR, faceR);

            // Rune ticks around the face, like a clock (only where there's room to see them).
            if (detailed)
            {
                for (var i = 0; i < 12; i++)
                {
                    var a = i * Math.PI / 6;
                    var major = i % 3 == 0;
                    var r1 = major ? 88 : 94;
                    var p1 = new Point(128 + Math.Sin(a) * r1, 128 - Math.Cos(a) * r1);
                    var p2 = new Point(128 + Math.Sin(a) * 102, 128 - Math.Cos(a) * 102);
                    dc.DrawLine(new Pen(gold, major ? 6 : 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, p1, p2);
                }
            }

            // Small sizes: enlarge the hourglass so it fills the face.
            var small = size <= 32;
            if (small) dc.PushTransform(new ScaleTransform(1.22, 1.22, 128, 128));

            // Hourglass glass (smoky at large sizes, dark at small ones for contrast), sand, and gold frame.
            var glass = Geometry.Parse(
                "M92,68 L164,68 C164,100 140,112 133,128 C140,144 164,156 164,188 L92,188 C92,156 116,144 123,128 C116,112 92,100 92,68 Z");
            var glassFill = small
                ? new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x03, 0x05))
                : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xF0, 0xD0));
            dc.DrawGeometry(glassFill, new Pen(gold, 5 * thick), glass);

            var topSand = Geometry.Parse("M104,96 L152,96 C148,110 136,117 130,126 L126,126 C120,117 108,110 104,96 Z");
            var bottomSand = Geometry.Parse("M100,184 C106,166 118,158 128,156 C138,158 150,166 156,184 Z");
            dc.DrawGeometry(sand, null, topSand);
            dc.DrawGeometry(sand, null, bottomSand);
            dc.DrawLine(new Pen(sand, 4 * Math.Min(thick, 1.6)), new Point(128, 124), new Point(128, 168));

            // Frame: caps and side posts.
            var capPen = new Pen(goldDark, 2.5 * thick);
            dc.DrawRoundedRectangle(gold, capPen, new Rect(72, 52 - 2 * (thick - 1), 112, 16 + 4 * (thick - 1)), 6, 6);
            dc.DrawRoundedRectangle(gold, capPen, new Rect(72, 188 - 2 * (thick - 1), 112, 16 + 4 * (thick - 1)), 6, 6);
            if (detailed)
            {
                var post = new Pen(gold, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(post, new Point(80, 68), new Point(80, 188));
                dc.DrawLine(post, new Point(176, 68), new Point(176, 188));
            }
            if (small) dc.Pop();

            // Soft highlight on the medallion.
            if (size >= 32)
            {
                var shine = new RadialGradientBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF))
                {
                    Center = new Point(0.5, 0.5),
                    GradientOrigin = new Point(0.5, 0.5),
                };
                dc.DrawEllipse(shine, null, new Point(96, 82), 52, 34);
            }
            dc.Pop();
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>ICO with PNG-compressed entries (supported by Windows Vista and later at every size).</summary>
    private static void WriteIco(string path, List<(int Size, byte[] Png)> images)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);            // reserved
        w.Write((short)1);            // type: icon
        w.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);         // palette
            w.Write((byte)0);         // reserved
            w.Write((short)1);        // planes
            w.Write((short)32);       // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }
}
