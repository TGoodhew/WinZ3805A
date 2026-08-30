<#
.SYNOPSIS
    CI gate for §9.4.4 / A11Y-12: every pair of chart series stays distinguishable, including
    to a deuteranope and a protanope.

.DESCRIPTION
    WHY THIS EXISTS. §9.4.4 said the categorical palette was "derived from the Okabe-Ito
    colour-universal palette", which is designed so that every pair separates under the common
    dichromacies. Three of its eight entries were then substituted for values that read better
    as thin lines - a reasonable-looking change that passed review, and that silently gave up
    the one property the palette had been chosen for. #87 found series 1 and 7 at 4.5 dE00
    under deuteranopia, which is not two colours. Nothing in the repository could have caught
    that, so nothing did, for three months.

    This is that check. It is the same shape as Test-ContrastFloor.ps1 and exists for the same
    reason: a design property nobody can verify by looking is a design property that decays.

    WHAT IS CHECKED. All 28 pairs, in Light and in Dark, under three vision models - normal,
    deuteranopia and protanopia - with CIEDE2000 as the metric. Plus two constraints the
    derivation had to learn the hard way, each recorded here so the next revision inherits them
    rather than rediscovering them:

      1. SEPARATION. Every pair, every model, at or above MinSeparation.
      2. HUE SPACING. Categorical colours have to be nameable, not merely measurable. An early
         candidate scored well by putting two browns and two purples in the ramp separated by
         lightness; that passes a dE00 threshold and fails a person asked which trace is which.
      3. CLEAR OF §9.4.3. A trace coloured as critical implies an alarm nobody asserted, which
         is why §9.4.4 is a separate namespace. That is a perceptual claim, so it is measured.
         The neutral series is exempt: series 8 is grey and WzNeutralBrush is grey, and both
         mean "nothing is being asserted". Requiring those two apart makes the constraint
         unsatisfiable, which is exactly what happened on the first attempt.

    HIGHCONTRAST IS NOT CHECKED, and cannot be. Its series resolve to SystemColorWindowText and
    SystemColorHighlight - the user's own two colours, alternating. Eight traces cannot be told
    apart by colour there at all, which is not a defect this gate can fix: it is the reason
    #87's second channel (dash pattern plus direct labelling) is required regardless of what
    this ramp contains. Do not read a pass here as "the chart is accessible".

    THE MATHS. Vienot, Brettel & Mollon's LMS projection for the dichromat simulation and
    CIEDE2000 for the difference - the same pair #87's analysis used, so the numbers here and
    the numbers there are comparable. build/palette/derive.py reproduces #87's eight
    published figures to within 0.3, and this script agrees with it.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER MinSeparation
    The dE00 floor for a pair. Default 8.0.

    WHY 8 AND NOT THE MEASURED 10.5. The palette measures 10.5 at its worst, so the floor sits
    below what shipped on purpose. A gate pinned to the current value fails on a one-step
    refinement of an entry and teaches everyone to edit the threshold, which is how a gate
    stops meaning anything. 8 is comfortably above the 3-5 that #87 called a collapse, and
    leaves room to tune a colour without an argument.

.EXAMPLE
    pwsh build/Test-SeriesSeparation.ps1
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [double] $MinSeparation = 8.0,
    [double] $MinHueGap = 28.0,
    [double] $MinSemanticDistance = 9.0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$colorsPath = Join-Path $Root 'src\WinZ3805A\Themes\Colors.xaml'
if (-not (Test-Path $colorsPath)) {
    Write-Error "Cannot find '$colorsPath'. If it moved, update this gate - do not delete it."
}

# ---------------------------------------------------------------------------
# Colors.xaml, per theme. Comments are stripped first, the way the contrast gate does it,
# so that prose quoting a token name is never mistaken for a definition.
# ---------------------------------------------------------------------------
$xaml = Get-Content -LiteralPath $colorsPath -Raw
$xaml = [regex]::Replace($xaml, '<!--.*?-->', '', 'Singleline')

$themes = @{}
foreach ($m in [regex]::Matches($xaml, '<ResourceDictionary x:Key="(?<name>Light|Dark|HighContrast)">(?<body>.*?)</ResourceDictionary>', 'Singleline')) {
    $tokens = @{}
    foreach ($t in [regex]::Matches($m.Groups['body'].Value, '<SolidColorBrush\s+x:Key="(?<key>[A-Za-z0-9_]+)"\s+Color="(?<val>[^"]*)"')) {
        $tokens[$t.Groups['key'].Value] = $t.Groups['val'].Value.Trim()
    }
    $themes[$m.Groups['name'].Value] = $tokens
}

