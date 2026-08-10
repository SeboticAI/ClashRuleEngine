# Generates the ribbon / installer icons for the OConnors Clash Engine.
# NOTE: keep this file ASCII-only. PowerShell 5.1 misreads BOM-less UTF-8.
#
#   .\make-icons.ps1
#
# Input : Resources\oconnors_logo.png  (200x200 brand lockup: blue mark + wordmark)
# Output: Resources\oconnors_clash_16.ico    ribbon Icon      - mark only
#         Resources\oconnors_clash_32.ico    ribbon LargeIcon - mark + clash glyph
#         Resources\oconnors_clash_32.png    same, for docs/README
#         Resources\oconnors_clash_256.png   hero size, for docs
#         Installer\oconnors_clash.ico       multi-size 16/32/48, for setup.exe
#
# DESIGN NOTES
#   - The wordmark is cropped away: it is illegible below ~64px.
#   - At 16/32px the mark is DRAWN as vector geometry (blue disc + four white dots)
#     rather than downscaled from the PNG. A 22px bicubic crop of the real pinwheel
#     mark goes mushy; the vector form stays crisp and still reads unmistakably as
#     the OConnors mark. The 256px hero uses the real crop, where detail survives.
#   - The clash glyph is a "+" (two services crossing in plan) with a red dot at the
#     intersection, tucked into the bottom-right quadrant. A "+" - unlike an "X" -
#     never reaches back toward the circle, so the two elements stay visually
#     separate with no outline or halo trickery, and it clears the tile edge.
#   - .ico, not .png: the Navisworks CustomRibbon SDK sample uses .ico for Command
#     Icon/LargeIcon and Navisworks loads those through its own image loader, not
#     WPF. These are written as classic BMP-encoded 32bpp+mask ICOs - the format
#     every Windows loader reads - NOT PNG-compressed ICOs, which
#     System.Drawing.Icon has never handled reliably.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$resDir = Join-Path $root "Resources"
$insDir = Join-Path $root "Installer"
$logo = Join-Path $resDir "oconnors_logo.png"

if (-not (Test-Path $logo)) { Write-Error "Brand logo not found: $logo"; exit 1 }
New-Item -ItemType Directory -Force -Path $resDir | Out-Null
New-Item -ItemType Directory -Force -Path $insDir | Out-Null

# -- Brand constants (sampled from Resources\oconnors_logo.png) --------
# The blue mark occupies x 42..157, y 16..130 of the 200x200 lockup.
$MARK = New-Object System.Drawing.Rectangle(42, 16, 116, 115)
$BLUE = [System.Drawing.Color]::FromArgb(255, 0, 114, 182)     # #0072B6 OConnors blue
$AMBER = [System.Drawing.Color]::FromArgb(255, 245, 158, 11)   # #F59E0B service run
$SLATE = [System.Drawing.Color]::FromArgb(255, 51, 65, 85)     # #334155 crossing run
$RED = [System.Drawing.Color]::FromArgb(255, 220, 38, 38)      # #DC2626 clash point

