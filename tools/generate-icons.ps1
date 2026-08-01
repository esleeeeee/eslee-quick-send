<#
.SYNOPSIS
    Generates every Windows and Android icon resource from the approved master artwork.

.DESCRIPTION
    The master file is the only source of truth. This script never redraws the logo: it
    only removes the flat backdrop, rescales, and derives the single-colour silhouettes
    that the tray and the Android notification API require.

    Steps
      1. Make the backdrop transparent with a border-seeded flood fill, so the white
         inside the "eslee" wordmark and the arrows is preserved.
      2. Emit a multi-resolution Windows .ico plus a tray-sized arrow-only .ico.
      3. Emit Android adaptive foreground, legacy, round and monochrome launcher icons.
      4. Emit the white arrow-only silhouette used for the notification small icon.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/generate-icons.ps1
#>
[CmdletBinding()]
param(
    [string]$Master,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path }
if (-not $Master) { $Master = Join-Path $RepoRoot 'assets\branding\eslee-quicksend-master.png' }
if (-not (Test-Path $Master)) { throw "Master icon not found: $Master" }

# Pixel work runs in C#: LockBits keeps a 1254x1254 flood fill instant, and the typed
# code avoids PowerShell's loose scalar/array coercion on per-pixel maths.
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

public static class IconForge
{
    static int[] Read(Bitmap source, out int w, out int h)
    {
        w = source.Width; h = source.Height;
        var rect = new Rectangle(0, 0, w, h);
        using (var clone = source.Clone(rect, PixelFormat.Format32bppArgb))
        {
            var data = clone.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var pixels = new int[w * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            clone.UnlockBits(data);
            return pixels;
        }
    }

    static Bitmap Write(int[] pixels, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    /// <summary>Clears the flat backdrop, seeded from the border so interior white stays.</summary>
    public static Bitmap TransparentBackdrop(Bitmap source, int tolerance)
    {
        int w, h;
        var pixels = Read(source, out w, out h);
        var cleared = new bool[w * h];
        var stack = new Stack<int>();

        Func<int, bool> isBackdrop = delegate(int argb)
        {
            int r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
            return (255 - r) <= tolerance && (255 - g) <= tolerance && (255 - b) <= tolerance;
        };

        for (int x = 0; x < w; x++)
        {
            foreach (int y in new[] { 0, h - 1 })
            {
                int i = y * w + x;
                if (!cleared[i] && isBackdrop(pixels[i])) { cleared[i] = true; stack.Push(i); }
            }
        }
        for (int y = 0; y < h; y++)
        {
            foreach (int x in new[] { 0, w - 1 })
            {
                int i = y * w + x;
                if (!cleared[i] && isBackdrop(pixels[i])) { cleared[i] = true; stack.Push(i); }
            }
        }
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            int x = i % w, y = i / w;
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            for (int k = 0; k < 4; k++)
            {
                int nx = x + dx[k], ny = y + dy[k];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int ni = ny * w + nx;
                if (cleared[ni]) continue;
                if (isBackdrop(pixels[ni])) { cleared[ni] = true; stack.Push(ni); }
            }
        }

        var output = new int[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
            output[i] = cleared[i] ? 0 : (unchecked((int)0xFF000000) | (pixels[i] & 0x00FFFFFF));
        return Write(output, w, h);
    }

    /// <summary>Keeps the light artwork and drops the green field: a single-colour mask.</summary>
    public static Bitmap WhiteSilhouette(Bitmap source, int threshold)
    {
        int w, h;
        var pixels = Read(source, out w, out h);
        var output = new int[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            int a = (pixels[i] >> 24) & 0xFF;
            int r = (pixels[i] >> 16) & 0xFF, g = (pixels[i] >> 8) & 0xFF, b = pixels[i] & 0xFF;
            if (a < 128) { output[i] = 0; continue; }
            double luma = 0.299 * r + 0.587 * g + 0.114 * b;
            bool green = g > r + 18;
            output[i] = (luma >= threshold && !green) ? unchecked((int)0xFFFFFFFF) : 0;
        }
        return Write(output, w, h);
    }

    public static Bitmap Resize(Bitmap source, int size, double contentScale)
    {
        var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(output))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            int inner = (int)Math.Round(size * contentScale);
            int offset = (int)Math.Round((size - inner) / 2.0);
            g.DrawImage(source, offset, offset, inner, inner);
        }
        return output;
    }

