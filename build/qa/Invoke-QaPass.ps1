<#
.SYNOPSIS
    Runs the automated half of the release QA pass against a build's zips (#633).

.DESCRIPTION
    Each scenario starts a test VM from its QA Clean snapshot, does what a person would do on a
    fresh machine, reads the outcome from the installer's exit code, its log and the state of the
    guest, and powers the VM off again. Nothing is left running, even when a scenario fails.

    The results go to a folder - one subfolder per scenario with everything collected from the
    guest - and to report.md there, written to be pasted into the release's QA-run issue.

    Scenarios (manual-qa.md §12, and §8 on the host):
      binary-audit       §8   no excluded command in the shipped assemblies (host, no VM)
      fresh-online       §12  the online zip on a machine with no .NET: exit 3, app installed
      fresh-offline      §12  the offline zip installs .NET too: exit 0, the start check passes
      upgrade-1.2.0      §12  a used v1.2.0 replaced: data moved, old copy and certificate gone
      leftover-cert      §12  v1.2.0 uninstalled by hand: its certificate is still removed
      app-checks         §11, §25, §18  on the running app, after a clean offline install: the
                              guide in the package and F1, a second launch typed into the Start
                              menu bringing a covered window forward, and the tray icon surviving
                              an Explorer restart (guest\AppChecks.ps1); a screenshot is kept
      receiver           the app against the simulated Z3805A on the VM's COM2 (#639): connects and
                              locks, follows a pulled antenna into holdover and back, and comes
                              back by itself after the receiver goes silent. Needs the VM's
                              simulator port (Add-QaSimulatorPort)

.PARAMETER Online
    The candidate's online zip. With -Offline, or with -Release instead.

.PARAMETER Offline
    The candidate's offline zip.

.PARAMETER Release
    A published release tag (e.g. v1.3.3) whose zips are downloaded and used as the candidate.

.PARAMETER Machines
    Which VMs to run on: QA-Win10, QA-Win11, or both (the default).

.PARAMETER Scenarios
    Which scenarios to run; all of them by default.

.EXAMPLE
    .\build\qa\Invoke-QaPass.ps1 -Release v1.3.3
#>
[CmdletBinding()]
param(
    [string]$Online,
    [string]$Offline,
    [string]$Release,
    [string[]]$Machines = @('QA-Win10', 'QA-Win11'),
    [string[]]$Scenarios = @('binary-audit', 'fresh-online', 'fresh-offline', 'upgrade-1.2.0', 'leftover-cert', 'app-checks', 'receiver'),
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'QaVm.psm1') -Force

