<#
.SYNOPSIS
    Turns the raw captures from capture-shots.ps1 into the images the README uses.

.DESCRIPTION
    Two jobs. First it swaps anything identifying, the VPN name and the addresses, for placeholders,
    by rebuilding the background under each run of text and redrawing it in the app's own font. That
    beats blurring, which reads as something to hide, and beats retaking the shots against a fake VPN,
    which would mean creating one.

    Then it rounds the corners and drops a soft shadow onto a transparent background, so the window
    sits on the page instead of ending in a hard rectangle.

    Coordinates are in pixels against a capture taken at 125 percent display scale, and are scaled
    from there to whatever the capture actually is. They follow the layout of the windows, so a
    change to either window means checking them again. Every replacement asserts that it found text
    where it expected some, which is the tripwire for that.

.EXAMPLE
    .\capture-shots.ps1
    .\make-docs-shots.ps1
#>

param(
    [string] $ShotDir = (Join-Path $env:TEMP 'bifurcate-shots'),
    [string] $DocsDir = (Join-Path $PSScriptRoot '..\docs')
)

$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives,
    System.Private.Windows.GdiPlus, System.Private.Windows.Core,
    System.Collections, System.Console, System.Runtime -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

public static class Shots
{
    public static Bitmap Load(string path)
    {
        // Through memory, so GDI+ does not hold the file open and block the save.
        using (var stream = new System.IO.MemoryStream(System.IO.File.ReadAllBytes(path)))
        using (var image = new Bitmap(stream))
        {
            return new Bitmap(image);
        }
    }

    /// <summary>
    /// Replaces one run of text. The background is rebuilt per column by interpolating between the
    /// clean rows above and below the strip, which keeps whatever gradient the backdrop has.
    /// </summary>
    public static void Replace(Bitmap image, Rectangle area, string text, string family, float sizePx,
                               bool bold, string inkHex)
    {
        Color[] above = Row(image, area, area.Top - 3);
        Color[] below = Row(image, area, area.Bottom + 2);

        Rectangle target = TextBounds(image, area, above, below);
        if (target.IsEmpty) { throw new InvalidOperationException("no text found in " + area); }

        // The colour is passed in rather than sampled back out of the image, where the antialiased
        // edge pixels outnumber the solid cores and the answer comes out too light.
        Color ink = ColorTranslator.FromHtml(inkHex);

        using (var font = new Font(family, sizePx, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(ink))
        {
            PointF at = new PointF(target.Left, target.Top);

            // Draw, see where the glyphs actually landed, and correct. Beats guessing at metrics.
            for (int pass = 0; pass < 6; pass++)
            {
                Erase(image, area, above, below);
                Draw(image, area, font, brush, text, at);

                Rectangle drawn = TextBounds(image, area, above, below);
                if (drawn.IsEmpty) { break; }

                int dx = target.Left - drawn.Left;
                int dy = target.Top - drawn.Top;
                if (dx == 0 && dy == 0) { break; }

                at = new PointF(at.X + dx, at.Y + dy);
            }
        }
    }

    private static void Draw(Bitmap image, Rectangle area, Font font, Brush brush, string text, PointF at)
    {
        using (var graphics = Graphics.FromImage(image))
        {
            // Clipped, so a pass that lands off target cannot leave pixels the next erase misses.
            graphics.SetClip(area);
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.DrawString(text, font, brush, at, StringFormat.GenericTypographic);
        }
    }

    private static Color[] Row(Bitmap image, Rectangle area, int y)
    {
        var row = new Color[area.Width];
        for (int i = 0; i < area.Width; i++) { row[i] = image.GetPixel(area.Left + i, y); }
        return row;
    }

    private static Color Background(Color[] above, Color[] below, Rectangle area, int x, int y)
    {
        int index = x - area.Left;
        double t = (y - (area.Top - 3)) / (double)((area.Bottom + 2) - (area.Top - 3));
        Color a = above[index];
        Color b = below[index];
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static void Erase(Bitmap image, Rectangle area, Color[] above, Color[] below)
    {
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                image.SetPixel(x, y, Background(above, below, area, x, y));
            }
        }
    }

