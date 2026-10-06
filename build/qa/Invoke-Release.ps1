<#
.SYNOPSIS
    Runs a release from its version bump to its closed QA-run issue, stopping only for judging and
    for Tony's go (#633).

.DESCRIPTION
    A release was a dozen steps stitched together by hand: dispatch the dry run, download it, run
    the pass, soak against the last release, write the QA-run issue, wait for the go, merge, tag,
    watch the publish, check the assets, run the pass again, close the issue. This runs them as
    stages with their state in a file, so it can run for hours detached and be resumed where it
    stopped.

    It stops, and says so in its state, at three points:
      - judging: a pass whose photographs need verdicts (Complete-QaRun.ps1), then -Resume;
      - the go: nothing is merged or tagged without -Go, which is passed only on Tony's word -
        his standing rule is that releases are tagged on his go and no one else's;
      - a failure: any FAIL or ERROR, a soak that grew, a workflow that failed. Fix, then -Resume,
        or start the stage again with -Restart <stage>.

    Stages, in order:
      dry-run      dispatch release.yml with dry_run on the bump branch, wait, download the zips
      pass         Invoke-QaPass.ps1 on the dry run's zips, every default scenario
      soak         the soak scenario for the last release and for the dry run, on QA-Win11,
                   compared: the candidate may not grow more than 3 MB/hour faster than the release
      issue        the QA-run issue, created with the pass's report and the soak's numbers
      go           waits for -Go
      tag          merge the bump PR (rebase), tag the merge commit, wait for the publish
      published    Invoke-QaPass.ps1 -Release on the published zips (release-assets included)
      close        the results posted on the QA-run issue, the issue closed

.PARAMETER Version
    The version being released, as the manifest gives it (1.3.6 or 1.3.6.0).

.PARAMETER BumpPr
    The pull request that bumps Package.appxmanifest to this version. Its branch is what the dry run
    builds and what is merged before the tag.

.PARAMETER Resume
    Carry on from the stage the state file records.

.PARAMETER Go
    Tony's go: lets the tag stage run. Recorded in the state with the time it was given.

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
    [ValidateSet('dry-run', 'pass', 'soak', 'issue', 'go', 'tag', 'published', 'close')][string]$Restart
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = ($Version -replace '^v', '')
if ($Version -notmatch '^\d+\.\d+\.\d+(\.0)?$') { throw "a version is x.y.z: $Version" }
$short = $Version -replace '\.0$', ''
$full = "$short.0"
$tag = "v$short"
$home_ = Join-Path $env:LOCALAPPDATA "WinZ3805A QA\releases\$tag"
New-Item -ItemType Directory -Force $home_ | Out-Null
$statePath = Join-Path $home_ 'state.json'
$logPath = Join-Path $home_ 'release.log'

function Say { param([string]$Text) $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $Text; Write-Host $line; Add-Content $logPath $line }

function Read-State {
    if (Test-Path $statePath) { Get-Content $statePath -Raw | ConvertFrom-Json -AsHashtable }
    else { @{ version = $full; tag = $tag; stage = 'dry-run'; status = 'new'; started = (Get-Date -Format 'yyyy-MM-dd HH:mm') } }
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
    $procs = foreach ($machine in 'QA-Win10', 'QA-Win11') {
        New-Item -ItemType Directory -Force (Join-Path $parts $machine) | Out-Null
        $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$script`"") + $quoted + @('-Machines', $machine, '-OutDir', "`"$(Join-Path $parts $machine)`"")
        Start-Process powershell.exe -ArgumentList $argumentList -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $parts "$machine.log") -RedirectStandardError (Join-Path $parts "$machine.err")
    }
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

function Get-SoakRate {
    param([string]$OutDir)
    $results = Get-Content (Join-Path $OutDir 'results.json') -Raw | ConvertFrom-Json
    $check = @($results | ForEach-Object { $_.Checks } | Where-Object { $_.Name -like '*private bytes*' }) | Select-Object -First 1
    if ($check -and $check.Detail -match '(-?[\d.]+) MB/hour') { [double]$Matches[1] } else { $null }
}