# Called with powershell -File, a list such as -Scenarios a,b arrives as the one string 'a,b', which
# names no scenario - the first run of this script ran nothing and reported success.
$Machines = @($Machines | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Scenarios = @($Scenarios | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$known = @('binary-audit', 'fresh-online', 'fresh-offline', 'upgrade-1.2.0', 'leftover-cert', 'app-checks', 'receiver')
$unknown = @($Scenarios | Where-Object { $known -notcontains $_ })
if ($unknown.Count) { throw "Unknown scenario(s): $($unknown -join ', '). Known: $($known -join ', ')." }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$cache = Join-Path $env:LOCALAPPDATA 'WinZ3805A QA\cache'
New-Item -ItemType Directory -Force $cache | Out-Null
if (-not $OutDir) { $OutDir = Join-Path $env:LOCALAPPDATA ('WinZ3805A QA\runs\{0:yyyyMMdd-HHmmss}' -f (Get-Date)) }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$vmRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Virtual Machines\QA'

# The certificates every release has been signed with. Named by thumbprint, as the installer does.
$currentCert = '7F47E8D75FB856C5A1665900293EDB491E278B3C'
$retiredCert = '655D07E31BDA80CBF6AAC2F2635EA6664B9399DD'

function Say { param([string]$Text) Write-Host ('{0:HH:mm:ss}  {1}' -f (Get-Date), $Text) }

# A published release's zip, downloaded once into the cache.
function Get-ReleaseZip {
    param([string]$Tag, [switch]$OfflineZip)
    $version = ($Tag.TrimStart('v') + '.0')
    $name = "WinZ3805A-$version-x64$(if ($OfflineZip) { '-offline' }).zip"
    $path = Join-Path $cache $name
    if (-not (Test-Path $path)) {
        Say "downloading $name"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -UseBasicParsing "https://github.com/TGoodhew/WinZ3805A/releases/download/$Tag/$name" -OutFile $path
    }
    $path
}

if ($Release) {
    $Online = Get-ReleaseZip $Release
    $Offline = Get-ReleaseZip $Release -OfflineZip
}
if (-not $Online -or -not $Offline) { throw 'Give the candidate as -Online and -Offline zips, or as -Release <tag>.' }
$Online = (Resolve-Path $Online).Path
$Offline = (Resolve-Path $Offline).Path
Say "candidate: $(Split-Path $Online -Leaf) and $(Split-Path $Offline -Leaf)"

# ---------------------------------------------------------------------------
# Results
# ---------------------------------------------------------------------------
$results = New-Object System.Collections.Generic.List[object]

function New-Result {
    param([string]$Scenario, [string]$Machine)
    [pscustomobject]@{ Scenario = $Scenario; Machine = $Machine; Checks = (New-Object System.Collections.Generic.List[object]); Error = $null; Folder = $null }
}

function Check {
    param($Result, [string]$Name, [bool]$Ok, [string]$Detail = '')
    $Result.Checks.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail })
    Say ("  {0}  {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { " - $Detail" }))
}

# ---------------------------------------------------------------------------
# Guest steps
# ---------------------------------------------------------------------------
# A zip copied in and expanded to C:\qa\<label>.
function Send-Zip {
    param($Vm, [string]$Zip, [string]$Label)
    $name = Split-Path $Zip -Leaf
    try { Invoke-VmRun $Vm createDirectoryInGuest -Arguments 'C:\qa' -Guest | Out-Null } catch { }
    Copy-QaFile $Vm -Source $Zip -Destination "C:\qa\$name" -ToGuest
    $r = Invoke-QaGuestScript $Vm -Name "expand-$Label" -Script "Expand-Archive -Path 'C:\qa\$name' -DestinationPath 'C:\qa\$Label' -Force"
    if ($r.ExitCode -ne 0) { throw "expanding $name in the guest failed: $($r.Output)" }
}

# The candidate's installer, unattended. Returns its exit code (see install.ps1's -Unattended).
#
# A candidate built before -Unattended existed - every release up to v1.3.3 - would sit at its
# first "Press Enter" forever; the first full run hung there for ten minutes. Such an installer
# gets its prompts answered with newlines, and the exit code is read from the outcome its log
# records instead, so older releases can be candidates too: the proof that a scenario can fail is
# v1.3.2 failing the Windows 10 upgrade.
function Install-Candidate {
    param($Vm, [string]$Label)
    $r = Invoke-QaGuestScript $Vm -Name "install-$Label" -Script @"
if (Select-String -Path 'C:\qa\$Label\install.ps1' -Pattern '\[switch\]\`$Unattended' -Quiet) {
    & cmd.exe /c 'C:\qa\$Label\Install.cmd' -Unattended
    exit `$LASTEXITCODE
}
'this installer has no -Unattended; answering its prompts and reading its log'
(1..8 | ForEach-Object { '' }) | & cmd.exe /c 'C:\qa\$Label\Install.cmd'
`$log = Get-ChildItem (Join-Path `$env:LOCALAPPDATA 'WinZ3805A Installer\logs') -Filter 'install-*.log' | Sort-Object Name | Select-Object -Last 1 | Get-Content -Raw
if (`$log -match 'FAILED:') { exit 1 }
if (`$log -match 'started ok: True') { exit 0 }
if (`$log -match 'started ok: False') { exit 2 }
exit 3
"@
    $r.ExitCode
}

# An older release's installer, which has no -Unattended: its prompts and its closing pause are
# answered by newlines on standard input.
function Install-Old {
    param($Vm, [string]$Label)
    $r = Invoke-QaGuestScript $Vm -Name "install-old-$Label" -Script "(1..8 | ForEach-Object { '' }) | & cmd.exe /c 'C:\qa\$Label\Install.cmd'`r`nexit `$LASTEXITCODE"
    $r.ExitCode
}

# .NET 10, from the offline zip's own copy of Microsoft's installer, which the old releases need.
function Install-DotNet {
    param($Vm, [string]$OfflineLabel)
    $r = Invoke-QaGuestScript $Vm -Name 'dotnet' -Script @"
`$installer = Get-ChildItem 'C:\qa\$OfflineLabel\Runtime' -Filter 'dotnet-runtime-*.exe' | Select-Object -First 1
`$p = Start-Process `$installer.FullName -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
exit `$p.ExitCode
"@
    if ($r.ExitCode -notin 0, 3010) { throw ".NET did not install in the guest: exit $($r.ExitCode)" }
}

# Starts an installed copy and leaves it 20 s to write its data, then stops it.
function Use-App {
    param($Vm, [string]$Family)
    $r = Invoke-QaGuestScript $Vm -Name 'use-app' -Script @"
Start-Process 'shell:AppsFolder\$Family!App'
Start-Sleep -Seconds 20
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
"@
}

# The guest's state as far as WinZ3805A is concerned, as an object.
function Get-Facts {
    param($Vm)
    $r = Invoke-QaGuestScript $Vm -Name 'facts' -Script @'
$facts = [ordered]@{}
$facts.build = [int](Get-CimInstance Win32_OperatingSystem).BuildNumber
$facts.packages = @(Get-AppxPackage -Name WinZ3805A | ForEach-Object { [ordered]@{ family = $_.PackageFamilyName; version = "$($_.Version)"; status = "$($_.Status)" } })
$facts.certificates = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople | ForEach-Object Thumbprint)
$facts.savedCopies = @(Get-ChildItem ([Environment]::GetFolderPath('MyDocuments')) -Directory -Filter 'WinZ3805A earlier copy*' -ErrorAction SilentlyContinue | ForEach-Object { [ordered]@{ name = $_.Name; trendDb = (Test-Path (Join-Path $_.FullName 'trend.db')) } })
$facts.data = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'WinZ3805A_*' -ErrorAction SilentlyContinue | ForEach-Object { [ordered]@{ family = $_.Name; trendDb = (Test-Path (Join-Path $_.FullName 'LocalCache\Local\WinZ3805A\trend.db')) } })
$facts.dotnet = @(Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App') -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
$facts | ConvertTo-Json -Depth 4 -Compress
'@
    if ($r.ExitCode -ne 0) { throw "reading the guest's state failed: $($r.Output)" }
    ($r.Output -split "`r?`n" | Where-Object { $_.StartsWith('{') } | Select-Object -Last 1) | ConvertFrom-Json
}

# The installer logs and every copy's app.log, into the scenario's folder; returns the newest
# installer log's text.
function Save-Evidence {
    param($Vm, [string]$Folder)
    $r = Invoke-QaGuestScript $Vm -Name 'collect' -Script @'
$bag = 'C:\qa\evidence'
Remove-Item $bag -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$bag\installer" | Out-Null
Copy-Item (Join-Path $env:LOCALAPPDATA 'WinZ3805A Installer\logs\*') "$bag\installer" -ErrorAction SilentlyContinue
Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'WinZ3805A_*' -ErrorAction SilentlyContinue | ForEach-Object {
    $log = Join-Path $_.FullName 'LocalCache\Local\WinZ3805A\logs\app.log'
    if (Test-Path $log) { Copy-Item $log "$bag\app-$($_.Name).log" }
}
Compress-Archive -Path "$bag\*" -DestinationPath 'C:\qa\evidence.zip' -Force
'@
    $zip = Join-Path $Folder 'evidence.zip'
    if ($r.ExitCode -eq 0) {
        Copy-QaFile $Vm -Source 'C:\qa\evidence.zip' -Destination $zip
        Expand-Archive $zip -DestinationPath $Folder -Force
    }
    $newest = Get-ChildItem (Join-Path $Folder 'installer') -Filter 'install-*.log' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
    if ($newest) { Get-Content $newest.FullName -Raw } else { '' }
}

function Get-Vm {
    param([string]$Machine)
    New-QaVm -VmxPath (Join-Path $vmRoot "$Machine\$Machine.vmx") -GuestTarget "WinZ3805A-QA:$Machine"
}

# ---------------------------------------------------------------------------
# Scenarios
# ---------------------------------------------------------------------------
function Test-FreshOnline {
    param($Vm, $Result)
    Send-Zip $Vm $Online 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    $log = Save-Evidence $Vm $Result.Folder
    $facts = Get-Facts $Vm
    Check $Result 'exit code 3 (installed, .NET missing)' ($code -eq 3) "exit $code"
    Check $Result 'this release installed' (@($facts.packages | Where-Object { $_.family -like '*c2j642r4w54xr' }).Count -eq 1)
    Check $Result 'its certificate trusted' ($facts.certificates -contains $currentCert)
    Check $Result 'start check skipped for .NET' ($log -match 'start check   skipped: \.NET 10 is not installed yet')
}

function Test-FreshOffline {
    param($Vm, $Result)
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    $log = Save-Evidence $Vm $Result.Folder
    $facts = Get-Facts $Vm
    Check $Result 'exit code 0 (installed and started)' ($code -eq 0) "exit $code"
    Check $Result '.NET 10 installed' (@($facts.dotnet | Where-Object { $_ -like '10.*' }).Count -gt 0) ($facts.dotnet -join ', ')
    Check $Result 'one elevation, for the certificate and .NET' (([regex]::Matches($log, 'elevating for:')).Count -eq 1 -and $log -match 'elevating for: certificate \.NET')
    Check $Result 'start check passed' ($log -match 'finished      started ok: True')
}

# A used v1.2.0 on the machine, which is what the replacement scenarios start from.
function Install-UsedV120 {
    param($Vm)
    Send-Zip $Vm $Offline 'candidate-offline'
    Install-DotNet $Vm 'candidate-offline'
    Send-Zip $Vm (Get-ReleaseZip 'v1.2.0') 'v120'
    $code = Install-Old $Vm 'v120'
    $old = @((Get-Facts $Vm).packages | Where-Object { $_.version -eq '1.2.0.0' })
    if ($old.Count -ne 1) { throw "v1.2.0 did not install (its installer exited $code)" }
    Use-App $Vm $old[0].family
    $old[0].family
}

function Test-Upgrade120 {
    param($Vm, $Result)
    $oldFamily = Install-UsedV120 $Vm
    Send-Zip $Vm $Online 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    $log = Save-Evidence $Vm $Result.Folder
    $facts = Get-Facts $Vm
    $expectedOrder = if ($facts.build -lt 22000) { 'save and remove the earlier copy, then install' } else { 'install, then save and remove the earlier copy' }
    Check $Result 'exit code 0' ($code -eq 0) "exit $code"
    Check $Result "order for build $($facts.build)" ($log -match [regex]::Escape("decide order  $expectedOrder"))
    Check $Result 'only this release installed' (@($facts.packages).Count -eq 1 -and $facts.packages[0].family -like '*c2j642r4w54xr') (($facts.packages | ForEach-Object { "$($_.family) $($_.version)" }) -join ', ')
    Check $Result 'earlier certificate removed' (-not ($facts.certificates -contains $retiredCert))
    Check $Result 'earlier data saved to Documents' (@($facts.savedCopies | Where-Object { $_.trendDb }).Count -ge 1)
    Check $Result 'history moved into the new copy' (@($facts.data | Where-Object { $_.family -like '*c2j642r4w54xr' -and $_.trendDb }).Count -eq 1)
    Check $Result 'start check passed' ($log -match 'finished      started ok: True')
}

function Test-LeftoverCert {
    param($Vm, $Result)
    $oldFamily = Install-UsedV120 $Vm
    $r = Invoke-QaGuestScript $Vm -Name 'uninstall-old' -Script "Get-AppxPackage -Name WinZ3805A | Remove-AppxPackage"
    Send-Zip $Vm $Online 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    $log = Save-Evidence $Vm $Result.Folder
    $facts = Get-Facts $Vm
    Check $Result 'exit code 0' ($code -eq 0) "exit $code"
    Check $Result 'leftover certificate removed' (-not ($facts.certificates -contains $retiredCert))
    Check $Result 'this release trusted' ($facts.certificates -contains $currentCert)
}


function Test-AppChecks {
    param($Vm, $Result)
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed and started (exit 0)' ($code -eq 0) "exit $code"
    $r = Invoke-QaGuestScript $Vm -Name 'app-checks' -Script (Get-Content (Join-Path $PSScriptRoot 'guest\AppChecks.ps1') -Raw)
    # For the visual review the pass leaves to the agent (manual-qa.md, #633).
    try { Invoke-VmRun $Vm captureScreen -Arguments (Join-Path $Result.Folder 'screen.png') -Guest | Out-Null } catch { }
    $null = Save-Evidence $Vm $Result.Folder
    $line = $r.Output -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('[') -or $_.TrimStart().StartsWith('{') } | Select-Object -Last 1
    if (-not $line) { throw "the app checks printed no results: $($r.Output)" }
    # Assigned first, then enumerated: Windows PowerShell's ConvertFrom-Json emits a JSON array as
    # one object, so @($line | ConvertFrom-Json) is a one-element list holding the whole array - the
    # first run reported seven checks as one PASS, which would have hidden any failure among them.
    $parsed = $line | ConvertFrom-Json
    foreach ($c in $parsed) { Check $Result "[$($c.section)] $($c.name)" ($c.ok -eq $true) "$($c.detail)" }
}

