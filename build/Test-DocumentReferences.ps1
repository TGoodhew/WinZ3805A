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

      4. THE NOTICES TABLE MATCHES THE PROJECT FILES. Every <PackageReference> in a SHIPPING project has
         its OWN row in THIRD-PARTY-NOTICES.md, and that row's Version column carries the same
         version. The audit found two packages removed on 15 August still listed fourteen days
         later, and two referenced packages missing.

         The project files are read as XML and each version is looked for on its package's own
         row (#564). Until 29 Sep 2026 a regex skipped any reference with an attribute after
         Version - two of twelve - and a version was accepted anywhere in the file. -SelfTest
         holds both as deliberate violations.

         THE REVERSE IS NOT CHECKED: a row naming a package nothing references any more passes.
         This description once said otherwise. The 15 August rows above were exactly that
         case, so removing a package still needs its row removed by hand.

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

    # Runs rule 4 against fixtures and nothing else (#564).
    [switch] $SelfTest,

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
# Rule 4's two readers, as functions so -SelfTest can hand them fixtures (#564)
# ---------------------------------------------------------------------------------------

# A PackageReference setting, written either as an attribute or as a child element: this
# repository writes PrivateAssets as a child ('<PrivateAssets>all</PrivateAssets>') and other
# projects write it as an attribute. $null when it is neither.
function Read-Setting {
    param([System.Xml.XmlElement] $Reference, [string] $Setting)

    if ($Reference.HasAttribute($Setting)) { return $Reference.GetAttribute($Setting) }
    $child = $Reference.SelectSingleNode("*[local-name()='$Setting']")
    if ($child) { return $child.InnerText }
    return $null
}
# Every <PackageReference> in one project file that ships something, as id -> version.
#
# READ AS XML, NOT MATCHED AS TEXT (#564). The regex this replaced wanted Include, then
# Version, then the end of the element - so a reference with any attribute after Version was
# not a match at all, and was skipped without a word. Two of the twelve were, on 29 Sep 2026:
# both carry ExcludeAssets after Version. A parser does not care about attribute order or
# whether a setting is an attribute or a child element.
function Get-ShippedPackages {
    param([string] $ProjectXml, [string] $Name)

    $packages = [ordered]@{}
    try {
        $doc = [xml]$ProjectXml
    }
    catch {
        Add-Hit $Name 0 $Name 'a project file that is not well-formed XML, so its packages could not be read'
        return $packages
    }

    foreach ($ref in $doc.SelectNodes('//*[local-name()="PackageReference"]')) {
        $id = $ref.GetAttribute('Include')
        if (-not $id) { continue }


        # An analyzer with PrivateAssets all produces no assembly and is not distributed, so it is
        # not a third-party notice. CLAUDE.md draws the same line for the Device library's
        # dependency set.
        if ((Read-Setting $ref 'PrivateAssets') -match '^\s*all\s*$') { continue }

        $version = Read-Setting $ref 'Version'
        if (-not $version) {
            Add-Hit $Name 0 $id 'a package reference with no version for the notices to carry'
            continue
        }

        $packages[$id] = $version.Trim()
    }

    return $packages
}

# The notices table's rows, each as its component cell and its version cell.
function Get-NoticeRows {
    param([string] $Notices)

    foreach ($line in ($Notices -split '\r?\n')) {
        if ($line -notmatch '^\s*\|') { continue }

        $cells = $line.Trim().Trim('|') -split '\|'
        if ($cells.Count -lt 2) { continue }

        $component = $cells[0].Trim()
        $version = $cells[1].Trim()

        # The header row and the |---| rule under it.
        if ($component -eq 'Component' -or $component -match '^:?-+:?$') { continue }

        [pscustomobject]@{ Component = $component; Version = $version; Text = $line.Trim() }
    }
}

# A name or a version as a whole token, not as the start or end of a longer one.
#
# 'Microsoft.Windows.SDK.BuildTools' is the start of 'Microsoft.Windows.SDK.BuildTools.WinApp',
# and version '2.0.5' is the start of '2.0.51'. Either as a plain substring would find its
# neighbour's row and pass. A trailing full stop is allowed, since a name can end a sentence.
function Test-Token {
    param([string] $Text, [string] $Token)
    return $Text -match ('(?<![\w.])' + [regex]::Escape($Token) + '(?!\.?\w)')
}

# Rule 4 over one set of project files and one notices document. Returns how many packages it
# read; the defects go to Add-Hit.
function Test-NoticesTable {
    param([hashtable] $Projects, [string] $Notices, [string] $NoticesName)

    $referenced = [ordered]@{}
    foreach ($name in ($Projects.Keys | Sort-Object)) {
        $shipped = Get-ShippedPackages -ProjectXml $Projects[$name] -Name $name
        foreach ($id in $shipped.Keys) { $referenced[$id] = $shipped[$id] }
    }

    $rows = @(Get-NoticeRows $Notices)

    foreach ($id in $referenced.Keys) {
        $version = $referenced[$id]

        # THE PACKAGE'S OWN ROW, NOT THE WHOLE FILE (#564). The version used to be looked for
        # anywhere in the document, so a stale row passed whenever the new version number
        # happened to be written somewhere else - and every Microsoft.Extensions package shares
        # one, so for those it always did.
        #
        # THE TABLE CONTRACTS ITS NAMES, AND THAT IS THE DOCUMENT'S CHOICE RATHER THAN A DEFECT:
        # it writes 'Microsoft.Extensions.Logging, .Abstractions' for two packages on one row, so
        # no exact-id matcher can read it without the notices being rewritten to suit a gate. A
        # legal document does not get reformatted for a script's convenience. So a package's row
        # is the one naming its id in the Component column, or failing any such row, the one
        # naming its family - the id with its last segment dropped. That follows the contraction
        # without inventing one.
        $own = @($rows | Where-Object { Test-Token $_.Component $id })
        if ($own.Count -eq 0) {
            $family = $id -replace '\.[^.]+$', ''
            if ($family -ne $id) {
                $own = @($rows | Where-Object { Test-Token $_.Component $family })
            }
        }

        if ($own.Count -eq 0) {
            Add-Hit $NoticesName 0 $id 'a package this project ships with no row in the notices'
        }
        elseif (-not ($own | Where-Object { Test-Token $_.Version $version })) {
            Add-Hit $NoticesName 0 "$id $version" "a package whose notices row does not carry the referenced version (the row says '$($own[0].Version)')"
        }
    }

    return $referenced.Count
}

# ---------------------------------------------------------------------------------------
# -SelfTest: rule 4 against fixtures, including both of #564's defects as deliberate violations
#
# Each case is a project, a notices table, and the number of defects the rule must report. A
# rule that finds nothing in the real repository today proves nothing about whether it would -
# the old one found nothing too, while skipping two packages.
# ---------------------------------------------------------------------------------------
function Invoke-SelfTest {
    $table = @'
| Component | Version | Licence | Redistributed as |
|---|---|---|---|
| Markdig | 1.3.2 | BSD 2-Clause | Assembly in the package |
| Microsoft.Extensions.Logging, .Abstractions | 10.0.11 | MIT | Assemblies in the package |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2526 | Microsoft | Build-time only |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.5.0 | MIT | Build-time only |
| Microsoft.Windows.AI.MachineLearning, Microsoft.WindowsAppSDK.Widgets | 2.1.74, 2.0.5 | Microsoft | Nothing ships |
'@

    function Project([string] $refs) { "<Project Sdk=`"Microsoft.NET.Sdk`"><ItemGroup>$refs</ItemGroup></Project>" }

    $cases = @(
        @{ Name = 'a plain reference on its own row'; Want = 0
           Refs = '<PackageReference Include="Markdig" Version="1.3.2" />' }
        @{ Name = 'attributes after Version, current'; Want = 0
           Refs = '<PackageReference Include="Microsoft.WindowsAppSDK.Widgets" Version="2.0.5" ExcludeAssets="runtime;native" />' }
        @{ Name = '#564: attributes after Version, and a version the row does not carry'; Want = 1
           Refs = '<PackageReference Include="Microsoft.WindowsAppSDK.Widgets" Version="2.0.6" ExcludeAssets="runtime;native" />' }
        @{ Name = '#564: attributes before Version as well as after'; Want = 1
           Refs = '<PackageReference ExcludeAssets="runtime" Include="Microsoft.Windows.AI.MachineLearning" Version="2.1.75" GeneratePathProperty="true" />' }
        @{ Name = 'Version as a child element'; Want = 1
           Refs = '<PackageReference Include="Markdig"><Version>1.3.3</Version></PackageReference>' }
        @{ Name = '#564: the new version is on another row, not this one'; Want = 1
           Refs = '<PackageReference Include="Markdig" Version="10.0.11" />' }
        @{ Name = 'a version that is the start of a longer one on the row'; Want = 1
           Refs = '<PackageReference Include="Microsoft.WindowsAppSDK.Widgets" Version="2.0" ExcludeAssets="runtime;native" />' }
        @{ Name = 'a name that is the start of a neighbour row''s name, whose version it matches'; Want = 1
           Refs = '<PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="0.5.0" />' }
        @{ Name = 'a contracted family row'; Want = 0
           Refs = '<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.11" />' }
        @{ Name = 'a package with no row at all'; Want = 1
           Refs = '<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />' }
        @{ Name = 'PrivateAssets all as a child element is not shipped'; Want = 0
           Refs = '<PackageReference Include="Some.Analyzer" Version="1.0.0"><PrivateAssets>all</PrivateAssets></PackageReference>' }
        @{ Name = 'PrivateAssets all as an attribute is not shipped'; Want = 0
           Refs = '<PackageReference Include="Some.Analyzer" Version="1.0.0" PrivateAssets="all" />' }
        @{ Name = 'a reference with no version'; Want = 1
           Refs = '<PackageReference Include="Markdig" />' }
    )

    $failed = 0
    foreach ($case in $cases) {
        $hits.Clear()
        $null = Test-NoticesTable -Projects @{ 'fixture.csproj' = (Project $case.Refs) } -Notices $table -NoticesName 'fixture notices'
        $ok = $hits.Count -eq $case.Want
        if (-not $ok) { $failed++ }
        Write-Host ('  {0}  {1} (reported {2}, expected {3})' -f ($(if ($ok) { 'ok  ' } else { 'FAIL' })), $case.Name, $hits.Count, $case.Want) `
            -ForegroundColor $(if ($ok) { 'Gray' } else { 'Red' })
    }

    if ($failed -gt 0) {
        Write-Host "FAIL: $failed of $($cases.Count) self-test case(s) judged wrongly." -ForegroundColor Red
        exit 1
    }

    Write-Host "Self-test passed: $($cases.Count) cases, $(@($cases | Where-Object { $_.Want -gt 0 }).Count) of them deliberate violations." -ForegroundColor Green
    exit 0
}

# Before anything that reads the repository: the self-test needs none of it.
if ($SelfTest) { Invoke-SelfTest }

if (-not (Test-Path $specPath)) {
    Write-Host "FAIL: $specRelative not found; the gate has nothing to resolve against." -ForegroundColor Red
    exit 1
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

    # Wrapped, because Get-Content returns a bare string for a one-line file and nothing at all for
    # an empty one, and strict mode refuses .Count on either. The first empty tracked document was a
    # screen reply kept while a receiver was switched off (#639).
    $lines = @(Get-Content -LiteralPath $full)
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
    # SHIPPING PROJECTS ONLY. The notices document sets its own scope in its second
    # paragraph - "packages used only by the tests ship nothing and are not the subject of a
    # notice" - and naming xunit there would be wrong, not merely noisy. So the gate reads
    # the same set the document promises to cover.
    $projects = @{}
    foreach ($proj in (git -C $repoRoot ls-files '*.csproj' | Where-Object { $_ -match '^(src|tools)/' })) {
        $projects[$proj] = Get-Content -LiteralPath (Join-Path $repoRoot $proj) -Raw
    }

    $count = Test-NoticesTable -Projects $projects -Notices (Get-Content -LiteralPath $noticesPath -Raw) -NoticesName $noticesRelative
    Write-Host "Checked $count shipped package reference(s) against their own rows in $noticesRelative."
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
