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

    A '#' preceded by '&' is an XML character reference, not a colour: '&#183;' for a
    middle dot, '&#xE80F;' for a Segoe Fluent glyph. Those are the ordinary way to write
    a nav icon or a typographic mark in XAML and there is no hex-colour spelling that
    begins '&#', so the lookbehind excludes them without weakening the rule.

    Comments are stripped before the scan, because '#114' in a prose reference to an issue
    is three hex digits and this gate cannot tell it from a colour. That distinction only
    started to matter once issue numbers reached three figures, and rewording every comment
    that cites one is the wrong way round: a hex colour inside a comment paints nothing, so
    excluding comments costs the rule nothing it was protecting.

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

$pattern = '(?<!&)#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})(?![0-9a-fA-F])'

$targets = @()
$targets += Get-ChildItem -Path $src -Recurse -Filter '*.xaml' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$targets += Get-ChildItem -Path $src -Recurse -Filter '*.cs' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.FullName -match '\\(Views|Controls)\\' }

# Comment spans, stripped before the scan. XAML has one form and C# has three; a line
# comment is only recognised outside a string, so a '//' inside a URL or a literal does not
# blank the rest of the line.
$blockComment = '(?s)<!--.*?-->|/\*.*?\*/'

# Everything before a '//' on the line is kept, so a '//' inside a string or a URL does not
# blank the rest of it. .NET has no \K, hence the capture group.
$lineComment = '(?m)^((?:[^"''\r\n]|"[^"\r\n]*"|''[^''\r\n]*'')*?)//[^\r\n]*$'

function Remove-Comments {
    param([string] $Text)

    # Block comments collapse to their own newlines, so every later line keeps its number.
    $withoutBlocks = [regex]::Replace(
        $Text,
        $blockComment,
        { param($m) [string]::new("`n", $m.Value.Split("`n").Length - 1) })

    return [regex]::Replace($withoutBlocks, $lineComment, '$1')
}

$hits = @()
foreach ($f in $targets) {
    if ([System.IO.Path]::GetFullPath($f.FullName) -eq $allowed) { continue }
    $text = Get-Content -LiteralPath $f.FullName -Raw
    if ($null -eq $text) { continue }

    $n = 0
    foreach ($line in (Remove-Comments $text) -split "`r?`n") {
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