# The colour a token resolves to, or $null when it is not a literal this script can measure.
function Get-Literal {
    param([string] $Theme, [string] $Key)

    $tokens = $themes[$Theme]
    for ($hop = 0; $hop -lt 8; $hop++) {
        if (-not $tokens.ContainsKey($Key)) { return $null }
        $v = $tokens[$Key]
        if ($v -match '^#(?<h>[0-9a-fA-F]{6})$') {
            $h = $Matches['h']
            return , @([Convert]::ToInt32($h.Substring(0, 2), 16),
                       [Convert]::ToInt32($h.Substring(2, 2), 16),
                       [Convert]::ToInt32($h.Substring(4, 2), 16))
        }
        if ($v -match '\{StaticResource\s+(?<next>[A-Za-z0-9_]+)\s*\}') { $Key = $Matches['next']; continue }
        return $null
    }
    return $null
}

# ---------------------------------------------------------------------------
# Colour maths.
# ---------------------------------------------------------------------------
function Get-Linear {
    param([double] $V)

    $s = $V / 255.0
    if ($s -le 0.04045) { return $s / 12.92 }
    return [Math]::Pow((($s + 0.055) / 1.055), 2.4)
}

function Get-FromLinear {
    param([double] $V)

    $v = [Math]::Max(0.0, [Math]::Min(1.0, $V))
    if ($v -le 0.0031308) { return 12.92 * $v * 255.0 }
    return (1.055 * [Math]::Pow($v, 1.0 / 2.4) - 0.055) * 255.0
}

function ConvertTo-Lab {
    param([int[]] $Rgb)

    $r = Get-Linear $Rgb[0]; $g = Get-Linear $Rgb[1]; $b = Get-Linear $Rgb[2]
    $x = (0.4124564 * $r + 0.3575761 * $g + 0.1804375 * $b) / 0.95047
    $y = (0.2126729 * $r + 0.7151522 * $g + 0.0721750 * $b) / 1.00000
    $z = (0.0193339 * $r + 0.1191920 * $g + 0.9503041 * $b) / 1.08883

    $f = {
        param($t)
        if ($t -gt (216.0 / 24389.0)) { return [Math]::Pow($t, 1.0 / 3.0) }
        return (841.0 / 108.0) * $t + 4.0 / 29.0
    }

    $fx = & $f $x; $fy = & $f $y; $fz = & $f $z
    # Each element is parenthesised because PowerShell's comma binds TIGHTER than binary
    # minus: '116.0 * $fy - 16.0, 500.0 * ...' parses as a subtraction of an array.
    return , @((116.0 * $fy - 16.0), (500.0 * ($fx - $fy)), (200.0 * ($fy - $fz)))
}

# Vienot, Brettel & Mollon: project onto the dichromat's single surviving plane in LMS.
function ConvertTo-Dichromat {
    param([int[]] $Rgb, [string] $Kind)

    $r = Get-Linear $Rgb[0]; $g = Get-Linear $Rgb[1]; $b = Get-Linear $Rgb[2]
    $l = 17.8824 * $r + 43.5161 * $g + 4.11935 * $b
    $m = 3.45565 * $r + 27.1554 * $g + 3.86714 * $b
    $s = 0.0299566 * $r + 0.184309 * $g + 1.46709 * $b

    if ($Kind -eq 'protan') { $l = 2.02344 * $m - 2.52581 * $s }
    else                    { $m = 0.494207 * $l + 1.24827 * $s }

    $r2 = 0.0809444479 * $l - 0.130504409 * $m + 0.116721066 * $s
    $g2 = -0.0102485335 * $l + 0.0540193266 * $m - 0.113614708 * $s
    $b2 = -0.000365296938 * $l - 0.00412161469 * $m + 0.693511405 * $s

    return , @([int][Math]::Round((Get-FromLinear $r2)),
               [int][Math]::Round((Get-FromLinear $g2)),
               [int][Math]::Round((Get-FromLinear $b2)))
}

