<#
.SYNOPSIS
    Regenerates every MSIX logo asset and the window icon from one mark.

.DESCRIPTION
    §15 step 12 needs Store assets, and the Visual Studio template's placeholder
    glyph cannot ship. The mark drawn here is StatusMedallion reduced to an icon:
    §9.10.2 makes the medallion the application's signature element, so the tile
    the user clicks and the surface they land on are the same shape.

    The assets are *generated* rather than drawn once and committed as opaque
    binaries. An icon set is forty files that have to agree with each other, and
    editing one by hand while forgetting its four scale variants is not a mistake
    anyone notices until the taskbar looks wrong on a high-DPI machine.
    Regenerate instead:

        pwsh build/New-AppAssets.ps1

    Output is deterministic, so a rerun that changes nothing leaves the working
    tree clean and a rerun after editing the mark shows exactly which assets
    moved.

.NOTES
    The colour is §9.4.2's brand accent, written as a literal. A PNG cannot
    carry a ThemeResource - an app icon has one appearance in every theme - so
    this is not the hard-coded hex §9.13 item 2 forbids, and
    build/Test-NoHexLiterals.ps1 scans XAML under src/ and C# under Views/ and
    Controls/ rather than this folder. If the brand ramp ever moves, it moves
    here too; there is no binding to inherit it through.
#>

