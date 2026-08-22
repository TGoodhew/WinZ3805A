<#
.SYNOPSIS
    CI gate for A11Y-4 / §9.4.5: every token pair meets its contrast floor in Light and Dark.

.DESCRIPTION
    §9.12 and P0-16 both say A11Y-4 gates CI. Until now nothing computed a contrast ratio
    anywhere in the repository, so the claim was not true. This is that computation.

    TWO CHECKS, and the second is the one that keeps the first honest.

    1. CONTRAST. Every pair below is measured against its §9.4.5 floor, in Light and in Dark.

    2. INHERITANCE. Every §9.4.5 text and surface token must still resolve to a stock Fluent
       colour rather than a literal. Those colours are Microsoft's, chosen against each other,
       and this project inherits their guarantees by not overriding them. The day someone
       writes a literal into one of those tokens, this project has quietly taken ownership of
       a contrast relationship it did not previously own - and the numbers in
       fluent-stock-colours.txt stop describing what ships. That is worth failing on by
       itself, before any ratio is computed.

    ALPHA IS THE TRAP. Almost every stock token is semi-transparent: TextFillColorPrimary is
    89% black, LayerFillColorDefault is 50% white, CardStrokeColorDefault is 6% black. Reading
    them as opaque produces confident nonsense, so every colour is composited over what sits
    beneath it before its luminance is taken.

    WHAT THE BASE IS. Compositing has to bottom out somewhere, and it bottoms out at
    §9.4.1's opaque page background - WzPageBackgroundFallbackBrush, which is what the window
    shows when Mica is unavailable. Under Mica the true backdrop is a live blur of the user's
    wallpaper and is not knowable from any file. That is a real limit on this gate rather than
    an oversight, and it is the reason A11Y-4 also keeps a manual Accessibility Insights pass:
    only a tool looking at rendered pixels can measure the Mica case.

    HIGHCONTRAST IS NOT CHECKED. Its tokens resolve to SystemColor*, which are the user's own
    colours. Nothing here can know them, and asserting anything about them would be inventing
    a result. It stays a manual pass.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.EXAMPLE
    pwsh build/Test-ContrastFloor.ps1
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$colorsPath = Join-Path $Root 'src\WinZ3805A\Themes\Colors.xaml'
$stockPath  = Join-Path $Root 'build\fluent-stock-colours.txt'

foreach ($required in @($colorsPath, $stockPath)) {
    if (-not (Test-Path $required)) {
        Write-Error "Cannot find '$required'. If it moved, update this gate - do not delete it."
    }
}

# ---------------------------------------------------------------------------
# The measured stock colours.
# ---------------------------------------------------------------------------
$stock = @{}
foreach ($line in Get-Content -LiteralPath $stockPath) {
    if ($line -match '^\s*#' -or $line.Trim().Length -eq 0) { continue }
    $parts = $line -split '\s+' | Where-Object { $_.Length -gt 0 }
    if ($parts.Count -lt 3) { continue }
    if (-not $stock.ContainsKey($parts[0])) { $stock[$parts[0]] = @{} }
    $stock[$parts[0]][$parts[1]] = $parts[2]
}

if ($stock.Count -eq 0) {
    Write-Error "Parsed no colours from '$stockPath'. Fix the parse rather than removing the check."
}

# ---------------------------------------------------------------------------
# Colors.xaml, per theme. Deliberately the same resolution rule ThemePalette uses:
# follow {StaticResource} *within the asking theme*, because a token can point at
# different ramp steps in Light and Dark and a resolver fixed to one theme returns
# the same colour for both while looking entirely plausible.
# ---------------------------------------------------------------------------
$xaml = Get-Content -LiteralPath $colorsPath -Raw
$xaml = [regex]::Replace($xaml, '<!--.*?-->', '', 'Singleline')

