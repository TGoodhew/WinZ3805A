<#
.SYNOPSIS
    Re-photographs the guide's close-up images: the main window and its parts, the
    time-zone flyout, compact mode, the connection dialog, the Details chrome, and
    single controls (#556).
.DESCRIPTION
    Capture-GuideImages.ps1 photographs whole pages. The other images in
    docs\images\how-to-use - main-*.png, details-*.png, settings-*.png,
    connection-dialog.png, satellites-sky-plot.png, satellites-tracked-table.png
    and timing-ti-trend.png - were taken by hand, so nothing could re-take them,
    and by 29 Sep 2026 several showed a window the application no longer draws:
    the footer said "updated just now", which it has not said since #547.

    EACH IMAGE IS A CROP OF A CONTAINER, NOT A PICTURE OF ONE ELEMENT. The first
    draft of this script photographed each element by its x:Name, and most of the
    names it needed are grids and stack panels, which UI Automation does not expose
    - so winapp fell back to the whole window, silently, for most of them. What
    works is to photograph something UI Automation does report (the window's root
    pane) and cut out the union of the reported bounds of the controls that make up
    the image, plus a margin. Both come from the same `winapp ui inspect` read, so
    the crop cannot disagree with the capture about where anything is.

    A few images need a step first - the main window is sized, the flyout opened,
    compact mode entered, the connection dialog opened - and each is undone before
    the next image. The main window is put back where it was at the end.

    IT NEEDS THE RECEIVER AND THE DESKTOP, like Capture-GuideImages.ps1: the
    readouts are photographed live, and both windows are driven - including real
    mouse clicks - while this runs. The main window and the Details window must
    both be open. It is a release step, listed in docs\manual-qa.md section 13,
    not a CI job.

    LOOK AT EVERY IMAGE AFTERWARDS. It crops a control that had not finished
    loading just as willingly as one that had.
.PARAMETER ProcessId
    The running WinZ3805A.
.PARAMETER Only
    Image names (without .png) to take; defaults to all of them.
.PARAMETER OutputDirectory
    Defaults to docs\images\how-to-use.
.NOTES
    Needs the Windows App Development CLI: winget install Microsoft.WinAppCli.

    main-compact.png is in the set but was NOT re-taken on 29 Sep 2026: compact mode
    was drawing the figures-of-merit pills clipped under the medallion (#565), and a
    picture of a defect is not an illustration of the feature.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$ProcessId,

    [string[]]$Only,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'docs\images\how-to-use' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

if (-not (Get-Command winapp -ErrorAction SilentlyContinue)) {
    throw 'winapp was not found. Install it with: winget install Microsoft.WinAppCli'
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CloseupWin {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

# The size the guide's main-window pictures were taken at. The medallion, the readouts and the
# footer all reflow with the width, so a different size is a different picture, not a larger one.
$MainWidth = 546
$MainHeight = 613

function Wanted([string]$name) { -not $Only -or $Only -contains $name }

function Quiet([scriptblock]$block) { & $block 2>&1 | Out-Null }

# Every element of a window, keyed by winapp's selector - which is the x:Name where there is one,
# and a slug otherwise. JSON rather than the text tree, because the text tree wraps long accessible
# names across lines and separates a name from its bounds. The first occurrence of a selector wins.
function Get-Elements([long]$window) {
    $json = winapp ui inspect -w $window -d 16 --json 2>$null | Out-String | ConvertFrom-Json
    $map = [ordered]@{}
    $stack = [System.Collections.Generic.Stack[object]]::new()
    foreach ($w in $json.windows) { foreach ($e in $w.elements) { $stack.Push($e) } }
    while ($stack.Count -gt 0) {
        $e = $stack.Pop()
        if ($e.selector -and -not $map.Contains($e.selector) -and [int]$e.width -gt 0) {
            $map[$e.selector] = [pscustomobject]@{
                Type = $e.type; Name = $e.name; Node = $e
                X = [int]$e.x; Y = [int]$e.y; W = [int]$e.width; H = [int]$e.height
            }
        }
        foreach ($c in $e.children) { $stack.Push($c) }
    }
    $map
}

# The window's root content pane: the widest unnamed pane, which carries the title bar and every
# control. Its screen position is known, so a picture of it can be cropped by screen coordinates.
function Get-RootPane($elements) {
    $elements.Keys | Where-Object { $_ -like 'pn-*' -and $elements[$_].Type -eq 'Pane' -and -not $elements[$_].Name } |
        Sort-Object { $elements[$_].W * $elements[$_].H } -Descending | Select-Object -First 1
}

function Save-Bitmap([System.Drawing.Bitmap]$src, [int]$x, [int]$y, [int]$w, [int]$h, [string]$name) {
    # The container's image can be a pixel short of its reported bounds; clamp to what was drawn.
    $x = [Math]::Max(0, $x); $y = [Math]::Max(0, $y)
    $w = [Math]::Min($src.Width - $x, $w); $h = [Math]::Min($src.Height - $y, $h)
    $cut = $src.Clone([System.Drawing.Rectangle]::new($x, $y, $w, $h), $src.PixelFormat)
    try { $cut.Save((Join-Path $OutputDirectory "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $cut.Dispose() }
    Write-Host ('{0,-36} {1}x{2}' -f "$name.png", $w, $h)
}

# Photographs the window's root pane and cuts out the union of the parts' bounds, plus a margin.
# A part ending in * matches every selector with that prefix - the navigation items, for one.
function Save-Crop([long]$window, [string]$name, [string[]]$parts, [int]$margin) {
    if (-not (Wanted $name)) { return }

    $els = Get-Elements $window
    $root = Get-RootPane $els
    if (-not $root) { throw "No root pane in window $window." }
    $c = $els[$root]

    $boxes = foreach ($p in $parts) {
        $keys = @($els.Keys | Where-Object { $_ -like $p })
        if (-not $keys) { Write-Warning "$name`: no element '$p'" }
        foreach ($k in $keys) { $els[$k] }
    }
    if (-not $boxes) { throw "$name`: none of $($parts -join ', ') found." }

    $left = ($boxes | ForEach-Object X | Measure-Object -Minimum).Minimum - $margin
    $top = ($boxes | ForEach-Object Y | Measure-Object -Minimum).Minimum - $margin
    $right = ($boxes | ForEach-Object { $_.X + $_.W } | Measure-Object -Maximum).Maximum + $margin
    $bottom = ($boxes | ForEach-Object { $_.Y + $_.H } | Measure-Object -Maximum).Maximum + $margin

    $whole = Join-Path ([IO.Path]::GetTempPath()) "winz3805a-closeup-$([guid]::NewGuid().ToString('N')).png"
    Quiet { winapp ui screenshot $root -w $window -o $whole }
    $src = [System.Drawing.Bitmap]::FromFile($whole)
    try { Save-Bitmap $src ($left - $c.X) ($top - $c.Y) ($right - $left) ($bottom - $top) $name }
    finally { $src.Dispose(); Remove-Item $whole -ErrorAction SilentlyContinue }
}

# One element that UI Automation does expose - a button, a whole pane - photographed as itself.
function Save-Element([long]$window, [string]$name, [string]$selector) {
    if (-not (Wanted $name)) { return }
    $out = Join-Path $OutputDirectory "$name.png"
    Quiet { winapp ui screenshot $selector -w $window -o $out }
    if (-not (Test-Path $out)) { throw "No image for $name." }
    $b = [System.Drawing.Bitmap]::FromFile($out)
    Write-Host ('{0,-36} {1}x{2}' -f "$name.png", $b.Width, $b.Height)
    $b.Dispose()
}

function Save-RootPane([long]$window, [string]$name) {
    if (-not (Wanted $name)) { return }
    Save-Element $window $name (Get-RootPane (Get-Elements $window))
}

function Get-Rect([long]$window) {
    $r = New-Object CloseupWin+RECT
    [void][CloseupWin]::GetWindowRect([IntPtr]$window, [ref]$r)
    $r
}

function Resize([long]$window, [int]$x, [int]$y, [int]$w, [int]$h) {
    [void][CloseupWin]::MoveWindow([IntPtr]$window, $x, $y, $w, $h, $true)
    Start-Sleep -Milliseconds 900
}

function Open-Page([string]$page) {
    $els = Get-Elements $details
    $item = $els.Keys | Where-Object { $_ -like "itm-$page-*" } | Select-Object -First 1
    if (-not $item) { throw "No navigation item for '$page'." }
    Quiet { winapp ui invoke $item -w $details }
    # A page reads from the receiver when it is navigated to; photographing at once catches dashes.
    Start-Sleep -Seconds 4
}

function Show-Element([string]$selector) {
    Quiet { winapp ui scroll-into-view $selector -w $details }
    Start-Sleep -Milliseconds 600
}

# ------------------------------------------------------------------------------ the two windows
# They share a title, so they are told apart by what they contain: only the main window has a
# Details button, and only the Details window has navigation items. Handles are printed in decimal.
$windows = winapp ui list-windows -a $ProcessId 2>&1 | ForEach-Object { "$_" } |
    Select-String -Pattern 'HWND (\d+)' | ForEach-Object { [long]$_.Matches[0].Groups[1].Value }

$main = $null
$details = $null
foreach ($handle in $windows) {
    $keys = (Get-Elements $handle).Keys
    if ($keys -like 'itm-overview-*') { $details = $handle } elseif ($keys -contains 'DetailsButton') { $main = $handle }
}

if (-not $main) { throw 'No main window found. Bring it back from the notification area first.' }
if (-not $details) { throw 'No Details window found. Open it from the main window (Ctrl+D) first.' }

$original = Get-Rect $main
Resize $main $original.Left $original.Top $MainWidth $MainHeight

try {
    # ---------------------------------------------------------------------------- main window
    # A click on the mode words takes focus off whichever button had it, so no image carries a
    # focus ring the reader would take for part of the control.
    Quiet { winapp ui click ModeText -w $main }
    Start-Sleep -Milliseconds 500

    Save-RootPane $main 'main-window'
    Save-Crop $main 'main-medallion-and-mode' @('Medallion', 'ModeText') 12
    Save-Crop $main 'main-readouts' @('Satellites', 'TimeInterval') 8
    Save-Crop $main 'main-figures-of-merit' @('TfomPill', 'FfomPill') 12
    Save-Crop $main 'main-clock-line' @('ClockText', 'RolloverBadge', 'ZoneButton') 12
    Save-Crop $main 'main-footer' @('FooterText', 'DetailsButton', 'AlwaysOnTopButton', 'ConnectButton') 12
    Save-Element $main 'main-zone-button' 'ZoneButton'
    Save-Element $main 'main-pin-button' 'AlwaysOnTopButton'

    if (Wanted 'main-time-zone-flyout') {
        # A click, not an invoke: invoking the button on 29 Sep 2026 produced its tooltip and no
        # flyout. The flyout is a popup, so it is cut from a capture of the SCREEN under the
        # window, whose origin is the window's own rectangle.
        Quiet { winapp ui click ZoneButton -w $main }
        Start-Sleep -Seconds 1

        $els = Get-Elements $main
        $toggle = $els['UseMachineZone']
        if (-not $toggle) { throw 'The time-zone flyout did not open.' }
        # The flyout presenter: the smallest element that contains the toggle and is wider than it.
        $flyout = $els.Values | Where-Object {
            $_.X -le $toggle.X -and $_.Y -le $toggle.Y -and
            ($_.X + $_.W) -ge ($toggle.X + $toggle.W) -and ($_.Y + $_.H) -ge ($toggle.Y + $toggle.H) -and
            $_.W -ge 360 -and $_.W -lt $MainWidth - 40
        } | Sort-Object { $_.W * $_.H } | Select-Object -First 1
        if (-not $flyout) { throw 'Could not find the flyout around the toggle.' }

        $window = Get-Rect $main
        $whole = Join-Path ([IO.Path]::GetTempPath()) "winz3805a-flyout-$([guid]::NewGuid().ToString('N')).png"
        Quiet { winapp ui screenshot -w $main --capture-screen -o $whole }
        $src = [System.Drawing.Bitmap]::FromFile($whole)
        try { Save-Bitmap $src ($flyout.X - $window.Left) ($flyout.Y - $window.Top) $flyout.W $flyout.H 'main-time-zone-flyout' }
        finally { $src.Dispose(); Remove-Item $whole -ErrorAction SilentlyContinue }

        Quiet { winapp ui send-keys escape -w $main }
        Start-Sleep -Milliseconds 600
    }

    if (Wanted 'main-compact') {
        Quiet { winapp ui send-keys 'ctrl+shift+m' -w $main }
        Start-Sleep -Seconds 1
        Save-RootPane $main 'main-compact'
        Quiet { winapp ui send-keys 'ctrl+shift+m' -w $main }
        Start-Sleep -Seconds 1
        Resize $main $original.Left $original.Top $MainWidth $MainHeight
    }

    if (Wanted 'connection-dialog') {
        # The dialog is taller than the guide's main window and is clipped inside it, so the window
        # is made taller for this one image. The Details window's status pill opens the dialog on
        # the main window; Cancel closes it without touching the link.
        $top = [Math]::Max(0, $original.Top - 150)
        Resize $main $original.Left $top $MainWidth 800
        Quiet { winapp ui invoke StatusPill -w $details }
        Start-Sleep -Seconds 1
        Save-Crop $main 'connection-dialog' @('Title', 'ContentScrollViewer', 'PrimaryButton', 'CloseButton') 24
        Quiet { winapp ui invoke CloseButton -w $main }
        Start-Sleep -Milliseconds 600
        Resize $main $original.Left $original.Top $MainWidth $MainHeight
    }

    # ---------------------------------------------------------------------------- Details chrome
    # On Overview first: Diagnostics and Status registers have a Refresh button of their own with
    # the same x:Name as the title bar's, and the first one found would be photographed.
    Open-Page 'overview'
    Save-Element $details 'details-title-bar' 'AppTitleBar'
    Save-Element $details 'details-refresh' 'RefreshButton'
    Save-Element $details 'details-export' 'ExportButton'
    Save-Element $details 'details-settings' 'SettingsButton'
    Save-Element $details 'details-help' 'HelpButton'
    Save-Crop $details 'details-nav' @('TogglePaneButton', 'itm-*') 4

    # ---------------------------------------------------------------------------- page parts
    if ((Wanted 'satellites-sky-plot') -or (Wanted 'satellites-tracked-table')) {
        Open-Page 'satellites'
        Show-Element 'SkyPlot'
        Save-Crop $details 'satellites-sky-plot' @('SkyPlot') 0
        # The list's own bounds hold only its rows; the column headings sit above it in the card.
        Show-Element 'TrackedRows'
        Save-Crop $details 'satellites-tracked-table' @('StrengthHeader', 'TrackedRows') 8
    }

    if (Wanted 'timing-ti-trend') {
        Open-Page 'timing'
        Show-Element 'TimeIntervalTrend'
        Save-Crop $details 'timing-ti-trend' @('TimeIntervalTrend') 0
    }

    $settings = @(
        @{ Name = 'settings-consoleswitch'; Selector = 'ConsoleSwitch' },
        @{ Name = 'settings-experimentalswitch'; Selector = 'ExperimentalSwitch' },
        @{ Name = 'settings-activitylampswitch'; Selector = 'ActivityLampSwitch' },
        @{ Name = 'settings-systemaccentswitch'; Selector = 'SystemAccentSwitch' },
        @{ Name = 'settings-locknotificationsswitch'; Selector = 'LockNotificationsSwitch' },
        @{ Name = 'settings-keeprunningswitch'; Selector = 'KeepRunningSwitch' },
        @{ Name = 'settings-startminimisedswitch'; Selector = 'StartMinimisedSwitch' },
        @{ Name = 'settings-exitbutton'; Selector = 'ExitButton' })

    if ($settings | Where-Object { Wanted $_.Name }) {
        Open-Page 'settings'
        foreach ($s in $settings) {
            if (-not (Wanted $s.Name)) { continue }
            Show-Element $s.Selector
            Save-Crop $details $s.Name @($s.Selector) 4
        }
    }
}
finally {
    Resize $main $original.Left $original.Top ($original.Right - $original.Left) ($original.Bottom - $original.Top)
}

Write-Host ''
Write-Host 'Now look at every image. A control that had not loaded is cropped just as willingly.'
