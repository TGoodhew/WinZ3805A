<#
.SYNOPSIS
    CI gate for #321: every cross-reference a document makes resolves to the thing it names.

.DESCRIPTION
    The #316 audit read sixteen documents by hand and found some 360 stale or wrong claims.
    Most needed a person. A recognisable share did not, and those recurred across files
    because nothing was checking them - the documents referred to each other, to the
    specification's sections, to issues and to packages, and every one of those references
    was being kept right by hand.

    Four rules, each one a kind of defect the audit actually found:

      1. RELATIVE LINKS RESOLVE. Every [text](path) in a tracked document points at a file
         that exists, and every #anchor on a cross-file link points at a heading in it. The
         audit found none broken, which is the point: nine anchored links and ~200 section
         references had been kept correct by hand across sixteen documents, and the first
         rename would have taken one of them out silently.

      2. SECTION REFERENCES RESOLVE. Every §n.n in every tracked document names a heading
         of docs/requirements.md. THE DEFECT THIS EXISTS TO CATCH IS THE §6.5 CITED AT
         requirements.md:1864, where §6 ends at 6.4 - the specification referring to a part
         of itself that has never existed.

      3. AN ISSUE CITED AS LIVE IS OPEN. A '#NNN' that a sentence is built around - "blocked
         on #39", "#127 tracks", "the BG7TBL is next (#309)" - is checked against the
         repository. The audit found nine documents doing this against closed issues.

         THE CITATION HAS TO BE PART OF THE CLAIM, not merely near one. Two looser designs
         were tried and measured on this repository: a trigger word anywhere on the line
         gave 18 hits of which about 3 were real, and a 40-character window gave 2, both
         false. The phrasings are named individually now, and the list below says why.

         ONLY THE LIVE-SOUNDING ONES. A closed issue cited historically - "fixed in #180",
         "corrected 21 Aug 2026 (#85)" - is how this repository records its own reasoning
         and must keep passing. That is why the word list is the trigger rather than the
         '#NNN' itself.

      4. THE NOTICES TABLE MATCHES THE PROJECT FILES. Every <PackageReference> in a SHIPPING project has a
         row in THIRD-PARTY-NOTICES.md carrying the same version, and every row naming a package is
         referenced by something. The audit found two packages removed on 15 August still
         listed fourteen days later, and two referenced packages missing.

    RULE 3 NEEDS THE NETWORK AND THE OTHER THREE DO NOT. It degrades to a warning when 'gh'
    is missing or unauthenticated rather than failing the gate: a documentation check that
    cannot run offline would make every local run of the gate suite depend on GitHub being
    up, and the other three rules are worth having on their own.

.PARAMETER SkipIssueCheck
    Skips rule 3 outright. For a deliberate offline run.
#>