[CmdletBinding()]
param(
    [string]$AssetRoot = (Join-Path $PSScriptRoot '..\src\WinZ3805A\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$AssetRoot = (Resolve-Path $AssetRoot).Path

# One colour, WzAccentBase #0E7C86, for the whole mark.
#
# The first version used WzAccentDark2 for the centre disc and it disappeared
# against a dark taskbar - an app icon has no theme to respond to, so any part
# of it near either end of the luminance range is invisible half the time. The
# mid-ramp teal is the one step that holds on both grounds.
$Accent = [System.Drawing.Color]::FromArgb(0xFF, 0x0E, 0x7C, 0x86)

function New-Mark {
    <#
    .SYNOPSIS
        Draws the mark into a square bitmap of the given edge length.

    .DESCRIPTION
        A segmented ring around a centre dot: StatusMedallion's 60-sample
        sparkline ring and its mode glyph, at the only fidelity an icon has room
        for.

        The segments are *arcs following the circle*, not radial spokes. Spokes
        were tried first and read as a flower rather than an instrument - round
        caps on a radial line are petals, and no amount of tuning the count
        fixed it.

        Below 24 px the gaps close and the ring becomes solid. That is the
        intended degradation rather than a second mark: twelve gaps at 16 px are
        each less than a pixel, so they antialias into a smudge that is dirtier
        than an honest ring and no longer recognisably the same shape.
    #>
    param([int]$Edge)

    $bmp = New-Object System.Drawing.Bitmap($Edge, $Edge, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $c = $Edge / 2.0

    # Radius to the centre of the stroke, leaving roughly Windows 11's icon-grid
    # margin once half the stroke width is added back.
    $radius = $Edge * 0.355
    $stroke = [float]([Math]::Max(1.0, $Edge * 0.115))

    $pen = New-Object System.Drawing.Pen($Accent, $stroke)
    $box = New-Object System.Drawing.RectangleF(
        [float]($c - $radius), [float]($c - $radius), [float]($radius * 2), [float]($radius * 2))

    if ($Edge -lt 24) {
        $g.DrawEllipse($pen, $box)
    }
    else {
        # Twelve segments on a 30 degree pitch. Butt caps, so a segment is an arc
        # of the ring rather than a lozenge sitting on it.
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat

        # The gap grows a little at small sizes: a fixed angular gap is fewer
        # pixels the smaller the icon, and closes up entirely before the solid
        # ring takes over.
        $gap = if ($Edge -lt 48) { 11.0 } else { 8.0 }

        for ($i = 0; $i -lt 12; $i++) {
            # -90 puts the first segment's leading edge at twelve o'clock.
            $start = $i * 30.0 - 90.0 + ($gap / 2.0)
            $g.DrawArc($pen, $box, [float]$start, [float](30.0 - $gap))
        }
    }

    $pen.Dispose()

    $dot = $Edge * 0.135
    $brush = New-Object System.Drawing.SolidBrush($Accent)
    $g.FillEllipse($brush, [float]($c - $dot), [float]($c - $dot), [float]($dot * 2), [float]($dot * 2))
    $brush.Dispose()

    $g.Dispose()
    return $bmp
}

function New-Canvas {
    <#
    .SYNOPSIS
        Centres the mark in a canvas that is not square - the wide tile and the
        splash screen.
    #>
    param([int]$Width, [int]$Height, [double]$MarkFraction = 1.0)

    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $edge = [int]([Math]::Min($Width, $Height) * $MarkFraction)
    $mark = New-Mark -Edge $edge
    $g.DrawImage($mark, [int](($Width - $edge) / 2), [int](($Height - $edge) / 2), $edge, $edge)
    $mark.Dispose()

    $g.Dispose()
    return $bmp
}

function Save-Asset {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Name)

    $size = "$($Bitmap.Width)x$($Bitmap.Height)"
    $Bitmap.Save((Join-Path $AssetRoot $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $Bitmap.Dispose()
    Write-Host ("  {0,-58} {1}" -f $Name, $size)
}

# The five MSIX scale factors. MSIX picks by the display's scaling, so a missing
# variant is not an error - it is a blurry icon on somebody else's monitor.
#
# A list of pairs rather than a hashtable keyed by percentage: PowerShell indexes
# an [ordered] dictionary by *position* when the subscript is an integer, so
# $scales[100] is an out-of-range read that returns $null rather than 1.0, and
# every asset comes out zero-sized with "Parameter is not valid" from GDI+.
$scales = @(
    @{ Percent = 100; Factor = 1.00 },
    @{ Percent = 125; Factor = 1.25 },
    @{ Percent = 150; Factor = 1.50 },
    @{ Percent = 200; Factor = 2.00 },
    @{ Percent = 400; Factor = 4.00 }
)

Write-Host "Writing assets to $AssetRoot"

foreach ($tile in @(
        @{ Name = 'Square44x44Logo'; Edge = 44 },
        @{ Name = 'Square71x71Logo'; Edge = 71 },
        @{ Name = 'Square150x150Logo'; Edge = 150 },
        @{ Name = 'Square310x310Logo'; Edge = 310 },
        @{ Name = 'StoreLogo'; Edge = 50 }
    )) {
    foreach ($scale in $scales) {
        $edge = [int][Math]::Round($tile.Edge * $scale.Factor)
        Save-Asset -Bitmap (New-Mark -Edge $edge) -Name "$($tile.Name).scale-$($scale.Percent).png"
    }
}

# targetsize variants drive the taskbar, Start's all-apps list, and Alt+Tab. The
# plated form sits on a Windows-drawn background and the unplated ones do not,
# which is the whole reason both exist.
foreach ($size in @(16, 24, 32, 48, 256)) {
    foreach ($form in @('', '_altform-unplated', '_altform-lightunplated')) {
        Save-Asset -Bitmap (New-Mark -Edge $size) -Name "Square44x44Logo.targetsize-$size$form.png"
    }
}

# Wide tile and splash screen: the mark centred and smaller relative to its
# canvas, so it is not stretched across a landscape rectangle.
foreach ($scale in $scales) {
    $f = $scale.Factor
    Save-Asset -Bitmap (New-Canvas -Width ([int](310 * $f)) -Height ([int](150 * $f)) -MarkFraction 0.72) `
        -Name "Wide310x150Logo.scale-$($scale.Percent).png"
    Save-Asset -Bitmap (New-Canvas -Width ([int](620 * $f)) -Height ([int](300 * $f)) -MarkFraction 0.55) `
        -Name "SplashScreen.scale-$($scale.Percent).png"
}

# The lock-screen badge is monochrome white by platform rule, not brand colour.
foreach ($scale in @(100, 200, 400)) {
    $edge = [Math]::Max([int](24 * ($scale / 100)), 24)
    $mark = New-Mark -Edge $edge
    $white = New-Object System.Drawing.Bitmap($mark.Width, $mark.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    for ($y = 0; $y -lt $mark.Height; $y++) {
        for ($x = 0; $x -lt $mark.Width; $x++) {
            $white.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($mark.GetPixel($x, $y).A, 255, 255, 255))
        }
    }

    $mark.Dispose()
    Save-Asset -Bitmap $white -Name "LockScreenLogo.scale-$scale.png"
}

# The window icon, which AppWindow.SetIcon loads and which is not an MSIX asset.
# The container is written by hand because System.Drawing can only *read* a
# multi-image .ico; Icon.Save round-trips one frame and silently drops the rest.
$icoSizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = foreach ($size in $icoSizes) {
    $mark = New-Mark -Edge $size
    $buffer = New-Object System.IO.MemoryStream
    $mark.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
    $mark.Dispose()
    , $buffer.ToArray()
}

$ico = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($ico)
$writer.Write([uint16]0)                    # reserved
$writer.Write([uint16]1)                    # type: icon
$writer.Write([uint16]$icoSizes.Count)

$offset = 6 + 16 * $icoSizes.Count
for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    # 256 is written as 0: the directory's width and height fields are one byte.
    $edge = [byte]$(if ($icoSizes[$i] -ge 256) { 0 } else { $icoSizes[$i] })
    $writer.Write($edge)                    # width
    $writer.Write($edge)                    # height
    $writer.Write([byte]0)                  # palette entries
    $writer.Write([byte]0)                  # reserved
    $writer.Write([uint16]1)                # colour planes
    $writer.Write([uint16]32)               # bits per pixel
    $writer.Write([uint32]$frames[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}

foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $AssetRoot 'AppIcon.ico'), $ico.ToArray())
$writer.Dispose()
Write-Host ("  {0,-58} {1} images" -f 'AppIcon.ico', $icoSizes.Count)

Write-Host 'Done.'
