<#
.SYNOPSIS
    Fails when XAML uses a spacing value outside §9.6's scale or a corner radius
    outside §9.3's three.

.DESCRIPTION
    §9.13 item 4: "Only 4 / 8 / circle (§9.3) and only the §9.6 spacing scale. A
    Margin="13,7,13,9" anywhere is a defect."

    That is a counting rule, and counting rules are what CI is for. It was
    review-only until 15 Aug 2026, when the §15 step 11 anti-pattern audit found
    **nine** off-scale values across four pages that had each passed review:
    Padding="0,3" on the log rows in Diagnostics and Status Registers,
    Margin="0,0,0,6" on three Position field labels, and Margin="28,0,0,0" on
    four Timing indents. None is a bug anyone would ever notice, which is exactly
    why none of them was caught - an off-by-two margin is invisible one at a time
    and is how a spacing scale stops being one.

    §9.6's scale is 4, 8, 12, 16, 20, 24, 32, 40, 48. §9.3's radii are 4 and 8,
    plus the circle, which belongs to the state medallion alone and is expressed
    there as a CornerRadius equal to half the side rather than as a literal.
    Zero is allowed everywhere: it is the absence of spacing, not a step.

    Only spacing and radii are checked. BorderThickness is a stroke width and is
    governed by §9.2 and §9.4.5, not by the spacing scale - SkyPlotControl's
    1 px and 1.5 px marker outlines are correct and must not be dragged onto this
    scale.

.NOTES
    Comments are stripped before scanning. Spacing.xaml's own header quotes
    §9.13's Margin="13,7,13,9" example, and a gate that fails on the
    specification's illustration of the rule would be its own worst advertisement.
#>

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $PSScriptRoot '..\src')
)

$ErrorActionPreference = 'Stop'

# §9.6.  Zero is not a step on the scale; it is the absence of one, and is allowed.
$scale = @(0, 4, 8, 12, 16, 20, 24, 32, 40, 48)

# §9.3.  The circle is not here: it belongs to one control and is computed there.
$radii = @(0, 4, 8)

$spacingAttributes = 'Margin|Padding|Spacing|ColumnSpacing|RowSpacing'

function Values {
    <#
    .SYNOPSIS
        Splits an attribute value into its numbers.

    .DESCRIPTION
        A Thickness is written as one number, two, or four, separated by commas or
        spaces; Spacing is always one. Returning them as doubles rather than
        strings is what makes "8" and "8.0" the same value, which matters because
        both appear in hand-written XAML.
    #>
    param([string]$Raw)

    return $Raw -split '[ ,]+' |
        Where-Object { $_ -ne '' } |
        ForEach-Object { [double]$_ }
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$files = Get-ChildItem (Resolve-Path $Root) -Recurse -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

$failures = @()

foreach ($file in $files) {
    $text = Get-Content $file.FullName -Raw
    if ($null -eq $text) { continue }

    # Blank out comments while preserving newlines, so reported line numbers
    # still point at the real line. Singleline so . spans a multi-line comment.
    $text = [regex]::Replace($text, '<!--.*?-->', {
            param($m) ($m.Value -replace '[^\r\n]', ' ')
        }, [Text.RegularExpressions.RegexOptions]::Singleline)

    $lines = $text -split "`r`n|`n|`r"

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        foreach ($match in [regex]::Matches($line, "($spacingAttributes)=`"([0-9 ,.]+)`"")) {
            $bad = (Values $match.Groups[2].Value) | Where-Object { $scale -notcontains $_ }
            if ($bad) {
                $failures += [pscustomobject]@{
                    File = $file.FullName.Replace("$repo\", '')
                    Line = $i + 1
                    Text = $match.Value
                    Why  = "not on the §9.6 scale (" + ($bad -join ', ') + ')'
                }
            }
        }

        foreach ($match in [regex]::Matches($line, 'CornerRadius="([0-9 ,.]+)"')) {
            $bad = (Values $match.Groups[1].Value) | Where-Object { $radii -notcontains $_ }
            if ($bad) {
                $failures += [pscustomobject]@{
                    File = $file.FullName.Replace("$repo\", '')
                    Line = $i + 1
                    Text = $match.Value
                    Why  = "not a §9.3 radius (" + ($bad -join ', ') + ')'
                }
            }
        }
    }
}

Write-Host "Scanned $($files.Count) XAML file(s) for off-scale spacing and corner radii."

if ($failures.Count -eq 0) {
    Write-Host 'PASS: every spacing value is on the §9.6 scale and every radius is a §9.3 one.'
    exit 0
}

Write-Host ''
Write-Host "FAIL: $($failures.Count) off-scale value(s)."
foreach ($f in $failures) {
    Write-Host ("  {0}:{1}  {2}" -f $f.File, $f.Line, $f.Text)
    Write-Host ("      {0}" -f $f.Why)
}

Write-Host ''
Write-Host '§9.6 spacing scale: 4, 8, 12, 16, 20, 24, 32, 40, 48 (and 0).'
Write-Host '§9.3 corner radii:  4 and 8. No 2, no 6, no 12, no 16.'
Write-Host 'Pick the nearest step. A value that genuinely cannot come from the scale is a'
Write-Host 'specification question (§9.13 item 4), not a local exception.'
exit 1