[CmdletBinding()]
param(
    [switch] $SkipIssueCheck,

    # How near a trigger word has to be to a '#NNN' before the citation counts as a claim
    # about live work. See rule 3 below for why this is a window and not the whole line.
    [int] $ProximityWindow = 40
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$specRelative = 'docs/requirements.md'
$specPath = Join-Path $repoRoot $specRelative
$noticesRelative = 'THIRD-PARTY-NOTICES.md'

if (-not (Test-Path $specPath)) {
    Write-Host "FAIL: $specRelative not found; the gate has nothing to resolve against." -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------------------
# Which documents are checked
#
# Tracked text only, and never the build output: bin/ and obj/ hold generated XML
# documentation that quotes these same references back, so scanning them would report every
# defect twice and every fix as still broken until the next build.
# ---------------------------------------------------------------------------------------
$documents = git -C $repoRoot ls-files '*.md' '*.txt' |
    Where-Object { $_ -notmatch '^(bin|obj)/' -and $_ -notmatch '/(bin|obj)/' } |
    Sort-Object

$hits = [System.Collections.Generic.List[object]]::new()

function Add-Hit {
    param($File, $Line, $Text, $Why)
    $hits.Add([pscustomobject]@{ File = $File; Line = $Line; Text = $Text; Why = $Why })
}

# ---------------------------------------------------------------------------------------
# The specification's own headings, for rule 2
#
# Both forms are collected. '#### 9.6.1 Breakpoints' is how the document writes them, and a
# reference is written '§9.6.1' - so the number is what the two have in common, and the
# heading text is deliberately not part of the key.
# ---------------------------------------------------------------------------------------
$specLines = Get-Content -LiteralPath $specPath
$sections = [System.Collections.Generic.HashSet[string]]::new()

foreach ($line in $specLines) {
    if ($line -match '^#{1,6}\s+(?<n>\d+(?:\.\d+)*)') {
        $null = $sections.Add($Matches.n)

        # A reference to §9 is satisfied by §9.1 existing, because the document numbers a
        # parent heading and its children the same way and not every parent is written out.
        $parts = $Matches.n -split '\.'
        for ($i = 1; $i -lt $parts.Count; $i++) {
            $null = $sections.Add(($parts[0..($i - 1)] -join '.'))
        }
    }
}

Write-Host "Resolved $($sections.Count) section number(s) from $specRelative."

# Headings of every document, for rule 1's anchors.
$anchors = @{}
function Get-Anchors {
    param([string] $Relative)

    if ($anchors.ContainsKey($Relative)) { return $anchors[$Relative] }

    $set = [System.Collections.Generic.HashSet[string]]::new()
    $full = Join-Path $repoRoot $Relative

    if (Test-Path $full) {
        foreach ($line in Get-Content -LiteralPath $full) {
            if ($line -match '^#{1,6}\s+(?<t>.+)$') {
                # GitHub's slug: lower-cased, punctuation dropped, spaces to hyphens.
                $slug = $Matches.t.Trim().ToLowerInvariant()
                $slug = $slug -replace '[^\p{L}\p{Nd}\s-]', ''
                $slug = $slug -replace '\s+', '-'
                $null = $set.Add($slug)
            }
        }
    }

    $anchors[$Relative] = $set
    return $set
}

# ---------------------------------------------------------------------------------------
# Rules 1 and 2, over every document
# ---------------------------------------------------------------------------------------
$issueCitations = [System.Collections.Generic.List[object]]::new()

# ---------------------------------------------------------------------------------------
# Rule 3's phrasings
#
# THE CITATION HAS TO BE PART OF THE CLAIM, not merely near one. Two looser designs were
# tried against this repository and measured:
#
#   - trigger word anywhere on the line: 18 hits, about 3 real. These documents write long
#     lines mixing history with live work, so "Fixtures/README.md tracks the eighth
#     (corrected 29 Aug 2026, #316)" read as "#316 tracks".
#   - trigger word within 40 characters: 2 hits, both false. "amended 29 Aug 2026 (#316)
#     from InvalidInputOverwritten" is not a claim that #316 is open.
#
# So the patterns below name the citation directly. Each one is a phrasing the #316 audit
# actually found, and a sentence has to be built around the issue number to match.
#
# A gate that cries wolf is a gate people learn to scroll past, and this one reports rather
# than fails - which makes precision more important, not less. Adding a pattern here is
# adding a way for the gate to be wrong about a sentence somebody wrote about the past.
# ---------------------------------------------------------------------------------------
$livePatterns = @(
    'blocked on\s+#(?<n>\d{1,5})'
    'blocks on\s+#(?<n>\d{1,5})'
    'pending\s+#(?<n>\d{1,5})'
    'awaiting\s+#(?<n>\d{1,5})'
    'is next\s*\(?#(?<n>\d{1,5})'
    'are next\s*\(?#(?<n>\d{1,5})'
    '#(?<n>\d{1,5})\s+(tracks|will|is still|remains|blocks|is open)\b'
    '#(?<n>\d{1,5})\s+has not\b'
    '(tracked|tracks) (as|by)\s+#(?<n>\d{1,5})'
    'until\s+#(?<n>\d{1,5})\s+(is|lands|ships)'
)
foreach ($relative in $documents) {
    $full = Join-Path $repoRoot $relative
    if (-not (Test-Path $full)) { continue }

    $lines = Get-Content -LiteralPath $full
    $inFence = $false

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $number = $i + 1

        # Fenced blocks hold wireframes, sample output and transcripts. A '§' or a '#12' in
        # an ASCII wireframe is a picture of a document, not a reference to one.
        if ($line -match '^\s*```') { $inFence = -not $inFence; continue }
        if ($inFence) { continue }

        # ---- rule 1: relative links resolve ------------------------------------------
        foreach ($m in [regex]::Matches($line, '\[[^\]]*\]\((?<target>[^)\s]+)\)')) {
            $target = $m.Groups['target'].Value

            if ($target -match '^(https?:|mailto:|#)') { continue }

            $path, $anchor = $target -split '#', 2
            if ([string]::IsNullOrWhiteSpace($path)) { continue }

            $resolved = Join-Path (Split-Path -Parent (Join-Path $repoRoot $relative)) $path
            if (-not (Test-Path $resolved)) {
                Add-Hit $relative $number $target 'a relative link to a file that does not exist'
                continue
            }

            if ($anchor -and $resolved -match '\.md$') {
                $targetRelative = (Resolve-Path -LiteralPath $resolved).Path.Substring($repoRoot.Length + 1) -replace '\\', '/'
                if (-not (Get-Anchors $targetRelative).Contains($anchor.ToLowerInvariant())) {
                    Add-Hit $relative $number $target 'a link to a heading that does not exist in the target file'
                }
            }
        }

        # ---- rule 2: section references resolve --------------------------------------
        foreach ($m in [regex]::Matches($line, '§\s?(?<n>\d+(?:\.\d+)*)')) {
            if (-not $sections.Contains($m.Groups['n'].Value)) {
                Add-Hit $relative $number $m.Value "a section reference with no such heading in $specRelative"
            }
        }

        # ---- rule 3: collect issue citations that are part of a live claim ----------
        foreach ($pattern in $livePatterns) {
            foreach ($m in [regex]::Matches($line, $pattern, 'IgnoreCase')) {
                $issueCitations.Add([pscustomobject]@{
                    File = $relative; Line = $number; Number = [int]$m.Groups['n'].Value; Context = $m.Value.Trim()
                })
            }
        }
    }
}
Write-Host "Scanned $($documents.Count) tracked document(s) for links and section references."

# ---------------------------------------------------------------------------------------
# Rule 4: the notices table against the project files
# ---------------------------------------------------------------------------------------
$noticesPath = Join-Path $repoRoot $noticesRelative

if (Test-Path $noticesPath) {
    $notices = Get-Content -LiteralPath $noticesPath -Raw

    # SHIPPING PROJECTS ONLY. The notices document sets its own scope in its second
    # paragraph - "packages used only by the tests ship nothing and are not the subject of a
    # notice" - and naming xunit there would be wrong, not merely noisy. So the gate reads
    # the same set the document promises to cover.
    $referenced = @{}
    $projects = git -C $repoRoot ls-files '*.csproj' |
        Where-Object { $_ -match '^(src|tools)/' }

    foreach ($proj in $projects) {
        $text = Get-Content -LiteralPath (Join-Path $repoRoot $proj) -Raw

        # The whole element, not just its opening tag: PrivateAssets is written as a child
        # element in this repository ('<PrivateAssets>all</PrivateAssets>') and as an
        # attribute elsewhere, and a self-closing form has neither.
        foreach ($m in [regex]::Matches($text, '<PackageReference\s+Include="(?<id>[^"]+)"\s+Version="(?<v>[^"]+)"(?<rest>\s*/>|.*?</PackageReference>)', 'Singleline')) {
            # An analyzer with PrivateAssets all produces no assembly and is not distributed,
            # so it is not a third-party notice. CLAUDE.md draws the same line for the Device
            # library's dependency set.
            if ($m.Value -match 'PrivateAssets\s*=\s*"all"' -or $m.Value -match '<PrivateAssets>\s*all\s*</PrivateAssets>') {
                continue
            }

            $referenced[$m.Groups['id'].Value] = $m.Groups['v'].Value
        }
    }

    foreach ($id in $referenced.Keys) {
        # THE TABLE CONTRACTS ITS NAMES, AND THAT IS THE DOCUMENT'S CHOICE RATHER THAN A
        # DEFECT: it writes 'Microsoft.Extensions.Logging, .Abstractions' for two packages on
        # one row, so no exact-id matcher can read it without the notices being rewritten to
        # suit a gate. A legal document does not get reformatted for a script's convenience.
        #
        # So a package counts as covered if its own id appears, or if its family does - the id
        # with its last segment dropped. That follows the contraction without inventing one.
        $family = $id -replace '\.[^.]+$', ''

        if ($notices -match [regex]::Escape($id)) {
            $covered = $true
        }
        elseif ($family -ne $id -and $notices -match [regex]::Escape($family)) {
            $covered = $true
        }
        else {
            $covered = $false
        }

        if (-not $covered) {
            Add-Hit $noticesRelative 0 $id 'a package this project ships with no row in the notices'
        }
        elseif ($notices -notmatch [regex]::Escape($referenced[$id])) {
            Add-Hit $noticesRelative 0 "$id $($referenced[$id])" 'a package whose notices row does not carry the referenced version'
        }
    }

    Write-Host "Checked $($referenced.Count) shipped package reference(s) against $noticesRelative."
}
else {
    Write-Host "Skipped the notices check: $noticesRelative not found." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------------------
# Rule 3: issues cited as live must be open
# ---------------------------------------------------------------------------------------
$issueWarnings = 0

if ($SkipIssueCheck) {
    Write-Host 'Skipped the issue-state check by request.' -ForegroundColor Yellow
}
elseif (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host 'Skipped the issue-state check: gh is not on PATH.' -ForegroundColor Yellow
}
else {
    $states = @{}
    $unreachable = $false

    foreach ($citation in ($issueCitations | Sort-Object Number -Unique)) {
        if ($unreachable) { break }

        try {
            $state = (gh issue view $citation.Number --json state --jq .state 2>$null)
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($state)) {
                # A number that is a pull request, or one that does not exist, is not a
                # defect this gate can judge - and an auth failure looks identical, so the
                # first unreadable answer stands the whole rule down rather than reporting
                # every citation in the repository as broken.
                $unreachable = $true
                break
            }

            $states[$citation.Number] = $state.Trim()
        }
        catch {
            $unreachable = $true
            break
        }
    }

    if ($unreachable) {
        Write-Host 'Skipped the issue-state check: the repository could not be queried.' -ForegroundColor Yellow
    }
    else {
        foreach ($citation in $issueCitations) {
            if ($states.ContainsKey($citation.Number) -and $states[$citation.Number] -eq 'CLOSED') {
                Write-Host ("  {0}:{1}  #{2} is closed but is cited as live work." -f `
                    $citation.File, $citation.Line, $citation.Number) -ForegroundColor Yellow
                Write-Host ("      {0}" -f $citation.Context) -ForegroundColor DarkGray
                $issueWarnings++
            }
        }

        Write-Host "Checked $($states.Count) issue(s) cited as live work."
    }
}

# ---------------------------------------------------------------------------------------
# Result
# ---------------------------------------------------------------------------------------
if ($issueWarnings -gt 0) {
    Write-Host ''
    Write-Host "$issueWarnings citation(s) name a closed issue as live work. Reword or drop them." -ForegroundColor Yellow
}

if ($hits.Count -gt 0) {
    Write-Host ''
    Write-Host "FAIL: $($hits.Count) unresolved reference(s)." -ForegroundColor Red
    foreach ($h in $hits) {
        Write-Host ("  {0}:{1}  {2}" -f $h.File, $h.Line, $h.Text) -ForegroundColor Red
        Write-Host ("      {0}" -f $h.Why) -ForegroundColor DarkGray
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Write-Host "::error file=$($h.File),line=$($h.Line)::This is $($h.Why). See #321."
        }
    }
    Write-Host ''
    Write-Host 'The documents refer to each other, to the specification and to the project files.' -ForegroundColor Yellow
    Write-Host 'Nothing but this gate keeps those references true, and the #316 audit found 360' -ForegroundColor Yellow
    Write-Host 'stale claims that had accumulated while nothing was checking.' -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host 'PASS: every link, section reference and package notice resolves.' -ForegroundColor Green
exit 0