$themes = @{}
foreach ($m in [regex]::Matches($xaml, '<ResourceDictionary x:Key="(?<name>Light|Dark|HighContrast)">(?<body>.*?)</ResourceDictionary>', 'Singleline')) {
    $tokens = @{}
    foreach ($t in [regex]::Matches($m.Groups['body'].Value, '<(?:SolidColorBrush|Color)\s+x:Key="(?<key>[A-Za-z0-9_]+)"(?:\s+Color="(?<val>[^"]*)")?\s*(?:/>|>(?<inner>[^<]*)<)')) {
        $v = if ($t.Groups['val'].Success) { $t.Groups['val'].Value } else { $t.Groups['inner'].Value }
        $tokens[$t.Groups['key'].Value] = $v.Trim()
    }
    $themes[$m.Groups['name'].Value] = $tokens
}

function ConvertFrom-Hex {
    param([string] $Text)

    $h = $Text.TrimStart('#')
    if ($h.Length -eq 6) { $h = 'FF' + $h }
    if ($h.Length -ne 8) { return $null }

    return [pscustomobject]@{
        A = [Convert]::ToInt32($h.Substring(0,2), 16)
        R = [Convert]::ToInt32($h.Substring(2,2), 16)
        G = [Convert]::ToInt32($h.Substring(4,2), 16)
        B = [Convert]::ToInt32($h.Substring(6,2), 16)
    }
}

# Returns the colour a token resolves to, or $null when it cannot be known here.
function Resolve-Token {
    param([string] $Theme, [string] $Key)

    $tokens = $themes[$Theme]
    for ($hop = 0; $hop -lt 8; $hop++) {
        if (-not $tokens.ContainsKey($Key)) { return $null }
        $v = $tokens[$Key]

        if ($v -match '^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$') { return ConvertFrom-Hex $v }

        if ($v -match '\{StaticResource\s+(?<next>[A-Za-z0-9_]+)\s*\}') { $Key = $Matches['next']; continue }

        if ($v -match '\{ThemeResource\s+(?<stock>[A-Za-z0-9_]+)\s*\}') {
            $name = $Matches['stock']
            if ($stock[$Theme] -and $stock[$Theme].ContainsKey($name)) { return ConvertFrom-Hex $stock[$Theme][$name] }
            return $null
        }

        return $null
    }
    return $null
}

# True when the token is a stock reference rather than a literal - check 2.
function Test-Inherited {
    param([string] $Theme, [string] $Key)

    $tokens = $themes[$Theme]
    if (-not $tokens.ContainsKey($Key)) { return $false }
    return $tokens[$Key] -match '\{ThemeResource\s+[A-Za-z0-9_]+\s*\}'
}

# Source-over composite, which is what the compositor does with a translucent brush.
function Merge-Colour {
    param([object] $Over, [object] $Under)

    $a = $Over.A / 255.0
    return [pscustomobject]@{
        A = 255
        R = [Math]::Round($Over.R * $a + $Under.R * (1 - $a))
        G = [Math]::Round($Over.G * $a + $Under.G * (1 - $a))
        B = [Math]::Round($Over.B * $a + $Under.B * (1 - $a))
    }
}

function Get-Luminance {
    param([object] $Colour)

    $channel = {
        param($v)
        $s = $v / 255.0
        if ($s -le 0.03928) { return $s / 12.92 }
        return [Math]::Pow((($s + 0.055) / 1.055), 2.4)
    }
    return 0.2126 * (& $channel $Colour.R) + 0.7152 * (& $channel $Colour.G) + 0.0722 * (& $channel $Colour.B)
}

function Get-ContrastRatio {
    param([object] $First, [object] $Second)

    $a = Get-Luminance $First
    $b = Get-Luminance $Second
    $hi = [Math]::Max($a, $b)
    $lo = [Math]::Min($a, $b)
    return ($hi + 0.05) / ($lo + 0.05)
}