function New-Canvas {
    param([int]$Size)
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

function Add-VectorMark {
    param($Graphics, [single]$Diameter)
    $b = New-Object System.Drawing.SolidBrush($BLUE)
    try { $Graphics.FillEllipse($b, [single]0, [single]0, $Diameter, $Diameter) }
    finally { $b.Dispose() }

    $w = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    try {
        $dotR = $Diameter * 0.160
        $off = $Diameter * 0.190
        $c = $Diameter / 2
        foreach ($sx in @(-1, 1)) {
            foreach ($sy in @(-1, 1)) {
                $dx = $c + ($off * $sx)
                $dy = $c + ($off * $sy)
                $Graphics.FillEllipse($w, [single]($dx - $dotR), [single]($dy - $dotR),
                    [single]($dotR * 2), [single]($dotR * 2))
            }
        }
    }
    finally { $w.Dispose() }
}

function Add-BitmapMark {
    param($Graphics, [int]$Size)
    $src = New-Object System.Drawing.Bitmap($logo)
    try {
        $dest = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
        $Graphics.DrawImage($src, $dest, $MARK, [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally { $src.Dispose() }
}

function Add-ClashGlyph {
    param($Graphics, [int]$Size)
    $s = $Size / 32.0                     # everything scales off the 32px design
    $cx = $Size - (9.0 * $s)              # glyph centre, bottom-right quadrant
    $cy = $Size - (9.0 * $s)
    $arm = 5.5 * $s                       # half-length of each service run
    $w = [Math]::Max(2.0, 2.9 * $s)       # run thickness

    $penA = New-Object System.Drawing.Pen($AMBER, $w)
    $penB = New-Object System.Drawing.Pen($SLATE, $w)
    try {
        foreach ($p in @($penA, $penB)) {
            $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $p.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        }
        $Graphics.DrawLine($penA, [single]($cx - $arm), [single]$cy, [single]($cx + $arm), [single]$cy)
        $Graphics.DrawLine($penB, [single]$cx, [single]($cy - $arm), [single]$cx, [single]($cy + $arm))

        $r = [Math]::Max(1.3, 1.85 * $s)
        $dot = New-Object System.Drawing.SolidBrush($RED)
        try {
            $Graphics.FillEllipse($dot, [single]($cx - $r), [single]($cy - $r),
                [single]($r * 2), [single]($r * 2))
        }
        finally { $dot.Dispose() }
    }
    finally { $penA.Dispose(); $penB.Dispose() }
}

# "Pull the data out": a down arrow landing on a tray. Distinguishes the Batch Extract
# ribbon button from Open Panel — two Large buttons sharing one icon is unreadable.
function Add-ExtractGlyph {
    param($Graphics, [int]$Size)
    $s = $Size / 32.0
    $cx = $Size - (9.0 * $s)
    $top = $Size - (15.5 * $s)
    $headY = $Size - (7.0 * $s)
    $w = [Math]::Max(2.0, 2.9 * $s)

    $pen = New-Object System.Drawing.Pen($SLATE, $w)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
    try {
        # shaft
        $Graphics.DrawLine($pen, [single]$cx, [single]$top, [single]$cx, [single]($headY - (2.6 * $s)))
        # arrowhead
        $half = [Math]::Max(2.5, 4.0 * $s)
        $pts = @(
            (New-Object System.Drawing.PointF([single]($cx - $half), [single]($headY - (3.4 * $s)))),
            (New-Object System.Drawing.PointF([single]($cx + $half), [single]($headY - (3.4 * $s)))),
            (New-Object System.Drawing.PointF([single]$cx, [single]$headY))
        )
        $head = New-Object System.Drawing.SolidBrush($SLATE)
        try { $Graphics.FillPolygon($head, $pts) } finally { $head.Dispose() }
    }
    finally { $pen.Dispose() }

    # tray the data lands on, in amber so it reads at a glance
    $tray = New-Object System.Drawing.Pen($AMBER, [Math]::Max(2.0, 2.6 * $s))
    $tray.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $tray.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    try {
        $half = [Math]::Max(3.0, 5.2 * $s)
        $y = $Size - (2.6 * $s)
        $Graphics.DrawLine($tray, [single]($cx - $half), [single]$y, [single]($cx + $half), [single]$y)
    }
    finally { $tray.Dispose() }
}

function New-Tile {
    param([int]$Size, [ValidateSet('none', 'clash', 'extract')][string]$Glyph = 'none',
          [switch]$UseBitmapMark)
    $pair = New-Canvas -Size $Size
    $bmp = $pair[0]; $g = $pair[1]
    try {
        # With a glyph the mark yields 30% of the tile so the two elements do not touch.
        $markSize = if ($Glyph -ne 'none') { [int][Math]::Round($Size * 0.70) } else { $Size }
        if ($UseBitmapMark) { Add-BitmapMark -Graphics $g -Size $markSize }
        else { Add-VectorMark -Graphics $g -Diameter ([single]$markSize) }
        switch ($Glyph) {
            'clash' { Add-ClashGlyph -Graphics $g -Size $Size }
            'extract' { Add-ExtractGlyph -Graphics $g -Size $Size }
        }
    }
    finally { $g.Dispose() }
    return $bmp
}

# -- Classic BMP-encoded ICO writer (single or multi image) -------------
# System.Drawing cannot save a real .ico (GetHicon loses alpha; Save(...,Icon)
# writes a PNG with an .ico name), so write the container by hand:
#   ICONDIR -> one ICONDIRENTRY per image -> for each image
#   BITMAPINFOHEADER (biHeight doubled for the AND mask) + XOR pixels
#   (32bpp BGRA, bottom-up) + AND mask (zeroed; alpha carries transparency).
function Save-Ico {
    param([System.Drawing.Bitmap[]]$Bitmaps, [string]$Path)

    $payloads = @()
    foreach ($bmp in $Bitmaps) {
        $w = $bmp.Width; $h = $bmp.Height
        $maskStride = [int][Math]::Floor(($w + 31) / 32) * 4
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        try {
            $bw.Write([uint32]40)
            $bw.Write([int32]$w)
            $bw.Write([int32]($h * 2))
            $bw.Write([uint16]1)
            $bw.Write([uint16]32)
            $bw.Write([uint32]0)                                  # BI_RGB
            $bw.Write([uint32](($w * $h * 4) + ($maskStride * $h)))
            1..4 | ForEach-Object { $bw.Write([uint32]0) }         # ppm x/y, clrUsed/Important
            for ($y = $h - 1; $y -ge 0; $y--) {
                for ($x = 0; $x -lt $w; $x++) {
                    $c = $bmp.GetPixel($x, $y)
                    $bw.Write([byte]$c.B); $bw.Write([byte]$c.G)
                    $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
                }
            }
            $bw.Write((New-Object byte[] ($maskStride * $h)))
            $bw.Flush()
            $payloads += , @{ W = $w; H = $h; Data = $ms.ToArray() }
        }
        finally { $bw.Dispose(); $ms.Dispose() }
    }

    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([uint16]0)                       # reserved
        $bw.Write([uint16]1)                       # type: 1 = icon
        $bw.Write([uint16]$payloads.Count)

        $offset = 6 + (16 * $payloads.Count)
        foreach ($p in $payloads) {
            $bw.Write([byte]$(if ($p.W -ge 256) { 0 } else { $p.W }))
            $bw.Write([byte]$(if ($p.H -ge 256) { 0 } else { $p.H }))
            $bw.Write([byte]0)                     # palette entries (0 = 32bpp)
            $bw.Write([byte]0)                     # reserved
            $bw.Write([uint16]1)                   # colour planes
            $bw.Write([uint16]32)                  # bits per pixel
            $bw.Write([uint32]$p.Data.Length)
            $bw.Write([uint32]$offset)
            $offset += $p.Data.Length
        }
        foreach ($p in $payloads) { $bw.Write($p.Data) }
    }
    finally { $bw.Dispose(); $fs.Dispose() }
}

function Report($path) {
    Write-Host ("  wrote {0} ({1:N0} bytes)" -f (Split-Path $path -Leaf), (Get-Item $path).Length)
}

# -- Emit --------------------------------------------------------------
Write-Host "Ribbon icons (vector mark, crisp at small sizes):"

$t16 = New-Tile -Size 16
try {
    $p = Join-Path $resDir "oconnors_clash_16.ico"
    Save-Ico -Bitmaps @($t16) -Path $p; Report $p
}
finally { $t16.Dispose() }

$t32 = New-Tile -Size 32 -Glyph clash
try {
    $p = Join-Path $resDir "oconnors_clash_32.ico"
    Save-Ico -Bitmaps @($t32) -Path $p; Report $p
    $p = Join-Path $resDir "oconnors_clash_32.png"
    $t32.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); Report $p
}
finally { $t32.Dispose() }

Write-Host "Batch Extract button (down-arrow-into-tray glyph):"
foreach ($sz in @(16, 32)) {
    $t = New-Tile -Size $sz -Glyph extract
    try {
        $p = Join-Path $resDir "oconnors_extract_$sz.ico"
        Save-Ico -Bitmaps @($t) -Path $p; Report $p
    }
    finally { $t.Dispose() }
}

Write-Host "Docs hero (real mark crop - detail survives at this size):"
$t256 = New-Tile -Size 256 -Glyph clash -UseBitmapMark
try {
    $p = Join-Path $resDir "oconnors_clash_256.png"
    $t256.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); Report $p
}
finally { $t256.Dispose() }

Write-Host "Installer icon (multi-size 16/32/48):"
$multi = @((New-Tile -Size 16), (New-Tile -Size 32 -Glyph clash), (New-Tile -Size 48 -Glyph clash))
try {
    $p = Join-Path $insDir "oconnors_clash.ico"
    Save-Ico -Bitmaps $multi -Path $p; Report $p
}
finally { foreach ($b in $multi) { $b.Dispose() } }

Write-Host ""
Write-Host "Done. The .ico files are copied next to the DLL by the build (see csproj)."
