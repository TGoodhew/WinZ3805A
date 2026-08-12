<#
.SYNOPSIS
    CI gate for P0-7 / §8.4: no excluded command is named anywhere but the one file that
    holds the exclusion patterns.

.DESCRIPTION
    §8.4 requires that the commands it excludes are absent from the application in every
    user-visible sense - not in the catalog, a picker, an autocomplete, help text, or any
    log a user can read. They are not entries carrying a flag; they do not exist as data.
    CLAUDE.md extends the same rule to comments, tests, and fixtures.

    P0-7 states the check as a manual audit of the built binary's string table. This
    script does the same job earlier and repeatably, against source, in the shape the
    other two gates already use: one permitted file, everything else clean.

    It takes the exclusion patterns from the permitted file itself rather than restating
    them, so there is exactly one place in the repository where these names appear and
    the gate cannot drift from what it guards.

    Two rules come out of §8.4, and the script applies them separately:

      - The named exclusions (firmware transfer, flash erase, the language node) may not
        be named at all. Only the leaf node of each pattern is searched, because their
        parent nodes are ordinary and appear throughout the catalog.

      - The undocumented parser nodes are blocked in SET form only. §8.5 enables the query
        form of a small subset as an opt-in read-only card, so ':NODE?' is permitted
        elsewhere and ':NODE' without the question mark is not.

    Matching requires a leading colon, so ordinary English in a comment is not a hit.

    docs/ is not scanned. The specification is where §8.4 is written down, and a gate that
    flagged its own source would be nonsense.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.EXAMPLE
    pwsh build/Test-NoBlockedCommands.ps1
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The one file permitted to name them.
$permittedRelative = 'src\WinZ3805A.Device\Commands\BlockedCommands.cs'
$permitted = [System.IO.Path]::GetFullPath((Join-Path $Root $permittedRelative))

if (-not (Test-Path $permitted)) {
    Write-Error "Cannot find the exclusion patterns at '$permittedRelative'. If it moved, update this gate - do not delete it."
}

# ---------------------------------------------------------------------------
# Take the tokens from the patterns, so this script never restates them.
# ---------------------------------------------------------------------------
$source = Get-Content -LiteralPath $permitted -Raw

# Each [GeneratedRegex("...")] block, paired with the method it decorates.
$blockPattern = '\[GeneratedRegex\(\s*(?<body>(?:@"(?:[^"]|"")*"\s*\+?\s*)+)[^)]*?\)\]\s*private static partial Regex\s+(?<name>\w+)\s*\('
$blocks = [regex]::Matches($source, $blockPattern, 'Singleline')

if ($blocks.Count -eq 0) {
    Write-Error "Found no exclusion patterns in '$permittedRelative'. The gate cannot verify anything - fix the parse rather than removing the check."
}

# Tokens that may never appear, and tokens that may appear only as a query.
$neverTokens = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$queryOnlyTokens = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

foreach ($block in $blocks) {
    $body = $block.Groups['body'].Value
    $name = $block.Groups['name'].Value

    if ($name -eq 'UndocumentedSetFormPattern') {
        # The whole alternation is the leaf set here.
        foreach ($m in [regex]::Matches($body, '[A-Z]{3,}')) {
            [void]$queryOnlyTokens.Add($m.Value)
        }
    }
    else {
        # Only the last node matters: the parents (':DIAG', ':SYST') are ordinary and are
        # named legitimately all over the catalog.
        $leaf = ($body -split ':')[-1]
        foreach ($m in [regex]::Matches($leaf, '[A-Z]{3,}')) {
            [void]$neverTokens.Add($m.Value)
        }
    }
}

if ($neverTokens.Count -eq 0 -and $queryOnlyTokens.Count -eq 0) {
    Write-Error "Parsed $($blocks.Count) pattern(s) from '$permittedRelative' but extracted no tokens. Fix the parse rather than removing the check."
}

# ---------------------------------------------------------------------------
# Scan everything that ships or describes what ships.
# ---------------------------------------------------------------------------
$scanRoots = @('src', 'tests', 'build', '.github') |
    ForEach-Object { Join-Path $Root $_ } |
    Where-Object { Test-Path $_ }

$targets = @()
foreach ($dir in $scanRoots) {
    $targets += Get-ChildItem -Path $dir -Recurse -File -Include '*.cs', '*.xaml', '*.ps1', '*.yml', '*.yaml', '*.md', '*.txt', '*.json', '*.appxmanifest' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
}

$hits = @()
foreach ($file in $targets) {
    if ([System.IO.Path]::GetFullPath($file.FullName) -eq $permitted) { continue }

    $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName)
    $number = 0

    foreach ($line in (Get-Content -LiteralPath $file.FullName)) {
        $number++

        foreach ($token in $neverTokens) {
            foreach ($m in [regex]::Matches($line, ":$token[A-Za-z]*", 'IgnoreCase')) {
                $hits += [pscustomobject]@{
                    File = $relative; Line = $number; Text = $m.Value; Context = $line.Trim()
                    Why  = 'named in §8.4 and must not appear outside the exclusion patterns'
                }
            }
        }

        foreach ($token in $queryOnlyTokens) {
            foreach ($m in [regex]::Matches($line, ":$token[A-Za-z]*", 'IgnoreCase')) {
                $after = $m.Index + $m.Length
                $isQuery = $after -lt $line.Length -and $line[$after] -eq '?'
                if (-not $isQuery) {
                    $hits += [pscustomobject]@{
                        File = $relative; Line = $number; Text = $m.Value; Context = $line.Trim()
                        Why  = 'an undocumented node in set form, which §8.4 blocks permanently with no override'
                    }
                }
            }
        }
    }
}

Write-Host "Scanned $($targets.Count) file(s) against $($neverTokens.Count + $queryOnlyTokens.Count) exclusion token(s) taken from $permittedRelative."

if ($hits.Count -gt 0) {
    Write-Host ''
    Write-Host "FAIL: $($hits.Count) reference(s) to an excluded command outside the exclusion patterns." -ForegroundColor Red
    foreach ($h in $hits) {
        Write-Host ("  {0}:{1}  {2}" -f $h.File, $h.Line, $h.Text) -ForegroundColor Red
        Write-Host ("      {0}" -f $h.Context) -ForegroundColor DarkGray
        if ($env:GITHUB_ACTIONS -eq 'true') {
            $msg = "This is $($h.Why). See docs/requirements.md §8.4: the command catalog is an allowlist and excluded commands do not exist as data anywhere but CommandCatalog's exclusion patterns."
            Write-Host "::error file=$($h.File),line=$($h.Line)::$msg"
        }
    }
    Write-Host ''
    Write-Host 'Excluded commands are unreachable, not merely warned about (goal G4). They are not' -ForegroundColor Yellow
    Write-Host 'catalog entries with a flag, and they belong in no list, comment, test, or fixture.' -ForegroundColor Yellow
    exit 1
}

Write-Host 'PASS: no excluded command is named outside the exclusion patterns.' -ForegroundColor Green
exit 0
