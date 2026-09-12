<#
.SYNOPSIS
    Fails when a Wz* resource key is referenced and defined nowhere.

.DESCRIPTION
    §9's design system is a set of named tokens, and every `{ThemeResource Wz...}`
    in the tree is an assumption that somebody spelled one correctly. Until
    13 Sep 2026 nothing checked that assumption: the token system was verified
    from the DEFINITION side - Test-ThemeDictionaryParity asks that a key defined
    in one theme is defined in the others - and never from the USAGE side.

    The failure that prompted this (#512, #520) is worse than a typo deserves to
    be. A missing resource key is not a compile error and not a visual glitch: it
    throws while the page's XAML is being loaded, so the page simply does not
    open. A card on the Timing page referenced `{ThemeResource WzSpaceS}`, which
    has never existed - the §9.6 scale is Xxs / Xs / Sm / Md / Lg / Xl / Xxl /
    3Xl / 4Xl. The result was a navigation item that highlighted while the frame
    stayed on the previous page, with nothing logged, the app responsive, and the
    receiver still polling. It looked exactly like a click that had missed. It
    compiled, and all fifteen other gates passed.

    Only the `Wz` prefix is checked, and that is the honest scope rather than a
    convenience. Stock Fluent keys - AccentButtonStyle, SystemColorWindowTextColor
    and several hundred others - are referenced here and defined in the SDK's own
    dictionaries, which this repository does not contain. Checking those would
    need an allowlist of somebody else's API surface, which would rot. `Wz*` is
    what this project defines, so `Wz*` is what it can verify.

    C# is scanned as well as XAML. ThemePalette reads Colors.xaml at run time and
    AccentRamp names brushes as string literals, so a key can be reached without
    any XAML mentioning it - and a typo there fails the same way, at run time,
    with the same silence.

.NOTES
    Comments are stripped before scanning, for the reason Test-SpacingScale
    strips them: a dictionary's own header may quote an example, and a gate that
    fails on the illustration of its rule would be its own worst advertisement.

    Definitions are collected from every scanned XAML file rather than from
    Themes/*.xaml alone. A page may legitimately define a key in its own
    Page.Resources, and the question this gate asks is "does this reference
    resolve", not "is it in the folder I expected".
#>

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $PSScriptRoot '..\src')
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = (Resolve-Path $Root).Path

function Remove-Comments {
    <#
    .SYNOPSIS
        Strips XML comments, so an example in a header is not read as a reference.
    #>
    param([string]$Text)

    return [regex]::Replace($Text, '<!--.*?-->', '', 'Singleline')
}

function Source-Files {
    param([string]$Extension)

    return Get-ChildItem -Path $root -Recurse -Filter "*.$Extension" -File |
        Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }
}

# ---- what exists ------------------------------------------------------------------

$defined = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$xamlFiles = @(Source-Files 'xaml')

foreach ($file in $xamlFiles) {
    $text = Remove-Comments (Get-Content $file.FullName -Raw)
    foreach ($match in [regex]::Matches($text, 'x:Key="(Wz[A-Za-z0-9_]*)"')) {
        [void]$defined.Add($match.Groups[1].Value)
    }
}

# ---- what is asked for ------------------------------------------------------------

$failures = @()

foreach ($file in $xamlFiles) {
    $lines = (Remove-Comments (Get-Content $file.FullName -Raw)) -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        # Both markup extensions. StaticResource is rarer here - §9.13 requires
        # ThemeResource for anything that must re-resolve on a theme change - but a
        # misspelling in either one fails identically.
        foreach ($match in [regex]::Matches($lines[$i], '\{(?:Theme|Static)Resource\s+(Wz[A-Za-z0-9_]*)\s*\}')) {
            $key = $match.Groups[1].Value
            if (-not $defined.Contains($key)) {
                $failures += [pscustomobject]@{
                    File = $file.FullName.Replace("$repo\", '')
                    Line = $i + 1
                    Key  = $key
                    Text = $match.Value
                }
            }
        }
    }
}

$csFiles = @(Source-Files 'cs')

foreach ($file in $csFiles) {
    $lines = Get-Content $file.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        # A comment mentioning a key is prose, not a lookup. This is deliberately
        # cruder than the XAML pass: C# comments are line-oriented here, and a
        # key named inside a /* */ block is still only prose.
        if ($line -match '^\s*(//|///|\*)') {
            continue
        }

        foreach ($match in [regex]::Matches($line, '"(Wz[A-Za-z0-9_]*)"')) {
            $key = $match.Groups[1].Value
            if (-not $defined.Contains($key)) {
                $failures += [pscustomobject]@{
                    File = $file.FullName.Replace("$repo\", '')
                    Line = $i + 1
                    Key  = $key
                    Text = $match.Value
                }
            }
        }
    }
}

Write-Host ("Scanned {0} XAML and {1} C# file(s) against {2} defined Wz* key(s)." -f
    $xamlFiles.Count, $csFiles.Count, $defined.Count)

if ($failures.Count -eq 0) {
    Write-Host 'PASS: every Wz* resource key referenced is defined.'
    exit 0
}

Write-Host ''
Write-Host "FAIL: $($failures.Count) reference(s) to a Wz* key that is defined nowhere."
foreach ($f in $failures) {
    Write-Host ("  {0}:{1}  {2}" -f $f.File, $f.Line, $f.Text)
}

Write-Host ''
Write-Host 'A missing resource key is not caught by the compiler and does not degrade gracefully:'
Write-Host 'the page throws while its XAML is loading, so it silently will not open. The navigation'
Write-Host 'item highlights, the frame stays where it was, and nothing is logged.'
Write-Host ''
Write-Host 'Check the spelling against src/WinZ3805A/Themes/. The §9.6 spacing scale in particular is'
Write-Host 'Xxs, Xs, Sm, Md, Lg, Xl, Xxl, 3Xl, 4Xl - there is no WzSpaceS.'
exit 1
