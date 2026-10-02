<#
.SYNOPSIS
    Regenerates Assets\MultiTerminal.ico, the application icon (GH #36, task 158d60ac).

.DESCRIPTION
    This script IS the icon's source. The mark is drawn here with System.Drawing rather than
    rasterised from an SVG, so the small sizes can be simplified and pixel-snapped instead of
    being a blurred shrink of the large one.

    Design (256-unit grid, scaled per size):
      - rounded square, diagonal gradient #1a1a2e -> #16213e, 1-unit #2a2a4a border
      - a ">_" prompt in #e94560
      - two "output line" accents in #53a8f5 above the prompt, dropped at 32px and below, where
        they turn to mush

    Frames: 16, 24, 32, 48, 64, 128, 256. Sizes below 256 are stored as 32-bit DIBs, the format
    every Win32 consumer reads; 256 is stored as PNG, which is what Explorer expects at that size.

    Run it from the repo root after changing the design, and commit the .ico it writes:
        pwsh -File scripts\generate-app-icon.ps1
        pwsh -File scripts\generate-app-icon.ps1 -PreviewDir <dir>   # also writes one PNG per size

    The build does NOT run this script; it only consumes the checked-in .ico.
#>
[CmdletBinding()]
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\Assets\MultiTerminal.ico'),
    [string]$PreviewDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Sizes = 16, 24, 32, 48, 64, 128, 256

function New-Color([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
}

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-IconBitmap([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $s = $size / 256.0
        $small = $size -le 32

        # Background: the square fills the frame at every size (no inset), so the icon reads at
        # the same visual weight as other taskbar icons.
        $radius = [Math]::Max(2.0, 44 * $s)
        $bg = New-RoundedRect 0 0 ($size - 0.01) ($size - 0.01) $radius
        $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new($size, $size),
            (New-Color '#1a1a2e'), (New-Color '#16213e'))
        $g.FillPath($grad, $bg)
        $borderWidth = [Math]::Max(1.0, 6 * $s)
        $inset = $borderWidth / 2
        $border = New-RoundedRect $inset $inset ($size - $borderWidth) ($size - $borderWidth) ([Math]::Max(1.5, $radius - $inset))
        $pen = [System.Drawing.Pen]::new((New-Color '#2a2a4a'), $borderWidth)
        $g.DrawPath($pen, $border)
        $pen.Dispose(); $border.Dispose(); $grad.Dispose(); $bg.Dispose()

        $red = New-Color '#e94560'

        if ($small) {
            # 16/24/32px: pixel-snapped so the chevron stays two crisp diagonals instead of a grey smear.
            # The prompt is enlarged relative to the frame, because the accents are gone.
            $u = $size / 16.0
            $stroke = [Math]::Max(2.0, 2 * $u)
            $chev = [System.Drawing.Pen]::new($red, $stroke)
            $chev.StartCap = [System.Drawing.Drawing2D.LineCap]::Square
            $chev.EndCap = [System.Drawing.Drawing2D.LineCap]::Square
            $chev.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Miter
            $pts = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new(4 * $u, 4 * $u),
                [System.Drawing.PointF]::new(8 * $u, 8 * $u),
                [System.Drawing.PointF]::new(4 * $u, 12 * $u))
            $g.DrawLines($chev, $pts)
            $chev.Dispose()
            $brush = [System.Drawing.SolidBrush]::new($red)
            $g.FillRectangle($brush, [single](9 * $u), [single](11 * $u), [single](4 * $u), [single]$stroke)
            $brush.Dispose()
        }
        else {
            # Accents: two lines of "output" above the prompt line, left-aligned with it. (Placed
            # beside the chevron instead, they stack with the cursor into a ">=" glyph.)
            $blue1 = [System.Drawing.SolidBrush]::new((New-Color '#53a8f5' 230))
            $blue2 = [System.Drawing.SolidBrush]::new((New-Color '#53a8f5' 140))
            $l1 = New-RoundedRect (56 * $s) (50 * $s) (120 * $s) (18 * $s) (9 * $s)
            $l2 = New-RoundedRect (56 * $s) (84 * $s) (78 * $s) (18 * $s) (9 * $s)

            # Prompt ">_"
            $stroke = 26 * $s
            $chev = [System.Drawing.Pen]::new($red, $stroke)
            $chev.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $chev.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $chev.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $pts = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new(66 * $s, 128 * $s),
                [System.Drawing.PointF]::new(116 * $s, 164 * $s),
                [System.Drawing.PointF]::new(66 * $s, 200 * $s))
            $g.DrawLines($chev, $pts)
            $chev.Dispose()

            $brush = [System.Drawing.SolidBrush]::new($red)
            $under = New-RoundedRect (136 * $s) (187 * $s) (64 * $s) (26 * $s) (7 * $s)
            $g.FillPath($brush, $under)
            $under.Dispose(); $brush.Dispose()

            $g.FillPath($blue1, $l1)
            $g.FillPath($blue2, $l2)
            $l1.Dispose(); $l2.Dispose(); $blue1.Dispose(); $blue2.Dispose()
        }
    }
    finally {
        $g.Dispose()
    }
    $bmp
}

# A 32-bit DIB icon frame: BITMAPINFOHEADER (height doubled for the AND mask), bottom-up BGRA rows,
# then an all-zero 1bpp AND mask (alpha does the masking).
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $raw = [byte[]]::new($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    }
    finally { $bmp.UnlockBits($data) }

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($raw, $y * $stride, $w * 4) }
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $bw.Write([byte[]]::new($maskStride * $h))
    $bw.Flush()
    ,$ms.ToArray()   # comma: return the byte[] whole, not unrolled into the pipeline
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    ,$ms.ToArray()   # comma: return the byte[] whole, not unrolled into the pipeline
}

$frames = foreach ($size in $Sizes) {
    $bmp = New-IconBitmap $size
    try {
        if ($PreviewDir) {
            New-Item -ItemType Directory -Force -Path $PreviewDir | Out-Null
            $bmp.Save((Join-Path $PreviewDir "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        [byte[]]$bytes = if ($size -ge 256) { Get-PngBytes $bmp } else { Get-DibBytes $bmp }
        [pscustomobject]@{ Size = $size; Bytes = $bytes }
    }
    finally { $bmp.Dispose() }
}

$OutFile = [System.IO.Path]::GetFullPath($OutFile)
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$fs = [System.IO.File]::Create($OutFile)
$bw = [System.IO.BinaryWriter]::new($fs)
try {
    # ICONDIR
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }   # 0 means 256 in an ICONDIRENTRY
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]$f.Bytes.Length); $bw.Write([int]$offset)
        $offset += $f.Bytes.Length
    }
    foreach ($f in $frames) { $bw.Write($f.Bytes) }
}
finally { $bw.Dispose(); $fs.Dispose() }

Write-Host "Wrote $OutFile ($((Get-Item $OutFile).Length) bytes, sizes: $($Sizes -join ', '))"
