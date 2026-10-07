<#
.SYNOPSIS
    Runs a release from its version bump to its closed QA-run issue, stopping only for judging and
    for Tony's go (#633, #743).

.DESCRIPTION
    A release was a dozen steps stitched together by hand: dispatch the dry run, download it, run
    the pass, soak against the last release, write the QA-run issue, wait for the go, merge, tag,
    watch the publish, check the assets, run the pass again, close the issue. This runs them as
    stages with their state in a file, so it can run for hours detached and be resumed where it
    stopped.

    Since #743 (7 Oct 2026) the tag makes a DRAFT release, which nobody but the repository's owner
    can see, and the pass and the soak run on that draft's own zips. So the bump is merged and the
    tag pushed without a go - neither makes anything public - and Tony's go is what publishes the
    draft. What users download is byte for byte what passed, which the publish stage checks by hash.
    Until then a tag published at once: the pass ran on a dry-run rebuild before the go and on the
    public zips after it, so the bits users got were tested only once they had them.

    It stops, and says so in its state, at three points:
      - judging: a pass whose photographs need verdicts (Complete-QaRun.ps1), then -Resume;
      - the go: nothing is published without -Go, which is passed only on Tony's word;
      - a failure: any FAIL or ERROR, a soak that grew, a workflow that failed. Fix, then -Resume,
        or start the stage again with -Restart <stage>. A fault in the app itself needs a new build:
        delete the draft and its tag (gh release delete vX --cleanup-tag), merge the fix, then
        -Restart tag, which tags the new main. Nothing was released, so nothing is withdrawn.

    Stages, in order:
      tag          check the bump PR sets the manifest, merge it (rebase), tag the merge commit,
                   wait for release.yml, and check what it made is a draft
      pass         Invoke-QaPass.ps1 -Release -Draft on the draft's zips, every default scenario
                   (release-assets included, checking the release is a draft)
      soak         the soak scenario for the last published release and for the draft, on
                   QA-Win11, compared: the draft may not grow more than 3 MB/hour faster
      issue        the QA-run issue, created with the pass's report and the soak's numbers
      go           waits for -Go
      publish      the draft made public; the public zips' hashes checked against the tested ones
      close        the hashes posted on the QA-run issue, the issue closed
.PARAMETER Version
    The version being released, as the manifest gives it (1.3.6 or 1.3.6.0).

.PARAMETER BumpPr
    The pull request that bumps Package.appxmanifest to this version. The tag stage merges it, once
    its branch is checked to set the manifest to this version, and tags the merge.

.PARAMETER Resume
    Carry on from the stage the state file records.

.PARAMETER Go
    Tony's go: lets the publish stage make the draft public. Recorded in the state with the time it
    was given.

.PARAMETER Restart
    Start again from the named stage.

.EXAMPLE
    pwsh build\qa\Invoke-Release.ps1 -Version 1.3.6 -BumpPr 712
.EXAMPLE
    pwsh build\qa\Invoke-Release.ps1 -Version 1.3.6 -Resume -Go
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [int]$BumpPr,
    [switch]$Resume,
    [switch]$Go,
    [ValidateSet('tag', 'pass', 'soak', 'issue', 'go', 'publish', 'close')][string]$Restart
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = ($Version -replace '^v', '')
if ($Version -notmatch '^(\d+\.\d+\.\d+)(\.0)?$') { throw "a version is x.y.z: $Version" }
# The first three parts, from the match: stripping a trailing ".0" instead turned 1.4.0 into 1.4, a
# tag of v1.4 and a manifest check for the wrong version (6 Oct 2026, the first x.y.0 release).
$short = $Matches[1]
$full = "$short.0"
$tag = "v$short"
$home_ = Join-Path $env:LOCALAPPDATA "WinZ3805A QA\releases\$tag"
New-Item -ItemType Directory -Force $home_ | Out-Null
$statePath = Join-Path $home_ 'state.json'
$logPath = Join-Path $home_ 'release.log'

function Say { param([string]$Text) $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $Text; Write-Host $line; Add-Content $logPath $line }