# Builds the Z3805A simulator once per run, into the run's own folder.
function Get-Simulator {
    $exe = Join-Path $OutDir 'simulator\SmartClockSimulator.exe'
    if (-not (Test-Path $exe)) {
        $output = & dotnet build (Join-Path $repo 'tools\SmartClockSimulator\SmartClockSimulator.csproj') -c Release -o (Split-Path $exe) 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw "the simulator did not build: $output" }
    }
    $exe
}

# One control command to a running simulator, and its answer.
function Send-SimulatorControl {
    param([string]$Pipe, [string]$Command)
    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect(10000)
        $writer = New-Object System.IO.StreamWriter($client)
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($client)
        $writer.WriteLine($Command)
        $reader.ReadLine()
    }
    finally { $client.Dispose() }
}

# Waits in the guest for the app's log to gain a line matching a pattern after a given line count,
# and returns what it found: { found, line, count }.
function Wait-AppLog {
    param($Vm, [string]$Pattern, [int]$After, [int]$Seconds, [string]$Label)
    $r = Invoke-QaGuestScript $Vm -Name "wait-$Label" -Script @"
`$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
`$log = Join-Path `$env:LOCALAPPDATA "Packages\`$(`$pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A\logs\app.log"
`$deadline = (Get-Date).AddSeconds($Seconds)
do {
    `$lines = @(if (Test-Path `$log) { Get-Content `$log })
    `$hit = `$lines | Select-Object -Skip $After | Where-Object { `$_ -match '$Pattern' } | Select-Object -First 1
    if (`$hit) { break }
    Start-Sleep -Seconds 2
} while ((Get-Date) -lt `$deadline)
[ordered]@{ found = [bool]`$hit; line = "`$hit"; count = `$lines.Count; tail = (`$lines | Select-Object -Last 3) -join ' | ' } | ConvertTo-Json -Compress
"@
    $line = $r.Output -split "`r?`n" | Where-Object { $_.StartsWith('{') } | Select-Object -Last 1
    if (-not $line) { throw "waiting for '$Pattern' printed nothing: $($r.Output)" }
    $line | ConvertFrom-Json
}