    private static Rectangle TextBounds(Bitmap image, Rectangle area, Color[] above, Color[] below)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                if (Distance(image.GetPixel(x, y), Background(above, below, area, x, y)) <= 24) { continue; }

                if (x < left) { left = x; }
                if (x > right) { right = x; }
                if (y < top) { top = y; }
                if (y > bottom) { bottom = y; }
            }
        }

        return left == int.MaxValue
            ? Rectangle.Empty
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static int Distance(Color left, Color right) =>
        Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);

    /// <summary>Rounds the corners and drops a soft shadow onto a transparent canvas.</summary>
    public static void Polish(Bitmap source, string destination, int radius, int padding,
                              int shadowDrop, int blur, double shadowAlpha)
    {
        int width = source.Width;
        int height = source.Height;
        int canvasWidth = width + padding * 2;
        int canvasHeight = height + padding * 2;

        float[] coverage = RoundedCoverage(width, height, radius);

        // The silhouette is laid out on the full canvas before blurring. Blurring it at window size
        // would clip the shadow to the window's own edges, leaving nothing in the padding.
        var silhouette = new float[canvasWidth * canvasHeight];
        for (int y = 0; y < height; y++)
        {
            int row = y + padding + shadowDrop;
            if (row < 0 || row >= canvasHeight) { continue; }

            for (int x = 0; x < width; x++)
            {
                silhouette[row * canvasWidth + x + padding] = coverage[y * width + x];
            }
        }

        float[] shadow = Blur(silhouette, canvasWidth, canvasHeight, blur);

        using (var canvas = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb))
        {
            for (int y = 0; y < canvasHeight; y++)
            {
                for (int x = 0; x < canvasWidth; x++)
                {
                    int shapeX = x - padding;
                    int shapeY = y - padding;

                    double shade = shadow[y * canvasWidth + x] * shadowAlpha;
                    double solid = Sample(coverage, width, height, shapeX, shapeY);

                    Color pixel = solid > 0
                        ? source.GetPixel(Math.Min(Math.Max(shapeX, 0), width - 1),
                                          Math.Min(Math.Max(shapeY, 0), height - 1))
                        : Color.Black;

                    // The window over a shadow that is only visible where the window is not.
                    double alpha = solid + shade * (1 - solid);
                    if (alpha <= 0.002) { continue; }

                    double weight = solid / alpha;
                    int r = (int)Math.Round(pixel.R * weight);
                    int g = (int)Math.Round(pixel.G * weight);
                    int b = (int)Math.Round(pixel.B * weight);

                    canvas.SetPixel(x, y, Color.FromArgb((int)Math.Round(alpha * 255), r, g, b));
                }
            }

            canvas.Save(destination, ImageFormat.Png);
        }
    }

    private static float[] RoundedCoverage(int width, int height, int radius)
    {
        var coverage = new float[width * height];
        const int Steps = 4;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < Steps; sy++)
                {
                    for (int sx = 0; sx < Steps; sx++)
                    {
                        double px = x + (sx + 0.5) / Steps;
                        double py = y + (sy + 0.5) / Steps;
                        if (InRounded(px, py, width, height, radius)) { inside++; }
                    }
                }

                coverage[y * width + x] = inside / (float)(Steps * Steps);
            }
        }

        return coverage;
    }

    private static bool InRounded(double x, double y, int width, int height, int radius)
    {
        double cx = x < radius ? radius : (x > width - radius ? width - radius : x);
        double cy = y < radius ? radius : (y > height - radius ? height - radius : y);
        double dx = x - cx;
        double dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static float[] Blur(float[] source, int width, int height, int radius)
    {
        // Three box passes approximate a gaussian closely enough for a drop shadow.
        float[] current = source;
        for (int pass = 0; pass < 3; pass++) { current = BoxBlur(current, width, height, radius); }
        return current;
    }

    private static float[] BoxBlur(float[] source, int width, int height, int radius)
    {
        var horizontal = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float total = 0;
                int count = 0;
                for (int offset = -radius; offset <= radius; offset++)
                {
                    total += Sample(source, width, height, x + offset, y);
                    count++;
                }

                horizontal[y * width + x] = total / count;
            }
        }

        var result = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float total = 0;
                int count = 0;
                for (int offset = -radius; offset <= radius; offset++)
                {
                    total += Sample(horizontal, width, height, x, y + offset);
                    count++;
                }

                result[y * width + x] = total / count;
            }
        }

        return result;
    }

    private static float Sample(float[] buffer, int width, int height, int x, int y) =>
        x < 0 || y < 0 || x >= width || y >= height ? 0f : buffer[y * width + x];
}
'@