function Read-State {
    if (Test-Path $statePath) { Get-Content $statePath -Raw | ConvertFrom-Json -AsHashtable }
    else { @{ version = $full; tag = $tag; stage = 'tag'; status = 'new'; started = (Get-Date -Format 'yyyy-MM-dd HH:mm') } }
}
function Save-State { param($State) $State.updated = (Get-Date -Format 'yyyy-MM-dd HH:mm'); $State | ConvertTo-Json -Depth 5 | Set-Content $statePath -Encoding UTF8 }
function Stop-At { param($State, [string]$Status, [string]$Why) $State.status = $Status; $State.why = $Why; Save-State $State; Say "STOPPED at $($State.stage): $Status - $Why"; exit $(if ($Status -like 'waiting*') { 3 } else { 1 }) }

$state = Read-State
if ($BumpPr) { $state.bumpPr = $BumpPr }
if (-not $state.bumpPr) { throw 'Give the bump pull request with -BumpPr the first time.' }
if ($Restart) { $state.stage = $Restart; $state.status = 'restarted' }
elseif ((Test-Path $statePath) -and -not $Resume -and -not $BumpPr) { throw "A release of $tag is under way ($($state.stage), $($state.status)). Use -Resume, or -Restart <stage>." }
if ($Go) { $state.go = (Get-Date -Format 'yyyy-MM-dd HH:mm'); Say "Tony's go recorded" }
Save-State $state