function Get-DeltaE2000 {
    param([double[]] $Lab1, [double[]] $Lab2)

    $L1 = $Lab1[0]; $a1 = $Lab1[1]; $b1 = $Lab1[2]
    $L2 = $Lab2[0]; $a2 = $Lab2[1]; $b2 = $Lab2[2]

    $C1 = [Math]::Sqrt($a1 * $a1 + $b1 * $b1)
    $C2 = [Math]::Sqrt($a2 * $a2 + $b2 * $b2)
    $Cb = ($C1 + $C2) / 2.0
    $Cb7 = [Math]::Pow($Cb, 7)
    $G = 0.5 * (1 - [Math]::Sqrt($Cb7 / ($Cb7 + [Math]::Pow(25.0, 7))))

    $a1p = (1 + $G) * $a1
    $a2p = (1 + $G) * $a2
    $C1p = [Math]::Sqrt($a1p * $a1p + $b1 * $b1)
    $C2p = [Math]::Sqrt($a2p * $a2p + $b2 * $b2)

    $h1p = if ($b1 -eq 0 -and $a1p -eq 0) { 0.0 } else { (([Math]::Atan2($b1, $a1p) * 180 / [Math]::PI) + 360) % 360 }
    $h2p = if ($b2 -eq 0 -and $a2p -eq 0) { 0.0 } else { (([Math]::Atan2($b2, $a2p) * 180 / [Math]::PI) + 360) % 360 }

    $dLp = $L2 - $L1
    $dCp = $C2p - $C1p

    if ($C1p * $C2p -eq 0) { $dhp = 0.0 }
    else {
        $d = $h2p - $h1p
        $dhp = if ($d -gt 180) { $d - 360 } elseif ($d -lt -180) { $d + 360 } else { $d }
    }
    $dHp = 2 * [Math]::Sqrt($C1p * $C2p) * [Math]::Sin(($dhp * [Math]::PI / 180) / 2)

    $Lbp = ($L1 + $L2) / 2.0
    $Cbp = ($C1p + $C2p) / 2.0

    if ($C1p * $C2p -eq 0) { $hbp = $h1p + $h2p }
    else {
        $d = [Math]::Abs($h1p - $h2p)
        $sum = $h1p + $h2p
        $hbp = if ($d -le 180) { $sum / 2 } elseif ($sum -lt 360) { ($sum + 360) / 2 } else { ($sum - 360) / 2 }
    }

    $rad = { param($deg) $deg * [Math]::PI / 180 }
    $T = 1 - 0.17 * [Math]::Cos((& $rad ($hbp - 30))) `
           + 0.24 * [Math]::Cos((& $rad (2 * $hbp))) `
           + 0.32 * [Math]::Cos((& $rad (3 * $hbp + 6))) `
           - 0.20 * [Math]::Cos((& $rad (4 * $hbp - 63)))
    $dTheta = 30 * [Math]::Exp(-[Math]::Pow(($hbp - 275) / 25.0, 2))
    $Cbp7 = [Math]::Pow($Cbp, 7)
    $Rc = 2 * [Math]::Sqrt($Cbp7 / ($Cbp7 + [Math]::Pow(25.0, 7)))
    $Sl = 1 + (0.015 * [Math]::Pow($Lbp - 50, 2)) / [Math]::Sqrt(20 + [Math]::Pow($Lbp - 50, 2))
    $Sc = 1 + 0.045 * $Cbp
    $Sh = 1 + 0.015 * $Cbp * $T
    $Rt = -[Math]::Sin((& $rad (2 * $dTheta))) * $Rc

    return [Math]::Sqrt([Math]::Pow($dLp / $Sl, 2) + [Math]::Pow($dCp / $Sc, 2) + [Math]::Pow($dHp / $Sh, 2) `
                        + $Rt * ($dCp / $Sc) * ($dHp / $Sh))
}

function Get-Hue {
    param([int[]] $Rgb)

    $lab = ConvertTo-Lab $Rgb
    return ((([Math]::Atan2($lab[2], $lab[1]) * 180 / [Math]::PI) + 360) % 360)
}

# Chroma is what tells a coloured series from the neutral one, which several rules exempt.
function Get-Chroma {
    param([int[]] $Rgb)

    $lab = ConvertTo-Lab $Rgb
    return [Math]::Sqrt($lab[1] * $lab[1] + $lab[2] * $lab[2])
}

# ---------------------------------------------------------------------------
$seriesKeys   = 1..8 | ForEach-Object { "WzSeries${_}Brush" }
$semanticKeys = @('WzSuccessBrush', 'WzCautionBrush', 'WzCriticalBrush', 'WzInfoBrush')
$models       = @('normal', 'deutan', 'protan')
$neutralFloor = 8.0

$failures = @()
$checked  = 0
$worstAll = [double]::MaxValue

