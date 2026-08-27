<#
.SYNOPSIS
    CI gate for A11Y-2 / §9.12: the two-tone focus visual clears 3:1 against ANY surface.

.DESCRIPTION
    §9.12, as amended by #187, judges the focus visual as the two-tone assembly Fluent draws
    rather than stroke by stroke: for each adjacent surface, at least one stroke must clear 3:1.

    That reading is what makes the criterion checkable at all, because the surface is not knowable
    from source. The accent-filled button uses stock AccentButtonStyle, and this application does
    NOT remap AccentFillColorDefault - so the fill behind the focus ring is the END USER'S Windows
    accent colour. Every measurement of it is therefore specific to the machine it was taken on:
    3.06:1 on 24 Aug and 3.10:1 on 27 Aug are both against Tony's default blue, and say nothing
    about a user who has chosen yellow.

    So this does not check a measured pair. It checks the PROPERTY that makes the pair irrelevant:
    one stroke near black and one near white cover the whole luminance range between them, and no
    surface can sit far enough from both to defeat them. A colour cannot be within 3:1 of black and
    within 3:1 of white at the same time.

    The gate walks every surface luminance from 0 to 1 and asserts the better of the two strokes
    clears the floor at each. Measured worst cases when this was written: Light 4.14:1 at L=0.200,
    Dark 4.43:1 at L=0.187. Both comfortable, and both a property of the strokes rather than of
    anyone's accent.

    WHAT THIS PROTECTS. If a future change gives either stroke a mid-tone - a "softer" focus ring,
    a themed one, a brand-coloured one - the covering property is lost and some surface defeats the
    assembly. That is invisible to review, invisible to Test-ContrastFloor (which cannot see focus
    visuals), and only shows up on the machine of a user whose accent happens to land in the gap.
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [double] $Floor = 3.0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = Join-Path $Root 'build/fluent-stock-colours.txt'
if (-not (Test-Path $source)) { Write-Error "Missing $source." }

function Get-Luminance([string] $argb) {
    $h = $argb.TrimStart('#')
    if ($h.Length -eq 8) { $h = $h.Substring(2) }
    $ch = @(0, 2, 4) | ForEach-Object {
        $c = [Convert]::ToInt32($h.Substring($_, 2), 16) / 255.0
        if ($c -le 0.03928) { $c / 12.92 } else { [Math]::Pow((($c + 0.055) / 1.055), 2.4) }
    }
    (0.2126 * $ch[0]) + (0.7152 * $ch[1]) + (0.0722 * $ch[2])
}

function Get-Contrast([double] $a, [double] $b) {
    $hi = [Math]::Max($a, $b); $lo = [Math]::Min($a, $b)
    ($hi + 0.05) / ($lo + 0.05)
}

# Rows look like:  Light  FocusVisualPrimary  #FF1B1A1B
$strokes = @{}
foreach ($line in Get-Content -LiteralPath $source) {
    if ($line -match '^\s*#') { continue }
    if ($line -match '^\s*(?<theme>Light|Dark)\s+(?<key>FocusVisual\w+)\s+(?<hex>#[0-9A-Fa-f]{6,8})\s*$') {
        $strokes["$($Matches.theme)/$($Matches.key)"] = $Matches.hex
    }
}

$required = @('Light/FocusVisualPrimary', 'Light/FocusVisualSecondary',
              'Dark/FocusVisualPrimary',  'Dark/FocusVisualSecondary')
$missing = @($required | Where-Object { -not $strokes.ContainsKey($_) })
if ($missing.Count -gt 0) {
    Write-Error "fluent-stock-colours.txt is missing: $($missing -join ', '). This gate cannot pass by finding nothing to check."
}

$failures = @()
$report = @()

foreach ($theme in @('Light', 'Dark')) {
    $lp = Get-Luminance $strokes["$theme/FocusVisualPrimary"]
    $ls = Get-Luminance $strokes["$theme/FocusVisualSecondary"]

    $worst = [double]::MaxValue
    $worstAt = 0.0
    for ($i = 0; $i -le 1000; $i++) {
        $surface = $i / 1000.0
        $best = [Math]::Max((Get-Contrast $lp $surface), (Get-Contrast $ls $surface))
        if ($best -lt $worst) { $worst = $best; $worstAt = $surface }
    }

    $report += "  {0,-6} worst case {1:N2}:1 at surface luminance {2:N3}" -f $theme, $worst, $worstAt
    if ($worst -lt $Floor) {
        $failures += "  $theme : a surface at luminance $([Math]::Round($worstAt,3)) defeats both strokes - best is $([Math]::Round($worst,2)):1 against a $Floor`:1 floor."
    }
}

if ($failures.Count -gt 0) {
    Write-Host 'FAIL: the two-tone focus visual does not cover every surface (A11Y-2, §9.12).' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host ''
    Write-Host 'One stroke must sit near black and the other near white. A mid-tone stroke breaks the' -ForegroundColor Yellow
    Write-Host 'covering property, and the failure only appears for users whose accent lands in the gap.' -ForegroundColor Yellow
    exit 1
}

$report | ForEach-Object { Write-Host $_ }
Write-Host "PASS: the two-tone focus visual clears $Floor`:1 against every possible surface, in both themes."