# The pass's verdict: 0 PASS, 3 AWAITING JUDGEMENT, anything else a failure.
#
# Both VMs at once unless the arguments name the machines (the soak does): one Invoke-QaPass per VM,
# each into its own folder under <OutDir>.parts, merged into OutDir as one run (Merge-QaRuns). Halves a
# full pass - about three hours where one pass over both VMs in turn took five and a half - and is
# what the per-VM lock allows since 6 Oct 2026.
function Invoke-Pass {
    param([string[]]$Arguments, [string]$OutDir)
    $script = Join-Path $repo 'build\qa\Invoke-QaPass.ps1'
    if ($Arguments -contains '-Machines') {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script @Arguments -OutDir $OutDir *>> $logPath
        return $LASTEXITCODE
    }
    $parts = "$OutDir.parts"
    $quoted = @($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    # Windows PowerShell's own module path for the children. PowerShell 7 hands it to a powershell.exe
    # it runs with &, but Start-Process passes its own on, and Windows PowerShell then cannot load its
    # built-in modules: the first parallel pass of 1.4.0 failed every scenario on Get-FileHash not
    # being found (6 Oct 2026).
    $savedModulePath = $env:PSModulePath
    $env:PSModulePath = @(
        (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'WindowsPowerShell\Modules')
        (Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules')
        (Join-Path $env:SystemRoot 'system32\WindowsPowerShell\v1.0\Modules')
    ) -join ';'
    try {
        $procs = foreach ($machine in 'QA-Win10', 'QA-Win11') {
            New-Item -ItemType Directory -Force (Join-Path $parts $machine) | Out-Null
            $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$script`"") + $quoted + @('-Machines', $machine, '-OutDir', "`"$(Join-Path $parts $machine)`"")
            Start-Process powershell.exe -ArgumentList $argumentList -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $parts "$machine.log") -RedirectStandardError (Join-Path $parts "$machine.err")
        }
    }
    finally { $env:PSModulePath = $savedModulePath }
    Say "pass running on both VMs at once: $(($procs | ForEach-Object Id) -join ', '); logs in $parts"
    $procs | Wait-Process
    foreach ($machine in 'QA-Win10', 'QA-Win11') { Get-Content (Join-Path $parts "$machine.log") -ErrorAction SilentlyContinue | Add-Content $logPath }
    $merge = "Import-Module '$(Join-Path $repo 'build\qa\QaJudging.psm1')' -Force; `$s = Merge-QaRuns -Parts '$(Join-Path $parts 'QA-Win10')', '$(Join-Path $parts 'QA-Win11')' -RunDir '$OutDir'; `"merged: `$(`$s.Verdict)`"; exit `$(switch (`$s.Verdict) { 'PASS' { 0 } 'AWAITING JUDGEMENT' { 3 } default { 1 } })"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $merge *>> $logPath
    $LASTEXITCODE
}

# Through Windows PowerShell, as the pass itself runs: QaJudging.psm1 compiles against System.Drawing's
# Bitmap, which PowerShell 7 does not have, so importing it here fails before any verdict is read.
function Get-PassVerdict {
    param([string]$OutDir)
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'build\qa\Complete-QaRun.ps1') -Run $OutDir *>> $logPath
    switch ($LASTEXITCODE) { 0 { 'PASS' } 3 { 'AWAITING JUDGEMENT' } default { "FAIL (exit $LASTEXITCODE)" } }
}

# A run's report as GitHub will take it, which is at most 65,536 characters a body: 1.4.0's merged
# report was 155,540 and its QA-run issue was refused (6 Oct 2026). The summary - everything before
# the first per-scenario <details> - goes where the caller puts it; the details come back as chunks
# under 60,000 characters each, split between whole <details> blocks, for comments of their own.
function Split-Report {
    param([string]$Path)
    $text = Get-Content $Path -Raw
    $at = $text.IndexOf('<details>')
    $summary = if ($at -ge 0) { $text.Substring(0, $at).TrimEnd() } else { $text.TrimEnd() }
    $chunks = New-Object System.Collections.Generic.List[string]
    if ($at -ge 0) {
        $current = ''
        foreach ($block in [regex]::Split($text.Substring($at), '(?=<details>)') | Where-Object { $_.Trim() }) {
            if ($current.Length + $block.Length -gt 60000 -and $current) { $chunks.Add($current.TrimEnd()); $current = '' }
            $current += $block
        }
        if ($current.Trim()) { $chunks.Add($current.TrimEnd()) }
    }
    [pscustomobject]@{ Summary = $summary; Details = @($chunks) }
}

# The details as numbered comments on an issue.
function Add-ReportDetails {
    param([int]$Issue, $Details, [string]$What)
    for ($i = 0; $i -lt $Details.Count; $i++) {
        $file = Join-Path $home_ "details-$What-$($i + 1).md"
        @("### $What, scenario by scenario ($($i + 1) of $($Details.Count))", '', $Details[$i]) | Set-Content $file -Encoding UTF8
        gh issue comment $Issue --repo TGoodhew/WinZ3805A --body-file $file *>> $logPath
    }
}

function Get-SoakRate {
    param([string]$OutDir)
    $results = Get-Content (Join-Path $OutDir 'results.json') -Raw | ConvertFrom-Json
    $check = @($results | ForEach-Object { $_.Checks } | Where-Object { $_.Name -like '*private bytes*' }) | Select-Object -First 1
    if ($check -and $check.Detail -match '(-?[\d.]+) MB/hour') { [double]$Matches[1] } else { $null }
}

while ($true) {
    Say "stage $($state.stage)"
    switch ($state.stage) {
        'tag' {
            # Merged and tagged without a go: the tag makes only a draft, which no user can see
            # (#743, decided by Tony 7 Oct 2026). The go is what publishes it.
            $branch = gh pr view --repo TGoodhew/WinZ3805A $state.bumpPr --json headRefName -q .headRefName
            $pr = gh pr view --repo TGoodhew/WinZ3805A $state.bumpPr --json state | ConvertFrom-Json
            if ($pr.state -ne 'MERGED') {
                $manifest = gh api "repos/TGoodhew/WinZ3805A/contents/src/WinZ3805A/Package.appxmanifest?ref=$branch" -H 'Accept: application/vnd.github.raw'
                if (($manifest -join "`n") -notmatch "Version=""$([regex]::Escape($full))""") { Stop-At $state 'failed' "the bump branch $branch does not set the manifest to $full" }
                gh pr merge --repo TGoodhew/WinZ3805A $state.bumpPr --rebase --delete-branch *>> $logPath
                if ($LASTEXITCODE -ne 0) { Stop-At $state 'failed' "PR #$($state.bumpPr) did not merge" }
                Start-Sleep -Seconds 5
            }
            git -C $repo fetch -q origin main
            $sha = git -C $repo rev-parse origin/main
            $manifest = git -C $repo show "${sha}:src/WinZ3805A/Package.appxmanifest"
            if (($manifest -join "`n") -notmatch "Version=""$([regex]::Escape($full))""") { Stop-At $state 'failed' "main at $sha does not carry $full" }
            if (-not (git -C $repo tag -l $tag)) { git -C $repo tag $tag $sha }
            git -C $repo push origin $tag *>> $logPath
            $state.tagged = $sha; Save-State $state
            Say "tagged $tag on $sha; release.yml makes it a draft"
            $run = $null
            for ($i = 0; $i -lt 60 -and -not $run; $i++) { Start-Sleep -Seconds 10; $run = gh run list --repo TGoodhew/WinZ3805A --workflow release.yml --branch $tag --limit 1 --json databaseId | ConvertFrom-Json | Select-Object -First 1 }
            if (-not $run) { Stop-At $state 'failed' "release.yml did not start for $tag" }
            $state.build = $run.databaseId; Save-State $state
            gh run watch --repo TGoodhew/WinZ3805A $run.databaseId --exit-status --interval 30 *> $null
            if ($LASTEXITCODE -ne 0) { Stop-At $state 'failed' "release.yml run $($run.databaseId) failed for $tag" }
            $view = gh release view $tag --repo TGoodhew/WinZ3805A --json isDraft | ConvertFrom-Json
            if (-not $view.isDraft) { Stop-At $state 'failed' "$tag is public already: release.yml must create a draft (#743)" }
            $state.stage = 'pass'
        }
        'pass' {
            # The draft's own zips: what passes here is what the go publishes.
            $out = Join-Path $home_ 'draft-pass'
            if ($state.status -ne 'waiting for judgement' -or -not (Test-Path (Join-Path $out 'results.json'))) {
                $state.passStarted = (Get-Date -Format 'yyyy-MM-dd HH:mm'); Save-State $state
                $null = Invoke-Pass @('-Release', $tag, '-Draft') $out
            }
            $verdict = Get-PassVerdict $out
            $state.passVerdict = $verdict
            if ($verdict -eq 'AWAITING JUDGEMENT') { Stop-At $state 'waiting for judgement' "judge $out with Complete-QaRun.ps1, then -Resume" }
            if ($verdict -ne 'PASS') { Stop-At $state 'failed' "the draft's pass is ${verdict}: $out\report.md" }
            $state.stage = 'soak'
        }
        'soak' {
            $previous = @(gh release list --repo TGoodhew/WinZ3805A --limit 10 --json tagName,isDraft,isPrerelease -q '.[] | select(.isDraft | not) | select(.isPrerelease | not) | .tagName') | Where-Object { $_ -ne $tag } | Select-Object -First 1
            $baseOut = Join-Path $home_ 'soak-baseline'; $candOut = Join-Path $home_ 'soak-candidate'
            $null = Invoke-Pass @('-Release', $previous, '-Machines', 'QA-Win11', '-Scenarios', 'soak') $baseOut
            $null = Invoke-Pass @('-Release', $tag, '-Draft', '-Machines', 'QA-Win11', '-Scenarios', 'soak') $candOut
            $base = Get-SoakRate $baseOut; $cand = Get-SoakRate $candOut
            $state.soak = @{ previous = $previous; baseline = $base; candidate = $cand }
            if ($null -eq $base -or $null -eq $cand) { Stop-At $state 'failed' "a soak did not measure private bytes: $baseOut, $candOut" }
            # Read against another soak, not a threshold - but a candidate growing 3 MB/hour faster
            # than the release is outside the noise two soaks of one build showed (about 1.6), and
            # #399's leak was 19.
            if ($cand - $base -gt 3) { Stop-At $state 'failed' "the candidate's private bytes grew $cand MB/hour against $previous's $base" }
            Say "soak: $previous $base MB/hour, candidate $cand MB/hour"
            $state.stage = 'issue'
        }
        'issue' {
            if (-not $state.issue) {
                $body = Join-Path $home_ 'issue.md'
                $report = Split-Report (Join-Path $home_ 'draft-pass\report.md')
                # Scenarios re-run after a first-attempt failure, when the run says so (reruns.md).
                $reruns = Join-Path $home_ 'draft-pass\reruns.md'
                @(
                    "The release QA for **$tag**, run on the draft release's own zips (build $($state.build), from PR #$($state.bumpPr)). Run by ``build/qa/Invoke-Release.ps1``. The release is not public until Tony's go."
                    ''
                    "## Soak (§14): $($state.soak.previous) $($state.soak.baseline) MB/hour private, this build $($state.soak.candidate) MB/hour"
                    ''
                    '## The pass on the draft'
                    ''
                    $report.Summary
                    ''
                    $(if (Test-Path $reruns) { (Get-Content $reruns -Raw) -replace '^# ', '### ' })
                    ''
                    "Every scenario's checks follow in the comments below ($($report.Details.Count))."
                    ''
                    '## Owed after the go'
                    '- [ ] The draft published, its zips unchanged.'
                    ''
                    '🤖 Generated with [Claude Code](https://claude.com/claude-code)'
                ) | Set-Content $body -Encoding UTF8
                $url = gh issue create --repo TGoodhew/WinZ3805A --title "QA run: $tag" --label documentation --body-file $body
                if ($LASTEXITCODE -ne 0 -or -not $url) { Stop-At $state 'failed' "the QA-run issue was not created: $url" }
                $state.issue = [int]($url -replace '.*/', ''); Save-State $state
                Add-ReportDetails $state.issue $report.Details 'The draft''s pass'
                Say "QA-run issue #$($state.issue)"
            }
            $state.stage = 'go'
        }
        'go' {
            if (-not $state.go) { Stop-At $state 'waiting for the go' "the draft passed and #$($state.issue) has the report; publish only on Tony's go: -Resume -Go" }
            $state.stage = 'publish'
        }
        'publish' {
            if (-not $state.go) { Stop-At $state 'waiting for the go' 'no go recorded' }
            # The zips that passed, hashed before publishing, so publishing provably changed none.
            $tested = @{}
            foreach ($name in "WinZ3805A-$full-x64.zip", "WinZ3805A-$full-x64-offline.zip") {
                $tested[$name] = (Get-FileHash (Join-Path $env:LOCALAPPDATA "WinZ3805A QA\cache\$name") -Algorithm SHA256).Hash
            }
            gh release edit $tag --repo TGoodhew/WinZ3805A --draft=false *>> $logPath
            if ($LASTEXITCODE -ne 0) { Stop-At $state 'failed' "$tag did not publish" }
            $check = Join-Path $home_ 'published-zips'
            if (Test-Path $check) { Remove-Item -LiteralPath $check -Recurse -Force }
            gh release download $tag --repo TGoodhew/WinZ3805A --pattern '*.zip' --dir $check *>> $logPath
            $same = foreach ($name in $tested.Keys) { $tested[$name] -eq (Get-FileHash (Join-Path $check $name) -Algorithm SHA256).Hash }
            $view = gh release view $tag --repo TGoodhew/WinZ3805A --json isDraft,url | ConvertFrom-Json
            if ($view.isDraft -or @($same | Where-Object { -not $_ }).Count -or @($same).Count -ne 2) { Stop-At $state 'failed' "$tag published, but its zips do not match the ones that passed: check $check" }
            $state.published = (Get-Date -Format 'yyyy-MM-dd HH:mm'); $state.hashes = $tested; Save-State $state
            Say "published $tag ($($view.url)); its zips are the ones that passed"
            $state.stage = 'close'
        }
        'close' {
            $comment = Join-Path $home_ 'close.md'
            @(
                "## $tag published on Tony's go: $($state.published)"
                ''
                'The public zips are byte for byte the ones the pass and the soak ran on:'
                ''
                '| Zip | SHA-256 |'
                '|---|---|'
                $($state.hashes.GetEnumerator() | Sort-Object Name | ForEach-Object { "| ``$($_.Name)`` | ``$($_.Value)`` |" })
                ''
                '🤖 Generated with [Claude Code](https://claude.com/claude-code)'
            ) | Set-Content $comment -Encoding UTF8
            gh issue comment $state.issue --repo TGoodhew/WinZ3805A --body-file $comment *>> $logPath
            gh issue close $state.issue --repo TGoodhew/WinZ3805A *>> $logPath
            $state.status = 'done'; Save-State $state
            Say "$tag QA'd as a draft and published; #$($state.issue) closed"
            exit 0
        }
    }
    $state.status = 'running'
    Save-State $state
}
