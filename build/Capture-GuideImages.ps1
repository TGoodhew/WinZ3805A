<#
.SYNOPSIS
    Re-photographs the pages the user's guide shows (#358).

.DESCRIPTION
    docs\images\how-to-use\page-*.png are the screenshots in docs\how-to-use.md,
    which is also the application's F1 help. They were taken by hand, and the
    audit on 30 Aug 2026 found what that costs: page-holdover.png still showed a
    "Threshold" card with "Enter holdover above" and an "Apply threshold" button
    long after the page had been rebuilt around two settings with different
    names, and page-satellites-2.png had no elevation-mask slider because the
    slider did not exist when the picture was taken.

    A WRONG SCREENSHOT IS WORSE THAN A MISSING ONE. Prose that has drifted reads
    as prose; a picture is read as evidence, and a reader who sees a control in
    the guide that is not in the application concludes they have the wrong
    version. This exists to make re-taking them cheap enough to do whenever a
    page changes, rather than a chore deferred until the pictures describe a
    different program.

    IT NEEDS THE RECEIVER AND THE DESKTOP. The pages are photographed live, with
    real values in them - a guide illustrated with em dashes teaches nothing - so
    a connected receiver is a prerequisite and the window is driven while this
    runs. It is not a CI job and never will be; docs\manual-qa.md section 13 is
    where it is listed as a release step.

.PARAMETER ProcessId
    The running WinZ3805A. Required: this drives an app rather than starting one,
    because which build is being photographed is a decision, not a default.

.PARAMETER Pages
    Nav tags to photograph. Defaults to the pages the guide illustrates.

.PARAMETER OutputDirectory
    Defaults to docs\images\how-to-use.

.PARAMETER ContentWidth
    The page area's width. DEFAULTS TO 860 AND THE NUMBER IS LOAD-BEARING: since
    #351 the cards flow into as many columns as fit, and the threshold is 864 -
    (w + 24) / (420 + 24) reaching 2. At 860 every page is a single column, which
    is what the guide's prose and its "upper half" / "lower half" pairs are
    written around. Widen this past 863 and every picture silently becomes a
    two-column layout the text beside it does not describe.

.NOTES
    IT PHOTOGRAPHS THE CONTENT PANE AS AN ELEMENT, NOT THE WINDOW WITH A CROP.
    Two earlier attempts cropped a window capture and both were wrong - once by
    150 px - because the capture includes the drop shadow and by how much is not
    reliably knowable from the window rect: measured here, a 1136 x 859 window
    captured as 1253 x 880. Asking UI Automation for the pane's own bounds moves
    that problem to the one component that already knows the answer.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$ProcessId,

    [string[]]$Pages,

    [string]$OutputDirectory,

    [int]$ContentWidth = 860,

    [int]$ContentHeight = 778
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'docs\images\how-to-use' }
if (-not $Pages) {
    $Pages = @('overview', 'satellites', 'position', 'timing', 'holdover',
               'time', 'statusregisters', 'diagnostics', 'settings', 'advancedconsole')
}