foreach ($theme in @('Light', 'Dark')) {

    $series = @{}
    foreach ($key in $seriesKeys) {
        $c = Get-Literal $theme $key
        if (-not $c) {
            $failures += "[$theme] $key does not resolve to a literal colour, so its separation cannot be measured. Every §9.4.4 token is a literal by design."
            continue
        }
        $series[$key] = $c
    }
    if ($series.Count -ne 8) { continue }

    # ---- 1. every pair, every vision model ----
    $worstTheme = [double]::MaxValue
    foreach ($model in $models) {
        $view = @{}
        foreach ($key in $seriesKeys) {
            $view[$key] = if ($model -eq 'normal') { ConvertTo-Lab $series[$key] }
                          else { ConvertTo-Lab (ConvertTo-Dichromat $series[$key] $model) }
        }

        for ($i = 0; $i -lt 8; $i++) {
            for ($j = $i + 1; $j -lt 8; $j++) {
                $d = Get-DeltaE2000 $view[$seriesKeys[$i]] $view[$seriesKeys[$j]]
                $checked++
                if ($d -lt $worstTheme) { $worstTheme = $d }
                if ($d -lt $MinSeparation) {
                    $failures += ("[{0}] series {1} and {2} are {3:N1} dE00 apart under {4} (floor {5:N1}). To that viewer they are one colour." -f `
                        $theme, ($i + 1), ($j + 1), $d, $model, $MinSeparation)
                }
            }
        }
    }
    if ($worstTheme -lt $worstAll) { $worstAll = $worstTheme }
    Write-Host ("  {0,-5} worst pair across all three vision models: {1,5:N1} dE00" -f $theme, $worstTheme)

    # ---- 2. hues have to be nameable, not merely measurable ----
    $chromatic = @()
    foreach ($key in $seriesKeys) {
        if ((Get-Chroma $series[$key]) -ge $neutralFloor) {
            $chromatic += , @($key, (Get-Hue $series[$key]))
        }
    }
    for ($i = 0; $i -lt $chromatic.Count; $i++) {
        for ($j = $i + 1; $j -lt $chromatic.Count; $j++) {
            $gap = [Math]::Abs($chromatic[$i][1] - $chromatic[$j][1])
            if ($gap -gt 180) { $gap = 360 - $gap }
            if ($gap -lt $MinHueGap) {
                $failures += ("[{0}] {1} and {2} are only {3:N0} degrees apart in hue (floor {4:N0}). Two entries of one hue read as 'the two brown ones' however far apart they measure." -f `
                    $theme, $chromatic[$i][0], $chromatic[$j][0], $gap, $MinHueGap)
            }
        }
    }

    # ---- 3. clear of the §9.4.3 semantics ----
    foreach ($key in $seriesKeys) {
        if ((Get-Chroma $series[$key]) -lt $neutralFloor) { continue }     # the neutral slot, see the header
        foreach ($sem in $semanticKeys) {
            $s = Get-Literal $theme $sem
            if (-not $s) { continue }
            $d = Get-DeltaE2000 (ConvertTo-Lab $series[$key]) (ConvertTo-Lab $s)
            if ($d -lt $MinSemanticDistance) {
                $failures += ("[{0}] {1} is only {2:N1} dE00 from {3} (floor {4:N1}). A trace that close to a severity colour implies an alarm nobody asserted." -f `
                    $theme, $key, $d, $sem, $MinSemanticDistance)
            }
        }
    }
}

Write-Host ''
Write-Host "Checked $checked series pairings across Light and Dark under normal vision, deuteranopia and protanopia."
Write-Host 'HighContrast is not checked: its series alternate between two SystemColor* values, so eight traces'
Write-Host 'cannot be separated by colour there at all. That is what #87''s second channel is for.'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "FAIL: $($failures.Count) problem(s) with the §9.4.4 palette." -ForegroundColor Red
    foreach ($f in $failures) {
        Write-Host "  $f" -ForegroundColor Red
        if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::error::$f See docs/requirements.md §9.4.4." }
    }
    Write-Host ''
    Write-Host 'The last time an entry here changed for a good-looking reason, the palette quietly stopped' -ForegroundColor Yellow
    Write-Host 'separating for dichromats and stayed that way for three months (#87). Re-derive with' -ForegroundColor Yellow
    Write-Host 'build/palette/derive.py rather than nudging a value until this passes.' -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host ("PASS: every §9.4.4 pair separates by at least {0:N1} dE00 in both themes and all three vision models (worst {1:N1})." -f $MinSeparation, $worstAll) -ForegroundColor Green
exit 0