# ---------------------------------------------------------------------------
# What §9.4.5 asks for.
#
# WzTextDisabledBrush is absent on purpose: WCAG 1.4.3 exempts text in an inactive
# control, and a disabled field that met the same floor as an enabled one would stop
# reading as disabled - the contrast IS the affordance.
# ---------------------------------------------------------------------------
$surfaces = @('WzLayerFillBrush', 'WzCardFillBrush', 'WzCardFillSecondaryBrush', 'WzOverlayFillBrush')
$bodyText = @('WzTextPrimaryBrush', 'WzTextSecondaryBrush', 'WzTextTertiaryBrush')
$graphics = @('WzSuccessBrush', 'WzCautionBrush', 'WzCriticalBrush', 'WzInfoBrush', 'WzNeutralBrush',
              'WzSeries1Brush', 'WzSeries2Brush', 'WzSeries3Brush', 'WzSeries4Brush',
              'WzSeries5Brush', 'WzSeries6Brush', 'WzSeries7Brush', 'WzSeries8Brush')


# Tokens this project has deliberately taken off stock Fluent, each with the issue that decided
# it and the reason. Check 2 exempts exactly these Theme|Token pairs and nothing else, so
# ownership stays a decision someone made rather than a thing that quietly happened.
#
# A row here is the OPPOSITE of a baseline row below: baselined means "fails, and we know";
# owned means "passes because we changed it, and we now own the relationship Fluent used to
# guarantee". Both still have to clear the floor - an owned token gets no contrast exemption.
$ownedTokens = @{
    'Light|WzTextTertiaryBrush' = '#176 - stock TextFillColorTertiary is 45% black, 3.28:1 against a 4.5:1 floor for the 12 px captions this application uses it for. Owned at 54.9% black. Dark still inherits.'
}

$inheritTokens = $surfaces + $bodyText + @('WzTextDisabledBrush', 'WzStrokeDefaultBrush', 'WzStrokeSubtleBrush')

# Known failures, each with the issue that owns it. The gate locks in "nothing new"; it does
# not pretend these pass. A baseline entry is a debt with a number on it, not an exemption -
# remove the row when the issue closes and the pair starts passing.
$baseline = @{
    'Light|WzSeries4Brush|WzCardFillBrush'               = '#177'
    'Light|WzSeries5Brush|WzCardFillBrush'               = '#177'
    'Dark|WzSeries5Brush|WzCardFillBrush'                = '#177'
}

$checked = 0
$failures = @()
$known = @()

foreach ($theme in @('Light', 'Dark')) {

    # ---- check 2, first: inheritance ----
    foreach ($token in $inheritTokens) {
        if ($ownedTokens.ContainsKey("$theme|$token")) { continue }
        if (-not (Test-Inherited $theme $token)) {
            $failures += [pscustomobject]@{
                Theme = $theme; What = $token; Against = '(inheritance)'; Ratio = 0.0; Floor = 0.0
                Why = 'is no longer a stock Fluent colour. This project has taken ownership of its contrast, and fluent-stock-colours.txt no longer describes what ships.'
            }
        }
    }

    $base = Resolve-Token $theme 'WzPageBackgroundFallbackBrush'
    if (-not $base) {
        Write-Error "$theme has no opaque page background to composite against. The gate cannot measure anything - fix the token rather than removing the check."
    }

    foreach ($surfaceKey in $surfaces) {
        $raw = Resolve-Token $theme $surfaceKey
        if (-not $raw) { continue }
        $surface = Merge-Colour $raw $base

        foreach ($textKey in $bodyText) {
            $rawText = Resolve-Token $theme $textKey
            if (-not $rawText) { continue }

            $ratio = Get-ContrastRatio (Merge-Colour $rawText $surface) $surface
            $checked++

            # 4.5:1. Every use of these tokens in this application is WzCaptionTextStyle (12 px)
            # or WzBodyTextStyle (14 px), and both are "small text" - §9.4.5's 3:1 row starts at
            # 18.66 px semibold.
            if ($ratio -lt 4.5) {
                $key = "$theme|$textKey|$surfaceKey"
                $row = [pscustomobject]@{
                    Theme = $theme; What = $textKey; Against = $surfaceKey
                    Ratio = $ratio; Floor = 4.5; Why = 'body text below §9.4.5''s 4.5:1 floor'
                }
                if ($baseline.ContainsKey($key)) { $known += [pscustomobject]@{ Row = $row; Issue = $baseline[$key] } }
                else { $failures += $row }
            }
        }
    }

    # Meaningful graphics: severity colours and chart series, on the card they are drawn on.
    $card = Merge-Colour (Resolve-Token $theme 'WzCardFillBrush') $base
    foreach ($graphicKey in $graphics) {
        $raw = Resolve-Token $theme $graphicKey
        if (-not $raw) { continue }

        $ratio = Get-ContrastRatio (Merge-Colour $raw $card) $card
        $checked++

        if ($ratio -lt 3.0) {
            $key = "$theme|$graphicKey|WzCardFillBrush"
            $row = [pscustomobject]@{
                Theme = $theme; What = $graphicKey; Against = 'WzCardFillBrush'
                Ratio = $ratio; Floor = 3.0; Why = 'meaningful non-text below §9.4.5''s 3:1 floor'
            }
            if ($baseline.ContainsKey($key)) { $known += [pscustomobject]@{ Row = $row; Issue = $baseline[$key] } }
            else { $failures += $row }
        }
    }
}