    public static Bitmap CropSquare(Bitmap source, double top, double bottom)
    {
        int y0 = (int)Math.Round(source.Height * top);
        int y1 = (int)Math.Round(source.Height * bottom);
        int side = y1 - y0;
        int x0 = (int)Math.Round((source.Width - side) / 2.0);
        return source.Clone(new Rectangle(x0, y0, side, side), PixelFormat.Format32bppArgb);
    }

    /// <summary>Writes a real multi-image .ico with PNG-compressed frames.</summary>
    public static void SaveIco(Bitmap source, int[] sizes, string path)
    {
        var frames = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (var bmp = Resize(source, s, 1.0))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                frames.Add(ms.ToArray());
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)frames.Count);
            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
                bw.Write(dim); bw.Write(dim); bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write((uint)frames[i].Length); bw.Write((uint)offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) bw.Write(f);
        }
    }

    public static void SavePng(Bitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        bitmap.Save(path, ImageFormat.Png);
    }
}
'@

Write-Host "master: $Master"
$original = New-Object System.Drawing.Bitmap($Master)
Write-Host ("  {0}x{1} {2}" -f $original.Width, $original.Height, $original.PixelFormat)
$logo = [IconForge]::TransparentBackdrop($original, 26)
$original.Dispose()

# ---- Windows -------------------------------------------------------------------------
$winAssets = Join-Path $RepoRoot 'src\QuickSend.Windows\Assets'
[IconForge]::SaveIco($logo, @(16, 20, 24, 32, 40, 48, 64, 128, 256), (Join-Path $winAssets 'eslee-quicksend.ico'))
Write-Host "windows ico  -> $winAssets\eslee-quicksend.ico"

# The tray renders around 16px where the wordmark is unreadable, so the tray derivative
# keeps only the arrow symbol. Same colours, same arrows, no redesign.
$arrows = [IconForge]::CropSquare($logo, 0.27, 0.97)
[IconForge]::SaveIco($arrows, @(16, 20, 24, 32, 48, 64), (Join-Path $winAssets 'eslee-quicksend-tray.ico'))
Write-Host "tray ico     -> $winAssets\eslee-quicksend-tray.ico"

[IconForge]::SaveIco($logo, @(16, 24, 32, 48, 64, 128, 256), (Join-Path $RepoRoot 'installer\eslee-quicksend-setup.ico'))
Write-Host "setup ico    -> $RepoRoot\installer\eslee-quicksend-setup.ico"

# ---- Android -------------------------------------------------------------------------
$res = Join-Path $RepoRoot 'android\app\src\main\res'
$densities = [ordered]@{ mdpi = 48; hdpi = 72; xhdpi = 96; xxhdpi = 144; xxxhdpi = 192 }
$monoFull = [IconForge]::WhiteSilhouette($logo, 170)

foreach ($d in $densities.Keys) {
    $size = $densities[$d]
    $legacy = [IconForge]::Resize($logo, $size, 1.0)
    [IconForge]::SavePng($legacy, (Join-Path $res "mipmap-$d\ic_launcher.png"))
    [IconForge]::SavePng($legacy, (Join-Path $res "mipmap-$d\ic_launcher_round.png"))
    $legacy.Dispose()

    # Adaptive foreground: a 108dp canvas with the artwork inside the 72dp safe zone, so
    # no circular or squircle mask can clip the wordmark or the arrows.
    $fgSize = [int][math]::Round($size * 108.0 / 48.0)
    $foreground = [IconForge]::Resize($logo, $fgSize, 0.62)
    [IconForge]::SavePng($foreground, (Join-Path $res "mipmap-$d\ic_launcher_foreground.png"))
    $foreground.Dispose()

    $mono = [IconForge]::Resize($monoFull, $fgSize, 0.62)
    [IconForge]::SavePng($mono, (Join-Path $res "mipmap-$d\ic_launcher_monochrome.png"))
    $mono.Dispose()
}
Write-Host "android launcher icons -> $res\mipmap-*"

# Notification small icon: white arrow silhouette only, per the platform rule that status
# bar icons must be a single-colour mask.
$arrowMono = [IconForge]::WhiteSilhouette($arrows, 170)
$statusSizes = [ordered]@{ mdpi = 24; hdpi = 36; xhdpi = 48; xxhdpi = 72; xxxhdpi = 96 }
foreach ($d in $statusSizes.Keys) {
    $n = [IconForge]::Resize($arrowMono, $statusSizes[$d], 0.92)
    [IconForge]::SavePng($n, (Join-Path $res "drawable-$d\ic_quicksend_status.png"))
    $n.Dispose()
}
$arrowMono.Dispose()
Write-Host "android notification icons -> $res\drawable-*"

$monoFull.Dispose(); $arrows.Dispose(); $logo.Dispose()
Write-Host "done."