# Widths of the two captures at the scale every coordinate below was measured against.
$referenceDashboard = 625
$referenceSettings = 700

# Text colours, taken from the app rather than the image. Status lines are the Good green.
$bodyText = '#1A1A1A'
$goodText = '#1B7F3B'

function New-Rect {
    param([int] $Left, [int] $Top, [int] $Right, [int] $Bottom, [double] $Scale)

    [System.Drawing.Rectangle]::FromLTRB(
        [int][Math]::Round($Left * $Scale), [int][Math]::Round($Top * $Scale),
        [int][Math]::Round($Right * $Scale), [int][Math]::Round($Bottom * $Scale))
}

function Complete-Image {
    param([System.Drawing.Bitmap] $Image, [string] $Destination, [double] $Scale)

    $round = { param($value) [int][Math]::Round($value * $Scale) }
    [Shots]::Polish($Image, $Destination, (& $round 11), (& $round 34), (& $round 8), (& $round 10), 0.38)
    $Image.Dispose()
    Write-Host "wrote $Destination"
}

function Build-Dashboard {
    param([string] $Source, [string] $Destination)

    $image = [Shots]::Load($Source)
    $scale = $image.Width / $referenceDashboard
    $body = 16.25 * $scale
    $heading = 18.75 * $scale

    [Shots]::Replace($image, (New-Rect 30 66 460 94 $scale),
        'Bifurcate - Acme VPN', 'Segoe UI Semibold', $heading, $false, $bodyText)
    [Shots]::Replace($image, (New-Rect 52 149 540 172 $scale),
        'Tunnel: 10.10.20.5 answered in 24 ms', 'Segoe UI', $body, $false, $goodText)
    [Shots]::Replace($image, (New-Rect 52 180 540 208 $scale),
        'Routing: 10.10.0.0/16 only, others not tunneled', 'Segoe UI', $body, $false, $goodText)
    [Shots]::Replace($image, (New-Rect 52 212 540 240 $scale),
        'External IP: 203.0.113.42 - from your ISP, as intended', 'Segoe UI', $body, $false, $goodText)

    Complete-Image -Image $image -Destination $Destination -Scale $scale
}

function Build-Settings {
    param([string] $Source, [string] $Destination)

    $image = [Shots]::Load($Source)
    $scale = $image.Width / $referenceSettings
    $body = 16.25 * $scale

    [Shots]::Replace($image, (New-Rect 200 108 600 130 $scale),
        'Acme VPN', 'Segoe UI', $body, $false, $bodyText)
    [Shots]::Replace($image, (New-Rect 200 183 660 204 $scale),
        '10.10.0.0/16', 'Segoe UI', $body, $false, $bodyText)
    [Shots]::Replace($image, (New-Rect 341 276 470 293 $scale),
        '10.10.20.5', 'Segoe UI', $body, $false, $bodyText)

    Complete-Image -Image $image -Destination $Destination -Scale $scale
}

$DocsDir = [System.IO.Path]::GetFullPath($DocsDir)
New-Item -ItemType Directory -Path $DocsDir -Force | Out-Null

Build-Dashboard -Source (Join-Path $ShotDir 'dashboard-Light.png') `
    -Destination (Join-Path $DocsDir 'dashboard.png')
Build-Settings -Source (Join-Path $ShotDir 'settings-Light.png') `
    -Destination (Join-Path $DocsDir 'settings.png')