while ($true) {
    Say "stage $($state.stage)"
    switch ($state.stage) {
        'dry-run' {
            $branch = gh pr view $state.bumpPr --json headRefName -q .headRefName
            $manifest = gh api "repos/TGoodhew/WinZ3805A/contents/src/WinZ3805A/Package.appxmanifest?ref=$branch" -H 'Accept: application/vnd.github.raw'
            if (($manifest -join "`n") -notmatch "Version=""$([regex]::Escape($full))""") { Stop-At $state 'failed' "the bump branch $branch does not set the manifest to $full" }
            $since = [DateTimeOffset]::UtcNow.AddSeconds(-5)
            gh workflow run release.yml --ref $branch -f dry_run=true | Out-Null
            $run = $null
            for ($i = 0; $i -lt 30 -and -not $run; $i++) {
                Start-Sleep -Seconds 5
                $run = gh run list --workflow release.yml --branch $branch --limit 5 --json databaseId,createdAt | ConvertFrom-Json |
                    Where-Object { [DateTimeOffset]$_.createdAt -ge $since } | Select-Object -First 1
            }
            if (-not $run) { Stop-At $state 'failed' 'the dry run did not start' }
            $state.dryRun = $run.databaseId; Save-State $state
            Say "dry run $($run.databaseId) building"
            gh run watch $run.databaseId --exit-status --interval 30 *> $null
            if ($LASTEXITCODE -ne 0) { Stop-At $state 'failed' "dry run $($run.databaseId) failed" }
            $zips = Join-Path $home_ 'dry-run-zips'
            gh run download $run.databaseId -D $zips *>> $logPath
            $state.online = (Get-ChildItem $zips -Recurse -Filter "WinZ3805A-$full-x64.zip" | Select-Object -First 1).FullName
            $state.offline = (Get-ChildItem $zips -Recurse -Filter "WinZ3805A-$full-x64-offline.zip" | Select-Object -First 1).FullName
            if (-not $state.online -or -not $state.offline) { Stop-At $state 'failed' "the dry run's artifact has no zips named for $full" }
            $state.stage = 'pass'
        }
        'pass' {
            $out = Join-Path $home_ 'dry-run-pass'
            if ($state.status -ne 'waiting for judgement' -or -not (Test-Path (Join-Path $out 'results.json'))) {
                $state.passStarted = (Get-Date -Format 'yyyy-MM-dd HH:mm'); Save-State $state
                $null = Invoke-Pass @('-Online', $state.online, '-Offline', $state.offline) $out
            }
            $verdict = Get-PassVerdict $out
            $state.passVerdict = $verdict
            if ($verdict -eq 'AWAITING JUDGEMENT') { Stop-At $state 'waiting for judgement' "judge $out with Complete-QaRun.ps1, then -Resume" }
            if ($verdict -ne 'PASS') { Stop-At $state 'failed' "the dry run's pass is ${verdict}: $out\report.md" }
            $state.stage = 'soak'
        }
        'soak' {
            $previous = @(gh release list --repo TGoodhew/WinZ3805A --limit 10 --json tagName,isDraft,isPrerelease -q '.[] | select(.isDraft | not) | select(.isPrerelease | not) | .tagName') | Where-Object { $_ -ne $tag } | Select-Object -First 1
            $baseOut = Join-Path $home_ 'soak-baseline'; $candOut = Join-Path $home_ 'soak-candidate'
            $null = Invoke-Pass @('-Release', $previous, '-Machines', 'QA-Win11', '-Scenarios', 'soak') $baseOut
            $null = Invoke-Pass @('-Online', $state.online, '-Offline', $state.offline, '-Machines', 'QA-Win11', '-Scenarios', 'soak') $candOut
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
                @(
                    "The release QA for **$tag**, built from PR #$($state.bumpPr) (dry run $($state.dryRun)). Run by ``build/qa/Invoke-Release.ps1``."
                    ''
                    "## Soak (§14): $($state.soak.previous) $($state.soak.baseline) MB/hour private, this build $($state.soak.candidate) MB/hour"
                    ''
                    '## The pass on the signed dry run'
                    ''
                    (Get-Content (Join-Path $home_ 'dry-run-pass\report.md') -Raw)
                    ''
                    '## Owed after the tag'
                    '- [ ] The pass on the published zips, with `release-assets`.'
                    ''
                    '🤖 Generated with [Claude Code](https://claude.com/claude-code)'
                ) | Set-Content $body -Encoding UTF8
                $url = gh issue create --repo TGoodhew/WinZ3805A --title "QA run: $tag" --label documentation --body-file $body
                $state.issue = [int]($url -replace '.*/', '')
                Say "QA-run issue #$($state.issue)"
            }
            $state.stage = 'go'
        }
        'go' {
            if (-not $state.go) { Stop-At $state 'waiting for the go' "the dry run passed and #$($state.issue) has the report; tag only on Tony's go: -Resume -Go" }
            $state.stage = 'tag'
        }
        'tag' {
            if (-not $state.go) { Stop-At $state 'waiting for the go' 'no go recorded' }
            $pr = gh pr view $state.bumpPr --json state,mergeCommit | ConvertFrom-Json
            if ($pr.state -ne 'MERGED') {
                gh pr merge $state.bumpPr --rebase --delete-branch *>> $logPath
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
            Say "tagged $tag on $sha"
            $run = $null
            for ($i = 0; $i -lt 60 -and -not $run; $i++) { Start-Sleep -Seconds 10; $run = gh run list --workflow release.yml --branch $tag --limit 1 --json databaseId | ConvertFrom-Json | Select-Object -First 1 }
            if (-not $run) { Stop-At $state 'failed' "release.yml did not start for $tag" }
            gh run watch $run.databaseId --exit-status --interval 30 *> $null
            if ($LASTEXITCODE -ne 0) { Stop-At $state 'failed' "release.yml run $($run.databaseId) failed for $tag" }
            $state.stage = 'published'
        }
        'published' {
            $out = Join-Path $home_ 'published-pass'
            if ($state.status -ne 'waiting for judgement' -or -not (Test-Path (Join-Path $out 'results.json'))) {
                $null = Invoke-Pass @('-Release', $tag) $out
            }
            $verdict = Get-PassVerdict $out
            $state.publishedVerdict = $verdict
            if ($verdict -eq 'AWAITING JUDGEMENT') { Stop-At $state 'waiting for judgement' "judge $out with Complete-QaRun.ps1, then -Resume" }
            if ($verdict -ne 'PASS') { Stop-At $state 'failed' "the published pass is ${verdict}: $out\report.md" }
            $state.stage = 'close'
        }
        'close' {
            $comment = Join-Path $home_ 'close.md'
            @(
                "## The pass on the published $tag zips: $($state.publishedVerdict)"
                ''
                (Get-Content (Join-Path $home_ 'published-pass\report.md') -Raw)
                ''
                '🤖 Generated with [Claude Code](https://claude.com/claude-code)'
            ) | Set-Content $comment -Encoding UTF8
            gh issue comment $state.issue --repo TGoodhew/WinZ3805A --body-file $comment *>> $logPath
            gh issue close $state.issue --repo TGoodhew/WinZ3805A *>> $logPath
            $state.status = 'done'; Save-State $state
            Say "$tag released and QA'd; #$($state.issue) closed"
            exit 0
        }
    }
    $state.status = 'running'
    Save-State $state
}