# The application against the simulated Z3805A, over the VM's second serial port (#639): it connects
# and locks, follows a pulled antenna into holdover and back, and survives the receiver going silent.
# Nothing here can be done to a real receiver without a person at the bench.
function Test-Receiver {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }

    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'

    # Installed is what this scenario needs; whether the start check passed is fresh-offline's
    # question. Exit 2 is recorded rather than failed: on the first run here the app took 14 s to
    # write its log on a freshly snapshotted VM, and the start check had given up at 13. Installers
    # built since #646 wait up to 45 s for the log; zips from before it, v1.3.3 among them, do not.
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"

    # Remembered settings for COM2 with connect-on-launch, as a person choosing the port would leave.
    $null = Invoke-QaGuestScript $Vm -Name 'connect-com2' -Script @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$dir = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
New-Item -ItemType Directory -Force $dir | Out-Null
'{"PortName":"COM2","AutoDetect":false,"BaudRate":9600,"DataBits":8,"Parity":0,"StopBits":1,"ReconnectAutomatically":true,"ConnectOnLaunch":true}' | Set-Content (Join-Path $dir 'connection.json') -Encoding ascii
Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
'@

    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    try {
        $seen = Wait-AppLog $Vm 'Session COM2 is now Connected' 0 120 'connected'
        Check $Result 'connected to the simulated receiver on COM2' $seen.found "$($seen.line)$($seen.tail)"
        $seen = Wait-AppLog $Vm 'State: LOCK' 0 60 'locked'
        Check $Result 'locked' $seen.found $seen.line
        $mark = $seen.count

        $null = Send-SimulatorControl "$pipe-control" 'antenna off'
        $seen = Wait-AppLog $Vm 'State: (WAIT|HOLD)' $mark 120 'holdover'
        Check $Result 'a pulled antenna is seen as holdover (WAIT, #642)' $seen.found $seen.line
        try { Invoke-VmRun $Vm captureScreen -Arguments (Join-Path $Result.Folder 'holdover.png') -Guest | Out-Null } catch { }

        # manual-qa.md section 10, the half a harness can see: "pull the antenna and wait". The app
        # announces a loss only after a minute of it (LockWatch.Grace), so the antenna stays off
        # until it has; a second run that plugged it back after 59 s rightly got no notification.
        # Its title follows the mode when the minute runs out: "Receiver in holdover" in holdover,
        # "has lost GPS lock" if recovery has already begun (LockWatch.Describe).
        # Whether they appear on screen, and that none comes with the switch off, stay with a person.
        $seen = Wait-AppLog $Vm 'Notified: Receiver (in holdover|has lost GPS lock)' $seen.count 120 'notified-lost'
        Check $Result '[10] a notification that lock was lost, after the grace minute' $seen.found $seen.line
        $mark = $seen.count

        $null = Send-SimulatorControl "$pipe-control" 'antenna on'
        $seen = Wait-AppLog $Vm 'State: REC' $mark 120 'recovery'
        Check $Result 'reconnecting the antenna starts recovery' $seen.found $seen.line
        $seen = Wait-AppLog $Vm 'State: LOCK' $seen.count 120 'relocked'
        Check $Result 'and the receiver locks again' $seen.found $seen.line
        $seen = Wait-AppLog $Vm 'Notified: Receiver has regained GPS lock' $mark 60 'notified-regained'
        Check $Result '[10] and a notification that it was regained' $seen.found $seen.line
        $mark = $seen.count

        # manual-qa.md section 2: the receiver power-cycled for 30 s. The far end goes quiet with
        # nothing thrown, which wedged the app twice on 28 Aug; then it comes back losing its first
        # command and taking over 15 s for its first screen, as the bench unit did on 2 Oct 2026.
        # A cycle that never reaches Reconnecting proves nothing, so that is checked too.
        $null = Send-SimulatorControl "$pipe-control" 'power off'
        $seen = Wait-AppLog $Vm 'Session COM2 is now Reconnecting' $mark 60 'powered-off'
        Check $Result '[2] a receiver with no power sends the session to Reconnecting' $seen.found $seen.line
        $mark = $seen.count
        Start-Sleep -Seconds 15
        $null = Send-SimulatorControl "$pipe-control" 'power on'
        $seen = Wait-AppLog $Vm 'Session COM2 is now Connected' $mark 120 'powered-on'
        Check $Result '[2] the session reconnects by itself when power returns' $seen.found $seen.line
        $seen = Wait-AppLog $Vm 'State: ' $seen.count 120 'polling-again'
        Check $Result '[2] and polls again: a State line after the reconnect' $seen.found $seen.line
        try { Invoke-VmRun $Vm captureScreen -Arguments (Join-Path $Result.Folder 'screen.png') -Guest | Out-Null } catch { }
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

$scenarioTable = [ordered]@{
    'fresh-online'     = ${function:Test-FreshOnline}
    'fresh-offline'    = ${function:Test-FreshOffline}
    'upgrade-1.2.0'    = ${function:Test-Upgrade120}
    'leftover-cert'    = ${function:Test-LeftoverCert}
    'app-checks'       = ${function:Test-AppChecks}
    'receiver'         = ${function:Test-Receiver}
}

# ---------------------------------------------------------------------------
# §8 on the host: the gate's own binary scan, so the tokens stay in the one file allowed them.
# ---------------------------------------------------------------------------
if ($Scenarios -contains 'binary-audit') {
    $result = New-Result 'binary-audit' 'host'
    $result.Folder = Join-Path $OutDir 'binary-audit'
    New-Item -ItemType Directory -Force $result.Folder | Out-Null
    Say 'binary-audit on the host'
    try {
        $unpacked = Join-Path $result.Folder 'unpacked'
        Expand-Archive $Online -DestinationPath "$unpacked\zip" -Force
        $bundle = Get-ChildItem "$unpacked\zip" -Filter *.msixbundle | Select-Object -First 1
        Copy-Item $bundle.FullName "$unpacked\bundle.zip"
        Expand-Archive "$unpacked\bundle.zip" -DestinationPath "$unpacked\bundle" -Force
        $msix = Get-ChildItem "$unpacked\bundle" -Filter '*_x64.msix' | Select-Object -First 1
        Copy-Item $msix.FullName "$unpacked\app.zip"
        Expand-Archive "$unpacked\app.zip" -DestinationPath "$unpacked\app" -Force
        $output = & pwsh -NoProfile -File (Join-Path $repo 'build\Test-NoBlockedCommands.ps1') -ScanBinaries "$unpacked\app" 2>&1 | Out-String
        $output | Set-Content (Join-Path $result.Folder 'scan.txt')
        Check $result 'no excluded command outside the exclusion patterns' ($LASTEXITCODE -eq 0) (($output -split "`r?`n" | Where-Object { $_ -match 'PASS|FAIL' } | Select-Object -Last 1))
        Remove-Item $unpacked -Recurse -Force
    }
    catch { $result.Error = $_.Exception.Message; Say "  ERROR $($result.Error)" }
    $results.Add($result)
}

# ---------------------------------------------------------------------------
# The VM scenarios. Every VM is powered off when its scenarios end, however they end.
# ---------------------------------------------------------------------------
foreach ($machine in $Machines) {
    $vm = Get-Vm $machine
    try {
        foreach ($name in $scenarioTable.Keys) {
            if ($Scenarios -notcontains $name) { continue }
            $result = New-Result $name $machine
            $result.Folder = Join-Path $OutDir "$machine-$name"
            New-Item -ItemType Directory -Force $result.Folder | Out-Null
            Say "$name on $machine"
            try {
                Start-QaVm -Vm $vm
                & $scenarioTable[$name] $vm $result
            }
            catch {
                $result.Error = $_.Exception.Message
                Say "  ERROR $($result.Error)"
            }
            $results.Add($result)
        }
    }
    finally {
        Stop-QaVm -Vm $vm
        Say "$machine powered off"
    }
}

# ---------------------------------------------------------------------------
# The report.
# ---------------------------------------------------------------------------
function Get-Verdict {
    param($Result)
    if ($Result.Error -like 'skipped:*') { 'skipped' }
    elseif ($Result.Error) { 'ERROR' }
    elseif (@($Result.Checks | Where-Object { -not $_.Ok }).Count) { 'FAIL' }
    else { 'PASS' }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("## Automated QA: $(Split-Path $Online -Leaf)")
$lines.Add('')
$lines.Add("Run $(Get-Date -Format 'yyyy-MM-dd HH:mm') by ``build/qa/Invoke-QaPass.ps1`` on $($Machines -join ' and '). Evidence: ``$OutDir``.")
$lines.Add('')
$lines.Add('| Scenario | Machine | Result | Detail |')
$lines.Add('|---|---|---|---|')
foreach ($r in $results) {
    $verdict = Get-Verdict $r
    $detail = if ($r.Error) { $r.Error } else { (($r.Checks | Where-Object { -not $_.Ok } | ForEach-Object { "$($_.Name)$(if ($_.Detail) { " ($($_.Detail))" })" }) -join '; ') }
    $lines.Add("| $($r.Scenario) | $($r.Machine) | **$verdict** | $($detail -replace '\|', '/') |")
}
$lines.Add('')
foreach ($r in $results) {
    $lines.Add("<details><summary>$($r.Scenario) on $($r.Machine): $(Get-Verdict $r)</summary>")
    $lines.Add('')
    foreach ($c in $r.Checks) { $lines.Add("- $(if ($c.Ok) { 'PASS' } else { '**FAIL**' }) $($c.Name)$(if ($c.Detail) { " - $($c.Detail)" })") }
    if ($r.Error) { $lines.Add("- $($r.Error)") }
    $lines.Add('')
    $lines.Add('</details>')
}
$report = Join-Path $OutDir 'report.md'
$lines | Set-Content -Path $report -Encoding UTF8
$results | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'results.json') -Encoding UTF8

$failed = @($results | Where-Object { (Get-Verdict $_) -in 'FAIL', 'ERROR' }).Count
Say "report: $report"
Say "$($results.Count) scenario run(s), $failed failed"
# A run that ran nothing proves nothing, and must not read as a pass.
if ($results.Count -eq 0) { Say 'nothing ran'; exit 1 }
if ($failed) { exit 1 }