Write-Host "Checked $checked pair(s) across Light and Dark, composited over the §9.4.1 opaque page background."
Write-Host "HighContrast is not checked: its tokens are the user's own SystemColor* choices (manual pass)."

if ($ownedTokens.Count -gt 0) {
    Write-Host ''
    Write-Host "$($ownedTokens.Count) token(s) deliberately owned rather than inherited:" -ForegroundColor Cyan
    foreach ($pair in $ownedTokens.GetEnumerator() | Sort-Object Name) {
        Write-Host ("  {0}" -f $pair.Name) -ForegroundColor Cyan
        Write-Host ("    {0}" -f $pair.Value) -ForegroundColor DarkCyan
    }
}

if ($known.Count -gt 0) {
    Write-Host ''
    Write-Host "$($known.Count) known failure(s), each owned by an open issue:" -ForegroundColor DarkYellow
    foreach ($k in $known) {
        Write-Host ("  {0,-5} {1,-24} on {2,-26} {3,6:N2} : 1  (floor {4})  {5}" -f `
            $k.Row.Theme, $k.Row.What, $k.Row.Against, $k.Row.Ratio, $k.Row.Floor, $k.Issue) -ForegroundColor DarkYellow
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "FAIL: $($failures.Count) pair(s) below the §9.4.5 floor with no issue owning them." -ForegroundColor Red
    foreach ($f in $failures) {
        if ($f.Against -eq '(inheritance)') {
            Write-Host ("  {0,-5} {1} {2}" -f $f.Theme, $f.What, $f.Why) -ForegroundColor Red
        }
        else {
            Write-Host ("  {0,-5} {1,-24} on {2,-26} {3,6:N2} : 1  (floor {4})  {5}" -f `
                $f.Theme, $f.What, $f.Against, $f.Ratio, $f.Floor, $f.Why) -ForegroundColor Red
        }
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Write-Host "::error::$($f.Theme) $($f.What) on $($f.Against): $($f.Why). See docs/requirements.md §9.4.5."
        }
    }
    Write-Host ''
    Write-Host 'A colour that fails here is unreadable for someone, in a theme they may not be able' -ForegroundColor Yellow
    Write-Host 'to leave. Fix the pairing or the token - do not add a baseline row without an issue.' -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host 'PASS: every measured pair meets its §9.4.5 floor, and every text and surface token either inherits its colour from Fluent or is listed above as owned.' -ForegroundColor Green
exit 0
