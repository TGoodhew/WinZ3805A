<#
.SYNOPSIS
    CI gate: a GitHub closing keyword appears only where the author means to close the issue.

.DESCRIPTION
    GitHub closes an issue on merge wherever a closing keyword stands immediately before an issue
    reference. It reads those two words and nothing else: not the sentence around them, not the
    heading above them, and not the negation inside them.

    Issue #487 was closed twice by this on 13 Sep 2026, and both closures came from sentences
    written to PREVENT it. The first PR carried, under a heading disclaiming what the change had
    proved, a one-line sentence naming the issue directly after a closing keyword. A concurrent PR
    carried the same trap in a differently-worded denial and would have fired had it merged first.
    The second closure came from the very change that added the note about the first: its PR body
    was written carefully and said "towards", and its COMMIT MESSAGE quoted the offending line.

    So there are two lessons and this gate holds both. A clean PR body does not protect a commit
    whose message carries the keyword, because a rebase merge puts that message on the default
    branch verbatim, where GitHub reads it. And quoting the offending line is not describing it:
    a quotation of a closing keyword IS a closing keyword.

    THE RULE IS STRUCTURAL, because intent cannot be read out of English. A PR that genuinely
    closes an issue writes the conventional bare line and nothing else on it; every observed defect
    was mid-sentence. So:

      * In a PR body or a commit message, a closing keyword and its reference may appear ONLY as an
        entire line of their own - optionally bulleted, optionally bold, optionally with trailing
        punctuation, optionally several pairs separated by commas. Anything else on the line fails.
      * In a PR title they may not appear at all. A title is not a place to close an issue, and a
        title saying an issue is NOT closed closes it.
      * A pair split across a newline always fails. It cannot be a line of its own, and whether
        GitHub matches that shape is not worth establishing on a live issue.

    What it cannot check is whether a bare closing line is TRUE. That is a claim about the work and
    no script reads the work. What it makes impossible is the closure nobody intended - which is
    the one that has actually happened, twice in a single day, the second time with the author's
    whole attention on the subject.

    To say it without the keyword: "this issue stays open", "towards #NNN", "one mechanism behind
    #NNN".

.PARAMETER SelfTest
    Run the rule against deliberate violations and against the legitimate forms, and fail if any is
    judged wrongly. Needs no repository and no pull request.
#>
[CmdletBinding()]
param(
    [string] $Title,
    [string] $Body,
    [string] $Base,
    [string] $Head,
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# GitHub's documented set, in all three tenses. Assembled from pieces so that this file does not
# itself contain a closing keyword standing next to an issue number - see the third lesson above.
$kw = '(?:clos(?:e|es|ed)|fix(?:|es|ed)|resolv(?:e|es|ed))'

# An issue reference: a plain number, a cross-repository one, or the GH- form.
$ref = '(?:(?:[\w.-]+\/[\w.-]+)?#\d+|GH-\d+)'

# A pair anywhere on a line.
$pairAnywhere = "(?i)\b$kw\s+$ref"

# The one legitimate shape: a whole line that is nothing but pairs. Optional bullet, optional bold
# markers, optional trailing punctuation, comma or "and" between pairs.
$bareLine = "(?i)^\s*(?:[-*+]\s+)?\*{0,2}$kw\s+$ref(?:\s*(?:,|,?\s*and)\s*$kw\s+$ref)*\*{0,2}\s*[.;]?\s*$"

function Test-Text {
    <#
        Returns the offending lines in one piece of text. $Where names it for the message only.
        A pair spanning a newline is reported against the line the keyword sits on.
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Text,
        [Parameter(Mandatory)] [string] $Where,
        [switch] $NoBareLineAllowed
    )

    $findings = @()
    if ([string]::IsNullOrWhiteSpace($Text)) { return $findings }

    $normalised = $Text -replace "`r`n", "`n" -replace "`r", "`n"
    $lines = @($normalised -split "`n")

    # A pair split across a line break: the keyword ends one line, the reference opens the next.
    for ($i = 0; $i -lt $lines.Count - 1; $i++) {
        if ($lines[$i] -match "(?i)\b$kw\s*$" -and $lines[$i + 1] -match "^\s*$ref") {
            $findings += [pscustomobject]@{
                Where  = $Where
                Line   = $i + 1
                Text   = $lines[$i].Trim()
                Reason = 'a closing keyword and its issue number split across a line break'
            }
        }
    }

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -notmatch $pairAnywhere) { continue }
        if (-not $NoBareLineAllowed -and $line -match $bareLine) { continue }

        $reason = if ($NoBareLineAllowed) {
            'a closing keyword beside an issue number in a PR title closes that issue on merge'
        }
        else {
            'a closing keyword beside an issue number, with other words on the line'
        }
        $findings += [pscustomobject]@{
            Where = $Where; Line = $i + 1; Text = $line.Trim(); Reason = $reason
        }
    }

    return $findings
}

