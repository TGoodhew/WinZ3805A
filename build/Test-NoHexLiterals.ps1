<#
.SYNOPSIS
    CI gate for P0-17 / §9.13 item 2: no literal hex colour outside Themes/Colors.xaml.

.DESCRIPTION
    §9.13 item 2 states the rule as "No literal hex outside Themes/Colors.xaml", and
    P0-17's acceptance criterion names Views/ and Controls/ as the minimum CI scope.
    This script implements the broader §9.13 rule:

        - every *.xaml under src/, except Themes/Colors.xaml
        - every *.cs under any Views/ or Controls/ folder (code-behind counts)

    A hit is a '#' followed by exactly 3, 4, 6 or 8 hex digits and not followed by a
    further hex digit — the four forms XAML accepts for a Color. Font URIs such as
    'CascadiaMono.ttf#Cascadia Mono' do not match, because 'Ca' is only two hex digits
    before a non-hex character.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.EXAMPLE
    pwsh build/Test-NoHexLiterals.ps1
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

# The one file permitted to declare colour.
$allowed = [System.IO.Path]::GetFullPath((Join-Path $src 'WinZ3805A\Themes\Colors.xaml'))

$pattern = '#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})(?![0-9a-fA-F])'

$targets = @()
$targets += Get-ChildItem -Path $src -Recurse -Filter '*.xaml' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$targets += Get-ChildItem -Path $src -Recurse -Filter '*.cs' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.FullName -match '\\(Views|Controls)\\' }

$hits = @()
foreach ($f in $targets) {
    if ([System.IO.Path]::GetFullPath($f.FullName) -eq $allowed) { continue }
    $n = 0
    foreach ($line in (Get-Content -LiteralPath $f.FullName)) {
        $n++
        foreach ($m in [regex]::Matches($line, $pattern)) {
            $hits += [pscustomobject]@{
                File = [System.IO.Path]::GetRelativePath($Root, $f.FullName)
                Line = $n
                Text = $m.Value
                Context = $line.Trim()
            }
        }
    }
}

Write-Host "Scanned $($targets.Count) file(s) for hex colour literals."

if ($hits.Count -gt 0) {
    Write-Host ''
    Write-Host "FAIL: $($hits.Count) hex colour literal(s) outside Themes/Colors.xaml." -ForegroundColor Red
    foreach ($h in $hits) {
        Write-Host ("  {0}:{1}  {2}" -f $h.File, $h.Line, $h.Text) -ForegroundColor Red
        Write-Host ("      {0}" -f $h.Context) -ForegroundColor DarkGray
        # Surface it as a GitHub annotation when running in Actions.
        if ($env:GITHUB_ACTIONS -eq 'true') {
            $msg = "Hex colour literal '$($h.Text)' is not permitted here. Declare it in Themes/Colors.xaml and reference it by key with {ThemeResource} (docs/requirements.md §9.4, §9.13 item 2)."
            Write-Host "::error file=$($h.File),line=$($h.Line)::$msg"
        }
    }
    Write-Host ''
    Write-Host 'Every brush is declared once in Themes/Colors.xaml and referenced by key with' -ForegroundColor Yellow
    Write-Host '{ThemeResource} - never {StaticResource}, which would not re-resolve on theme change.' -ForegroundColor Yellow
    exit 1
}

Write-Host 'PASS: no hex colour literals outside Themes/Colors.xaml.' -ForegroundColor Green
exit 0