if ($ContentWidth -gt 863) {
    Write-Warning ("A content width of $ContentWidth puts the cards into two columns (the threshold " +
                   'is 864). The guide is written around a single column - see this script''s notes.')
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

function Get-Tree([int]$handle, [int]$depth) {
    winapp ui inspect -w $handle -d $depth 2>&1 | ForEach-Object { $_ -replace '\x1b\[[0-9;]*m', '' }
}

function Get-Bounds([string[]]$tree, [string]$pattern) {
    # .*? and not [^(]*: an element's own label can contain brackets - the
    # NavigationView reports as "Nav Unknown(50025) (138,203 1120x778)" - and a
    # pattern that stops at the first "(" never reaches the bounds.
    $found = $tree | Select-String -Pattern "$pattern.*?\((\d+),(\d+) (\d+)x(\d+)\)" | Select-Object -First 1
    if (-not $found) { return $null }

    $g = $found.Matches[0].Groups
    [pscustomobject]@{
        Left = [int]$g[1].Value; Top = [int]$g[2].Value
        Width = [int]$g[3].Value; Height = [int]$g[4].Value
    }
}

# The page's content is a vertically scrolling pane sitting immediately right of
# the navigation pane. Found by that relationship rather than by its slug, which
# is regenerated every time the tree is read.
function Get-ContentPane([int]$handle, [int]$contentLeft, [int]$expectedWidth) {
    foreach ($line in (Get-Tree $handle 9)) {
        if ($line -match '^\s*(\S+) Pane \[scroll:v\] \((\d+),(\d+) (\d+)x(\d+)\)') {
            # BOTH the left edge and the width. Keying on the left alone matched
            # the NavigationView itself on some pages and an inner scroller on
            # others, and the only symptom was an image of the wrong size - which
            # is why the caller now asserts the size too.
            $leftMatches = [Math]::Abs([int]$Matches[2] - $contentLeft) -le 4
            $widthMatches = [Math]::Abs([int]$Matches[4] - $expectedWidth) -le 4

            if ($leftMatches -and $widthMatches) {
                return [pscustomobject]@{ Selector = $Matches[1]; Width = [int]$Matches[4]; Height = [int]$Matches[5] }
            }
        }
    }
    return $null
}

# The Details window is the one carrying a navigation pane. Identified by what it
# contains rather than by size or z-order, either of which is a coincidence that
# holds only until someone resizes the main window.
$hwnd = $null
foreach ($line in (winapp ui list-windows -a $ProcessId 2>&1 | ForEach-Object { $_ -replace '\x1b\[[0-9;]*m', '' })) {
    if ($line -match 'HWND (\d+):') {
        $candidate = [int]$Matches[1]
        if ((Get-Tree $candidate 8) -match 'PaneRoot') { $hwnd = $candidate; break }
    }
}

if (-not $hwnd) { throw 'No Details window found. Open it from the main window (Ctrl+D) first.' }
Write-Host "Details window: $hwnd"

$rect = New-Object Win+RECT
[void][Win]::GetWindowRect([IntPtr]$hwnd, [ref]$rect)

$pane = Get-Bounds (Get-Tree $hwnd 8) 'PaneRoot'
if (-not $pane) { throw 'Could not measure the navigation pane.' }

$inset = $pane.Left - $rect.Left
$currentWidth = ($rect.Right - $inset) - ($pane.Left + $pane.Width)
$currentHeight = ($rect.Bottom - $inset) - $pane.Top

$targetWidth = ($rect.Right - $rect.Left) + ($ContentWidth - $currentWidth)
$targetHeight = ($rect.Bottom - $rect.Top) + ($ContentHeight - $currentHeight)

if ($targetWidth -lt 400 -or $targetWidth -gt 6000 -or $targetHeight -lt 300 -or $targetHeight -gt 4000) {
    throw ("Computed a window of ${targetWidth}x${targetHeight}, which is not a window. The " +
           'measurement went wrong rather than the arithmetic; check `winapp ui inspect`.')
}

Write-Host "Sizing to ${targetWidth}x${targetHeight} for a ${ContentWidth}x${ContentHeight} page area."
[void][Win]::MoveWindow([IntPtr]$hwnd, $rect.Left, $rect.Top, $targetWidth, $targetHeight, $true)
Start-Sleep -Milliseconds 900

[void][Win]::GetWindowRect([IntPtr]$hwnd, [ref]$rect)
$pane = Get-Bounds (Get-Tree $hwnd 8) 'PaneRoot'
$contentLeft = $pane.Left + $pane.Width

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$fileNames = @{ statusregisters = 'status-registers'; advancedconsole = 'advanced-console' }

foreach ($page in $Pages) {
    $item = (Get-Tree $hwnd 12 | Select-String -Pattern "itm-$page-[0-9a-f]+" | Select-Object -First 1)
    if (-not $item) { Write-Warning "No navigation item for '$page'; skipped."; continue }

    winapp ui invoke $item.Matches[0].Value -w $hwnd | Out-Null

    # A page reads from the receiver when it is navigated to, and those reads are
    # round trips. Photographing immediately catches empty fields - which is how
    # an early attempt produced a Holdover page whose duration limit was blank, a
    # picture that would have taught a reader the field does not work.
    Start-Sleep -Seconds 5

    $name = if ($fileNames.ContainsKey($page)) { $fileNames[$page] } else { $page }

    # A page that fits its window has no scrolling pane - the Advanced Console is
    # one - so there is a second way in: photograph the NavigationView, whose
    # bounds UI Automation reports exactly, and take the navigation pane off the
    # left. Still an element capture, so still no window shadow in the arithmetic.
    $viaNav = $null
    if (-not (Get-ContentPane $hwnd $contentLeft $ContentWidth)) {
        $viaNav = Get-Bounds (Get-Tree $hwnd 7) 'Nav '
        if (-not $viaNav) { Write-Warning "No content on '$page'; skipped."; continue }
    }

    # Resolved ONCE per page and reused for both halves. A pane's reported bounds
    # change when it is scrolled, so re-resolving after the scroll matched a
    # different element on the Timing page and captured 859x852 of something
    # else. Scrolling a pane does not change which pane it is.
    $content = Get-ContentPane $hwnd $contentLeft $ContentWidth

    foreach ($half in @('', '-2')) {
        if (-not $content -and $half -eq '-2') { break }
        if (-not $content -and -not $viaNav) { Write-Warning "No content pane on '$page'; skipped."; break }

        if ($half -eq '-2') {
            # The lower half exists only where there is one. A page that fits
            # entirely gets one picture, and the guide should not carry a second
            # that is the same view scrolled nowhere.
            if ($content.Height -ge 1 -and -not (winapp ui scroll $content.Selector -w $hwnd --to bottom 2>&1 |
                    Select-String -Quiet 'error|cannot')) {
                Start-Sleep -Milliseconds 900
            }
            else {
                continue
            }
        }

        $out = Join-Path $OutputDirectory "page-$name$half.png"

        if ($content) {
            winapp ui screenshot $content.Selector -w $hwnd -o $out | Out-Null
        }
        else {
            $whole = Join-Path ([IO.Path]::GetTempPath()) "winz3805a-$page-nav.png"
            winapp ui screenshot 'Nav' -w $hwnd -o $whole | Out-Null

            Add-Type -AssemblyName System.Drawing
            $bitmap = [System.Drawing.Bitmap]::FromFile($whole)
            try {
                $cropX = $contentLeft - $viaNav.Left
                $width = [Math]::Min($ContentWidth, $bitmap.Width - $cropX)
                $height = [Math]::Min($ContentHeight, $bitmap.Height)
                $area = New-Object System.Drawing.Rectangle $cropX, 0, $width, $height
                $cropped = $bitmap.Clone($area, $bitmap.PixelFormat)
                try { $cropped.Save($out, [System.Drawing.Imaging.ImageFormat]::Png) }
                finally { $cropped.Dispose() }
            }
            finally { $bitmap.Dispose(); Remove-Item $whole -ErrorAction SilentlyContinue }
        }

        # A scrolled pane reports a taller rectangle than its viewport - measured,
        # 852 against 777 - so the capture comes back with more than is on screen.
        # Trimmed to the BOTTOM of it, which is what "the lower half" means, and
        # which keeps every image the same size.
        Add-Type -AssemblyName System.Drawing
        $shot = [System.Drawing.Bitmap]::FromFile($out)
        try {
            if ($shot.Height -gt $ContentHeight -or $shot.Width -gt $ContentWidth) {
                $area = New-Object System.Drawing.Rectangle 0, ($shot.Height - [Math]::Min($ContentHeight, $shot.Height)),
                    ([Math]::Min($ContentWidth, $shot.Width)), ([Math]::Min($ContentHeight, $shot.Height))
                $trimmed = $shot.Clone($area, $shot.PixelFormat)
                try {
                    $shot.Dispose()
                    $shot = $null
                    $trimmed.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
                }
                finally { $trimmed.Dispose() }
            }
        }
        finally { if ($shot) { $shot.Dispose() } }

        # Asserted, not hoped for. Every earlier failure of this script produced a
        # PLAUSIBLE image of the wrong thing - the navigation pane, an inner
        # scroller, the window with its title bar - and the only way to tell was
        # to look at all seventeen. The size is the cheap proxy for "the right
        # element", so it is checked here rather than discovered in review.
        Add-Type -AssemblyName System.Drawing
        $check = [System.Drawing.Bitmap]::FromFile($out)
        $checkWidth, $checkHeight = $check.Width, $check.Height
        $actual = "${checkWidth}x${checkHeight}"
        $check.Dispose()
        $check = [pscustomobject]@{ Width = $checkWidth; Height = $checkHeight }

        # Within a couple of pixels: the pane sits inside the window by a hairline
        # that varies with the theme, so demanding an exact match fails on an
        # off-by-one that means nothing. Two pixels still cannot be the wrong
        # element - the ones this caught were out by 60 and by 260.
        if ([Math]::Abs($check.Width - $ContentWidth) -gt 2 -or
            [Math]::Abs($check.Height - $ContentHeight) -gt 2) {
            throw ("$name$half captured $actual, not ${ContentWidth}x${ContentHeight}. That is the " +
                   'wrong element, not a wrong size - look at what `winapp ui inspect` reports for ' +
                   'this page before trusting any image from this run.')
        }

        Write-Host ("  {0,-20} {1,-32} {2}" -f "$name$half", (Split-Path $out -Leaf), $actual)
    }

    # Leave the page at the top so the next navigation starts clean.
    if ($content) { winapp ui scroll $content.Selector -w $hwnd --to top 2>&1 | Out-Null }
}

Write-Host ''
Write-Host "Written to $OutputDirectory."
Write-Host 'LOOK AT EVERY ONE before committing: this drives the application, and a page that failed'
Write-Host 'to load, or had not finished reading, photographs just as willingly as one that did.'
