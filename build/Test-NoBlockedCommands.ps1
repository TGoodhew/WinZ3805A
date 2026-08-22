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
#
# A token is a *pair* - the pattern's abbreviated stem and its fully spelled form, taken from
# the way the patterns themselves write it, 'STEM(?:REMAINDER)?'. A node is a hit when it falls
# between the two: the stem, the full spelling, and every SCPI abbreviation in between. A node
# that merely starts with the same letters and then diverges is a different node and is not.
#
# Matching the stem as a bare prefix instead - which this gate did until 21 Aug 2026 - is wrong
# in both directions. It flags any node beginning with those letters, and it then reports the
# collision as though the excluded command had been named. It also misses nothing the pair rule
# catches, so this is strictly the more precise test rather than a relaxation. A catalogued
# query whose node shares four leading letters with one of the undocumented nodes is what found
# it; the runtime validator never matched that command, because its alternation is anchored to
# a whole node and this scan was not.
#
# Note the gate holds itself to the same rule: a comment here that spelled the tokens out would
# be a hit, which is why this one describes the shape instead of giving an example.
$neverTokens = [System.Collections.Generic.List[object]]::new()
$queryOnlyTokens = [System.Collections.Generic.List[object]]::new()

# 'STEM(SUFFIX)?' and 'STEM(?:SUFFIX)?' alike - the patterns use both spellings.
$pairPattern = '(?<stem>[A-Z][A-Z0-9]{2,})\((?:\?:)?(?<suffix>[A-Z0-9]+)\)\?'

function Get-ExclusionToken {
    param([string] $Text)

    $tokens = [System.Collections.Generic.List[object]]::new()

    foreach ($m in [regex]::Matches($Text, $pairPattern)) {
        $stem = $m.Groups['stem'].Value
        $tokens.Add([pscustomobject]@{ Stem = $stem; Full = $stem + $m.Groups['suffix'].Value })
    }

    # Whatever is left once the pairs are removed is spelled only one way.
    foreach ($m in [regex]::Matches(($Text -replace $pairPattern, ' '), '[A-Z]{3,}')) {
        $tokens.Add([pscustomobject]@{ Stem = $m.Value; Full = $m.Value })
    }

    return $tokens
}

foreach ($block in $blocks) {
    $body = $block.Groups['body'].Value
    $name = $block.Groups['name'].Value

    if ($name -eq 'UndocumentedSetFormPattern') {
        # The whole alternation is the leaf set here.
        foreach ($t in @(Get-ExclusionToken -Text $body)) { [void]$queryOnlyTokens.Add($t) }
    }
    else {
        # Only the last node matters: the parents (':DIAG', ':SYST') are ordinary and are
        # named legitimately all over the catalog.
        $leaf = ($body -split ':')[-1]
        foreach ($t in @(Get-ExclusionToken -Text $leaf)) { [void]$neverTokens.Add($t) }
    }
}

if ($neverTokens.Count -eq 0 -and $queryOnlyTokens.Count -eq 0) {
    Write-Error "Parsed $($blocks.Count) pattern(s) from '$permittedRelative' but extracted no tokens. Fix the parse rather than removing the check."
}

# True when $node - the letters after a colon - names $token.
#
# Two ways to name it, and both are hits:
#
#   - It lies between the stem and the full spelling, which covers the stem itself, the full
#     spelling, and every SCPI abbreviation in between.
#   - It begins with the full spelling and runs on. The full name is still spelled out in the
#     text, which is what the "may not be named at all" rule is about, so this stays a hit even
#     though the receiver would parse it as some other node.
#
# What is deliberately *not* a hit is a node that starts with the stem and then diverges before
# reaching the full spelling. That is a different node which merely shares an opening, and
# treating it as a hit is what this gate did wrong until 21 Aug 2026.
function Test-ExclusionToken {
    param([string] $Node, [object] $Token)

    $betweenStemAndFull =
        $Node.StartsWith($Token.Stem, [StringComparison]::OrdinalIgnoreCase) -and
        $Token.Full.StartsWith($Node, [StringComparison]::OrdinalIgnoreCase)

    return $betweenStemAndFull -or
           $Node.StartsWith($Token.Full, [StringComparison]::OrdinalIgnoreCase)
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

        # Every ':NODE' on the line, once - then asked which token it is, rather than each
        # token being pattern-matched against the raw text.
        foreach ($m in [regex]::Matches($line, ':(?<node>[A-Za-z]+)')) {
            $node = $m.Groups['node'].Value
            $after = $m.Index + $m.Length
            $isQuery = $after -lt $line.Length -and $line[$after] -eq '?'

            foreach ($token in $neverTokens) {
                if (Test-ExclusionToken -Node $node -Token $token) {
                    $hits += [pscustomobject]@{
                        File = $relative; Line = $number; Text = $m.Value; Context = $line.Trim()
                        Why  = 'named in §8.4 and must not appear outside the exclusion patterns'
                    }
                }
            }

            if ($isQuery) { continue }

            foreach ($token in $queryOnlyTokens) {
                if (Test-ExclusionToken -Node $node -Token $token) {
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
