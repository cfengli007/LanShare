$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$src = @'

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class LanShareIconGen
{
    static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float pad = MathF.Max(1f, size * 0.045f);
        var rect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
        float radius = size * 0.235f;
        using var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using (var brush = new LinearGradientBrush(rect, Color.FromArgb(255, 37, 99, 235), Color.FromArgb(255, 109, 40, 217), 45f))
            g.FillPath(brush, path);
        var hlRect = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height * 0.48f);
        using (var hl = new LinearGradientBrush(hlRect, Color.FromArgb(36, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
        {
            g.SetClip(path);
            g.FillRectangle(hl, hlRect);
            g.ResetClip();
        }
        using var white = new SolidBrush(Color.White);
        float trayW = size * 0.50f, trayH = MathF.Max(1.6f, size * 0.075f);
        g.FillRectangle(white, new RectangleF((size - trayW) / 2f, size * 0.735f, trayW, trayH));
        float shaftW = MathF.Max(1.8f, size * 0.105f);
        g.FillRectangle(white, new RectangleF((size - shaftW) / 2f, size * 0.245f, shaftW, size * 0.42f));
        using var head = new GraphicsPath();
        head.AddPolygon(new[]
        {
            new PointF(size * 0.5f, size * 0.145f),
            new PointF(size * 0.745f, size * 0.375f),
            new PointF(size * 0.255f, size * 0.375f)
        });
        g.FillPath(white, head);
        return bmp;
    }

    public static void Generate(string outDir)
    {
        Directory.CreateDirectory(outDir);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = new System.Collections.Generic.List<(int size, byte[] data)>();
        foreach (int s in sizes)
        {
            using var bmp = Render(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            frames.Add((s, ms.ToArray()));
            if (s == 256) bmp.Save(Path.Combine(outDir, "app-256.png"), ImageFormat.Png);
        }
        string ico = Path.Combine(outDir, "app.ico");
        using var fs = File.Create(ico);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var f in frames)
        {
            bw.Write((byte)(f.size >= 256 ? 0 : f.size));
            bw.Write((byte)(f.size >= 256 ? 0 : f.size));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write((uint)f.data.Length); bw.Write((uint)offset);
            offset += f.data.Length;
        }
        foreach (var f in frames) bw.Write(f.data);
        Console.WriteLine($"icon: {ico} ({new FileInfo(ico).Length} bytes)");
    }
}

'@
Add-Type -AssemblyName System.Drawing.Common
$dllDir = Split-Path ([System.Drawing.Bitmap].Assembly.Location)
$refs = @(
  ([System.Drawing.Bitmap].Assembly.Location),
  (Join-Path $dllDir 'System.Private.Windows.GdiPlus.dll'),
  (Join-Path $dllDir 'System.Drawing.Primitives.dll')
)
Add-Type -TypeDefinition $src -ReferencedAssemblies $refs
[LanShareIconGen]::Generate('D:\project-dsh\work\lanshare\src\LanShare.Desktop\Assets')
Copy-Item 'D:\project-dsh\work\lanshare\src\LanShare.Desktop\Assets\app.ico' 'D:\project-dsh\work\lanshare\assets\app.ico' -Force
Copy-Item 'D:\project-dsh\work\lanshare\src\LanShare.Desktop\Assets\app-256.png' 'D:\project-dsh\work\lanshare\assets\app-256.png' -Force