# ---------------------------------------------------------------------------
# Self-test. Every case is a shape a person has written, or will write.
# ---------------------------------------------------------------------------
if ($SelfTest) {
    # Not a real issue, so that a quotation of this file is inert.
    $n = '#999999'

    $mustFail = @(
        @{ Name  = 'the denial under a disclaiming heading, which fired on 13 Sep 2026'
           Text  = "## What this does not claim`n`nThat it closes $n. The mechanism is certain."
           Title = $false }
        @{ Name  = 'the other denial, loaded and never fired'
           Text  = "It does not close $n."
           Title = $false }
        @{ Name  = 'a commit message quoting the offending line, which fired the same day'
           Text  = "Note the trap`n`nUnder the heading stood a line saying that it closes $n."
           Title = $false }
        @{ Name  = 'mid-sentence, asserting the close'
           Text  = "This change closes $n and tidies the gate."
           Title = $false }
        @{ Name  = 'inside a block quote, which GitHub still reads'
           Text  = "> resolves $n"
           Title = $false }
        @{ Name  = 'the pair split across a line break'
           Text  = "This does not fix`n$n in any sense."
           Title = $false }
        @{ Name  = 'past tense, mid-sentence'
           Text  = "The accessibility pass that closed $n did not touch this."
           Title = $false }
        @{ Name  = 'a cross-repository reference'
           Text  = "Note that it closes TGoodhew/WinZ3805A$n at the same time."
           Title = $false }
        @{ Name  = 'a title that denies closing'
           Text  = "Measure the fix - this does not close $n"
           Title = $true }
        @{ Name  = 'a title that asserts closing'
           Text  = "Stop the growth, closes $n"
           Title = $true }
    )

    $mustPass = @(
        @{ Name = 'the conventional bare line'        ; Text = "Does the thing.`n`nCloses $n"           ; Title = $false }
        @{ Name = 'bare line with a full stop'        ; Text = "Closes $n."                             ; Title = $false }
        @{ Name = 'bare line, bulleted'               ; Text = "- Fixes $n"                             ; Title = $false }
        @{ Name = 'bare line, two issues'             ; Text = "Closes $n, closes $n"                   ; Title = $false }
        @{ Name = 'bare line, bold'                   ; Text = "**Resolves $n**"                        ; Title = $false }
        @{ Name = 'towards, the recommended phrasing' ; Text = "Towards $n - and deliberately so."      ; Title = $false }
        @{ Name = 'one mechanism behind'              ; Text = "One mechanism behind $n."               ; Title = $false }
        @{ Name = 'a mention with no keyword at all'  ; Text = "The $n investigation continues."        ; Title = $false }
        @{ Name = 'keyword and number not adjacent'   ; Text = "This closed the issue raised in $n."    ; Title = $false }
        @{ Name = 'a title using towards'             ; Text = "Measure the fix - towards $n"           ; Title = $true }
        @{ Name = 'a title naming the issue plainly'  ; Text = "Bound the emit against a deadlock ($n)" ; Title = $true }
    )

    $bad = 0
    foreach ($case in $mustFail) {
        $f = @(Test-Text -Text $case.Text -Where 'self-test' -NoBareLineAllowed:$case.Title)
        if ($f.Count -eq 0) {
            Write-Host "  MISSED   $($case.Name)" -ForegroundColor Red
            $bad++
        }
        else {
            Write-Host "  caught   $($case.Name)" -ForegroundColor DarkGray
        }
    }
    foreach ($case in $mustPass) {
        $f = @(Test-Text -Text $case.Text -Where 'self-test' -NoBareLineAllowed:$case.Title)
        if ($f.Count -ne 0) {
            Write-Host "  FALSE +  $($case.Name) -> $($f[0].Reason)" -ForegroundColor Red
            $bad++
        }
        else {
            Write-Host "  allowed  $($case.Name)" -ForegroundColor DarkGray
        }
    }

    Write-Host ''
    if ($bad -gt 0) {
        Write-Error "Self-test failed: $bad of $($mustFail.Count + $mustPass.Count) cases judged wrongly."
    }
    Write-Host "Self-test passed: $($mustFail.Count) violations caught, $($mustPass.Count) legitimate forms allowed." -ForegroundColor Green
    Write-Host 'It cannot check whether a bare closing line is TRUE. That is a claim about the work.' -ForegroundColor DarkGray
    exit 0
}

