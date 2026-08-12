<#
.SYNOPSIS
    CI gate for §9.4 / A11Y-8: every ThemeDictionaries block defines the same keys, of
    the same type, in Light, Dark and HighContrast alike.

.DESCRIPTION
    §9.4 requires all colour to be declared under three theme dictionaries, and A11Y-8
    makes high contrast a first-class theme rather than a degraded one. A key present in
    Light but missing from HighContrast still compiles, still passes review, and still
    renders correctly on the developer's machine. It fails at run time, only for the
    user who needs high contrast most, as an unresolved-resource crash or an invisible
    control.

    That is the specific failure this gate exists to make impossible. Light and Dark get
    exercised whenever anyone runs the app; HighContrast realistically does not, because
    testing it means switching the whole desktop over.

    Two things are checked per dictionary block:

      - Key parity. Every key defined in any theme is defined in all of them.
      - Type parity. A key declared as a SolidColorBrush in one theme and a Color in
        another binds successfully in one and throws in the other.

    It also requires all three themes to be present. Two out of three is the same bug
    caught one step earlier.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.EXAMPLE
    pwsh build/Test-ThemeDictionaryParity.ps1
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$src = Join-Path $Root 'src'
if (-not (Test-Path $src)) {
    Write-Error "No src/ directory under '$Root'."
}

$xamlNs = 'http://schemas.microsoft.com/winfx/2006/xaml'
$required = @('Light', 'Dark', 'HighContrast')

# @() so a single match is still a collection. Under Set-StrictMode, .Count on a scalar
# that PowerShell unwrapped from a one-element pipeline is an error, and a one-file tree
# is exactly what a focused test of this gate produces.
$targets = @(Get-ChildItem -Path $src -Recurse -Filter '*.xaml' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

$failures = @()
$checked = 0

foreach ($file in $targets) {
    $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName)

    try {
        [xml] $doc = Get-Content -LiteralPath $file.FullName -Raw
    }
    catch {
        $failures += "${relative}: is not well-formed XML - $($_.Exception.Message)"
        continue
    }

    $blocks = @($doc.SelectNodes('//*[local-name()="ResourceDictionary.ThemeDictionaries"]'))
    if ($blocks.Count -eq 0) { continue }

    foreach ($block in $blocks) {
        $checked++
        $themes = @{}

        foreach ($dict in $block.ChildNodes) {
            if ($dict.NodeType -ne 'Element') { continue }
            $name = $dict.GetAttribute('Key', $xamlNs)
            if ([string]::IsNullOrEmpty($name)) { continue }

            $entries = @{}
            foreach ($entry in $dict.ChildNodes) {
                if ($entry.NodeType -ne 'Element') { continue }
                $key = $entry.GetAttribute('Key', $xamlNs)
                if (-not [string]::IsNullOrEmpty($key)) { $entries[$key] = $entry.LocalName }
            }
            $themes[$name] = $entries
        }

        foreach ($theme in $required) {
            if (-not $themes.ContainsKey($theme)) {
                $failures += "${relative}: has no '$theme' theme dictionary."
            }
        }

        # Every key seen anywhere must appear everywhere, with a consistent type.
        $union = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entries in $themes.Values) {
            foreach ($key in $entries.Keys) { [void]$union.Add($key) }
        }

        foreach ($key in @($union | Sort-Object)) {
            $types = @{}
            foreach ($theme in $themes.Keys) {
                if ($themes[$theme].ContainsKey($key)) {
                    $types[$themes[$theme][$key]] = $true
                }
                else {
                    $failures += "${relative}: '$key' is missing from the '$theme' theme."
                }
            }

            if (@($types.Keys).Count -gt 1) {
                $spelt = ($types.Keys | Sort-Object) -join ', '
                $failures += "${relative}: '$key' is declared as more than one type across themes ($spelt)."
            }
        }
    }
}

Write-Host "Checked $checked ThemeDictionaries block(s) across $($targets.Count) XAML file(s)."

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "FAIL: $($failures.Count) theme parity problem(s)." -ForegroundColor Red
    foreach ($f in $failures) {
        Write-Host "  $f" -ForegroundColor Red
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Write-Host "::error::$f"
        }
    }
    Write-Host ''
    Write-Host 'A key defined in one theme and not another compiles, passes review, and then' -ForegroundColor Yellow
    Write-Host 'fails at run time for the user who needs that theme (docs/requirements.md §9.4,' -ForegroundColor Yellow
    Write-Host 'A11Y-8). Define every token in Light, Dark and HighContrast.' -ForegroundColor Yellow
    exit 1
}

Write-Host 'PASS: every theme dictionary defines the same keys, of the same type.' -ForegroundColor Green
exit 0
