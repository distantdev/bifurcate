<#
.SYNOPSIS
    Regenerates assets\Bifurcate.ico, the icon embedded in both executables.

.DESCRIPTION
    The tray icon is drawn at run time so it can carry the current status colour, but Explorer,
    the taskbar and shortcuts read the icon out of the executable's resources, so that one has to
    exist as a real file. This script draws the same trunk-and-two-branches glyph at every size
    Windows asks for and packs them into a single .ico.

    Run it only when the glyph changes. The .ico is committed, so a normal build does not need it.
#>

param(
    [string] $OutFile = (Join-Path $PSScriptRoot '..\assets\Bifurcate.ico'),
    [string] $Color = '#1B7F3B'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# Sizes Windows picks between: title bars and the tray at the small end, Explorer's extra-large
# view at 256.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-GlyphPng {
    param([int] $Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

        # Same geometry as IconFactory, expressed against its 32 pixel grid and scaled.
        $scale = $Size / 32.0
        $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($Color), 4.5 * $scale)
        try {
            $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

            $graphics.DrawLine($pen, 16 * $scale, 29 * $scale, 16 * $scale, 17 * $scale)
            $graphics.DrawLine($pen, 16 * $scale, 17 * $scale, 5 * $scale, 5 * $scale)
            $graphics.DrawLine($pen, 16 * $scale, 17 * $scale, 27 * $scale, 5 * $scale)
        }
        finally {
            $pen.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)

        # Comma wraps the array so it is emitted as one object. Without it the output stream
        # enumerates the bytes and the caller gets loose bytes instead of a byte array.
        return , $stream.ToArray()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) { $frames.Add((New-GlyphPng -Size $size)) }

$OutFile = [System.IO.Path]::GetFullPath($OutFile)
$file = [System.IO.File]::Create($OutFile)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    # ICONDIR: reserved, type 1 (icon), image count.
    $writer.Write([uint16] 0)
    $writer.Write([uint16] 1)
    $writer.Write([uint16] $sizes.Count)

    # Each directory entry is 16 bytes, and the image data follows all of them.
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        # 256 is stored as 0, which is what the single byte width and height fields mean by it.
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }

        $writer.Write([byte] $dimension)      # width
        $writer.Write([byte] $dimension)      # height
        $writer.Write([byte] 0)               # palette size, 0 for true colour
        $writer.Write([byte] 0)               # reserved
        $writer.Write([uint16] 1)             # colour planes
        $writer.Write([uint16] 32)            # bits per pixel
        $writer.Write([uint32] $frames[$i].Length)
        $writer.Write([uint32] $offset)

        $offset += $frames[$i].Length
    }

    foreach ($frame in $frames) { $writer.Write($frame) }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host ("wrote {0} ({1:N0} bytes, sizes {2})" -f $OutFile, (Get-Item $OutFile).Length, ($sizes -join ', '))
