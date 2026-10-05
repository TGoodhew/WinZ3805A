<#
.SYNOPSIS
    Records verdicts on a QA run's photographs, regenerates its report, and makes a passed run the
    baseline (#633).

.DESCRIPTION
    Invoke-QaPass.ps1 compares every photograph a pass kept with the last accepted pass's, and ends
    AWAITING JUDGEMENT (exit 3) while any that differ has no verdict. The judge - the agent, with
    Tony's standing agreement - looks at each triptych in the run's judging\ folder, full size, and
    records a verdict here. Only when every difference has a verdict, no check failed and nothing
    was judged a failure is the run PASS, and only a PASS run can become the baseline.

    A verdict says what was looked at. "pass: only the clock and the 1 PPS reading differ" is a
    verdict; "pass" on its own is a rubber stamp, and the note is required for that reason.

.PARAMETER Run
    The run folder (%LOCALAPPDATA%\WinZ3805A QA\runs\<stamp>).

.PARAMETER Pass
    Photographs to pass: wildcards on their path within the run, e.g. 'QA-Win11-greyscale-states\*'.

.PARAMETER Fail
    Photographs to fail: wildcards, as for -Pass.

.PARAMETER Note
    What the judge saw. Required with -Pass or -Fail.

.PARAMETER Promote
    Make this run the baseline. Refused unless its verdict is PASS.

.EXAMPLE
    .\build\qa\Complete-QaRun.ps1 -Run $run -Pass 'QA-Win10-greyscale-states\*' -Note 'only readings and the clock differ'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Pass = @(),
    [string[]]$Fail = @(),
    [string]$Note,
    [switch]$Promote
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'QaJudging.psm1') -Force
$Run = (Resolve-Path $Run).Path

if (($Pass.Count -or $Fail.Count) -and -not $Note) { throw 'A verdict needs -Note: what was looked at, and what differed.' }
foreach ($p in $Pass) { "passed $(Set-QaVerdict -RunDir $Run -Image $p -Verdict pass -Note $Note) photograph(s) matching $p" }
foreach ($p in $Fail) { "failed $(Set-QaVerdict -RunDir $Run -Image $p -Verdict fail -Note $Note) photograph(s) matching $p" }

$summary = Write-QaReport $Run
"verdict: $($summary.Verdict) - $($summary.Failed) failed scenario(s), $($summary.Awaiting) photograph(s) awaiting a verdict, $($summary.Rejected) judged a failure"
"report: $($summary.Report)"
if ($Promote) { "baseline: $(Publish-QaBaseline -RunDir $Run) photograph(s) from this run" }
exit $(switch ($summary.Verdict) { 'PASS' { 0 } 'AWAITING JUDGEMENT' { 3 } default { 1 } })