# ---------------------------------------------------------------------------
# The real run. Inputs come from the parameters or, in CI, from the workflow's environment.
# ---------------------------------------------------------------------------
if (-not $PSBoundParameters.ContainsKey('Title')) { $Title = $env:PR_TITLE }
if (-not $PSBoundParameters.ContainsKey('Body')) { $Body = $env:PR_BODY }
if (-not $PSBoundParameters.ContainsKey('Base')) { $Base = $env:PR_BASE }
if (-not $PSBoundParameters.ContainsKey('Head')) { $Head = $env:PR_HEAD }

$findings = @()
$findings += Test-Text -Text ([string]$Title) -Where 'the PR title' -NoBareLineAllowed
$findings += Test-Text -Text ([string]$Body) -Where 'the PR body'

$checkedCommits = 0
if ($Base -and $Head) {
    # Commit messages are checked because a rebase merge lands each one on the default branch
    # verbatim, where GitHub reads it. This is the half that fired the second closure.
    $shas = @(& git rev-list --no-merges "$Base..$Head" 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Could not list $Base..$Head - a shallow checkout? This job needs fetch-depth: 0." -ForegroundColor Yellow
        $shas = @()
    }
    foreach ($sha in $shas) {
        if (-not $sha) { continue }
        $checkedCommits++
        $message = (& git log -1 --format='%B' $sha) -join "`n"
        $findings += Test-Text -Text $message -Where "commit $($sha.Substring(0, 7))"
    }
}

if ($findings.Count -gt 0) {
    Write-Host ''
    Write-Host 'A closing keyword stands beside an issue number where it was probably not meant.' -ForegroundColor Red
    Write-Host ''
    foreach ($f in $findings) {
        Write-Host ("  {0}, line {1}" -f $f.Where, $f.Line) -ForegroundColor Yellow
        Write-Host ("    {0}" -f $f.Text)
        Write-Host ("    {0}" -f $f.Reason) -ForegroundColor DarkGray
        Write-Host ''
    }
    Write-Host 'GitHub reads the two words and not the sentence around them, so a denial closes the issue.' -ForegroundColor DarkGray
    Write-Host 'Say it without the keyword - "this issue stays open", "towards #NNN", "one mechanism behind #NNN" -' -ForegroundColor DarkGray
    Write-Host 'or, if you do mean to close it, put the keyword and the number on a line of their own.' -ForegroundColor DarkGray
    Write-Error "$($findings.Count) place(s) where a closing keyword may close an issue unintentionally."
}

Write-Host "No unintended closing keywords: title, body and $checkedCommits commit message(s) checked." -ForegroundColor Green
