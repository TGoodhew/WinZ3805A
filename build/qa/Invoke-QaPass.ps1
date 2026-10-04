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
      repair-damaged     §12  this version installed and then damaged: rerunning the installer
                              repairs it with its data intact (#600)
      app-checks         §11, §25, §18  on the running app, after a clean offline install: the
                              guide in the package and F1, a second launch typed into the Start
                              menu bringing a covered window forward, and the tray icon surviving
                              an Explorer restart (guest\AppChecks.ps1); a screenshot is kept
      receiver           the app against the simulated Z3805A on the VM's COM2 (#639): connects and
                              locks, follows a pulled antenna into holdover and back, and comes
                              back by itself after the receiver goes silent; the lock
                              notification on screen, and none with the switch off (§10). Needs
                              the VM's simulator port (Add-QaSimulatorPort)
      sign-in            §20  start at sign-in, signed out and in for real: hidden, then with the
                              window, then off; a receiver that answers only a minute after
                              sign-in; the setting turned off in Windows. UI Automation and the
                              simulator port
      pin-compact        §22  pinning from compact mode by every route: the shortcut, the window's
                              and the notification area's menus, the footer pin; Windows' own
                              topmost flag, the title bar and the medallion measured; a restart
                              keeps it. UI Automation and Win32
      whole-layout       §23  first launch with no stored placement, at 100 % and 150 %: every row on
                              screen and nothing clipped; at the minimum width the clock wraps
                              before the date, and the rows go together as the height shrinks;
                              Copy gives one plain line. UI Automation and the simulator port
      accessibility      §4   A11Y-3, -9, -10 and -11 on the running app: icon-only controls named and
                              their tooltips opened by real pointer movement; the medallion and
                              every sky-plot marker exposed as sentences, and List showing the same
                              satellites; live-region events recorded for a mode change, a lost
                              connection and a tier C outcome. Needs the simulator port
      sky-export         §7   the sky plot saved as an image in Light, Dark and high contrast, and at
                              225 %: each file measured (opaque, the theme's background in the
                              corners, markers not in the surface colour), its caption read by OCR,
                              and the on-screen caption gone after Save and after Cancel. Needs the
                              simulator port
      history-reinstall §21 the history exported, the package uninstalled and reinstalled, and the file
                              imported back through the app's own pickers and confirmation; then a file
                              naming no receiver and one from a different receiver, with Enter pressed
                              to prove which button is the default. Needs the simulator port
      guide-pages        §13  every page the guide illustrates photographed as its images were taken, at an
                              860 x 778 page area, and set beside the guide's own picture for the agent to
                              judge. Needs the simulator port
      high-contrast      §4   A11Y-8: each of the four contrast themes switched live under the running app;
                              the main window and Details measured for the theme's colours and photographed
                              for the agent to judge. Needs the simulator port
      display-scaling    §3   100, 150, 200 and 225 %, each after a sign-out: both windows at the display's
                              scaling, their title-bar buttons clear of the caption buttons the system
                              reports, a real drag on each title bar, and a photograph of each for the
                              agent to judge. Needs the simulator port
      text-scaling       §4   A11Y-6: Windows' text size at 100, 150 and 200 % at each of §9.6.1's breakpoints;
                              the size confirmed in the app, a dialog's buttons on screen, and photographs
                              for the agent to judge clipping. Needs the simulator port
      keyboard-focus     §4   A11Y-1, -2 and -5: each surface walked with Tab alone; every control that takes
                              the keyboard reached, a focus ring drawn at each stop, and each stop at least
                              32 x 32. Needs the simulator port
      reduced-motion     §4   A11Y-13: page changes captured frame by frame with Windows' animation effects
                              on (the control) and off; with them off, no frames in between. Needs the
                              simulator port
      greyscale-states   §4   A11Y-12: the simulated receiver put through each state it can show, the main
                              window and Overview photographed beside their greyscale for the agent to
                              judge. Needs the simulator port
      contrast           §4   A11Y-4: every piece of text on every surface measured on screen against its
                              floor, at 200 %: Light and Dark over Mica on a grey wallpaper and on the
                              hardest of six hues (Windows 10: the solid fallback), then the four contrast
                              themes. Needs the simulator port, and on Windows 11 3D acceleration
      connect-cancel     §24  an auto-detect walk on a port where nothing answers, stopped by
                              Cancel and by Esc; then Connect works once the receiver answers.
                              Driven through UI Automation (guest\Ui.ps1). Needs the simulator port

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
    [string[]]$Scenarios = @('binary-audit', 'fresh-online', 'fresh-offline', 'upgrade-1.2.0', 'leftover-cert', 'repair-damaged', 'app-checks', 'receiver', 'connect-cancel', 'sign-in', 'pin-compact', 'whole-layout', 'accessibility', 'sky-export', 'history-reinstall', 'guide-pages', 'high-contrast', 'display-scaling', 'text-scaling', 'keyboard-focus', 'reduced-motion', 'greyscale-states', 'contrast'),
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'QaVm.psm1') -Force

# Called with powershell -File, a list such as -Scenarios a,b arrives as the one string 'a,b', which
# names no scenario - the first run of this script ran nothing and reported success.
$Machines = @($Machines | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Scenarios = @($Scenarios | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$known = @('binary-audit', 'fresh-online', 'fresh-offline', 'upgrade-1.2.0', 'leftover-cert', 'repair-damaged', 'app-checks', 'receiver', 'connect-cancel', 'sign-in', 'pin-compact', 'whole-layout', 'accessibility', 'sky-export', 'history-reinstall', 'guide-pages', 'high-contrast', 'display-scaling', 'text-scaling', 'keyboard-focus', 'reduced-motion', 'greyscale-states', 'contrast')
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


# #600: the copy of this very version, broken, and the installer run again. The damage is the one
# measured on 2 Oct 2026 - the app's main assembly overwritten with zeros of the same length - which
# Windows still reports as Ok, which re-registering does not repair, and which only a reinstall
# does. So passing means rung 1 was tried, did not do it, and rung 2 did, with the data kept.
function Test-RepairDamaged {
    param($Vm, $Result)
    Send-Zip $Vm $Offline 'candidate'
    $first = Install-Candidate $Vm 'candidate'
    Check $Result 'installed and started (exit 0)' ($first -eq 0) "exit $first"

    # A marker in the data folder stands for the history; the damage needs administrator rights,
    # because the install folder belongs to TrustedInstaller. UAC on these VMs elevates silently.
    # The start check left the app running, which locks the file and makes an unattended rerun
    # refuse to start, so it is closed first, as the person would.
    $r = Invoke-QaGuestScript $Vm -Name 'damage' -Script @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$data = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
Set-Content -LiteralPath (Join-Path $data 'repair-marker.txt') -Value 'kept'
$target = Join-Path $pkg.InstallLocation 'WinZ3805A.dll'
# As a file: Start-Process joins its arguments with spaces, so a -Command would arrive in pieces.
Set-Content -LiteralPath 'C:\qa\damage.ps1' -Value @"
takeown.exe /f "$target" | Out-Null
icacls.exe "$target" /grant '*S-1-5-32-544:F' | Out-Null
[IO.File]::WriteAllBytes('$target', (New-Object byte[] (Get-Item '$target').Length))
"@
Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\qa\damage.ps1'
$head = [IO.File]::ReadAllBytes($target) | Select-Object -First 2
"damaged: $(($head -join ',') -eq '0,0')"
'@
    Check $Result 'the app''s main assembly damaged' ($r.Output -match 'damaged: True') $r.Output

    $second = Install-Candidate $Vm 'candidate'
    $log = Save-Evidence $Vm $Result.Folder
    $after = Invoke-QaGuestScript $Vm -Name 'after' -Script @'
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$data = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
"marker: $(Test-Path (Join-Path $data 'repair-marker.txt'))"
"status: $($pkg.Status)"
'@
    Check $Result 'rerunning the installer: exit 0, started' ($second -eq 0) "exit $second"
    Check $Result 'it said beforehand that it would repair a copy that does not start' ($log -match 'decide repair') ''
    Check $Result 're-registering was tried first' ($log -match 'Registered it again from its own folder') ''
    Check $Result 'and the reinstall repaired it' ($log -match 'repair        repaired by rung 2') (($log -split "`r?`n" | Where-Object { $_ -match 'repair  ' }) -join ' | ')
    Check $Result 'its data was saved to Documents first' ($log -match 'Saved its data to .*WinZ3805A repair backup') ''
    Check $Result 'and is in the repaired copy' ($after.Output -match 'marker: True') $after.Output
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
# A notification stays on screen for MessageDuration seconds, five by default, which is shorter than
# the round trip from seeing it logged to taking the screenshot. Accessibility's own setting for it.
Set-ItemProperty -Path 'HKCU:\Control Panel\Accessibility' -Name MessageDuration -Value 60 -Type DWord
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
        # For the agent to judge that it is on screen, which the log cannot say.
        try { Invoke-VmRun $Vm captureScreen -Arguments (Join-Path $Result.Folder 'notification.png') -Guest | Out-Null } catch { }
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

        # manual-qa.md section 10's other half: with the switch off, a loss past the grace minute
        # raises nothing. The switch is read at start-up (App.StartLockNotifications), so the app is
        # restarted with it off, as Settings would leave it. Its absence is only evidence once the
        # holdover is known to have happened and the minute to have passed, so both are checked.
        $null = Invoke-QaGuestScript $Vm -Name 'notifications-off' -Script @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$dir = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
'{"AreLockNotificationsEnabled":false}' | Set-Content (Join-Path $dir 'advanced.json') -Encoding ascii
Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
'@
        $seen = Wait-AppLog $Vm 'State: LOCK' $seen.count 180 'locked-switch-off'
        Check $Result '[10] restarted with notifications off, and locked' $seen.found $seen.line
        $mark = $seen.count
        $null = Send-SimulatorControl "$pipe-control" 'antenna off'
        $seen = Wait-AppLog $Vm 'State: (WAIT|HOLD)' $mark 120 'holdover-switch-off'
        Check $Result '[10] the antenna pulled again: holdover' $seen.found $seen.line
        $quiet = Wait-AppLog $Vm 'Notified:' $mark 100 'silence'
        Check $Result '[10] and no notification with the switch off, 100 s on' (-not $quiet.found) $(if ($quiet.found) { $quiet.line } else { 'none logged' })
        $null = Send-SimulatorControl "$pipe-control" 'antenna on'
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# One cancelled auto-detect, through the dialog, by the Close button or by Esc. Returns what the
# guest saw as an object: the log lines that matter, how long the cancel took, whether any probe
# went out after it, and what the dialog and the window show.
$cancelStep = @'
$window = Get-AppWindow
if (-not $window) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
$before = (Get-AppLogLines).Count

# The footer's Connect opens the dialog unless it is already open.
$primary = Find-Control $window -AutomationId 'PrimaryButton' -Seconds 1
if (-not $primary) {
    $footer = Find-Control $window -Name 'Connect'
    if ($footer) { Invoke-Control $footer }
    $primary = Find-Control $window -AutomationId 'PrimaryButton'
}
if (-not $primary) { [ordered]@{ error = "no dialog; buttons: $(Get-ButtonList $window)" } | ConvertTo-Json -Compress; return }
$auto = Find-Control $window -Name 'Auto-detect settings' -Seconds 2
$autoChosen = $auto -and $auto.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected

Invoke-Control $primary
Start-Sleep -Seconds 10
$connecting = [bool](Find-Control $window -Name 'Connecting' -Seconds 1)
if ($how -eq 'esc') { Send-KeyTo $primary '{ESC}' }
else { Invoke-Control (Find-Control $window -AutomationId 'CloseButton') }
Start-Sleep -Seconds 8

$lines = @(Get-AppLogLines | Select-Object -Skip $before)
$pressed = $lines | Where-Object { $_ -match 'Cancel pressed while connecting' } | Select-Object -First 1
$doneAt = -1
for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match 'is now Disconnected\. Cancelled\.') { $doneAt = $i; break } }
$done = if ($doneAt -ge 0) { $lines[$doneAt] } else { $null }
$probesAfter = if ($doneAt -ge 0) { @($lines | Select-Object -Skip ($doneAt + 1) | Where-Object { $_ -match '\*CLS|\*IDN' }).Count } else { -1 }
$primaryNow = Find-Control $window -AutomationId 'PrimaryButton' -Seconds 1
[ordered]@{
    autoDetect   = [bool]$autoChosen
    connecting   = $connecting
    walked       = @($lines | Where-Object { $_ -match 'timed out|Opened' }).Count
    pressed      = "$pressed"
    done         = "$done"
    seconds      = $(if ($pressed -and $done) { ((Get-LineTime $done) - (Get-LineTime $pressed)).TotalSeconds } else { -1 })
    probesAfter  = $probesAfter
    dialogOpen   = [bool]$primaryNow
    connectReady = [bool]($primaryNow -and $primaryNow.Current.IsEnabled)
    disconnected = [bool](Find-Control $window -Name 'Disconnected' -Seconds 1)
    errorShown   = [bool](Find-Control $window -AutomationId 'ErrorBar' -Seconds 1)
} | ConvertTo-Json -Compress
'@

function Invoke-UiStep {
    param($Vm, [string]$Name, [string]$Prelude, [string]$Body)
    $ui = Get-Content (Join-Path $PSScriptRoot 'guest\Ui.ps1') -Raw
    # A terminating error escapes the guest runner's output capture, which left a failing step
    # reporting nothing at all; caught here, it reports what went wrong and what was running.
    $wrapped = $ui + "`r`n" + $Prelude + "`r`ntry {`r`n" + $Body + "`r`n}`r`ncatch { [ordered]@{ error = `"`$(`$_.Exception.Message) at line `$(`$_.InvocationInfo.ScriptLineNumber): `$(`$_.InvocationInfo.Line.Trim()) (`$(Get-WindowReport))`" } | ConvertTo-Json -Compress }"
    $r = Invoke-QaGuestScript $Vm -Name $Name -Script $wrapped
    $line = $r.Output -split "`r?`n" | Where-Object { $_.StartsWith('{') } | Select-Object -Last 1
    if (-not $line) { throw "the UI step '$Name' printed nothing: $($r.Output)" }
    $line | ConvertFrom-Json
}

# manual-qa.md section 24 (#585, #607), against the simulated receiver with its power off: the port
# is there and nothing answers, which is the case the bench needed a serial cable pulled for.
function Test-ConnectCancel {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }

    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"

    # COM2 remembered with auto-detect chosen, and nothing connecting by itself.
    $launch = Invoke-QaGuestScript $Vm -Name 'prefs-autodetect' -Script @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$dir = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
New-Item -ItemType Directory -Force $dir | Out-Null
'{"PortName":"COM2","AutoDetect":true,"BaudRate":9600,"DataBits":8,"Parity":0,"StopBits":1,"ReconnectAutomatically":false,"ConnectOnLaunch":false}' | Set-Content (Join-Path $dir 'connection.json') -Encoding ascii
# Started, and then waited for until it has written to its log. A launch straight after the copy the
# installer's start check left was killed, mid-way through a slow first start, once produced no
# working app at all (2 Oct 2026); a second launch is what a person would try.
$log = Join-Path $dir 'logs\app.log'
foreach ($attempt in 1, 2) {
    $lines = @(if (Test-Path $log) { Get-Content $log }).Count
    Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
    $deadline = (Get-Date).AddSeconds(60)
    do { Start-Sleep -Seconds 2 } while (@(if (Test-Path $log) { Get-Content $log }).Count -le $lines -and (Get-Date) -lt $deadline)
    if (@(if (Test-Path $log) { Get-Content $log }).Count -gt $lines) { "started on attempt $attempt"; break }
    "launch $attempt wrote nothing to the log in 60 s"
    Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
}
'@

    Check $Result 'the app started with auto-detect remembered' ($launch.Output -match 'started on attempt') ($launch.Output -replace '\s+', ' ')

    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    try {
        Start-Sleep -Seconds 5
        $null = Send-SimulatorControl "$pipe-control" 'power off'

        foreach ($how in 'button', 'esc') {
            $label = if ($how -eq 'esc') { 'Esc' } else { 'Cancel' }
            $s = Invoke-UiStep $Vm "cancel-$how" "`$how = '$how'" $cancelStep
            if ($s.error) { Check $Result "[24] $label`: the dialog" $false $s.error; continue }
            if ($how -eq 'button') {
                Check $Result '[24] the dialog opens with auto-detect chosen' $s.autoDetect ''
            }
            Check $Result "[24] $label`: a walk was under way when it was pressed" ($s.connecting -and $s.walked -gt 0) "window said Connecting: $($s.connecting); $($s.walked) walk line(s)"
            Check $Result "[24] $label`: logged as pressed while connecting" ([bool]$s.pressed) $s.pressed
            Check $Result "[24] $label`: the session is Disconnected, Cancelled, within 3 s" ($s.done -and $s.seconds -ge 0 -and $s.seconds -le 3) "$($s.done) ($($s.seconds) s)"
            Check $Result "[24] $label`: no probe after it" ($s.probesAfter -eq 0) "$($s.probesAfter) probe line(s) after"
            Check $Result "[24] $label`: the dialog stays open, Connect usable, no error" ($s.dialogOpen -and $s.connectReady -and -not $s.errorShown) "open $($s.dialogOpen), Connect enabled $($s.connectReady), error shown $($s.errorShown)"
            Check $Result "[24] $label`: the main window says Disconnected" $s.disconnected ''
        }

        # Cancel with nothing running closes the dialog.
        $closed = Invoke-UiStep $Vm 'cancel-idle' '' @'
$window = Get-AppWindow
$close = Find-Control $window -AutomationId 'CloseButton' -Seconds 2
if ($close) { Invoke-Control $close }
Start-Sleep -Seconds 2
[ordered]@{ hadDialog = [bool]$close; closed = -not [bool](Find-Control $window -AutomationId 'PrimaryButton' -Seconds 1) } | ConvertTo-Json -Compress
'@
        Check $Result '[24] Cancel with nothing running closes the dialog' ($closed.hadDialog -and $closed.closed) "dialog was open $($closed.hadDialog), closed $($closed.closed)"

        # And with the receiver answering again, Connect gets there.
        $null = Send-SimulatorControl "$pipe-control" 'power on'
        Start-Sleep -Seconds 5
        $mark = (Wait-AppLog $Vm 'Cancelled' 0 5 'mark').count
        $opened = Invoke-UiStep $Vm 'connect-again' '' @'
$window = Get-AppWindow
$footer = Find-Control $window -Name 'Connect'
if ($footer) { Invoke-Control $footer }
$primary = Find-Control $window -AutomationId 'PrimaryButton'
if ($primary) { Invoke-Control $primary }
[ordered]@{ pressed = [bool]$primary } | ConvertTo-Json -Compress
'@
        $seen = Wait-AppLog $Vm 'Session COM2 is now Connected' $mark 180 'connected-again'
        Check $Result '[24] with the receiver answering, Connect connects' ($opened.pressed -and $seen.found) "$($seen.line)$($seen.tail)"
        try { Invoke-VmRun $Vm captureScreen -Arguments (Join-Path $Result.Folder 'screen.png') -Guest | Out-Null } catch { }
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# Signs the guest's user out and waits for the automatic sign-in that follows. The answer file's
# AutoLogon only signs in at boot; ForceAutoLogon, set by the scenario, is what signs the user back
# in after a sign-out. The new session is told from the old by Explorer's process id, read from the
# process list: a guest program run while nobody is signed in can block for minutes, which is how
# the first try of this waited out its own timeout (2 Oct 2026).
function Invoke-SignOutAndIn {
    param($Vm)
    $explorerBefore = @((Invoke-VmRun $Vm listProcessesInGuest -Guest) -split "`n" | Where-Object { $_ -match 'explorer\.exe' }) -join '|'
    # -noWait: vmrun otherwise waits for shutdown.exe to finish, in a session that the sign-out has
    # just ended, and never returns (the second try of this, 2 Oct 2026).
    try { Invoke-VmRun $Vm runProgramInGuest -Arguments '-noWait', '-activeWindow', '-interactive', 'C:\Windows\System32\shutdown.exe', '/l' -Guest | Out-Null } catch { }
    Start-Sleep -Seconds 15
    $deadline = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $deadline) {
        try {
            $now = (Invoke-VmRun $Vm listProcessesInGuest -Guest) -split "`n"
            $explorer = @($now | Where-Object { $_ -match 'explorer\.exe' }) -join '|'
            $tools = $now | Where-Object { $_ -like "*\$($Vm.Guest.UserName), cmd=*vmtoolsd*" }
            if ($explorer -and $tools -and -not ($now -match 'LogonUI\.exe') -and $explorer -ne $explorerBefore) {
                $tries = Wait-QaGuestReady -Vm $Vm
                if ($tries -gt 1) { Say "  the guest ran a program again on try $tries after signing in" }
                # Windows holds startup apps back for a few seconds after the desktop appears.
                Start-Sleep -Seconds 20
                return
            }
        }
        catch { }
        Start-Sleep -Seconds 5
    }
    throw 'the guest did not sign back in within five minutes'
}

# Sets the "Start when I sign in to Windows" box on the Details window's Settings page, as a person
# would. Launched from Start first: that opens the app, or brings back a window it started hidden.
$setSignInStep = @'
Start-Process "shell:AppsFolder\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)!App"
Start-Sleep -Seconds 5
$main = Get-AppWindow
if (-not $main) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
$details = Get-AppWindowNamed 'Receiver Details' -Seconds 2
if (-not $details) { Invoke-Control (Find-Control $main -Name 'Details'); $details = Get-AppWindowNamed 'Receiver Details' }
[void](Select-NavigationItem $details 'Settings')
Start-Sleep -Seconds 3
$box = Find-Control $details -AutomationId 'SignInStartBox'
$ok = if ($box.Current.IsEnabled) { Select-ComboItem $box $choice } else { $false }
Start-Sleep -Seconds 4
$note = Find-Control $details -AutomationId 'SignInStartNote' -Seconds 2
[ordered]@{ ok = $ok; enabled = $box.Current.IsEnabled; value = (Get-Selection $box); note = "$($note.Current.Name)" } | ConvertTo-Json -Compress
'@

# What is running after a sign-in, before anything has touched it.
$afterSignInStep = @'
$processes = @(Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue)
[ordered]@{
    processes = $processes.Count
    visible   = [bool]($processes | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
} | ConvertTo-Json -Compress
'@

# manual-qa.md section 20 (#548). Every sign-in is real: the user signs out and Windows signs them
# back in, so the app is started by Windows' own startup task, as it would be on the bench.
function Test-SignIn {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }

    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"

    # The receiver remembered on COM2 with connect-on-launch, which is what a sign-in start retries;
    # and the automatic sign-in made to follow a sign-out too, which needs administrator rights.
    $null = Invoke-QaGuestScript $Vm -Name 'prefs-sign-in' -Script @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$dir = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
New-Item -ItemType Directory -Force $dir | Out-Null
'{"PortName":"COM2","AutoDetect":false,"BaudRate":9600,"DataBits":8,"Parity":0,"StopBits":1,"ReconnectAutomatically":true,"ConnectOnLaunch":true}' | Set-Content (Join-Path $dir 'connection.json') -Encoding ascii
Set-Content -LiteralPath 'C:\qa\force.ps1' -Value "Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name ForceAutoLogon -Value '1' -Type String"
Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\qa\force.ps1'
'@

    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    try {
        # 1. In the notification area.
        $s = Invoke-UiStep $Vm 'sign-in-hidden' "`$choice = 'In the notification area'" $setSignInStep
        Check $Result '[20] set to start in the notification area' ($s.ok -eq $true) "$($s.value) $($s.error)"
        $mark = (Wait-AppLog $Vm 'enable returned Enabled' 0 20 'enabled').count
        Invoke-SignOutAndIn $Vm
        $state = Invoke-UiStep $Vm 'after-hidden' '' $afterSignInStep
        $seen = Wait-AppLog $Vm 'Started by Windows at sign-in; hidden: True' $mark 90 'started-hidden'
        Check $Result '[20] started by Windows at sign-in, hidden' $seen.found "$($seen.line)$(if (-not $seen.found) { $seen.tail })"
        Check $Result '[20] no window, one copy running' ($state.processes -eq 1 -and -not $state.visible) "processes $($state.processes), a window visible $($state.visible)"
        $seen = Wait-AppLog $Vm 'Tray icon started' $mark 30 'tray'
        Check $Result '[20] its icon is in the notification area' $seen.found $seen.line
        # "Readings now come from" is written once per new session, when polling starts, so after the
        # sign-out it can only be the copy Windows started. A State line would not do: one is written
        # only when the state changes, so the copy running before the sign-out wrote the last, and
        # an earlier version of this check passed on it.
        $seen = Wait-AppLog $Vm 'Readings now come from COM2' $mark 120 'polled'
        Check $Result '[20] and the receiver is polled from the copy Windows started' $seen.found $seen.line
        $mark = $seen.count

        # Opened from Start afterwards: the window comes forward, and no second copy starts.
        $front = Invoke-UiStep $Vm 'open-from-start' '' @'
Start-Process "shell:AppsFolder\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)!App"
Start-Sleep -Seconds 6
$processes = @(Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue)
[ordered]@{ processes = $processes.Count; visible = [bool]($processes | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }) } | ConvertTo-Json -Compress
'@
        $seen = Wait-AppLog $Vm 'Brought to the front' $mark 20 'brought'
        Check $Result '[20] opened from Start after it: the window comes forward, still one copy' ($front.processes -eq 1 -and $front.visible -and $seen.found) "processes $($front.processes), visible $($front.visible); $($seen.line)"

        # 2. With the window open.
        $s = Invoke-UiStep $Vm 'sign-in-window' "`$choice = 'With the window open'" $setSignInStep
        Check $Result '[20] set to start with the window open' ($s.ok -eq $true) "$($s.value) $($s.error)"
        $mark = (Wait-AppLog $Vm 'Start at sign-in' 0 5 'mark-window').count
        Invoke-SignOutAndIn $Vm
        $seen = Wait-AppLog $Vm 'Started by Windows at sign-in; hidden: False' $mark 90 'started-window'
        $state = Invoke-UiStep $Vm 'after-window' '' $afterSignInStep
        Check $Result '[20] started by Windows at sign-in, with its window open' ($seen.found -and $state.visible) "$($seen.line); window visible $($state.visible)"

        # 3. Off.
        $s = Invoke-UiStep $Vm 'sign-in-off' "`$choice = 'Off'" $setSignInStep
        Check $Result '[20] set to off' ($s.ok -eq $true) "$($s.value) $($s.error)"
        $mark = (Wait-AppLog $Vm 'disable returned Disabled' 0 20 'disabled').count
        Invoke-SignOutAndIn $Vm
        Start-Sleep -Seconds 20
        $state = Invoke-UiStep $Vm 'after-off' '' $afterSignInStep
        $quiet = Wait-AppLog $Vm 'Started by Windows at sign-in' $mark 5 'not-started'
        Check $Result '[20] off: nothing starts at sign-in' ($state.processes -eq 0 -and -not $quiet.found) "processes $($state.processes); $($quiet.line)"

        # 4. A receiver that answers only a minute after sign-in.
        $s = Invoke-UiStep $Vm 'sign-in-retry' "`$choice = 'In the notification area'" $setSignInStep
        Check $Result '[20] set to start in the notification area again' ($s.ok -eq $true) "$($s.value) $($s.error)"
        $mark = (Wait-AppLog $Vm 'enable returned Enabled' 0 20 'enabled-again').count
        $null = Send-SimulatorControl "$pipe-control" 'power off'
        Invoke-SignOutAndIn $Vm
        $seen = Wait-AppLog $Vm 'did not answer; trying again every 30 s until it does' $mark 120 'retrying'
        Check $Result '[20] a silent receiver at sign-in: it says it will keep trying' $seen.found "$($seen.line)$(if (-not $seen.found) { $seen.tail })"
        Start-Sleep -Seconds 60
        $null = Send-SimulatorControl "$pipe-control" 'power on'
        $seen = Wait-AppLog $Vm 'Connected to COM2 after \d+ attempts since sign-in' $mark 120 'connected-after'
        Check $Result '[20] and connects within about 30 s of the receiver answering' $seen.found "$($seen.line)$(if (-not $seen.found) { $seen.tail })"
        $count = Invoke-QaGuestScript $Vm -Name 'count-retry' -Script ($(Get-Content (Join-Path $PSScriptRoot 'guest\Ui.ps1') -Raw) + "`r`n" + "@(Get-AppLogLines | Select-Object -Skip $mark | Where-Object { `$_ -match 'trying again every' }).Count")
        $said = [int](($count.Output -split "`r?`n" | Where-Object { $_ -match '^\d+$' } | Select-Object -Last 1))
        Check $Result '[20] saying so once, not every 30 s' ($said -eq 1) "$said time(s)"

        # 5. The connection dialog opened while the retry is still running stops it, and connects.
        $null = Send-SimulatorControl "$pipe-control" 'power off'
        $mark = (Wait-AppLog $Vm 'Connected to COM2' 0 5 'mark-dialog').count
        Invoke-SignOutAndIn $Vm
        $seen = Wait-AppLog $Vm 'did not answer; trying again every 30 s' $mark 120 'retrying-again'
        $dialog = Invoke-UiStep $Vm 'dialog-stops-retry' '' @'
Start-Process "shell:AppsFolder\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)!App"
Start-Sleep -Seconds 5
$window = Get-AppWindow
$footer = Find-Control $window -Name 'Connect'
if ($footer) { Invoke-Control $footer }
$primary = Find-Control $window -AutomationId 'PrimaryButton'
$opened = (Get-AppLogLines).Count
Start-Sleep -Seconds 70
$attempts = @(Get-AppLogLines | Select-Object -Skip $opened | Where-Object { $_ -match 'is now Connecting' }).Count
[ordered]@{ dialog = [bool]$primary; attemptsWhileOpen = $attempts } | ConvertTo-Json -Compress
'@
        Check $Result '[20] the dialog opened while it retries: no attempt in the next 70 s' ($seen.found -and $dialog.dialog -and $dialog.attemptsWhileOpen -eq 0) "retrying $($seen.found); dialog $($dialog.dialog); $($dialog.attemptsWhileOpen) attempt(s) while open $($dialog.error)"
        $null = Send-SimulatorControl "$pipe-control" 'power on'
        Start-Sleep -Seconds 5
        $mark = (Wait-AppLog $Vm 'Connected' 0 5 'mark-connect').count
        $press = Invoke-UiStep $Vm 'dialog-connects' '' @'
$window = Get-AppWindow
$primary = Find-Control $window -AutomationId 'PrimaryButton'
if ($primary) { Invoke-Control $primary }
[ordered]@{ pressed = [bool]$primary } | ConvertTo-Json -Compress
'@
        $seen = Wait-AppLog $Vm 'Session COM2 is now Connected' $mark 60 'dialog-connected'
        Check $Result '[20] and the dialog connects' ($press.pressed -and $seen.found) "$($seen.line) $($press.error)"

        # 6. Turned off in Windows. The startup task's State written as 1 is read back as
        # DisabledByUser (measured, 2 Oct 2026), the state Task Manager's Disable leaves; 2 is
        # Enabled. Written directly, because Task Manager has no interface a script can drive. The
        # page must refuse to change it, and say where to instead.
        $null = Invoke-QaGuestScript $Vm -Name 'disable-in-windows' -Script @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$pfn = (Get-AppxPackage -Name WinZ3805A).PackageFamilyName
Set-ItemProperty -Path "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$pfn\WinZ3805AStartAtSignIn" -Name State -Value 1 -Type DWord
'@
        $s = Invoke-UiStep $Vm 'disabled-by-user' "`$choice = 'With the window open'" $setSignInStep
        Check $Result '[20] turned off in Windows: the page shows Off and cannot change it' (-not $s.enabled -and $s.value -eq 'Off' -and -not $s.ok) "enabled $($s.enabled), value $($s.value)"
        Check $Result '[20] and says to turn it on in Windows Settings' ($s.note -match 'Windows Settings') $s.note
        $null = Invoke-QaGuestScript $Vm -Name 'enable-in-windows' -Script @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$pfn = (Get-AppxPackage -Name WinZ3805A).PackageFamilyName
Set-ItemProperty -Path "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$pfn\WinZ3805AStartAtSignIn" -Name State -Value 2 -Type DWord
'@
        $s = Invoke-UiStep $Vm 'enabled-by-user' "`$choice = 'In the notification area'" $setSignInStep
        Check $Result '[20] turned back on there: the page shows it on again' ($s.enabled -and $s.value -ne 'Off') "enabled $($s.enabled), value $($s.value)"
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# manual-qa.md section 22 (#568): one pin, four routes, and whether each changes what Windows does
# with the window. Windows' own answer is the window's WS_EX_TOPMOST flag, and whether Notepad, put
# over it and brought to the front, is what is on top at its centre.
$pinStep = @'
$process = Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { Start-Process "shell:AppsFolder\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)!App"; Start-Sleep -Seconds 8 }
$w = Get-AppWindow
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
$h = Get-Handle $w
$facts = [ordered]@{}

function Measure-Window([string]$Label) {
    $w = Get-AppWindow
    $r = [QaWin32]::Rect($h)
    $bar = Find-Control $w -AutomationId 'AppTitleBar' -Seconds 2
    $pin = Find-Control $w -AutomationId 'PinnedIndicator' -Seconds 1
    $min = Find-Control $w -Name 'Minimize' -Seconds 1
    $med = Find-Control $w -AutomationId 'Medallion' -Seconds 2
    $m = [ordered]@{
        topmost = [QaWin32]::IsTopmost($h)
        height  = $r.Bottom - $r.Top
        pushpin = [bool]$pin
    }
    if ($bar) { $m.bar = (Get-Bounds $bar).Height }
    if ($pin -and $min) { $m.gap = (Get-Bounds $min).Left - (Get-Bounds $pin).Right }
    if ($med) { $b = Get-Bounds $med; $m.medallion = "$($b.Width)x$($b.Height)"; $m.medallionInside = $b.Bottom -le $r.Bottom }
    $facts[$Label] = $m
}

# Notepad over the window's centre and brought to the front: which window is on top there.
function Test-Covered([string]$Label) {
    $r = [QaWin32]::Rect($h)
    $cx = [int](($r.Left + $r.Right) / 2); $cy = [int](($r.Top + $r.Bottom) / 2)
    # Windows 11's Notepad starts through a stub, so the process started may not be the one with
    # the window: the newest Notepad that has one is.
    Start-Process notepad
    Start-Sleep -Seconds 3
    $np = Get-Process -Name notepad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Sort-Object StartTime -Descending | Select-Object -First 1
    if (-not $np) { $facts[$Label] = 'no notepad window'; return }
    $nh = $np.MainWindowHandle
    [QaWin32]::SetWindowPos($nh, [IntPtr]::Zero, $r.Left - 40, $r.Top - 40, $r.Right - $r.Left + 80, $r.Bottom - $r.Top + 80, 0x0040) | Out-Null
    [QaWin32]::SetForegroundWindow($nh) | Out-Null
    Start-Sleep -Seconds 1
    $facts[$Label] = if ([QaWin32]::TopAt($cx, $cy) -eq $h) { 'app' } elseif ([QaWin32]::TopAt($cx, $cy) -eq $nh) { 'notepad' } else { 'other' }
    Get-Process -Name notepad -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

function Send-ToApp([string]$Keys) {
    [QaWin32]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 300
    Send-KeyTo (Get-AppWindow) $Keys
    Start-Sleep -Seconds 2
}

if ($phase -eq 'routes') {
    # Standard layout and unpinned, to begin with.
    if ([QaWin32]::IsTopmost($h)) { Send-ToApp '^+t' }
    Measure-Window 'standard'
    Send-ToApp '^+m'
    Measure-Window 'compact'

    # The shortcut.
    Send-ToApp '^+t'
    Measure-Window 'shortcut'
    Test-Covered 'coveredPinned'

    # The pushpin's tooltip, by real pointer movement.
    $pin = Find-Control (Get-AppWindow) -AutomationId 'PinnedIndicator' -Seconds 2
    if ($pin) {
        $b = Get-Bounds $pin
        [QaWin32]::MoveTo($b.Left - 60, $b.Top + 60)
        [QaWin32]::MoveTo($b.Left + 16, $b.Top + 16)
        Start-Sleep -Seconds 2
        $tips = $script:Ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::ToolTip)))
        $facts.tooltip = @($tips | ForEach-Object { $_.Current.Name }) -join ' | '
    }

    # A drag on the title bar.
    $bar = Get-Bounds (Find-Control (Get-AppWindow) -AutomationId 'AppTitleBar')
    $before = [QaWin32]::Rect($h)
    [QaWin32]::MoveTo($bar.Left + 120, $bar.Top + 16)
    [QaWin32]::LeftDown()
    [QaWin32]::MoveTo($bar.Left + 180, $bar.Top + 56)
    [QaWin32]::LeftUp()
    Start-Sleep -Seconds 1
    $after = [QaWin32]::Rect($h)
    $facts.dragged = "$($after.Left - $before.Left),$($after.Top - $before.Top)"

    # The window's own right-click menu, read, then used to unpin.
    $r = [QaWin32]::Rect($h)
    [QaWin32]::MoveTo([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2) + 20)
    [QaWin32]::RightClick()
    Start-Sleep -Seconds 1
    $keep = Find-Control $script:Ae::RootElement -AutomationId 'KeepAboveMenuItem' -Seconds 3
    $compact = Find-Control $script:Ae::RootElement -AutomationId 'CompactMenuItem' -Seconds 1
    if ($keep -and $compact) {
        $facts.menu = "$($keep.Current.Name) $(Get-ToggleState $keep) $($keep.Current.AcceleratorKey); $($compact.Current.Name) $(Get-ToggleState $compact) $($compact.Current.AcceleratorKey)"
        $keep.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Seconds 2
    }
    Measure-Window 'menuUnpinned'
    Test-Covered 'coveredUnpinned'

    # The notification area's menu: read, then used to pin again.
    $t = Use-TrayMenu 'Keep above other windows'
    $facts.trayItems = $t.Items -join ' | '
    Measure-Window 'trayPinned'
    $facts.trayAfter = (Use-TrayMenu '').Items -join ' | '
}
elseif ($phase -eq 'exit') {
    # Exit as a person would, from the Details window's Settings page, opened by Ctrl+D because the
    # compact layout has no footer.
    Send-ToApp '^d'
    $d = Get-AppWindowNamed 'Receiver Details'
    [void](Select-NavigationItem $d 'Settings')
    Start-Sleep -Seconds 3
    $exit = Find-Control $d -AutomationId 'ExitButton'
    if ($exit) { Invoke-Control $exit }
    Start-Sleep -Seconds 5
    $facts.running = @(Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue).Count
}
elseif ($phase -eq 'restarted') {
    Measure-Window 'restarted'
    Send-ToApp '^+m'
    $foot = Find-Control (Get-AppWindow) -AutomationId 'AlwaysOnTopButton' -Seconds 3
    $facts.footer = if ($foot) { Get-ToggleState $foot } else { 'missing' }
    $facts.standardTopmost = [QaWin32]::IsTopmost($h)
}
$facts | ConvertTo-Json -Compress -Depth 4
'@

function Test-PinCompact {
    param($Vm, $Result)
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"

    $s = Invoke-UiStep $Vm 'pin-routes' "`$phase = 'routes'" $pinStep
    if ($s.error) { Check $Result '[22] the window' $false $s.error; return }
    Check $Result '[22] standard layout, unpinned, to begin' (-not $s.standard.topmost) ($s.standard | ConvertTo-Json -Compress)
    Check $Result '[22] compact mode, still unpinned' (-not $s.compact.topmost -and $s.compact.height -lt $s.standard.height) ($s.compact | ConvertTo-Json -Compress)
    $k = $s.shortcut
    Check $Result '[22] Ctrl+Shift+T pins it: Windows keeps it on top' $k.topmost ($k | ConvertTo-Json -Compress)
    Check $Result '[22] and Notepad, brought to the front over it, does not cover it' ($s.coveredPinned -eq 'app') "on top at its centre: $($s.coveredPinned)"
    Check $Result '[22] the pushpin shows, just before the minimise button' ($k.pushpin -and $k.gap -ge 0 -and $k.gap -le 16) "gap $($k.gap) px"
    Check $Result '[22] the title bar stays its normal height' ($k.bar -eq $s.compact.bar) "bar $($k.bar) px, unpinned $($s.compact.bar) px"
    Check $Result '[22] the compact medallion is whole' ($k.medallion -eq '64x64' -and $k.medallionInside) "$($k.medallion), inside the window $($k.medallionInside)"
    Check $Result '[22] the pushpin has a tooltip naming the key' ($s.tooltip -match 'Ctrl\+Shift\+T') $s.tooltip
    Check $Result '[22] dragging the title bar still moves the window' ($s.dragged -eq '60,40') "moved $($s.dragged)"
    Check $Result '[22] the right-click menu: both ticked, each with its key' ($s.menu -match 'Keep this window above others On Ctrl\+Shift\+T; Compact mode On Ctrl\+Shift\+M') $s.menu
    Check $Result '[22] choosing it unpins: the flag and the pushpin go' (-not $s.menuUnpinned.topmost -and -not $s.menuUnpinned.pushpin) ($s.menuUnpinned | ConvertTo-Json -Compress)
    Check $Result '[22] and Notepad can cover it' ($s.coveredUnpinned -eq 'notepad') "on top at its centre: $($s.coveredUnpinned)"
    Check $Result '[22] the tray menu: Keep above other windows unticked, between Open and Exit' ($s.trayItems -eq 'Open | Keep above other windows | --- | Exit') $s.trayItems
    Check $Result '[22] choosing it pins again' ($s.trayPinned.topmost -and $s.trayPinned.pushpin) ($s.trayPinned | ConvertTo-Json -Compress)
    Check $Result '[22] and the tray menu then shows it ticked' ($s.trayAfter -match 'Keep above other windows \[ticked\]') $s.trayAfter

    $x = Invoke-UiStep $Vm 'pin-exit' "`$phase = 'exit'" $pinStep
    Check $Result '[22] Exit from Settings ends the app' ($x.running -eq 0) "$($x.running) running $($x.error)"
    $r = Invoke-UiStep $Vm 'pin-restarted' "`$phase = 'restarted'" $pinStep
    $q = $r.restarted
    Check $Result '[22] after a restart: compact, pinned, with the pushpin' ($q.topmost -and $q.pushpin -and $q.height -eq $s.compact.height) "$($q | ConvertTo-Json -Compress) $($r.error)"
    Check $Result '[22] in the standard layout the footer pin agrees' ($r.footer -eq 'On' -and $r.standardTopmost) "footer $($r.footer), topmost $($r.standardTopmost)"
    try { Invoke-VmRun $Vm captureScreen -Arguments (Join-Path $Result.Folder 'screen.png') -Guest | Out-Null } catch { }
    $null = Save-Evidence $Vm $Result.Folder
}

# manual-qa.md section 23 (#578, #580, #581). Positions are physical pixels; $scale turns the
# section's effective pixels into them, so the same checks hold at 100 % and at 150 %.
$layoutStep = @'
$facts = [ordered]@{}
$ids = 'Satellites', 'TfomPill', 'ClockText', 'RolloverBadge', 'ZoneButton', 'FooterText', 'DetailsButton', 'AlwaysOnTopButton', 'ConnectButton'

function Get-Layout {
    $w = Get-AppWindow
    $h = Get-Handle $w
    $r = [QaWin32]::Rect($h)
    $scale = [QaWin32]::GetDpiForWindow($h) / 96.0
    $m = [ordered]@{
        dpi = [QaWin32]::GetDpiForWindow($h)
        physical = "$($r.Right - $r.Left)x$($r.Bottom - $r.Top)"
        effective = '{0:N0}x{1:N0}' -f (($r.Right - $r.Left) / $scale), (($r.Bottom - $r.Top) / $scale)
        missing = @(); outside = @()
    }
    $b = @{}
    foreach ($id in $ids) {
        $e = Find-Control $w -AutomationId $id -Seconds 1
        if (-not $e) { $m.missing += $id; continue }
        $b[$id] = Get-Bounds $e
        if ($b[$id].Left -lt $r.Left -or $b[$id].Right -gt $r.Right -or $b[$id].Bottom -gt $r.Bottom) { $m.outside += $id }
    }
    $clock = Find-Control $w -AutomationId 'ClockText' -Seconds 1
    if ($clock) {
        $text = $clock.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $m.lines = @($text.DocumentRange.GetBoundingRectangles()).Count
        $first = $text.DocumentRange.Clone()
        $first.ExpandToEnclosingUnit([System.Windows.Automation.Text.TextUnit]::Line)
        $m.firstLine = $first.GetText(-1).Trim()
        $centre = ($b.ClockText.Top + $b.ClockText.Bottom) / 2
        $m.offCentre = [int][Math]::Max([Math]::Abs(($b.RolloverBadge.Top + $b.RolloverBadge.Bottom) / 2 - $centre), [Math]::Abs(($b.ZoneButton.Top + $b.ZoneButton.Bottom) / 2 - $centre))
    }
    if ($b.FooterText -and $b.DetailsButton) { $m.statusClear = $b.FooterText.Right -le $b.DetailsButton.Left }
    if ($b.ConnectButton) { $m.bottomGap = [int](($r.Bottom - [Math]::Max($b.ConnectButton.Bottom, $b.AlwaysOnTopButton.Bottom)) / $scale) }
    [pscustomobject]@{ Facts = $m; Handle = $h; Rect = $r; Scale = $scale }
}

if ($phase -eq 'first') {
    # Exit from the tray menu, as the section says; the stored placement deleted; started again.
    if (Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue) { [void](Use-TrayMenu 'Exit'); Start-Sleep -Seconds 3 }
    Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
    $pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
    $dir = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
    '{"PortName":"COM2","AutoDetect":false,"BaudRate":9600,"DataBits":8,"Parity":0,"StopBits":1,"ReconnectAutomatically":true,"ConnectOnLaunch":true}' | Set-Content (Join-Path $dir 'connection.json') -Encoding ascii
    Remove-Item (Join-Path $dir 'window.json') -ErrorAction SilentlyContinue
    $facts.storedPlacement = Test-Path (Join-Path $dir 'window.json')
    Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
    # The clock line appears once the receiver has answered, and its rollover badge once the
    # receiver's date has been read, a few seconds later; measuring at the first left the badge out.
    $deadline = (Get-Date).AddSeconds(90)
    do { Start-Sleep -Seconds 2; $w = Get-AppWindow -Seconds 5 } while ((Get-Date) -lt $deadline -and -not ($w -and (Find-Control $w -AutomationId 'ClockText' -Seconds 0) -and (Find-Control $w -AutomationId 'RolloverBadge' -Seconds 0)))
    Start-Sleep -Seconds 3
    $facts.layout = (Get-Layout).Facts
}
elseif ($phase -eq 'narrow') {
    $l = Get-Layout
    $h = $l.Handle; $r = $l.Rect; $scale = $l.Scale
    $tall = [int](640 * $scale)
    # As narrow as the window goes, and tall enough for the full layout.
    [QaWin32]::SetWindowPos($h, [IntPtr]::Zero, $r.Left, 20, 100, $tall, 0x0014) | Out-Null
    Start-Sleep -Seconds 2
    $l = Get-Layout
    $facts.narrow = $l.Facts
    $r = $l.Rect

    # The height down a step at a time, then back up: the rows that go must go together, and the
    # clock line must never be clipped on the way.
    $step = [Math]::Max(2, [int](2 * $scale))
    $transitions = @(); $clipped = @(); $last = $null
    $heights = @(); for ($y = $tall; $y -ge [int](460 * $scale); $y -= $step) { $heights += $y }
    $heights += $heights[($heights.Count - 1)..0]
    foreach ($y in $heights) {
        [QaWin32]::SetWindowPos($h, [IntPtr]::Zero, $r.Left, $r.Top, $r.Right - $r.Left, $y, 0x0016) | Out-Null
        Start-Sleep -Milliseconds 250
        $now = [QaWin32]::Rect($h)
        $win = Get-AppWindow -Seconds 2
        $c = Find-Control $win -AutomationId 'ClockText' -Seconds 0
        $state = '{0}{1}{2}' -f [int][bool]$c, [int][bool](Find-Control $win -AutomationId 'FooterText' -Seconds 0), [int][bool](Find-Control $win -AutomationId 'Satellites' -Seconds 0)
        if ($c -and (Get-Bounds $c).Bottom -gt $now.Bottom - [int](8 * $scale)) { $clipped += $now.Bottom - $now.Top }
        if ($state -ne $last) { $transitions += "$([int](($now.Bottom - $now.Top) / $scale)):$state"; $last = $state }
    }
    $facts.transitions = $transitions -join ' '
    $facts.clipped = $clipped -join ','

    # Copy from the clock line's menu, at full height again.
    [QaWin32]::SetWindowPos($h, [IntPtr]::Zero, $r.Left, $r.Top, $r.Right - $r.Left, $tall, 0x0016) | Out-Null
    Start-Sleep -Seconds 2
    [QaWin32]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Seconds 1
    $c = Find-Control (Get-AppWindow) -AutomationId 'ClockText' -Seconds 2
    $cb = Get-Bounds $c
    [QaWin32]::MoveTo($cb.Left + [int](30 * $scale), $cb.Top + [int](9 * $scale)); [QaWin32]::RightClick()
    $copy = Find-Control $script:Ae::RootElement -Name 'Copy value' -Seconds 4
    if ($copy) {
        Invoke-Control $copy
        Start-Sleep -Seconds 1
        $text = Get-Clipboard -Raw
        $facts.copied = $text
        $facts.copiedOdd = @($text.ToCharArray() | Where-Object { [int]$_ -lt 32 -or [int]$_ -eq 0xA0 -or [int]$_ -eq 0x202F -or [int]$_ -eq 0x2007 } | ForEach-Object { 'U+{0:X4}' -f [int]$_ }) -join ','
    }
}
elseif ($phase -eq 'scale150') {
    # A screen big enough for a window at 150 %, saved so the sign-out keeps it, and 150 % for the
    # next sign-in. VMware Tools' own resolution tool changes nothing without a console, so Win32.
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class QaDisplay
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
    public static int Set(int w, int h)
    {
        DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        EnumDisplaySettings(null, -1, ref dm);
        dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettings(ref dm, 1);
    }
}
"@
    $facts.resolution = [QaDisplay]::Set(1600, 1200)
    Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name LogPixels -Value 144 -Type DWord
    Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name Win8DpiScaling -Value 1 -Type DWord
    Set-Content -LiteralPath 'C:\qa\force.ps1' -Value "Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name ForceAutoLogon -Value '1' -Type String"
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\qa\force.ps1'
}
$facts | ConvertTo-Json -Compress -Depth 5
'@

function Test-WholeLayout {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"

    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    try {
        $sizes = @{}
        foreach ($scaling in '100', '150') {
            if ($scaling -eq '150') {
                $null = Invoke-UiStep $Vm 'scale-150' "`$phase = 'scale150'" $layoutStep
                Invoke-SignOutAndIn $Vm
            }
            $s = Invoke-UiStep $Vm "first-$scaling" "`$phase = 'first'" $layoutStep
            if ($s.error) { Check $Result "[23] $scaling %: the window" $false $s.error; continue }
            $l = $s.layout
            $sizes[$scaling] = $l.effective
            Check $Result "[23] $scaling %: opens with no stored placement at the display's scaling" (-not $s.storedPlacement -and $l.dpi -eq [int]($scaling) * 96 / 100) "dpi $($l.dpi), $($l.physical) physical, $($l.effective) effective"
            Check $Result "[23] $scaling %: readouts, merits, clock line and footer all showing, none outside" ($l.missing.Count -eq 0 -and $l.outside.Count -eq 0) "missing: $($l.missing -join ','); outside: $($l.outside -join ',')"
            Check $Result "[23] $scaling %: the whole clock line on one line" ($l.lines -eq 1) "$($l.lines) line(s): $($l.firstLine)"
            Check $Result "[23] $scaling %: the badge and the globe button centred on it" ($l.offCentre -le 2 * [int]$scaling / 100) "off centre by $($l.offCentre) px"
            Check $Result "[23] $scaling %: the status line clear of Details, the pin and Connect" ($l.statusClear -eq $true) ''
            Check $Result "[23] $scaling %: nothing clipped at the bottom, and no taller than it needs" ($l.bottomGap -ge 0 -and $l.bottomGap -le 40) "$($l.bottomGap) effective px below the footer"
            try { Invoke-VmRun $Vm captureScreen -Arguments (Join-Path $Result.Folder "first-$scaling.png") -Guest | Out-Null } catch { }

            $n = Invoke-UiStep $Vm "narrow-$scaling" "`$phase = 'narrow'" $layoutStep
            if ($n.error) { Check $Result "[23] $scaling %: narrowing" $false $n.error; continue }
            $w = $n.narrow
            Check $Result "[23] $scaling %: at the minimum width the clock wraps onto two lines, before the date" ($w.lines -eq 2 -and $w.firstLine -notmatch '\d{4}' -and $w.firstLine -match 'Time') "$($w.effective) effective; first line '$($w.firstLine)'"
            Check $Result "[23] $scaling %: the badge and the globe button on screen, centred on both lines" ($w.outside.Count -eq 0 -and $w.offCentre -le 2 * [int]$scaling / 100) "outside: $($w.outside -join ','); off centre by $($w.offCentre) px"
            Check $Result "[23] $scaling %: the status line stops short of the buttons" ($w.statusClear -eq $true) ''
            $states = @($n.transitions -split ' ' | ForEach-Object { ($_ -split ':')[1] })
            Check $Result "[23] $scaling %: shrinking, the footer, readouts and clock line go together, and come back" ((@($states | Where-Object { $_ -notin '111', '000' }).Count -eq 0) -and $states.Count -ge 3) "$($n.transitions) (effective height:clock,footer,readouts)"
            Check $Result "[23] $scaling %: the clock line is never clipped on the way" (-not $n.clipped) "clipped at: $($n.clipped)"
            Check $Result "[23] $scaling %: Copy gives one line with ordinary spaces" ($n.copied -and -not $n.copiedOdd) "'$($n.copied)' $($n.copiedOdd)"
            try { Invoke-VmRun $Vm captureScreen -Arguments (Join-Path $Result.Folder "narrow-$scaling.png") -Guest | Out-Null } catch { }
        }
        # The size is applied twice, against 100 % and then at the real scaling; only the second
        # landing gives the same effective size at 150 % as at 100 %.
        if ($sizes['100'] -and $sizes['150']) {
            $a = $sizes['100'] -split 'x'; $b = $sizes['150'] -split 'x'
            Check $Result '[23] 150 % opens at the same effective size as 100 %' ([Math]::Abs([int]$a[0] - [int]$b[0]) -le 3 -and [Math]::Abs([int]$a[1] - [int]$b[1]) -le 3) "100 %: $($sizes['100']); 150 %: $($sizes['150'])"
        }
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# manual-qa.md section 4, the criteria a script can judge on the running app.
$a11yStep = @'
$facts = [ordered]@{}
function Open-Details([string]$Page) {
    $d = Get-AppWindowNamed 'Receiver Details' -Seconds 2
    if (-not $d) {
        $w = Get-AppWindow
        $bar = Get-Bounds (Find-Control $w -AutomationId 'AppTitleBar' -Seconds 2)
        [QaWin32]::MoveTo($bar.Left + 120, $bar.Top + [int]($bar.Height / 2)); [QaWin32]::LeftDown(); [QaWin32]::LeftUp()
        Start-Sleep -Milliseconds 500
        Send-KeyTo $w '^d'
        $d = Get-AppWindowNamed 'Receiver Details'
    }
    [void](Select-NavigationItem $d $Page)
    Start-Sleep -Seconds 3
    $d
}

if ($phase -eq 'names') {
    # A11Y-10: the medallion's state as a sentence; every satellite on the plot as one.
    $med = Find-Control (Get-AppWindow) -AutomationId 'Medallion'
    $facts.medallion = "$($med.Current.Name)"
    $facts.modeText = "$((Find-Control (Get-AppWindow) -AutomationId 'ModeText' -Seconds 1).Current.Name)"
    $d = Open-Details 'Satellites'
    $plot = Find-Control $d -AutomationId 'SkyPlot'
    $facts.markers = @($plot.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) |
        ForEach-Object { $_.Current.Name })
    # A11Y-11: the List view, row by row, each row's sentence and its cells.
    $list = Find-Control $d -AutomationId 'ListViewChoice'
    $list.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2
    $rows = Find-Control $d -AutomationId 'SkyRows'
    $facts.rows = @($rows.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
        $cells = @($_.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name })
        "$($_.Current.Name)|$($cells -join ';')"
    })
    $list = Find-Control $d -AutomationId 'PlotViewChoice'
    $list.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
elseif ($phase -eq 'tooltips') {
    # A11Y-3: every icon-only control named, and its tooltip opened by real pointer movement. Icon
    # buttons are squarish and text buttons wide; a button's text is not exposed as a child, so
    # shape is what tells them apart. Window chrome and stock parts of library controls are not
    # this application's to name. Details sits on the Timing page, where Export is enabled, and is
    # moved clear of the main window, which owns it and so is always beneath it.
    $d = Open-Details 'Timing'
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Find-Control $d -AutomationId 'ExportButton' -Seconds 1).Current.IsEnabled -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
    $m = [QaWin32]::Rect((Get-Handle (Get-AppWindow)))
    [QaWin32]::SetWindowPos((Get-Handle $d), [IntPtr]::Zero, 20, $m.Bottom + 10, 0, 0, 0x0015) | Out-Null
    Start-Sleep -Seconds 1
    $stock = 'Minimize', 'Maximize', 'Close', 'UpSpinButton', 'DownSpinButton', 'TogglePaneButton', 'CloseButton'
    $found = @()
    foreach ($label in 'main', 'details') {
        $win = if ($label -eq 'main') { Get-AppWindow } else { Get-AppWindowNamed 'Receiver Details' }
        $bar = Get-Bounds (Find-Control $win -AutomationId 'AppTitleBar' -Seconds 2)
        [QaWin32]::MoveTo($bar.Left + 120, $bar.Top + [int]($bar.Height / 2)); [QaWin32]::LeftDown(); [QaWin32]::LeftUp()
        Start-Sleep -Seconds 1
        foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
            $r = $e.Current.BoundingRectangle
            if ($e.Current.IsOffscreen -or $e.Current.AutomationId -in $stock -or $r.Width -gt 1.6 * $r.Height) { continue }
            $texts = @($e.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -match '\w' })
            if ($texts.Count) { continue }
            $b = Get-Bounds $e
            [QaWin32]::MoveTo($b.Left - 150, $b.Top + 150)
            Start-Sleep -Milliseconds 800
            [QaWin32]::MoveTo($b.Left + [int]($b.Width / 2), $b.Top + [int]($b.Height / 2) + 50)
            [QaWin32]::MoveTo($b.Left + [int]($b.Width / 2), $b.Top + [int]($b.Height / 2))
            Start-Sleep -Milliseconds 1800
            $tip = @($script:Ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::ToolTip))) | ForEach-Object { $_.Current.Name }) -join ' | '
            $found += "$label|$($e.Current.AutomationId)|$($e.Current.Name)|$($e.Current.IsEnabled)|$tip"
        }
    }
    $facts.controls = $found
}
elseif ($phase -eq 'tierC') {
    # A command with a confirmed outcome: the elevation mask, applied from the Satellites page. The
    # simulated receiver refuses it, which is the outcome that must interrupt.
    $d = Open-Details 'Satellites'
    [QaWin32]::SetWindowPos((Get-Handle $d), [IntPtr]::Zero, 20, 20, 0, 0, 0x0015) | Out-Null
    Start-Sleep -Seconds 1
    $apply = Find-Control $d -AutomationId 'ApplyMaskButton'
    if ($apply) { Invoke-Control $apply }
    $primary = Find-Control $d -AutomationId 'PrimaryButton' -Seconds 4
    if ($primary) { Invoke-Control $primary }
    Start-Sleep -Seconds 12
    $facts.outcome = "$((Find-Control $d -AutomationId 'MaskOutcome' -Seconds 2).Current.Name)"
}
$facts | ConvertTo-Json -Compress -Depth 4
'@

function Test-Accessibility {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    $null = Invoke-QaGuestScript $Vm -Name 'prefs-a11y' -Script @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$dir = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
'{"PortName":"COM2","AutoDetect":false,"BaudRate":9600,"DataBits":8,"Parity":0,"StopBits":1,"ReconnectAutomatically":true,"ConnectOnLaunch":true}' | Set-Content (Join-Path $dir 'connection.json') -Encoding ascii
Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
'@
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    try {
        $seen = Wait-AppLog $Vm 'State: LOCK' 0 120 'locked'
        Check $Result 'connected and locked to the simulated receiver' $seen.found $seen.line

        # A11Y-10 and A11Y-11.
        $n = Invoke-UiStep $Vm 'a11y-names' "`$phase = 'names'" $a11yStep
        Check $Result '[4] A11Y-10: the medallion states its mode as a sentence' ($n.medallion -match '^[A-Z].+, .+\.$' -and $n.modeText -and $n.medallion.StartsWith($n.modeText)) "'$($n.medallion)'"
        $markers = @($n.markers)
        Check $Result '[4] A11Y-10: the sky plot exposes every satellite as a sentence' ($markers.Count -ge 4 -and @($markers | Where-Object { $_ -notmatch '^PRN \d+, elevation \d+ degrees, azimuth \d+ degrees, .+' }).Count -eq 0) "$($markers.Count) markers: $($markers -join ' / ')"
        # The plot and the list are read moments apart from a receiver that is moving, so they are
        # compared satellite by satellite: the same PRNs and states, positions within a degree, and
        # each row's cells in agreement with its own sentence.
        $plotBy = @{}; foreach ($m in $markers) { if ($m -match '^PRN (\d+), elevation (\d+) degrees, azimuth (\d+) degrees, (.+)$') { $plotBy[$Matches[1]] = @([int]$Matches[2], [int]$Matches[3], ($Matches[4] -replace '^C/N \d+ of \d+, ', '')) } }
        $problems = @(); $listPrns = @()
        foreach ($row in @($n.rows)) {
            $name, $cells = $row -split '\|', 2
            $c = $cells -split ';'
            if ($name -notmatch '^PRN (\d+), elevation (\d+) degrees, azimuth (\d+) degrees, (.+)$') { $problems += "row '$name'"; continue }
            $prn = $Matches[1]; $el = [int]$Matches[2]; $az = [int]$Matches[3]; $state = $Matches[4] -replace '^C/N \d+ of \d+, ', ''
            $listPrns += $prn
            if ($c[0] -ne $prn -or ($c[1] -replace '\D') -ne "$el" -or ($c[2] -replace '\D') -ne "$az") { $problems += "PRN $prn cells $($c -join ',') against its sentence" }
            if (-not $plotBy.ContainsKey($prn)) { $problems += "PRN $prn listed, not plotted"; continue }
            $pl = $plotBy[$prn]
            if ([Math]::Abs($pl[0] - $el) -gt 1 -or [Math]::Abs($pl[1] - $az) -gt 1 -or $pl[2] -ne $state) { $problems += "PRN $prn plot $($pl -join ',') list $el,$az,$state" }
        }
        foreach ($prn in $plotBy.Keys) { if ($listPrns -notcontains $prn) { $problems += "PRN $prn plotted, not listed" } }
        Check $Result '[4] A11Y-11: List shows the same satellites with the same data as the plot' ($problems.Count -eq 0 -and $listPrns.Count -eq $plotBy.Count) "$($listPrns.Count) rows, $($plotBy.Count) markers; $($problems -join '; ')"

        # A11Y-3.
        $tt = Invoke-UiStep $Vm 'a11y-tooltips' "`$phase = 'tooltips'" $a11yStep
        $controls = @($tt.controls)
        foreach ($expected in 'main|ZoneButton', 'main|AlwaysOnTopButton', 'details|RefreshButton', 'details|ExportButton', 'details|SettingsButton', 'details|HelpButton') {
            $hit = $controls | Where-Object { $_ -like "$expected|*" } | Select-Object -First 1
            $f = if ($hit) { $hit -split '\|' } else { @() }
            Check $Result "[4] A11Y-3: $($expected -replace '\|', ' ') is named and its tooltip opens" ($hit -and $f[2] -and $f[4]) "$(if ($hit) { "name '$($f[2])', enabled $($f[3]), tooltip '$($f[4])'" } else { 'not found as an icon-only control' })"
        }
        $others = @($controls | Where-Object { $c = $_; -not ('main|ZoneButton', 'main|AlwaysOnTopButton', 'details|RefreshButton', 'details|ExportButton', 'details|SettingsButton', 'details|HelpButton' | Where-Object { $c -like "$_|*" }) })
        Check $Result '[4] A11Y-3: every other icon-only control is named, and enabled ones show a tooltip' (@($others | Where-Object { $f = $_ -split '\|'; -not $f[2] -or ($f[3] -eq 'True' -and -not $f[4]) }).Count -eq 0) "$($others.Count) more: $($others -join ' / ')"

        # A11Y-9: a listener records live-region events while the simulator forces a mode change and
        # a lost connection, and the Satellites page runs a tier C command.
        Copy-QaFile $Vm -Source (Join-Path $PSScriptRoot 'guest\LiveListen.ps1') -Destination 'C:\qa\LiveListen.ps1' -ToGuest
        Invoke-VmRun $Vm runProgramInGuest -Arguments '-noWait', '-activeWindow', '-interactive', 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\qa\LiveListen.ps1', '-Seconds', '200' -Guest | Out-Null
        Start-Sleep -Seconds 10
        $mark = (Wait-AppLog $Vm 'State:' 0 5 'mark-a11y').count
        $null = Send-SimulatorControl "$pipe-control" 'antenna off'
        $seen = Wait-AppLog $Vm 'State: (WAIT|HOLD)' $mark 120 'a11y-holdover'
        $null = Send-SimulatorControl "$pipe-control" 'antenna on'
        Start-Sleep -Seconds 20
        $null = Send-SimulatorControl "$pipe-control" 'power off'
        $seen = Wait-AppLog $Vm 'is now Reconnecting' $mark 60 'a11y-lost'
        $null = Send-SimulatorControl "$pipe-control" 'power on'
        $seen = Wait-AppLog $Vm 'is now Connected' $seen.count 120 'a11y-back'
        $null = Invoke-UiStep $Vm 'a11y-tierc' "`$phase = 'tierC'" $a11yStep
        $events = ''
        $deadline = (Get-Date).AddSeconds(150)
        do {
            try { Copy-QaFile $Vm -Source 'C:\qa\live-events.txt' -Destination (Join-Path $Result.Folder 'live-events.txt'); $events = Get-Content (Join-Path $Result.Folder 'live-events.txt') -Raw } catch { }
            if ($events -notmatch '(?m)^done') { Start-Sleep -Seconds 10 }
        } while ($events -notmatch '(?m)^done' -and (Get-Date) -lt $deadline)
        $lines = @($events -split "`r?`n" | Where-Object { $_ -match "`t" })
        $mode = $lines | Where-Object { $_ -match "`tAnnouncer`t.*Holdover" } | Select-Object -First 1
        Check $Result '[4] A11Y-9: a mode change is announced' ([bool]$mode) "$mode"
        $lost = $lines | Where-Object { $_ -match "`tAnnouncer`t.*(Reconnecting|Connection lost)" } | Select-Object -First 1
        Check $Result '[4] A11Y-9: a lost connection is announced' ([bool]$lost) "$lost"
        Check $Result '[4] A11Y-9: and assertively (#660)' ($lost -match "^\S+`tAssertive") "$lost"
        $outcome = $lines | Where-Object { $_ -match "`tMaskOutcome`t" } | Select-Object -First 1
        # The simulated receiver accepts the mask or refuses it depending on its state, and either is
        # an outcome: a failure must interrupt, and a success must not.
        $urgencyRight = if ($outcome -match "Couldn't") { $outcome -match "`tAssertive`t" } else { $outcome -match "`tPolite`t" }
        Check $Result '[4] A11Y-9: a tier C outcome is announced, assertively only if it failed' ($outcome -and $urgencyRight) "$outcome"
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# manual-qa.md section 7 (#47, §10.5): the image file is what is checked, so each export is saved
# through the app's own Save dialog and then measured, never eyeballed. Called with $theme naming
# the leg ('light', 'dark', 'contrast' or 'scaled') and $file the path to save to.
$skyStep = @'
$facts = [ordered]@{}
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaColours {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct HIGHCONTRAST { public int cbSize; public int dwFlags; public string lpszDefaultScheme; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(int action, int param, ref HIGHCONTRAST hc, int winIni);
    [DllImport("user32.dll")] public static extern int GetSysColor(int index);
    // High contrast on or off for the whole desktop, saved to the profile and broadcast.
    public static bool HighContrast(bool on) { var hc = new HIGHCONTRAST(); hc.cbSize = Marshal.SizeOf(hc); hc.dwFlags = on ? 1 : 0; hc.lpszDefaultScheme = on ? "High Contrast White" : null; return SystemParametersInfo(0x43, hc.cbSize, ref hc, 3); }
    public static string Hex(int c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF); }
}
"@

# The theme for this leg: Light and Dark are Windows' app mode, which the app follows; high
# contrast is the whole desktop's, through SPI_SETHIGHCONTRAST. Any contrast theme serves, as long
# as its window colour is neither page background, because the corner must match the live
# window colour and not coincide with another theme's.
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme -Value $(if ($theme -eq 'dark') { 0 } else { 1 }) -Type DWord
if ($theme -eq 'contrast') { $facts.contrastOn = [QaColours]::HighContrast($true); Start-Sleep -Seconds 8 }
# Restored in the finally below, so a leg that fails part way does not leave high contrast or Dark
# on for the next one: the 225 % leg once measured its corners under high contrast for that reason.
try {

Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process "shell:AppsFolder\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)!App"
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Seconds 2; $w = Get-AppWindow -Seconds 5 } while ((Get-Date) -lt $deadline -and -not ($w -and (Find-Control $w -AutomationId 'ClockText' -Seconds 0)))
# Details by its footer button, which does not depend on where the keyboard focus is: Ctrl+D
# missed at 225 %.
$detailsButton = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
if ($detailsButton) { Invoke-Control $detailsButton }
else {
    $bar = Get-Bounds (Find-Control $w -AutomationId 'AppTitleBar' -Seconds 2)
    [QaWin32]::MoveTo($bar.Left + [int]($bar.Width / 2), $bar.Top + [int]($bar.Height / 2)); [QaWin32]::LeftDown(); [QaWin32]::LeftUp()
    Start-Sleep -Seconds 1
    Send-KeyTo $w '^d'
}
# By its caption, or else as the app's other window: at 225 % its caption was the main window's (#663).
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 15
if (-not $d) {
    $other = [QaWin32]::WindowOtherThan([uint32](Get-Process -Name WinZ3805A | Select-Object -First 1).Id, (Get-Handle $w))
    if ($other -ne [IntPtr]::Zero) { $d = $script:Ae::FromHandle($other) }
}
$facts.detailsCaption = "$($d.Current.Name)"
[void](Select-NavigationItem $d 'Satellites')
Start-Sleep -Seconds 5
# Waited for: the mask once read as a dash on Win11, the page not yet filled five seconds after
# navigating.
$deadline = (Get-Date).AddSeconds(15)
do { $facts.mask = "$((Find-Control $d -AutomationId 'ElevationMaskText' -Seconds 2).Current.Name)"; if ($facts.mask -match '\d') { break }; Start-Sleep -Seconds 1 } while ((Get-Date) -lt $deadline)

# The card's own layout at this width (#664): the heading against the Plot choice beside it, and
# the legend's last label against the card's content edge, which Save image's right edge marks.
# The legend is Raw to assistive technology, so it is reached through the raw view.
# An element with nothing on screen - clipped away entirely - has an empty rectangle, whose
# coordinates are infinite; that is reported as 'empty' rather than failing the step.
function Get-ScreenBounds($element) {
    if (-not $element -or $element.Current.BoundingRectangle.IsEmpty) { return $null }
    Get-Bounds $element
}
function Format-Bounds($b) { if ($b) { "$($b.Left),$($b.Top) $($b.Width)x$($b.Height)" } else { 'empty' } }
$headingBounds = Get-ScreenBounds (Find-Control $d -AutomationId 'SkyCardHeading' -Seconds 2)
$choiceBounds = Get-ScreenBounds (Find-Control $d -AutomationId 'PlotViewChoice' -Seconds 2)
$saveBounds = Get-ScreenBounds (Find-Control $d -AutomationId 'ExportImageButton' -Seconds 2)
$facts.heading = Format-Bounds $headingBounds; $facts.plotChoice = Format-Bounds $choiceBounds; $facts.saveButton = Format-Bounds $saveBounds
# WinUI reports the heading's rectangle already clipped to its column, so an overlapped heading
# shows as one that ends exactly where the Plot choice starts: touching, never intersecting. So a
# shared row needs a real gap, and the text's own extent, from its text pattern, must end before
# the Plot choice too.
$textRight = $null
try {
    $lines = (Find-Control $d -AutomationId 'SkyCardHeading' -Seconds 1).GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetBoundingRectangles()
    if ($lines.Count) { $textRight = [int](($lines | Measure-Object -Property Right -Maximum).Maximum) }
} catch { }
$facts.headingTextRight = $textRight
$sameRow = $headingBounds -and $choiceBounds -and $headingBounds.Bottom -gt $choiceBounds.Top -and $choiceBounds.Bottom -gt $headingBounds.Top
$facts.headerOverlap = $sameRow -and (($choiceBounds.Left - $headingBounds.Right) -lt 4 -or ($textRight -and $textRight -gt $choiceBounds.Left))
$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
function Find-Raw($element, $name) {
    $child = $walker.GetFirstChild($element)
    while ($child) {
        if ($child.Current.Name -eq $name) { return $child }
        $found = Find-Raw $child $name
        if ($found) { return $found }
        $child = $walker.GetNextSibling($child)
    }
}
$plotControl = Find-Control $d -AutomationId 'SkyPlot' -Seconds 2
$facts.skyPlot = Format-Bounds (Get-ScreenBounds $plotControl)
$label = Find-Raw $plotControl 'elevation mask'
if ($label) {
    $labelBounds = Get-ScreenBounds $label
    $facts.maskLabel = Format-Bounds $labelBounds
    $facts.legendClipped = -not $labelBounds -or -not $saveBounds -or $labelBounds.Width -le 0 -or $labelBounds.Right -gt $saveBounds.Right
}

function Open-SaveDialog {
    Invoke-Control (Find-Control $d -AutomationId 'ExportImageButton')
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 500
        $dlg = $d.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ClassNameProperty, '#32770')))
    } while (-not $dlg -and (Get-Date) -lt $deadline)
    $dlg
}

# Cancel first: the caption is drawn for the render only, and must not be left on the page.
if ($theme -eq 'light') {
    $dlg = Open-SaveDialog
    # Esc with the file name field focused, which is the dialog's own Cancel: its Cancel button is not
    # found reliably through UI Automation, and other elements in it share the button's id.
    if ($dlg) {
        $b = Get-Bounds (Find-Control $dlg -AutomationId '1001' -Seconds 3)
        [QaWin32]::MoveTo($b.Left + [int]($b.Width / 2), $b.Top + [int]($b.Height / 2)); [QaWin32]::LeftDown(); [QaWin32]::LeftUp()
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Seconds 3
    }
    $facts.cancelDialog = [bool]$dlg
    $facts.dialogClosed = -not [bool]$d.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ClassNameProperty, '#32770')))
    $facts.captionAfterCancel = [bool](Find-Control $d -AutomationId 'SkyPlotCaptionText' -Seconds 1)
}

# The file name typed into the dialog's own field: UI Automation offers no value to set there, and
# will not focus it either, so it is clicked.
Remove-Item $file -ErrorAction SilentlyContinue
$dlg = Open-SaveDialog
if ($dlg) {
    $b = Get-Bounds (Find-Control $dlg -AutomationId '1001' -Seconds 3)
    [QaWin32]::MoveTo($b.Left + [int]($b.Width / 2), $b.Top + [int]($b.Height / 2)); [QaWin32]::LeftDown(); [QaWin32]::LeftUp()
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait($file)
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $file) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 2
}
$facts.saved = Test-Path $file
$facts.captionAfterSave = [bool](Find-Control $d -AutomationId 'SkyPlotCaptionText' -Seconds 1)

if ($facts.saved) {
    # Measured: the corners, the lowest alpha anywhere, and how much of the image is the window-text
    # colour, which is what every marker resolves to under high contrast (#218).
    Add-Type -AssemblyName System.Drawing
    $bmp = [System.Drawing.Bitmap]::FromFile($file)
    $facts.size = "$($bmp.Width)x$($bmp.Height)"
    $facts.corners = @(@(0, 0), @(($bmp.Width - 1), 0), @(0, ($bmp.Height - 1)), @(($bmp.Width - 1), ($bmp.Height - 1))) | ForEach-Object {
        $c = $bmp.GetPixel($_[0], $_[1]); '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B } | Select-Object -Unique
    $text = [QaColours]::GetSysColor(8)
    $tr = $text -band 0xFF; $tg = ($text -shr 8) -band 0xFF; $tb = ($text -shr 16) -band 0xFF
    $minAlpha = 255; $textSamples = 0
    for ($y = 0; $y -lt $bmp.Height; $y += 3) {
        for ($x = 0; $x -lt $bmp.Width; $x += 3) {
            $p = $bmp.GetPixel($x, $y)
            if ($p.A -lt $minAlpha) { $minAlpha = $p.A }
            if ($p.R -eq $tr -and $p.G -eq $tg -and $p.B -eq $tb) { $textSamples++ }
        }
    }
    $bmp.Dispose()
    $facts.minAlpha = $minAlpha
    $facts.windowColour = [QaColours]::Hex([QaColours]::GetSysColor(5))
    $facts.textSamples = $textSamples

    # The caption, read back out of the file with Windows' own OCR.
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
    $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
    $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
    function Await($op, [Type]$type) { $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $task.Wait(); $task.Result }
    $sf = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($file)) ([Windows.Storage.StorageFile])
    $stream = Await ($sf.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $soft = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $ocr = Await ([Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages().RecognizeAsync($soft)) ([Windows.Media.Ocr.OcrResult])
    $facts.lastLine = "$(@($ocr.Lines)[-1].Text)"
    $facts.ocr = "$($ocr.Text)"
    $stream.Dispose()
}
}
finally {
    if ($theme -eq 'contrast') { $facts.contrastOff = [QaColours]::HighContrast($false) }
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme -Value 1 -Type DWord
}
$facts | ConvertTo-Json -Compress -Depth 4
'@

# The screen to 2880 x 1800 at 225 % for the next sign-in, as whole-layout does for 150 %.
$scale225Step = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaDisplay {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
    public static int Set(int w, int h) {
        DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        EnumDisplaySettings(null, -1, ref dm); dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettings(ref dm, 1);
    }
}
"@
$result = [QaDisplay]::Set(2880, 1800)
# The placement stored at 100 % would restore a window too small at 225 % for the footer; with none,
# the app opens at its whole layout at the real scaling, which section 23 checks.
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path $env:LOCALAPPDATA "Packages\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)\LocalCache\Local\WinZ3805A\window.json") -ErrorAction SilentlyContinue
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name LogPixels -Value 216 -Type DWord
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name Win8DpiScaling -Value 1 -Type DWord
Set-Content -LiteralPath 'C:\qa\force.ps1' -Value "Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name ForceAutoLogon -Value '1' -Type String"
Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\qa\force.ps1'
[ordered]@{ resolution = $result } | ConvertTo-Json -Compress
'@

function Test-SkyExport {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    $null = Invoke-QaGuestScript $Vm -Name 'prefs-sky' -Script @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$dir = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
New-Item -ItemType Directory -Force $dir | Out-Null
'{"PortName":"COM2","AutoDetect":false,"BaudRate":9600,"DataBits":8,"Parity":0,"StopBits":1,"ReconnectAutomatically":true,"ConnectOnLaunch":true}' | Set-Content (Join-Path $dir 'connection.json') -Encoding ascii
'@
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    $legs = [ordered]@{
        light    = @{ Background = '#F3F3F3'; Label = 'Light' }
        dark     = @{ Background = '#202020'; Label = 'Dark' }
        contrast = @{ Background = $null; Label = 'High contrast' }
        scaled   = @{ Background = '#F3F3F3'; Label = '225 %' }
    }
    $sizes = @{}
    $scaledLastLine = $null
    try {
        foreach ($leg in $legs.Keys) {
            $l = $legs[$leg]
            if ($leg -eq 'scaled') {
                $null = Invoke-UiStep $Vm 'sky-225' '' $scale225Step
                Invoke-SignOutAndIn $Vm
            }
            $s = Invoke-UiStep $Vm "sky-$leg" "`$theme = '$leg'; `$file = 'C:\qa\sky-$leg.png'" $skyStep
            if ($s.error) { Check $Result "[7] $($l.Label): the export" $false $s.error; continue }
            try { Copy-QaFile $Vm -Source "C:\qa\sky-$leg.png" -Destination (Join-Path $Result.Folder "sky-$leg.png") } catch { }
            if ($leg -eq 'light') {
                Check $Result '[7] Cancel in the Save dialog leaves no caption on the page' ($s.cancelDialog -and $s.dialogClosed -and -not $s.captionAfterCancel) "dialog opened $($s.cancelDialog), closed by Esc $($s.dialogClosed), caption left $($s.captionAfterCancel)"
            }
            Check $Result "[7] $($l.Label): the Details window has its own caption (#637, #663)" ($s.detailsCaption -like 'Receiver Details - *') "'$($s.detailsCaption)'"
            Check $Result "[7] $($l.Label): the card's heading is clear of the Plot choice beside it (#664)" ($s.heading -and $s.heading -ne 'empty' -and $s.plotChoice -ne 'empty' -and -not $s.headerOverlap) "heading $($s.heading), its text ending at $($s.headingTextRight), Plot $($s.plotChoice)"
            Check $Result "[7] $($l.Label): the legend's last entry is inside the card (#664)" ($s.maskLabel -and -not $s.legendClipped) "'elevation mask' $($s.maskLabel), Save image $($s.saveButton), plot $($s.skyPlot)"
            Check $Result "[7] $($l.Label): Save image writes a file, and the caption is gone afterwards" ($s.saved -and -not $s.captionAfterSave) "saved $($s.saved) $($s.size), caption left $($s.captionAfterSave)"
            if (-not $s.saved) { continue }
            $sizes[$leg] = $s.size
            if ($leg -eq 'scaled') { $scaledLastLine = $s.lastLine }
            $background = if ($l.Background) { $l.Background } else { $s.windowColour }
            Check $Result "[7] $($l.Label): opaque, and the corners are the theme's background" ($s.minAlpha -eq 255 -and @($s.corners).Count -eq 1 -and $s.corners -eq $background) "corners $($s.corners -join ','), expected $background; lowest alpha $($s.minAlpha)"
            if ($leg -eq 'contrast') {
                Check $Result '[7] High contrast: on, with a window colour unlike either page background' ($s.contrastOn -and $s.windowColour -notin '#F3F3F3', '#202020') "on $($s.contrastOn), window $($s.windowColour)"
                Check $Result '[7] High contrast: the markers are drawn, not painted in the surface colour (#218)' ($s.textSamples -ge 500) "$($s.textSamples) window-text samples"
            }
            # OCR reads the degree sign as a 0 or an o, so '10°' comes back as '100'; anything else after
            # the digits is a different mask.
            # No reading on the page is a failure, not a wildcard: a placeholder here once became a regex
            # quantifier and passed a caption against a mask nobody had read.
            $maskValue = if ($s.mask -match '(\d+)') { $Matches[1] } else { $null }
            Check $Result "[7] $($l.Label): the caption, read from the file, is in UTC and gives the page's mask" ($maskValue -and $s.ocr -match 'UTC' -and $s.ocr -match "elevation mask $maskValue[°0oO]?(?!\d)") "mask on the page '$($s.mask)'; caption read as '$($s.lastLine)'"
        }
        # RenderTargetBitmap truncates rather than throwing when asked for more than it can give, so an
        # over-budget capture opens cleanly and is missing its bottom. The caption is the image's last
        # row, so it must be the last thing OCR reads. Not the image's shape: the Satellites page lays
        # out differently at another effective width, so the card is a different shape at 225 %.
        if ($scaledLastLine) {
            Check $Result '[7] 225 %: not cropped, the caption is still the last row' ($scaledLastLine -match 'elevation mask \d') "last line read: '$scaledLastLine'"
        }
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# manual-qa.md section 21 (#551): the history across an uninstall, through the app's own pickers
# and confirmation. Called with $action ('export' or 'import'), $file, and for an import $choice:
# 'cancel' and 'import' press those buttons, 'enter' presses Enter so the default button decides.
$historyStep = @'
$facts = [ordered]@{}
$w = Get-AppWindow
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 2
if (-not $d) {
    $button = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
    if ($button) { Invoke-Control $button } else { Send-KeyTo $w '^d' }
    $d = Get-AppWindowNamed 'Receiver Details' -Seconds 20
}
if (-not $d) { [ordered]@{ error = "no Details window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
[void](Select-NavigationItem $d 'Settings')
Start-Sleep -Seconds 3
$before = (Get-AppLogLines).Count
$dialogClass = New-Object System.Windows.Automation.PropertyCondition($script:Ae::ClassNameProperty, '#32770')

function Wait-FileDialog {
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 500
        $dlg = $d.FindFirst([System.Windows.Automation.TreeScope]::Children, $dialogClass)
    } while (-not $dlg -and (Get-Date) -lt $deadline)
    $dlg
}

# The file name typed into the dialog's own box, which UI Automation can neither set nor focus, so it
# is clicked: 1148 in an Open dialog, 1001 in a Save dialog.
function Enter-FileName($dlg, [string]$path) {
    $field = Find-Control $dlg -AutomationId '1148' -Seconds 2
    if (-not $field) { $field = Find-Control $dlg -AutomationId '1001' -Seconds 2 }
    $b = Get-Bounds $field
    [QaWin32]::MoveTo($b.Left + [int]($b.Width / 2), $b.Top + [int]($b.Height / 2)); [QaWin32]::LeftDown(); [QaWin32]::LeftUp()
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait($path)
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
}

function Read-Status {
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $status = "$((Find-Control $d -AutomationId 'HistoryStatus' -Seconds 1).Current.Name)"
        if ($status) { return $status }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    ''
}

if ($action -eq 'export') {
    Remove-Item $file -ErrorAction SilentlyContinue
    Invoke-Control (Find-Control $d -AutomationId 'ExportHistoryButton')
    $dlg = Wait-FileDialog
    $facts.dialog = [bool]$dlg
    if ($dlg) { Enter-FileName $dlg $file }
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $file) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 2
    $facts.saved = Test-Path $file
    if ($facts.saved) {
        $facts.size = (Get-Item $file).Length
        $facts.header = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($file), 0, 15)
    }
    $facts.status = Read-Status
}
else {
    Invoke-Control (Find-Control $d -AutomationId 'ImportHistoryButton')
    $dlg = Wait-FileDialog
    $facts.dialog = [bool]$dlg
    if ($dlg) { Enter-FileName $dlg $file }
    $primary = Find-Control $d -AutomationId 'PrimaryButton' -Seconds 20
    $facts.confirmation = [bool]$primary
    $texts = @($d.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name })
    $facts.text = "$($texts | Where-Object { $_ -like 'This file holds*' } | Select-Object -First 1)"
    if ($primary) {
        # Which button has the keyboard when the dialog opens is which one Enter presses.
        Start-Sleep -Seconds 1
        $facts.focused = "$([System.Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId)"
        switch ($choice) {
            'cancel' { Invoke-Control (Find-Control $d -AutomationId 'CloseButton') }
            'import' { Invoke-Control $primary }
            'enter'  { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
        }
        Start-Sleep -Seconds 5
        $facts.dialogClosed = -not (Find-Control $d -AutomationId 'PrimaryButton' -Seconds 1)
    }
    else {
        $facts.problem = ($texts | Where-Object { $_ -match 'import|history' }) -join ' / '
        $close = Find-Control $d -AutomationId 'CloseButton' -Seconds 1
        if ($close) { Invoke-Control $close }
    }
    $facts.status = if ($facts.dialogClosed -and $choice -ne 'cancel') { Read-Status } else { "$((Find-Control $d -AutomationId 'HistoryStatus' -Seconds 1).Current.Name)" }
}
$lines = @(Get-AppLogLines | Select-Object -Skip $before)
$facts.exported = "$($lines | Where-Object { $_ -match 'History exported' } | Select-Object -Last 1)"
$facts.imported = "$($lines | Where-Object { $_ -match 'History imported' } | Select-Object -Last 1)"
$facts | ConvertTo-Json -Compress -Depth 4
'@

# The Details window's Overview at its 7 d range, for the photograph section 21's pass criterion is.
$trendStep = @'
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 5
if (-not $d) { [ordered]@{ error = "no Details window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
[void](Select-NavigationItem $d 'Overview')
# #668: the 1 PPS readout's caption measured at the size Details opens at and again maximised. The
# readout itself is arranged at its full width and clipped by the cell around it, which UI Automation
# does not report, so its label measures the same either way. The caption wraps to the width it is
# given: squeezed by the merits beside it, it takes two lines at the first size and one at the second.
Start-Sleep -Seconds 3
$label = Find-Control $d -Name 'relative to GPS' -Seconds 5
$narrow = if ($label -and -not $label.Current.BoundingRectangle.IsEmpty) { [int]$label.Current.BoundingRectangle.Height } else { 0 }
# And photographed as it is, for the agent to judge: the host's screenshot comes after the step.
try {
    Add-Type -AssemblyName System.Drawing
    $r = $d.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.Left, [int]$r.Top, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save('C:\qa\overview-opening.png', [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
} catch { }
$range = Find-Control $d -AutomationId 'OverviewRange7d' -Seconds 10
if ($range) { $range.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
# Maximised, and the chart scrolled into view: on the VMs' 1024 x 768 screen the window runs off the
# right and the trend is below the fold, so the first photograph showed neither.
try { $d.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized) } catch { }
Start-Sleep -Seconds 2
$label = Find-Control $d -Name 'relative to GPS' -Seconds 5
$wide = if ($label -and -not $label.Current.BoundingRectangle.IsEmpty) { [int]$label.Current.BoundingRectangle.Height } else { 0 }
if ($range) { try { $range.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { } }
# Scrolling the buttons into view leaves them on the bottom edge with the chart below them, so the
# page is scrolled one screen further.
$scrollable = $d.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($script:Ae::IsScrollPatternAvailableProperty, $true))) | Where-Object { $_.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticallyScrollable } | Select-Object -First 1
if ($scrollable) { try { $scrollable.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).ScrollVertical([System.Windows.Automation.ScrollAmount]::LargeIncrement) } catch { } }
Start-Sleep -Seconds 4
[ordered]@{ narrow = $narrow; wide = $wide; range = [bool]$range; selected = [bool]($range -and $range.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) } | ConvertTo-Json -Compress
'@

# Remembered settings for COM2 with connect-on-launch, then the app started: as a person choosing
# the port would leave it, and again after the reinstall has removed them.
$connectCom2 = @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$dir = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
New-Item -ItemType Directory -Force $dir | Out-Null
'{"PortName":"COM2","AutoDetect":false,"BaudRate":9600,"DataBits":8,"Parity":0,"StopBits":1,"ReconnectAutomatically":true,"ConnectOnLaunch":true}' | Set-Content (Join-Path $dir 'connection.json') -Encoding ascii
Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
'@

function Test-HistoryReinstall {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    # The simulator first: an app that finds nothing on the port at launch goes to Disconnected and
    # does not retry, and a step between the two once let it look before the simulator was there.
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    $null = Invoke-QaGuestScript $Vm -Name 'connect-com2' -Script $connectCom2
    $here = 'Z3805A serial 3625A02931'
    $warnings = "older than|newer version|can't be separated|doesn't say|different receiver"
    function Step([string]$Name, [string]$Action, [string]$File, [string]$Choice = '') {
        Invoke-UiStep $Vm "history-$Name" "`$action = '$Action'; `$file = '$File'; `$choice = '$Choice'" $historyStep
    }
    try {
        $seen = Wait-AppLog $Vm 'Session COM2 is now Connected' 0 120 'connected'
        Check $Result 'connected to the simulated receiver on COM2' $seen.found $seen.line
        if (-not $seen.found) { return }
        # A trend sample is appended on every fast poll, so a minute connected is some history.
        Start-Sleep -Seconds 60

        $a = Step 'export' 'export' 'C:\qa\history-a.sqlite'
        if ($a.error) { Check $Result '[21] Export history' $false $a.error; return }
        $rows = if ($a.status -match 'Exported ([\d,]+) readings') { [int]($Matches[1] -replace ',', '') } else { 0 }
        Check $Result '[21] Export history writes a SQLite file, and says how many readings over which dates' ($a.saved -and $a.header -eq 'SQLite format 3' -and $rows -gt 0 -and $a.status -match ' from \d{1,2} \w+ \d{4}') "status '$($a.status)'; $($a.size) bytes; header '$($a.header)'"
        Check $Result '[21] and logs the same count' ($a.exported -match "History exported: $rows samples") $a.exported
        try { Copy-QaFile $Vm -Source 'C:\qa\history-a.sqlite' -Destination (Join-Path $Result.Folder 'history-a.sqlite') } catch { }

        $b = Step 'back-cancel' 'import' 'C:\qa\history-a.sqlite' 'cancel'
        if ($b.error) { Check $Result '[21] importing it straight back' $false $b.error; return }
        Check $Result '[21] imported straight back, the confirmation names this receiver and warns about nothing' ($b.text -match [regex]::Escape("It came from the receiver connected now, $here.") -and $b.text -notmatch $warnings) "'$($b.text)'"
        Check $Result '[21] Import is the default for the receiver''s own file' ($b.focused -eq 'PrimaryButton') "focused '$($b.focused)'"
        Check $Result '[21] Cancel closes it and imports nothing' ($b.dialogClosed -and -not $b.imported) "closed $($b.dialogClosed); '$($b.imported)'"

        # A file that names no receiver: exported while the receiver has no power.
        $null = Send-SimulatorControl "$pipe-control" 'power off'
        $seen = Wait-AppLog $Vm 'Session COM2 is now Reconnecting' $seen.count 60 'powered-off'
        $c = Step 'export-none' 'export' 'C:\qa\history-none.sqlite'
        Check $Result '[21] exported again with no receiver connected' ($seen.found -and $c.saved) "reconnecting $($seen.found); saved $($c.saved); '$($c.status)'"
        $null = Send-SimulatorControl "$pipe-control" 'power on'
        $seen = Wait-AppLog $Vm 'Session COM2 is now Connected' $seen.count 120 'powered-on'

        # The uninstall, which is the point: the history goes with the package, the files must not.
        $r = Invoke-QaGuestScript $Vm -Name 'uninstall' -Script @'
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$data = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A\trend.db"
$had = Test-Path $data
$pkg | Remove-AppxPackage
Start-Sleep -Seconds 3
[ordered]@{ had = $had; package = [bool](Get-AppxPackage -Name WinZ3805A); data = (Test-Path $data); files = @(Get-ChildItem 'C:\qa\history-*.sqlite' | ForEach-Object Name) } | ConvertTo-Json -Compress
'@
        $u = ($r.Output -split "`r?`n" | Where-Object { $_.StartsWith('{') } | Select-Object -Last 1) | ConvertFrom-Json
        Check $Result '[21] uninstalled: the package and its history are gone, the exported files are not' ($u.had -and -not $u.package -and -not $u.data -and @($u.files).Count -eq 2) "history before $($u.had); package after $($u.package); history after $($u.data); files $(@($u.files) -join ', ')"

        $code = Install-Candidate $Vm 'candidate'
        Check $Result '[21] reinstalled (exit 0, or 2)' ($code -in 0, 2) "exit $code"
        $null = Invoke-QaGuestScript $Vm -Name 'reconnect-com2' -Script $connectCom2
        $seen = Wait-AppLog $Vm 'Session COM2 is now Connected' 0 120 'reconnected'
        Check $Result '[21] and reconnected to the same receiver' $seen.found $seen.line
        Start-Sleep -Seconds 30

        $e = Step 'restore' 'import' 'C:\qa\history-a.sqlite' 'import'
        if ($e.error) { Check $Result '[21] the import after the reinstall' $false $e.error; return }
        Check $Result '[21] after the reinstall, the confirmation names the receiver connected now and warns about nothing' ($e.text -match [regex]::Escape("It came from the receiver connected now, $here.") -and $e.text -notmatch $warnings) "'$($e.text)'"
        Check $Result '[21] Import adds every exported reading back' ($e.imported -match "$rows samples added, 0 already present; receiver Same" -and $e.status -like "Imported $($rows.ToString('N0', [cultureinfo]::InvariantCulture)) readings from history-a.sqlite*") "'$($e.imported)'; status '$($e.status)'"

        $f = Step 'again' 'import' 'C:\qa\history-a.sqlite' 'import'
        Check $Result '[21] importing the same file again adds nothing' ($f.imported -match "0 samples added, $rows already present") "'$($f.imported)'; status '$($f.status)'"

        $g = Step 'unknown' 'import' 'C:\qa\history-none.sqlite' 'enter'
        Check $Result '[21] a file naming no receiver says so, naming the one connected now' ($g.text -match [regex]::Escape("It doesn't say which receiver it came from. If that wasn't $here, the one connected now")) "'$($g.text)'"
        Check $Result '[21] and Import is its default: Enter imports' ($g.focused -eq 'PrimaryButton' -and $g.imported -match 'receiver Unknown') "focused '$($g.focused)'; '$($g.imported)'"

        # Another unit on the cable, and the session reconnected so it is the one asked.
        $null = Send-SimulatorControl "$pipe-control" 'serial 3625A99999'
        $null = Send-SimulatorControl "$pipe-control" 'power off'
        $seen = Wait-AppLog $Vm 'Session COM2 is now Reconnecting' $seen.count 60 'other-off'
        $null = Send-SimulatorControl "$pipe-control" 'power on'
        $seen = Wait-AppLog $Vm 'Session COM2 is now Connected' $seen.count 120 'other-on'
        $h = Step 'different' 'import' 'C:\qa\history-a.sqlite' 'enter'
        Check $Result '[21] a file from a different receiver names both, and says they can''t be separated' ($h.text -match [regex]::Escape("It came from a different receiver, $here, and the one connected now is Z3805A serial 3625A99999.") -and $h.text -match "can't be separated") "'$($h.text)'"
        Check $Result '[21] and Cancel is its default: Enter imports nothing' ($h.focused -eq 'CloseButton' -and $h.dialogClosed -and -not $h.imported) "focused '$($h.focused)'; closed $($h.dialogClosed); '$($h.imported)'"
        # The pass criterion's own picture: the Overview trend at 7 d, the history from before the
        # uninstall joined to what the reinstalled copy has recorded since, for the agent to judge.
        $trend = Invoke-UiStep $Vm 'history-trend' '' $trendStep
        Check $Result '[#668] the Overview''s 1 PPS readout is drawn whole at the size Details opens at' ($trend.wide -gt 0 -and $trend.narrow -le $trend.wide + 1) "caption $($trend.narrow) px tall there, $($trend.wide) px maximised"
        Check $Result '[21] the Overview trend shown at 7 d, photographed for the agent to judge' ($trend.selected -eq $true) "$(if ($trend.error) { $trend.error } else { "7 d selected $($trend.selected)" })"
        try { Invoke-VmRun $Vm captureScreen -Arguments (Join-Path $Result.Folder 'trend-7d.png') -Guest | Out-Null } catch { }
        try { Copy-QaFile $Vm -Source 'C:\qa\overview-opening.png' -Destination (Join-Path $Result.Folder 'overview-opening.png') } catch { }
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# manual-qa.md section 13: the guide's page screenshots against the application. The pictures are
# taken the way build\Capture-GuideImages.ps1 takes them - an 860 x 778 page area, one column, each
# page photographed at the top and, where it scrolls, at the bottom - and every one is set beside the
# guide's own image for the agent to judge. What a picture shows cannot be checked by a script; that
# the right thing was photographed at the right size can, and is.
$guideStep = @'
$facts = [ordered]@{}
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaGuide {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
    public static int Resolution(int w, int h) {
        DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        EnumDisplaySettings(null, -1, ref dm); dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettings(ref dm, 0);
    }
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Add-Type -AssemblyName System.Drawing

# The window at the guide's size does not fit the VMs' 1024 x 768. The scaling stays at 100 %, so
# the resolution changes without a sign-out.
$facts.resolution = [QaGuide]::Resolution(1600, 1200)
Start-Sleep -Seconds 3

$w = Get-AppWindow
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 2
if (-not $d) {
    $button = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
    if ($button) { Invoke-Control $button } else { Send-KeyTo $w '^d' }
    $d = Get-AppWindowNamed 'Receiver Details' -Seconds 20
}
if (-not $d) { [ordered]@{ error = "no Details window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
$handle = Get-Handle $d
[void][QaGuide]::SetForegroundWindow($handle)

# The page area sized to 860 x 778 from the navigation pane's measured edge, as the capture script
# does, and then measured again: the window is clamped at a minimum content width without saying so.
$pane = Get-Bounds (Find-Control $d -AutomationId 'PaneRoot' -Seconds 10)
$rect = New-Object QaGuide+RECT
[void][QaGuide]::GetWindowRect($handle, [ref]$rect)
$inset = $pane.Left - $rect.Left
$width = ($rect.Right - $rect.Left) + (860 - (($rect.Right - $inset) - $pane.Right))
$height = ($rect.Bottom - $rect.Top) + (778 - (($rect.Bottom - $inset) - $pane.Top))
# Corrected until it is right: one resize landed 96 px short on the first run, the window settling
# after the resolution change underneath it.
foreach ($try in 1..4) {
    [void][QaGuide]::MoveWindow($handle, 40, 20, $width, $height, $true)
    Start-Sleep -Seconds 2
    [void][QaGuide]::GetWindowRect($handle, [ref]$rect)
    $pane = Get-Bounds (Find-Control $d -AutomationId 'PaneRoot' -Seconds 5)
    $areaWidth = ($rect.Right - $inset) - $pane.Right
    $areaHeight = ($rect.Bottom - $inset) - $pane.Top
    if ([Math]::Abs($areaWidth - 860) -le 1 -and [Math]::Abs($areaHeight - 778) -le 1) { break }
    $width += 860 - $areaWidth; $height += 778 - $areaHeight
}
$left = $pane.Right; $top = $pane.Top
$facts.navPane = $pane.Width
$facts.area = "$(($rect.Right - $inset) - $left)x$(($rect.Bottom - $inset) - $top)"

$out = 'C:\qa\guide'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
$buttons = New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$scrollers = New-Object System.Windows.Automation.PropertyCondition($script:Ae::IsScrollPatternAvailableProperty, $true)

function Save-Area([string]$path) {
    # The pointer off the page, so no tooltip is in the picture.
    [QaWin32]::MoveTo(1590, 1190)
    Start-Sleep -Milliseconds 600
    $bmp = New-Object System.Drawing.Bitmap 860, 778
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($left, $top, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# A page's own Refresh button is disabled while it reads, as the capture script found.
function Wait-ForRead {
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $busy = @($d.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttons) | Where-Object { $_.Current.Name -eq 'Refresh' -and -not $_.Current.IsEnabled }).Count
        if (-not $busy) { return $true }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    $false
}

# The page's own scrolling pane: the one whose left edge is the page area's.
function Get-PageScroller {
    $d.FindAll([System.Windows.Automation.TreeScope]::Descendants, $scrollers) | Where-Object {
        $r = $_.Current.BoundingRectangle
        [Math]::Abs($r.Left - $left) -le 4
    } | Sort-Object { $_.Current.BoundingRectangle.Width } -Descending | Select-Object -First 1
}

$pages = [ordered]@{ 'Overview' = 'overview'; 'Satellites' = 'satellites'; 'Position' = 'position'; 'Timing' = 'timing'; 'Holdover' = 'holdover'; 'Time' = 'time'; 'Status Registers' = 'status-registers'; 'Diagnostics' = 'diagnostics'; 'Settings' = 'settings'; 'Advanced Console' = 'advanced-console' }
$taken = @(); $missing = @(); $unread = @()
foreach ($label in $pages.Keys) {
    $name = $pages[$label]
    # The Advanced Console page exists only while its switch is on. Settings is photographed first with
    # it off, as the guide shows it, and then the switch is turned on there, as a person would.
    if ($label -eq 'Advanced Console') {
        $switch = Find-Control $d -AutomationId 'ConsoleSwitch' -Seconds 5
        if ($switch) {
            $toggle = $switch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ("$($toggle.Current.ToggleState)" -ne 'On') { $toggle.Toggle() }
            Start-Sleep -Seconds 2
        }
    }
    if (-not (Select-NavigationItem $d $label)) { $missing += $label; continue }
    Start-Sleep -Seconds 5
    if (-not (Wait-ForRead)) { $unread += $label }
    Save-Area "$out\page-$name.png"; $taken += "page-$name.png"
    # The lower half where there is one; Status Registers has one picture in the guide.
    if ($name -eq 'status-registers') { continue }
    $scroller = Get-PageScroller
    if ($scroller -and $scroller.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticallyScrollable) {
        $pattern = $scroller.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $pattern.SetScrollPercent(-1, 100)
        Start-Sleep -Seconds 1
        Save-Area "$out\page-$name-2.png"; $taken += "page-$name-2.png"
        $pattern.SetScrollPercent(-1, 0)
    }
}
$facts.taken = $taken
$facts.missing = $missing
$facts.unread = $unread
[void][QaGuide]::Resolution(1024, 768)
$facts | ConvertTo-Json -Compress -Depth 4
'@

function Test-GuidePages {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    # The simulator first: an app that finds nothing on the port at launch goes to Disconnected and
    # does not retry, and a step between the two once let it look before the simulator was there.
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    $null = Invoke-QaGuestScript $Vm -Name 'connect-com2' -Script $connectCom2
    try {
        $seen = Wait-AppLog $Vm 'State: LOCK' 0 120 'locked'
        Check $Result 'connected to the simulated receiver, and locked' $seen.found $seen.line
        if (-not $seen.found) { return }
        # Long enough for the trends and the satellite tables to have something in them.
        Start-Sleep -Seconds 60

        $g = Invoke-UiStep $Vm 'guide-pages' '' $guideStep
        if ($g.error) { Check $Result '[13] the pages photographed' $false $g.error; return }
        Check $Result '[13] the page area is the guide''s 860 x 778, with the navigation pane open' ($g.area -match '^(85[89]|86[012])x(77[6-9]|780)$' -and $g.navPane -ge 120) "area $($g.area); navigation pane $($g.navPane) px"

        $guide = Join-Path $repo 'docs\images\how-to-use'
        $expected = @(Get-ChildItem $guide -Filter 'page-*.png' | ForEach-Object Name | Sort-Object)
        $taken = @($g.taken | Sort-Object)
        Check $Result '[13] every page the guide illustrates was photographed, and nothing else' (-not (Compare-Object $expected $taken) -and -not @($g.missing).Count) "taken $($taken.Count) of $($expected.Count); missing pages $(@($g.missing) -join ', '); not in the guide $(@($taken | Where-Object { $expected -notcontains $_ }) -join ', '); in the guide, not taken $(@($expected | Where-Object { $taken -notcontains $_ }) -join ', ')"
        Check $Result '[13] each page had finished reading when photographed' (-not @($g.unread).Count) "still reading: $(@($g.unread) -join ', ')"

        # Each photograph beside the guide's own picture, for the agent to judge.
        $pairs = Join-Path $Result.Folder 'pairs'
        New-Item -ItemType Directory -Force $pairs | Out-Null
        Add-Type -AssemblyName System.Drawing
        foreach ($name in $taken) {
            $now = Join-Path $Result.Folder $name
            try { Copy-QaFile $Vm -Source "C:\qa\guide\$name" -Destination $now } catch { continue }
            $old = Join-Path $guide $name
            if (-not (Test-Path $old)) { continue }
            $a = [System.Drawing.Image]::FromFile($old); $b = [System.Drawing.Image]::FromFile($now)
            try {
                $pair = New-Object System.Drawing.Bitmap ($a.Width + $b.Width + 16), ([Math]::Max($a.Height, $b.Height) + 28)
                $gr = [System.Drawing.Graphics]::FromImage($pair)
                $gr.Clear([System.Drawing.Color]::White)
                $font = New-Object System.Drawing.Font 'Segoe UI', 11
                $gr.DrawString("guide: $name", $font, [System.Drawing.Brushes]::Black, 4, 4)
                $gr.DrawString("now: $($Result.Machine)", $font, [System.Drawing.Brushes]::Black, ($a.Width + 20), 4)
                $gr.DrawImage($a, 0, 28, $a.Width, $a.Height)
                $gr.DrawImage($b, ($a.Width + 16), 28, $b.Width, $b.Height)
                $gr.Dispose(); $font.Dispose()
                $pair.Save((Join-Path $pairs $name), [System.Drawing.Imaging.ImageFormat]::Png); $pair.Dispose()
            }
            finally { $a.Dispose(); $b.Dispose() }
        }
        Check $Result '[13] each photograph set beside the guide''s, for the agent to judge' (@(Get-ChildItem $pairs -Filter *.png).Count -eq $expected.Count) "$(@(Get-ChildItem $pairs -Filter *.png).Count) pairs in $pairs"
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# The screen at 1920 x 1200 so the windows fit beside each other. The scaling stays at 100 %, so the
# resolution changes without a sign-out.
$resolutionStep = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaRes {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
    public static int Set(int w, int h) {
        DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        EnumDisplaySettings(null, -1, ref dm); dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettings(ref dm, 0);
    }
}
"@
New-Item -ItemType Directory -Force 'C:\qa\hc' | Out-Null
[ordered]@{ resolution = [QaRes]::Set(1920, 1200) } | ConvertTo-Json -Compress
'@

# High contrast off again, whatever the scenario got to.
$contrastOff = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaHcOff {
    [StructLayout(LayoutKind.Sequential)] public struct HIGHCONTRAST { public int cbSize; public int dwFlags; public IntPtr lpszDefaultScheme; }
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, ref HIGHCONTRAST hc, int winIni);
    public static bool Off() { var hc = new HIGHCONTRAST(); hc.cbSize = Marshal.SizeOf(hc); return SystemParametersInfo(0x43, hc.cbSize, ref hc, 3); }
}
"@
[QaHcOff]::Off()
'@

# manual-qa.md section 4, A11Y-8: each of the four contrast themes, switched live under the running
# app, measured and photographed. Called with $scheme naming the theme to switch to, and $tag for
# the photographs' names.
$contrastStep = @'
$facts = [ordered]@{}
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaHc {
    [StructLayout(LayoutKind.Sequential)] public struct HIGHCONTRAST { public int cbSize; public int dwFlags; public IntPtr lpszDefaultScheme; }
    // Unicode, or the declaration binds SystemParametersInfoA, reads the scheme name as ANSI and applies
    // the default theme whatever is asked for: the first run switched to the same theme four times.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")] static extern bool SystemParametersInfo(int action, int param, ref HIGHCONTRAST hc, int winIni);
    [DllImport("user32.dll")] public static extern int GetSysColor(int index);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    public static bool Set(bool on, string scheme) {
        var hc = new HIGHCONTRAST(); hc.cbSize = Marshal.SizeOf(hc); hc.dwFlags = on ? 1 : 0;
        hc.lpszDefaultScheme = scheme == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(scheme);
        try { return SystemParametersInfo(0x43, hc.cbSize, ref hc, 3); } finally { if (hc.lpszDefaultScheme != IntPtr.Zero) Marshal.FreeHGlobal(hc.lpszDefaultScheme); }
    }
    public static string Name() {
        var hc = new HIGHCONTRAST(); hc.cbSize = Marshal.SizeOf(hc);
        SystemParametersInfo(0x42, hc.cbSize, ref hc, 0);
        return (hc.dwFlags & 1) == 0 ? "" : Marshal.PtrToStringUni(hc.lpszDefaultScheme);
    }
    public static string Hex(int c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF); }
}
"@
Add-Type -AssemblyName System.Drawing

# Off first, so the scheme named is the one applied, then on with it.
[void][QaHc]::Set($false, $null)
Start-Sleep -Seconds 3
$facts.applied = [QaHc]::Set($true, $scheme)
Start-Sleep -Seconds 10
$facts.active = [QaHc]::Name()
$window = [QaHc]::GetSysColor(5); $text = [QaHc]::GetSysColor(8)
$facts.windowColour = [QaHc]::Hex($window); $facts.textColour = [QaHc]::Hex($text)

function Save-Window($element, [string]$path, $Highlighted, $Subtitle) {
    [void][QaHc]::SetForegroundWindow((Get-Handle $element))
    [QaWin32]::MoveTo(1910, 1190)
    Start-Sleep -Seconds 2
    $r = $element.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.Left, [int]$r.Top, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    # #672: text on the highlight colour must be highlight text. Window-text pixels inside a highlighted
    # control mean the label is on a backplate; none of the four themes uses one colour for both.
    $inside = $null
    if ($Highlighted -and -not $Highlighted.Current.BoundingRectangle.IsEmpty) {
        $hb = $Highlighted.Current.BoundingRectangle; $inside = 0
        $tr0 = $text -band 0xFF; $tg0 = ($text -shr 8) -band 0xFF; $tb0 = ($text -shr 16) -band 0xFF
        for ($y = [int]($hb.Top - $r.Top) + 4; $y -lt [int]($hb.Bottom - $r.Top) - 4; $y++) { for ($x = [int]($hb.Left - $r.Left) + 4; $x -lt [int]($hb.Right - $r.Left) - 4; $x++) {
            if ($x -lt 0 -or $y -lt 0 -or $x -ge $bmp.Width -or $y -ge $bmp.Height) { continue }
            $q = $bmp.GetPixel($x, $y); if ($q.R -eq $tr0 -and $q.G -eq $tg0 -and $q.B -eq $tb0) { $inside++ }
        } }
    }
    # The title bar's subtitle, legible against the window colour: at least 3:1 for enough of its pixels.
    # Turning the framework's adjustment off for the whole application made it vanish in three of the four
    # Windows 11 themes, while every other check here passed (#672).
    $legible = $null
    if ($Subtitle -and -not $Subtitle.Current.BoundingRectangle.IsEmpty) {
        $sb = $Subtitle.Current.BoundingRectangle; $legible = 0
        function Get-Lum([double]$v) { $v /= 255; if ($v -le 0.03928) { $v / 12.92 } else { [Math]::Pow(($v + 0.055) / 1.055, 2.4) } }
        $wl = 0.2126 * (Get-Lum ($window -band 0xFF)) + 0.7152 * (Get-Lum (($window -shr 8) -band 0xFF)) + 0.0722 * (Get-Lum (($window -shr 16) -band 0xFF))
        for ($y = [int]($sb.Top - $r.Top); $y -lt [int]($sb.Bottom - $r.Top); $y++) { for ($x = [int]($sb.Left - $r.Left); $x -lt [int]($sb.Right - $r.Left); $x++) {
            if ($x -lt 0 -or $y -lt 0 -or $x -ge $bmp.Width -or $y -ge $bmp.Height) { continue }
            $q = $bmp.GetPixel($x, $y); $l = 0.2126 * (Get-Lum $q.R) + 0.7152 * (Get-Lum $q.G) + 0.0722 * (Get-Lum $q.B)
            if (([Math]::Max($l, $wl) + 0.05) / ([Math]::Min($l, $wl) + 0.05) -ge 3) { $legible++ }
        } }
    }
    # The window's commonest colour, and how many samples are the theme's text colour.
    $counts = @{}; $textSamples = 0
    $tr = $text -band 0xFF; $tg = ($text -shr 8) -band 0xFF; $tb = ($text -shr 16) -band 0xFF
    for ($y = 0; $y -lt $bmp.Height; $y += 4) { for ($x = 0; $x -lt $bmp.Width; $x += 4) {
        $p = $bmp.GetPixel($x, $y); $k = '#{0:X2}{1:X2}{2:X2}' -f $p.R, $p.G, $p.B
        $counts[$k] = 1 + [int]$counts[$k]
        if ($p.R -eq $tr -and $p.G -eq $tg -and $p.B -eq $tb) { $textSamples++ }
    } }
    $bmp.Dispose()
    [ordered]@{ commonest = ($counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key; textSamples = $textSamples; highlightedWindowText = $inside; subtitleLegible = $legible }
}

$w = Get-AppWindow
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
# The main window alone, Details closed, so nothing owned covers it.
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 1
if ($d) { try { $d.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }; Start-Sleep -Seconds 2 }
[void][QaHc]::MoveWindow((Get-Handle $w), 20, 20, 900, 640, $true)
Start-Sleep -Seconds 2
$facts.main = Save-Window $w "C:\qa\hc\$tag-main.png" (Find-Control $w -AutomationId 'ConnectButton' -Seconds 2)

$button = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
if ($button) { Invoke-Control $button } else { Send-KeyTo $w '^d' }
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 20
if ($d) {
    [void][QaHc]::MoveWindow((Get-Handle $d), 400, 20, 1200, 1000, $true)
    foreach ($page in 'Overview', 'Satellites') {
        [void](Select-NavigationItem $d $page)
        Start-Sleep -Seconds 4
        # The navigation item, not the page's heading of the same name.
        $selected = $d.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)), (New-Object System.Windows.Automation.PropertyCondition($script:Ae::NameProperty, $page)))))
        $subtitle = (Find-Control $d -AutomationId 'AppTitleBar' -Seconds 2).FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($script:Ae::NameProperty, 'Receiver Details')))
        $facts[$page.ToLowerInvariant()] = Save-Window $d "C:\qa\hc\$tag-$($page.ToLowerInvariant()).png" $selected $subtitle
    }
}
$facts.details = [bool]$d
$facts | ConvertTo-Json -Compress -Depth 4
'@

function Test-HighContrast {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    # The simulator first: an app that finds nothing on the port at launch goes to Disconnected and
    # does not retry, and a step between the two once let it look before the simulator was there.
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    $null = Invoke-QaGuestScript $Vm -Name 'connect-com2' -Script $connectCom2
    # The schemes by their internal names, on both systems. Windows 11 shows them as Aquatic, Dusk, Night
    # sky and Desert, but asked for those names it applies High Contrast Black every time, which is what
    # the distinct-themes check below caught; asked for the old names, it applies its own four.
    $build = (Get-Facts $Vm).build
    $schemes = if ($build -ge 22000) { [ordered]@{ aquatic = 'High Contrast #1'; dusk = 'High Contrast #2'; nightsky = 'High Contrast Black'; desert = 'High Contrast White' } }
               else { [ordered]@{ hc1 = 'High Contrast #1'; hc2 = 'High Contrast #2'; black = 'High Contrast Black'; white = 'High Contrast White' } }
    $active = @{}
    try {
        $seen = Wait-AppLog $Vm 'State: LOCK' 0 120 'locked'
        Check $Result 'connected to the simulated receiver, and locked' $seen.found $seen.line
        if (-not $seen.found) { return }
        $null = Invoke-UiStep $Vm 'hc-resolution' '' $resolutionStep
        foreach ($tag in $schemes.Keys) {
            $name = $schemes[$tag]
            $s = Invoke-UiStep $Vm "hc-$tag" "`$scheme = '$name'; `$tag = '$tag'" $contrastStep
            if ($s.error) { Check $Result "[A11Y-8] $name" $false $s.error; continue }
            $active[$tag] = [pscustomobject]@{ Name = $s.active; Window = $s.windowColour; Text = $s.textColour }
            Check $Result "[A11Y-8] $name is on ($tag)" ($s.applied -and $s.active -eq $name) "applied $($s.applied); active '$($s.active)'; window $($s.windowColour), text $($s.textColour)"
            foreach ($part in 'main', 'overview', 'satellites') {
                $m = $s.$part
                if (-not $m) { Check $Result "[A11Y-8] $name, $part" $false 'not photographed'; continue }
                if ($part -ne 'main') {
                    Check $Result "[A11Y-8] $name, $($part): the title bar's subtitle is legible" ($m.subtitleLegible -ge 20) "$($m.subtitleLegible) pixels at 3:1 or better against the window colour"
                }
                if ($null -ne $m.highlightedWindowText) {
                    $what = if ($part -eq 'main') { 'the Connect button''s label' } else { 'the selected navigation item''s label' }
                    Check $Result "[A11Y-8] $name, $($part): $what is highlight text, not on a backplate (#672)" ($m.highlightedWindowText -le 10) "$($m.highlightedWindowText) window-text pixels inside it"
                }
                Check $Result "[A11Y-8] $name, $($part): the app follows the theme live, in its colours" ($m.commonest -eq $s.windowColour -and $m.textSamples -ge 100) "commonest $($m.commonest) against window $($s.windowColour); $($m.textSamples) text-colour samples"
                try { Copy-QaFile $Vm -Source "C:\qa\hc\$tag-$part.png" -Destination (Join-Path $Result.Folder "$tag-$part.png") } catch { }
            }
        }
        # By name, not by colour: three of Windows 10's four have a black window.
        $names = @($active.Values | ForEach-Object Name | Where-Object { $_ } | Select-Object -Unique)
        Check $Result '[A11Y-8] four distinct contrast themes were applied' ($names.Count -eq $schemes.Count) "$(($active.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key): '$($_.Value.Name)' window $($_.Value.Window) text $($_.Value.Text)" }) -join '; ')"
    }
    finally {
        $null = Invoke-QaGuestScript $Vm -Name 'hc-off' -Script $contrastOff
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# manual-qa.md section 3 (A11Y-7): the display scaling set for the next sign-in, as whole-layout does
# for 150 %, with the screen sized so the effective desktop is 1280 x 800 at every scaling. The stored
# placements go too, so each window opens as it would on a display it has never seen. Called with
# $width, $height and $dpi.
$scalingSetStep = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaScale {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
    public static int Set(int w, int h) {
        DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        EnumDisplaySettings(null, -1, ref dm); dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettings(ref dm, 1);
    }
}
"@
$result = [QaScale]::Set($width, $height)
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
$dir = Join-Path $env:LOCALAPPDATA "Packages\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)\LocalCache\Local\WinZ3805A"
Remove-Item (Join-Path $dir 'window.json'), (Join-Path $dir 'details-window.json') -ErrorAction SilentlyContinue
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name LogPixels -Value $dpi -Type DWord
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name Win8DpiScaling -Value 1 -Type DWord
Set-Content -LiteralPath 'C:\qa\force.ps1' -Value "Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name ForceAutoLogon -Value '1' -Type String"
Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\qa\force.ps1'
[ordered]@{ resolution = $result } | ConvertTo-Json -Compress
'@

# Both windows at the scaling the sign-in brought: the app's own title-bar buttons against the
# caption buttons, whose bounds come from the system (DWMWA_CAPTION_BUTTON_BOUNDS) rather than from a
# formula, which is the point section 3 makes; a real drag on the title bar; and a photograph of each.
# Called with $tag for the photographs' names.
$scalingCheckStep = @'
$facts = [ordered]@{}
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaCaption {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out RECT value, int size);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    // The caption buttons' bounds, relative to the window's own top left.
    // DWMWA_EXTENDED_FRAME_BOUNDS: the frame as drawn, without the invisible resize borders.
    public static RECT Frame(IntPtr h) { RECT r; DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))); return r; }
    public static RECT Buttons(IntPtr h) { RECT r; DwmGetWindowAttribute(h, 5, out r, Marshal.SizeOf(typeof(RECT))); return r; }
    [StructLayout(LayoutKind.Sequential)] public struct TITLEBARINFOEX { public int cbSize; public RECT rcTitleBar; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public int[] rgstate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public int[] rgrect; }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref TITLEBARINFOEX l);
    // rgrect holds six rectangles; the fourth is the minimise button, in screen coordinates.
    public static int MinimiseLeft(IntPtr h) { var i = new TITLEBARINFOEX(); i.cbSize = Marshal.SizeOf(typeof(TITLEBARINFOEX)); i.rgstate = new int[6]; i.rgrect = new int[24]; SendMessage(h, 0x033F, IntPtr.Zero, ref i); return i.rgrect[12]; }
}
"@
Add-Type -AssemblyName System.Drawing
$buttonType = New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)

function Measure-Window($element, [string]$label) {
    $m = [ordered]@{}
    $h = Get-Handle $element
    [void][QaCaption]::SetForegroundWindow($h)
    Start-Sleep -Seconds 1
    $m.dpi = [QaCaption]::GetDpiForWindow($h)
    $rect = New-Object QaCaption+RECT; [void][QaCaption]::GetWindowRect($h, [ref]$rect)
    # Opened inside the work area, with nothing stored to restore: the screen less the taskbar. The
    # visible frame, so the invisible resize borders do not count against it.
    $frame = [QaCaption]::Frame($h); $work = [System.Windows.Forms.Screen]::FromHandle($h).WorkingArea
    $m.frame = "$($frame.Left),$($frame.Top) $($frame.Right - $frame.Left)x$($frame.Bottom - $frame.Top)"; $m.work = "$($work.Left),$($work.Top) $($work.Width)x$($work.Height)"
    $m.inside = $frame.Left -ge $work.Left - 1 -and $frame.Top -ge $work.Top - 1 -and $frame.Right -le $work.Right + 1 -and $frame.Bottom -le $work.Bottom + 1
    # The caption buttons from UI Automation's Minimize button; failing that, from the window's own
    # title-bar information. DWMWA_CAPTION_BUTTON_BOUNDS came back empty for these windows (3 Oct 2026).
    $minimise = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($buttonType, (New-Object System.Windows.Automation.PropertyCondition($script:Ae::NameProperty, 'Minimize')))))
    $captionLeft = if ($minimise -and -not $minimise.Current.BoundingRectangle.IsEmpty) { [int]$minimise.Current.BoundingRectangle.Left } else { [QaCaption]::MinimiseLeft($h) }
    $m.captionFrom = if ($minimise) { 'UI Automation' } else { 'WM_GETTITLEBARINFOEX' }
    $bar = Find-Control $element -AutomationId 'AppTitleBar' -Seconds 5
    $own = @(if ($bar) { $bar.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonType) | Where-Object { -not $_.Current.BoundingRectangle.IsEmpty -and $_.Current.Name -notin 'Minimize', 'Maximize', 'Restore', 'Close' } })
    $ownRight = if ($own.Count) { [int](($own | ForEach-Object { $_.Current.BoundingRectangle.Right } | Measure-Object -Maximum).Maximum) } else { 0 }
    $m.captionLeft = $captionLeft; $m.ownRight = $ownRight; $m.ownButtons = $own.Count
    # A title bar with no buttons of its own has nothing to collide with.
    $m.clear = $captionLeft -gt 0 -and ($own.Count -eq 0 -or $ownRight -lt $captionLeft)
    # The drag starts just past the title text - the subtitle on Details - which is drag region by
    # definition. A point that merely looked empty fell in the TitleBar's content area at 200 %, which
    # passes input through, and the window did not move.
    $b = $bar.Current.BoundingRectangle
    $texts = @($bar.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -in 'Receiver Details', (Get-Process -Name WinZ3805A | Select-Object -First 1).MainWindowTitle, 'WinZ3805A' -and -not $_.Current.BoundingRectangle.IsEmpty })
    $point = $null
    if ($texts.Count) {
        $right = ($texts | ForEach-Object { $_.Current.BoundingRectangle.Right } | Measure-Object -Maximum).Maximum
        $point = @([int]($right + 12 * $m.dpi / 96), [int]($b.Top + $b.Height / 2))
    }    if ($point) {
        $before = New-Object QaCaption+RECT; [void][QaCaption]::GetWindowRect($h, [ref]$before)
        [QaWin32]::MoveTo($point[0], $point[1]); Start-Sleep -Milliseconds 300
        [QaWin32]::LeftDown()
        foreach ($i in 1..8) { [QaWin32]::MoveTo($point[0] + 12 * $i, $point[1] + 8 * $i); Start-Sleep -Milliseconds 60 }
        [QaWin32]::LeftUp(); Start-Sleep -Seconds 1
        $after = New-Object QaCaption+RECT; [void][QaCaption]::GetWindowRect($h, [ref]$after)
        $m.moved = "$($after.Left - $before.Left),$($after.Top - $before.Top)"
        $m.dragged = [Math]::Abs(($after.Left - $before.Left) - 96) -le 8 -and [Math]::Abs(($after.Top - $before.Top) - 64) -le 8
    }
    else { $m.moved = 'no empty point on the bar'; $m.dragged = $false }
    [QaWin32]::MoveTo(5, 5); Start-Sleep -Seconds 1
    $r = $element.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.Left, [int]$r.Top, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save("C:\qa\scaling\$tag-$label.png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $m
}

New-Item -ItemType Directory -Force 'C:\qa\scaling' | Out-Null
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process "shell:AppsFolder\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)!App"
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Seconds 2; $w = Get-AppWindow -Seconds 5 } while ((Get-Date) -lt $deadline -and -not ($w -and (Find-Control $w -AutomationId 'ClockText' -Seconds 0)))
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
Start-Sleep -Seconds 5
$facts.main = Measure-Window $w 'main'
$button = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
if ($button) { Invoke-Control $button } else { Send-KeyTo $w '^d' }
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 30
if (-not $d) {
    $other = [QaWin32]::WindowOtherThan([uint32](Get-Process -Name WinZ3805A | Select-Object -First 1).Id, (Get-Handle $w))
    if ($other -ne [IntPtr]::Zero) { $d = $script:Ae::FromHandle($other) }
}
if ($d) { Start-Sleep -Seconds 5; $facts.details = Measure-Window $d 'details' }
$facts | ConvertTo-Json -Compress -Depth 4
'@

function Test-DisplayScaling {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    # The simulator first: an app that finds nothing on the port at launch goes to Disconnected and
    # does not retry, and a step between the two once let it look before the simulator was there.
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    $null = Invoke-QaGuestScript $Vm -Name 'connect-com2' -Script $connectCom2
    $scales = [ordered]@{ '100' = @(1280, 800, 96); '150' = @(1920, 1200, 144); '200' = @(2560, 1600, 192); '225' = @(2880, 1800, 216) }
    try {
        foreach ($scale in $scales.Keys) {
            $w, $h, $dpi = $scales[$scale]
            $null = Invoke-UiStep $Vm "scale-$scale" "`$width = $w; `$height = $h; `$dpi = $dpi" $scalingSetStep
            Invoke-SignOutAndIn $Vm
            $s = Invoke-UiStep $Vm "scaled-$scale" "`$tag = '$scale'" $scalingCheckStep
            if ($s.error) { Check $Result "[3] $scale %" $false $s.error; continue }
            foreach ($part in 'main', 'details') {
                $m = $s.$part
                $name = if ($part -eq 'main') { 'the main window' } else { 'Details' }
                if (-not $m) { Check $Result "[3] $scale %, $name" $false 'not opened'; continue }
                Check $Result "[3] $scale %, $($name): at the display's scaling" ($m.dpi -eq $dpi) "dpi $($m.dpi)"
                Check $Result "[3] $scale %, $($name): opens inside the work area" ($m.inside -eq $true) "frame $($m.frame); work area $($m.work)"
                Check $Result "[3] $scale %, $($name): its title-bar buttons stop short of the caption buttons" ($m.clear -eq $true) "$($m.ownButtons) buttons ending at $($m.ownRight); caption buttons from $($m.captionLeft) ($($m.captionFrom))"
                Check $Result "[3] $scale %, $($name): a drag on the title bar moves the window" ($m.dragged -eq $true) "moved $($m.moved) for a drag of 96,64"
                try { Copy-QaFile $Vm -Source "C:\qa\scaling\$scale-$part.png" -Destination (Join-Path $Result.Folder "$scale-$part.png") } catch { }
            }
        }
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# manual-qa.md section 4, A11Y-6: Windows' text size at 100, 150 and 200 %, at each of §9.6.1's
# breakpoints, which the screen's size sets at 100 % scaling with no sign-out. Called with $text (the
# percentage), $width and $height (the screen), and $tag for the photographs' names.
$textStep = @'
$facts = [ordered]@{}
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaText {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static int Resolution(int w, int h) {
        DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        EnumDisplaySettings(null, -1, ref dm); dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettings(ref dm, 0);
    }
    // What Settings does after writing the value: tell every window the accessibility settings changed.
    public static void Broadcast() { IntPtr r; SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "Accessibility", 2, 5000, out r); }
}
"@
Add-Type -AssemblyName System.Drawing

Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
$facts.resolution = [QaText]::Resolution($width, $height)
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Accessibility' -Name TextScaleFactor -Value $text -Type DWord
[QaText]::Broadcast()
# Each window opens as it would on a display it has never seen at this size.
$dir = Join-Path $env:LOCALAPPDATA "Packages\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)\LocalCache\Local\WinZ3805A"
Remove-Item (Join-Path $dir 'window.json'), (Join-Path $dir 'details-window.json') -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
Start-Process "shell:AppsFolder\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)!App"
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Seconds 2; $w = Get-AppWindow -Seconds 5 } while ((Get-Date) -lt $deadline -and -not ($w -and (Find-Control $w -AutomationId 'ClockText' -Seconds 0)))
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
Start-Sleep -Seconds 5

# At the Minimal breakpoint the navigation pane is closed behind its button, and its items are not
# there to select until it is opened.
function Go-To([string]$label) {
    if (Select-NavigationItem $d $label) { return $true }
    $toggle = Find-Control $d -AutomationId 'TogglePaneButton' -Seconds 2
    if (-not $toggle) { return $false }
    Invoke-Control $toggle; Start-Sleep -Seconds 2
    Select-NavigationItem $d $label
}

function Save-Shot($element, [string]$label) {
    [void][QaText]::SetForegroundWindow((Get-Handle $element))
    [QaWin32]::MoveTo(2, 2); Start-Sleep -Seconds 1
    $r = $element.Current.BoundingRectangle
    $left = [Math]::Max(0, [int]$r.Left); $top = [Math]::Max(0, [int]$r.Top)
    $bmp = New-Object System.Drawing.Bitmap ([Math]::Min([int]$r.Width, $width - $left)), ([Math]::Min([int]$r.Height, $height - $top))
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($left, $top, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save("C:\qa\text\$tag-$label.png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

# How much the clock's text grew, which is the text size reaching the app.
$clock = Find-Control $w -AutomationId 'ClockText' -Seconds 5
# Empty when the row is collapsed, which is what the page does when it has no room for it.
$facts.clockHeight = if ($clock -and -not $clock.Current.BoundingRectangle.IsEmpty) { [int]$clock.Current.BoundingRectangle.Height } else { 0 }
Save-Shot $w 'main'

$button = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
if ($button) { Invoke-Control $button } else { Send-KeyTo $w '^d' }
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 20
if ($d) {
    $facts.detailsWidth = [int]$d.Current.BoundingRectangle.Width
    foreach ($page in 'Overview', 'Settings') {
        $facts["went-$page"] = [bool](Go-To $page)
        Start-Sleep -Seconds 4
        Save-Shot $d $page.ToLowerInvariant()
    }
    # A dialog with lists in it: its buttons must stay on screen, because a dialog that scrolls
    # keeps them and one that truncates loses them.
    $facts.wentSatellites = [bool](Go-To 'Satellites')
    Start-Sleep -Seconds 4
    $manage = Find-Control $d -AutomationId 'ManageButton' -Seconds 5
    if ($manage) {
        try { $manage.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { }
        Invoke-Control $manage
        $close = Find-Control $d -AutomationId 'CloseButton' -Seconds 15
        Start-Sleep -Seconds 3
        if ($close) {
            $cb = $close.Current.BoundingRectangle
            $facts.dialogButton = "$([int]$cb.Left),$([int]$cb.Top) $([int]$cb.Width)x$([int]$cb.Height)"
            $facts.dialogButtonOnScreen = -not $cb.IsEmpty -and $cb.Left -ge 0 -and $cb.Top -ge 0 -and $cb.Right -le $width -and $cb.Bottom -le $height
            Save-Shot $d 'dialog'
            Invoke-Control $close
        }
        else { $facts.dialogButtonOnScreen = $false; $facts.dialogButton = 'no dialog' }
    }
}
$facts.details = [bool]$d
$facts | ConvertTo-Json -Compress -Depth 4
'@

function Test-TextScaling {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    # The simulator first: an app that finds nothing on the port at launch goes to Disconnected and
    # does not retry, and a step between the two once let it look before the simulator was there.
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    $null = Invoke-QaGuestScript $Vm -Name 'text-folder' -Script "New-Item -ItemType Directory -Force 'C:\qa\text' | Out-Null"
    $null = Invoke-QaGuestScript $Vm -Name 'connect-com2' -Script $connectCom2
    # §9.6.1's breakpoints, reached through the screen's size: the Details window is clamped to it.
    $breakpoints = [ordered]@{ medium = @(1280, 800); compact = @(800, 600); minimal = @(640, 480) }
    $clock = @{}
    try {
        $seen = Wait-AppLog $Vm 'State: LOCK' 0 120 'locked'
        Check $Result 'connected to the simulated receiver, and locked' $seen.found $seen.line
        if (-not $seen.found) { return }
        foreach ($text in 100, 150, 200) {
            foreach ($bp in $breakpoints.Keys) {
                $w, $h = $breakpoints[$bp]
                $tag = "$text-$bp"
                $s = Invoke-UiStep $Vm "text-$tag" "`$text = $text; `$width = $w; `$height = $h; `$tag = '$tag'" $textStep
                if ($s.error) { Check $Result "[A11Y-6] text $text %, $bp" $false $s.error; continue }
                if ($bp -eq 'medium') { $clock[$text] = $s.clockHeight }
                Check $Result "[A11Y-6] text $text %, $bp ($w x $h): the Manage dialog's buttons stay on screen" ($s.dialogButtonOnScreen -eq $true) "Close at $($s.dialogButton); Details $($s.detailsWidth) px wide"
                foreach ($part in 'main', 'overview', 'settings', 'dialog') {
                    try { Copy-QaFile $Vm -Source "C:\qa\text\$tag-$part.png" -Destination (Join-Path $Result.Folder "$tag-$part.png") } catch { }
                }
            }
        }
        # The text size really reached the app: the clock's line grows with it at each step. Not in
        # proportion - Windows scales large type less than body text, and the clock grew x1.22 and x1.56
        # at 150 and 200 % - so the check is that it grows, by a tenth at least, each time.
        $ratio150 = if ($clock[100]) { [Math]::Round($clock[150] / $clock[100], 2) } else { 0 }
        $ratio200 = if ($clock[100]) { [Math]::Round($clock[200] / $clock[100], 2) } else { 0 }
        Check $Result '[A11Y-6] the text size reaches the app: the clock line grows with it' ($clock[150] -ge 1.1 * $clock[100] -and $clock[200] -ge 1.1 * $clock[150]) "clock line $($clock[100]) / $($clock[150]) / $($clock[200]) px at 100 / 150 / 200 % (x$ratio150, x$ratio200)"
    }
    finally {
        $null = Invoke-QaGuestScript $Vm -Name 'text-reset' -Script "Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Accessibility' -Name TextScaleFactor -Value 100 -Type DWord"
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# manual-qa.md section 4, A11Y-1, A11Y-2 and A11Y-5 together, because all three are read at a focus
# stop: the keyboard alone walks each surface with Tab, and at each stop the focused control is
# recorded (A11Y-1), the strip around it compared with and without focus for a drawn ring (A11Y-2),
# and its size measured against §9.6.3's 32 px floor (A11Y-5). Called with $surface naming what to
# walk: 'main', or a Details page by its navigation label.
$keyboardStep = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaKeys {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
}
"@
Add-Type -AssemblyName System.Drawing
$Ae = [System.Windows.Automation.AutomationElement]

$w = Get-AppWindow
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
$root = $w
if ($surface -ne 'main') {
    $d = Get-AppWindowNamed 'Receiver Details' -Seconds 2
    if (-not $d) {
        $button = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
        if ($button) { Invoke-Control $button } else { Send-KeyTo $w '^d' }
        $d = Get-AppWindowNamed 'Receiver Details' -Seconds 20
    }
    if (-not $d) { [ordered]@{ error = "no Details window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
    [void](Select-NavigationItem $d $surface)
    Start-Sleep -Seconds 4
    $root = $d
}
$handle = Get-Handle $root
[void][QaKeys]::SetForegroundWindow($handle)
[QaWin32]::MoveTo(2, 2)
Start-Sleep -Seconds 1

function Get-Shot($r) {
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
    $bmp
}
# The pixels on a band either side of the element's edge that differ between two shots of it.
function Get-RingChange($a, $b, [int]$pad) {
    $changed = 0
    for ($y = 0; $y -lt $a.Height; $y++) { for ($x = 0; $x -lt $a.Width; $x++) {
        $inBand = $x -lt 2 * $pad -or $y -lt 2 * $pad -or $x -ge $a.Width - 2 * $pad -or $y -ge $a.Height - 2 * $pad
        if (-not $inBand) { continue }
        $p = $a.GetPixel($x, $y); $q = $b.GetPixel($x, $y)
        if ([Math]::Abs($p.R - $q.R) + [Math]::Abs($p.G - $q.G) + [Math]::Abs($p.B - $q.B) -gt 60) { $changed++ }
    } }
    $changed
}

# Wide enough for focus visuals drawn outside the element, as a toggle switch's are.
$pad = 8
$tag = $surface -replace ' ', ''
New-Item -ItemType Directory -Force 'C:\qa\keys' | Out-Null
$closedOn = ''
$stops = New-Object System.Collections.Generic.List[object]
$seen = @{}
$prev = $null; $prevShot = $null; $prevRect = $null; $lastKey = ''
$firstKey = ''; $unnamed = 0
foreach ($i in 1..400) {
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
    Start-Sleep -Milliseconds 350
    $f = $Ae::FocusedElement
    if (-not $f) { continue }
    $r = $f.Current.BoundingRectangle
    $key = ($f.GetRuntimeId() -join '.')
    # The ring on the previous stop, now that the focus has left it.
    if ($prev -and $prevShot) {
        $after = Get-Shot $prevRect
        $prev.ringPixels = Get-RingChange $prevShot $after $pad
        # The pair kept for the agent to look at wherever no ring was measured.
        if ($prev.ringPixels -lt [Math]::Max(20, $prev.perimeter / 2)) {
            $n = $stops.Count - 1
            $prevShot.Save("C:\qa\keys\$tag-$n-focused.png", [System.Drawing.Imaging.ImageFormat]::Png)
            $after.Save("C:\qa\keys\$tag-$n-unfocused.png", [System.Drawing.Imaging.ImageFormat]::Png)
            $prev.shot = "$tag-$n"
        }
        $after.Dispose(); $prevShot.Dispose(); $prevShot = $null
    }
    # The same element reported for several Tabs running is focus moving through things UI Automation
    # does not expose - selectable text reports its enclosing pane - and is counted, not taken as the end.
    if ($key -eq $lastKey) { $prev.repeats++; continue }
    $lastKey = $key
    # The walk ends when the cycle comes back to where it began. A repeat anywhere else is focus moving
    # through something UI Automation reports as an element already seen - selectable text reports
    # its enclosing pane - and is counted, because stopping there took a page's log for a trap.
    if ($seen.ContainsKey($key) -and $key -ne $firstKey) { $unnamed++; continue }
    if ($seen.ContainsKey($key)) { $closedOn = "$($f.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') '$($f.Current.Name)' [$($f.Current.AutomationId)]"; break }
    $seen[$key] = $true
    if (-not $firstKey) { $firstKey = $key }
    $stop = [ordered]@{
        id = "$($f.Current.AutomationId)"; name = "$($f.Current.Name)"; type = "$($f.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '')"
        left = [int]$r.Left; top = [int]$r.Top; width = [int]$r.Width; height = [int]$r.Height
        ringPixels = -1; perimeter = [int](2 * ($r.Width + $r.Height)); repeats = 0
        parent = ($(try { [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($f).GetRuntimeId() -join '.' } catch { '' }))
    }
    $stops.Add($stop)
    $prev = $stop
    if (-not $r.IsEmpty) {
        $prevRect = New-Object System.Drawing.Rectangle ([int]$r.Left - $pad), ([int]$r.Top - $pad), ([int]$r.Width + 2 * $pad), ([int]$r.Height + 2 * $pad)
        $prevShot = Get-Shot $prevRect
    }
}
if ($prevShot) { $prevShot.Dispose() }

# Every control that says it takes the keyboard, on screen and enabled, against the stops reached.
$focusable = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($Ae::IsKeyboardFocusableProperty, $true)),
    (New-Object System.Windows.Automation.PropertyCondition($Ae::IsEnabledProperty, $true)),
    (New-Object System.Windows.Automation.PropertyCondition($Ae::IsOffscreenProperty, $false)))
# A list, and the navigation, is one Tab stop: its items are reached with the arrow keys from the one
# that has the focus, so an item counts as reached when a sibling in the same list was a stop.
$stopParents = @{}; foreach ($s in $stops) { if ($s.parent) { $stopParents[$s.parent] = $true } }
$missed = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $focusable) | Where-Object { -not $seen.ContainsKey(($_.GetRuntimeId() -join '.')) } |
    Where-Object { -not ($_.Current.ControlType -in [System.Windows.Automation.ControlType]::ListItem, [System.Windows.Automation.ControlType]::TreeItem, [System.Windows.Automation.ControlType]::DataItem -and $stopParents.ContainsKey(([System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($_).GetRuntimeId() -join '.'))) } |
    ForEach-Object { "$($_.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') '$($_.Current.Name)' [$($_.Current.AutomationId)]" })
[ordered]@{ surface = $surface; dpi = 96; stops = $stops; missed = $missed; closedOn = $closedOn; unnamed = $unnamed; tabs = $i } | ConvertTo-Json -Compress -Depth 5
'@

# #684: a toggle switch's target measured by pressing 15 px above and below its centre - outside a 20 px
# band, inside the 32 px floor - and seeing whether the switch changes. Each press that changes it is put
# back with the pattern, not the pointer.
$toggleStep = @'
$facts = [ordered]@{}
$w = Get-AppWindow
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 2
if (-not $d) {
    $button = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
    if ($button) { Invoke-Control $button } else { Send-KeyTo $w '^d' }
    $d = Get-AppWindowNamed 'Receiver Details' -Seconds 20
}
if (-not $d) { [ordered]@{ error = "no Details window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
[void](Select-NavigationItem $d 'Settings')
Start-Sleep -Seconds 3
$switch = Find-Control $d -AutomationId 'ExperimentalSwitch' -Seconds 5
if (-not $switch) { [ordered]@{ error = 'no ExperimentalSwitch' } | ConvertTo-Json -Compress; return }
try { $switch.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { }
Start-Sleep -Seconds 1
$toggle = $switch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$r = $switch.Current.BoundingRectangle
$facts.rect = "$([int]$r.Left),$([int]$r.Top) $([int]$r.Width)x$([int]$r.Height)"
# The switch itself is at the right of the control: press over it.
$x = [int]($r.Right - 20)
function Press-At([int]$y) {
    $before = "$($toggle.Current.ToggleState)"
    [QaWin32]::MoveTo($x, $y); Start-Sleep -Milliseconds 300; [QaWin32]::LeftDown(); [QaWin32]::LeftUp(); Start-Sleep -Seconds 1
    $after = "$($toggle.Current.ToggleState)"
    if ($after -ne $before) { $toggle.Toggle(); Start-Sleep -Milliseconds 500 }
    $after -ne $before
}
$facts.inside = Press-At ([int]($r.Top + $r.Height / 2))
# 15 px either side of the switch's centre: outside the 20 px band the target was (±10) and inside the 32 px
# floor (±16), whatever rectangle the switch reports - after #684's fix it reports 32 px itself.
$middle = [int]($r.Top + $r.Height / 2)
$facts.above = Press-At ($middle - 15)
$facts.below = Press-At ($middle + 15)
[QaWin32]::MoveTo(2, 2)
$facts | ConvertTo-Json -Compress
'@

function Test-KeyboardFocus {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    $null = Invoke-QaGuestScript $Vm -Name 'connect-com2' -Script $connectCom2
    try {
        $seen = Wait-AppLog $Vm 'State: LOCK' 0 120 'locked'
        Check $Result 'connected to the simulated receiver, and locked' $seen.found $seen.line
        if (-not $seen.found) { return }
        $null = Invoke-UiStep $Vm 'keys-resolution' '' $resolutionStep
        # #684: the switches' measured target, so A11Y-5 judges the target and not the rectangle.
        $hit = Invoke-UiStep $Vm 'keys-toggle-target' '' $toggleStep
        $switchReach = $hit.inside -eq $true -and $hit.above -eq $true -and $hit.below -eq $true
        Check $Result '[A11Y-5] a toggle switch takes a press 15 px above and below its centre, so its target is 32 px (#684)' $switchReach "rectangle $($hit.rect); pressed inside $($hit.inside), above $($hit.above), below $($hit.below)$(if ($hit.error) { '; ' + $hit.error })"
        $all = [ordered]@{}
        foreach ($surface in 'main', 'Overview', 'Satellites', 'Position', 'Timing', 'Holdover', 'Time', 'Status Registers', 'Diagnostics', 'Settings') {
            $k = Invoke-UiStep $Vm "keys-$($surface -replace ' ', '')" "`$surface = '$surface'" $keyboardStep
            # Once more when something was missed: the satellite lists fill and empty as the sky turns, and a list
            # that was empty when Tab passed it was not a stop then. A control still missed twice is missed.
            if (-not $k.error -and @($k.missed).Count -gt 0) { $k = Invoke-UiStep $Vm "keys-$($surface -replace ' ', '')-again" "`$surface = '$surface'" $keyboardStep }
            if ($k.error) { Check $Result "[A11Y-1] $surface" $false $k.error; continue }
            $all[$surface] = $k
            $stops = @($k.stops)
            Check $Result "[A11Y-1] $($surface): every control that takes the keyboard is reached by Tab" (@($k.missed).Count -eq 0 -and $stops.Count -gt 0) "$($stops.Count) stops, the cycle closing on $($k.closedOn); missed: $(@($k.missed) -join '; ')"
            # Not the satellite rows: the list re-sorts as the sky turns, so the shot after the focus moves can be of
            # another row, which has its own ring, and the two compare as no ring at all. Their ring is the stock
            # list's, and the crops kept show it.
            $noRing = @($stops | Where-Object { $_.ringPixels -ge 0 -and $_.ringPixels -lt [Math]::Max(20, $_.perimeter / 2) -and -not ($_.type -eq 'ListItem' -and $_.name -like 'PRN *') } | ForEach-Object { "$($_.type) '$($_.name)' [$($_.id)] $($_.ringPixels)/$($_.perimeter)" })
            $swallowed = @($stops | Where-Object { $_.repeats -gt 0 } | ForEach-Object { "$($_.type) '$($_.name)' held the focus for $($_.repeats + 1) Tabs" })
            if ($k.unnamed -gt 0) { $swallowed += "$($k.unnamed) more Tabs landed on elements already seen" }
            Check $Result "[A11Y-1] $($surface): no stretch of Tab presses goes where UI Automation cannot say" ($swallowed.Count -eq 0) "$($k.tabs) Tabs in the cycle. $($swallowed -join '; ')"
            Check $Result "[A11Y-2] $($surface): a focus ring is drawn at every stop" ($noRing.Count -eq 0) "no ring at: $($noRing -join '; ')"
            # §9.10.2 answers the sky-plot markers' flag; their own size is the plot's business.
            $small = @($stops | Where-Object { $_.width -gt 0 -and ($_.width -lt 32 -or $_.height -lt 32) -and $_.name -notlike 'Satellite*' -and $_.name -notlike 'PRN*' -and -not ($switchReach -and $_.id -like '*Switch') } | ForEach-Object { "$($_.type) '$($_.name)' [$($_.id)] $($_.width)x$($_.height)" })
            Check $Result "[A11Y-5] $($surface): every focus stop is at least 32 x 32" ($small.Count -eq 0) "under 32: $($small -join '; ')"
        }
        $all | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Result.Folder 'focus-stops.json')
        $null = Invoke-QaGuestScript $Vm -Name 'keys-zip' -Script "if (Test-Path 'C:\qa\keys') { Compress-Archive -Path 'C:\qa\keys\*' -DestinationPath 'C:\qa\keys.zip' -Force }"
        try { Copy-QaFile $Vm -Source 'C:\qa\keys.zip' -Destination (Join-Path $Result.Folder 'keys.zip'); Expand-Archive (Join-Path $Result.Folder 'keys.zip') -DestinationPath (Join-Path $Result.Folder 'rings') -Force } catch { }
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# manual-qa.md section 4, A11Y-13: with Windows' animation effects off nothing animates. Measured as a
# page change in the Details window, captured as fast as the screen can be read: the frames that are
# neither the page before nor the page after are the transition. With animations on there must be
# some, which proves the capture can see one; with them off there must be none to speak of - a live
# reading arrives once a second and may account for one. Called with $animations ('on' or 'off').
$motionStep = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaMotion {
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] static extern bool Spi(int action, int param, IntPtr value, int winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] static extern bool SpiGet(int action, int param, out int value, int winIni);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    // SPI_SETCLIENTAREAANIMATION: the switch Settings calls Animation effects.
    public static bool Set(bool on) { return Spi(0x1043, 0, on ? (IntPtr)1 : IntPtr.Zero, 3); }
    public static bool Get() { int v; SpiGet(0x1042, 0, out v, 0); return v != 0; }
}
"@
Add-Type -AssemblyName System.Drawing
$facts = [ordered]@{}
$facts.set = [QaMotion]::Set($animations -eq 'on')
$facts.effects = [QaMotion]::Get()
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process "shell:AppsFolder\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)!App"
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Seconds 2; $w = Get-AppWindow -Seconds 5 } while ((Get-Date) -lt $deadline -and -not ($w -and (Find-Control $w -AutomationId 'ClockText' -Seconds 0)))
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
$button = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
if ($button) { Invoke-Control $button } else { Send-KeyTo $w '^d' }
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 20
if (-not $d) { [ordered]@{ error = "no Details window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
[void][QaMotion]::MoveWindow((Get-Handle $d), 100, 50, 1200, 900, $true)
[void][QaMotion]::SetForegroundWindow((Get-Handle $d))
[QaWin32]::MoveTo(2, 2)
Start-Sleep -Seconds 3

# The page area only, a quarter of the pixels, so a frame is read in a few tens of milliseconds.
$pane = Get-Bounds (Find-Control $d -AutomationId 'PaneRoot' -Seconds 5)
$area = New-Object System.Drawing.Rectangle ($pane.Right + 20), ($pane.Top + 60), 700, 500
function Get-Frame {
    $bmp = New-Object System.Drawing.Bitmap $area.Width, $area.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($area.X, $area.Y, 0, 0, $bmp.Size); $g.Dispose()
    $small = New-Object System.Drawing.Bitmap $bmp, 70, 50; $bmp.Dispose()
    $v = New-Object int[] 3500; $i = 0
    for ($y = 0; $y -lt 50; $y++) { for ($x = 0; $x -lt 70; $x++) { $p = $small.GetPixel($x, $y); $v[$i++] = $p.R + $p.G + $p.B } }
    $small.Dispose(); , $v
}
function Get-Distance($a, $b) { $s = 0; for ($i = 0; $i -lt $a.Length; $i++) { $s += [Math]::Abs($a[$i] - $b[$i]) }; $s / $a.Length }

$results = @()
# Overview and Position only: they show stored readings, while a page such as Time reads the receiver when it
# is opened and goes on filling in for a second or more, which reads as frames in between with or without any
# animation - on Windows 11 it did, 44 of 47 with effects off.
foreach ($pair in @(@('Overview', 'Position'), @('Position', 'Overview'), @('Overview', 'Position'))) {
    [void](Select-NavigationItem $d $pair[0]); Start-Sleep -Seconds 3
    $frames = New-Object System.Collections.Generic.List[object]
    $frames.Add((Get-Frame))
    [void](Select-NavigationItem $d $pair[1])
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt 1200) { $frames.Add((Get-Frame)) }
    Start-Sleep -Seconds 3
    $final = Get-Frame
    # In between: far from the page before and from the page after.
    $between = @($frames | Select-Object -Skip 1 | Where-Object { (Get-Distance $_ $frames[0]) -gt 6 -and (Get-Distance $_ $final) -gt 6 }).Count
    $results += [ordered]@{ from = $pair[0]; to = $pair[1]; frames = $frames.Count; between = $between }
}
$facts.changes = $results
[void][QaMotion]::Set($true)
$facts | ConvertTo-Json -Compress -Depth 4
'@

function Test-ReducedMotion {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    $null = Invoke-QaGuestScript $Vm -Name 'connect-com2' -Script $connectCom2
    try {
        $seen = Wait-AppLog $Vm 'State: LOCK' 0 120 'locked'
        Check $Result 'connected to the simulated receiver, and locked' $seen.found $seen.line
        if (-not $seen.found) { return }
        $null = Invoke-UiStep $Vm 'motion-resolution' '' $resolutionStep
        $runs = @{}
        foreach ($mode in 'on', 'off') {
            $m = Invoke-UiStep $Vm "motion-$mode" "`$animations = '$mode'" $motionStep
            if ($m.error) { Check $Result "[A11Y-13] animations $mode" $false $m.error; continue }
            $runs[$mode] = $m
        }
        if ($runs.on -and $runs.off) {
            $on = @($runs.on.changes); $off = @($runs.off.changes)
            $describe = { param($r) ($r | ForEach-Object { "$($_.from)->$($_.to) $($_.between) of $($_.frames)" }) -join ', ' }
            # The control: the capture sees a transition when Windows allows one.
            Check $Result '[A11Y-13] with animation effects on, a page change shows frames in between (the capture can see one)' ($runs.on.effects -and @($on | Where-Object { $_.between -ge 2 }).Count -ge 2) (& $describe $on)
            Check $Result '[A11Y-13] with animation effects off, a page change goes straight from one page to the next' (-not $runs.off.effects -and @($off | Where-Object { $_.between -gt 1 }).Count -eq 0) (& $describe $off)
        }
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# manual-qa.md section 4, A11Y-12: no state is carried by hue alone. Judged, not measured: each state the
# simulated receiver can be put in is photographed - the main window and the Details Overview - and each
# photograph set beside its greyscale, for the agent to read every state from the grey half alone.
# Called with $tag naming the state.
$greyStep = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaGrey {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
}
"@
Add-Type -AssemblyName System.Drawing
$w = Get-AppWindow
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
[void][QaGrey]::MoveWindow((Get-Handle $w), 10, 10, 700, 560, $true)
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 2
if (-not $d) {
    $button = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
    if ($button) { Invoke-Control $button } else { Send-KeyTo $w '^d' }
    $d = Get-AppWindowNamed 'Receiver Details' -Seconds 20
}
if ($d) { [void][QaGrey]::MoveWindow((Get-Handle $d), 720, 10, 1180, 1000, $true); [void](Select-NavigationItem $d 'Overview') }
Start-Sleep -Seconds 4
[QaWin32]::MoveTo(2, 1190)

# The photograph beside its greyscale (luminance, Rec. 709), one image per window.
function Save-Pair($element, [string]$label) {
    [void][QaGrey]::SetForegroundWindow((Get-Handle $element)); Start-Sleep -Seconds 1
    $r = $element.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.Left, [int]$r.Top, 0, 0, $bmp.Size); $g.Dispose()
    $pair = New-Object System.Drawing.Bitmap ($bmp.Width * 2 + 8), $bmp.Height
    $pg = [System.Drawing.Graphics]::FromImage($pair); $pg.Clear([System.Drawing.Color]::Magenta); $pg.DrawImage($bmp, 0, 0, $bmp.Width, $bmp.Height)
    $matrix = New-Object System.Drawing.Imaging.ColorMatrix(,[single[][]]@(
        [single[]]@(0.2126, 0.2126, 0.2126, 0, 0), [single[]]@(0.7152, 0.7152, 0.7152, 0, 0), [single[]]@(0.0722, 0.0722, 0.0722, 0, 0),
        [single[]]@(0, 0, 0, 1, 0), [single[]]@(0, 0, 0, 0, 1)))
    $attributes = New-Object System.Drawing.Imaging.ImageAttributes; $attributes.SetColorMatrix($matrix)
    $pg.DrawImage($bmp, (New-Object System.Drawing.Rectangle ($bmp.Width + 8), 0, $bmp.Width, $bmp.Height), 0, 0, $bmp.Width, $bmp.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    $pg.Dispose(); $bmp.Dispose()
    $pair.Save("C:\qa\grey\$tag-$label.png", [System.Drawing.Imaging.ImageFormat]::Png); $pair.Dispose()
}
New-Item -ItemType Directory -Force 'C:\qa\grey' | Out-Null
Save-Pair $w 'main'
if ($d) { Save-Pair $d 'overview' }
[ordered]@{ details = [bool]$d } | ConvertTo-Json -Compress
'@

function Test-GreyscaleStates {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    $null = Invoke-QaGuestScript $Vm -Name 'connect-com2' -Script $connectCom2
    $control = "$pipe-control"
    $taken = New-Object System.Collections.Generic.List[string]
    function Shoot([string]$tag, [string]$what) {
        $g = Invoke-UiStep $Vm "grey-$tag" "`$tag = '$tag'" $greyStep
        Check $Result "[A11Y-12] $what, photographed in colour and greyscale" (-not $g.error -and $g.details) "$($g.error)"
        foreach ($part in 'main', 'overview') { try { Copy-QaFile $Vm -Source "C:\qa\grey\$tag-$part.png" -Destination (Join-Path $Result.Folder "$tag-$part.png"); $taken.Add("$tag-$part") } catch { } }
    }
    try {
        $null = Invoke-UiStep $Vm 'grey-resolution' '' $resolutionStep
        $seen = Wait-AppLog $Vm 'State: LOCK' 0 120 'locked'
        Check $Result 'connected to the simulated receiver, and locked' $seen.found $seen.line
        if (-not $seen.found) { return }
        Shoot '1-locked' 'Locked'

        $null = Send-SimulatorControl $control 'antenna off'
        $seen = Wait-AppLog $Vm 'State: (WAIT|HOLD)' $seen.count 150 'holdover'
        if ($seen.found) { Shoot '2-holdover' 'Holdover (antenna pulled)' } else { Check $Result '[A11Y-12] holdover reached' $false $seen.tail }

        $null = Send-SimulatorControl $control 'antenna on'
        $seen = Wait-AppLog $Vm 'State: REC' $seen.count 150 'recovery'
        if ($seen.found) { Shoot '3-recovery' 'Recovery' } else { Check $Result '[A11Y-12] recovery reached' $false $seen.tail }
        $seen = Wait-AppLog $Vm 'State: LOCK' $seen.count 150 'relocked'

        $null = Send-SimulatorControl $control 'health ocxo fail'
        Start-Sleep -Seconds 20
        Shoot '4-health-fault' 'A failing health check (OCXO)'
        $null = Send-SimulatorControl $control 'health ocxo ok'

        $null = Send-SimulatorControl $control 'power-cycle'
        $seen = Wait-AppLog $Vm 'State: POW' $seen.count 150 'powerup'
        if ($seen.found) { Shoot '5-powerup' 'Power-up' } else { Check $Result '[A11Y-12] power-up reached' $false $seen.tail }

        $null = Send-SimulatorControl $control 'power off'
        $seen = Wait-AppLog $Vm 'Session COM2 is now Reconnecting' $seen.count 90 'reconnecting'
        if ($seen.found) { Shoot '6-reconnecting' 'Reconnecting (no power)' } else { Check $Result '[A11Y-12] reconnecting reached' $false $seen.tail }
        $null = Send-SimulatorControl $control 'power on'

        Check $Result '[A11Y-12] every state photographed for the agent to read from the greyscale' ($taken.Count -ge 12) "$($taken.Count) photographs: $($taken -join ', ')"
    }
    finally {
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

# A11Y-4's conditions: Windows' app theme ($theme, 'Light' or 'Dark'), a solid wallpaper ($wall, a colour
# name), and a contrast theme ($scheme, or '' for none), set under the running app. With $hues, each of
# them is tried as the wallpaper in turn and the main window's backdrop recorded under each, so the
# caller can choose the one that moves the backdrop furthest towards the text: Mica keeps a wallpaper's
# hue and replaces its lightness, so black and white give the same backdrop and a saturated hue does not.
$contrastSetStep = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaCondition {
    [StructLayout(LayoutKind.Sequential)] public struct HIGHCONTRAST { public int cbSize; public int dwFlags; public IntPtr lpszDefaultScheme; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")] static extern bool SpiHc(int action, int param, ref HIGHCONTRAST hc, int winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")] public static extern bool SpiStr(int action, int param, string value, int winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    public static bool Contrast(bool on, string scheme) {
        var hc = new HIGHCONTRAST(); hc.cbSize = Marshal.SizeOf(hc); hc.dwFlags = on ? 1 : 0;
        hc.lpszDefaultScheme = scheme == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(scheme);
        try { return SpiHc(0x43, hc.cbSize, ref hc, 3); } finally { if (hc.lpszDefaultScheme != IntPtr.Zero) Marshal.FreeHGlobal(hc.lpszDefaultScheme); }
    }
    public static string ContrastName() {
        var hc = new HIGHCONTRAST(); hc.cbSize = Marshal.SizeOf(hc); SpiHc(0x42, hc.cbSize, ref hc, 0);
        return (hc.dwFlags & 1) == 0 ? "" : Marshal.PtrToStringUni(hc.lpszDefaultScheme);
    }
    // What Settings sends after changing the app theme; WinUI follows it live.
    public static void ThemeChanged() { IntPtr r; SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "ImmersiveColorSet", 2, 5000, out r); }
}
"@
Add-Type -AssemblyName System.Drawing
$facts = [ordered]@{}

function Set-Wallpaper([string]$colour) {
    $bmp = New-Object System.Drawing.Bitmap 64, 64; $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::FromName($colour)); $g.Dispose()
    $path = "C:\qa\contrast\wall-$colour.bmp"; $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
    [void][QaCondition]::SpiStr(0x14, 0, $path, 3)
}
# The main window's commonest colour, which is its backdrop: most of it is not card.
function Get-Backdrop {
    $w = Get-AppWindow
    [void][QaCondition]::SetForegroundWindow((Get-Handle $w)); [QaWin32]::MoveTo(2550, 1590); Start-Sleep -Seconds 3
    $r = $w.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.Left, [int]$r.Top, 0, 0, $bmp.Size); $g.Dispose()
    $counts = @{}
    for ($y = 80; $y -lt $bmp.Height; $y += 10) { for ($x = 0; $x -lt $bmp.Width; $x += 10) { $p = $bmp.GetPixel($x, $y); $k = '#{0:X2}{1:X2}{2:X2}' -f $p.R, $p.G, $p.B; $counts[$k] = 1 + [int]$counts[$k] } }
    $bmp.Dispose()
    ($counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key
}

New-Item -ItemType Directory -Force 'C:\qa\contrast' | Out-Null
$w = Get-AppWindow
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
$d = Get-AppWindowNamed 'Receiver Details' -Seconds 1
if ($d) { try { $d.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }; Start-Sleep -Seconds 2 }
[void][QaCondition]::MoveWindow((Get-Handle $w), 40, 40, 1800, 1280, $true)

if ($scheme) {
    [void][QaCondition]::Contrast($false, $null); Start-Sleep -Seconds 3
    $facts.applied = [QaCondition]::Contrast($true, $scheme); Start-Sleep -Seconds 10
}
else {
    if ([QaCondition]::ContrastName()) { [void][QaCondition]::Contrast($false, $null); Start-Sleep -Seconds 5 }
    $light = [int]($theme -eq 'Light')
    $p = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
    Set-ItemProperty $p -Name AppsUseLightTheme -Value $light -Type DWord
    Set-ItemProperty $p -Name SystemUsesLightTheme -Value $light -Type DWord
    [QaCondition]::ThemeChanged(); Start-Sleep -Seconds 4
}
$facts.contrast = [QaCondition]::ContrastName()
$facts.micaLog = [bool](Get-AppLogLines | Where-Object { $_ -match 'Mica is not supported' })
if ($hues) {
    $facts.hues = [ordered]@{}
    foreach ($hue in $hues -split ',') { Set-Wallpaper $hue; Start-Sleep -Seconds 3; $facts.hues[$hue] = Get-Backdrop }
}
Set-Wallpaper $wall; Start-Sleep -Seconds 3
$facts.backdrop = Get-Backdrop
$facts | ConvertTo-Json -Compress -Depth 3
'@

# manual-qa.md section 4, A11Y-4: every piece of text measured on screen against section 9.4.5's floor,
# where the gate cannot read - over Mica, whose backdrop is the user's wallpaper, and under a contrast
# theme, whose colours are the user's. Each text element UI Automation reports on the surface is
# measured in a photograph of the window: its background is the commonest colour in its box, its text
# the most contrasting colour drawn there. Called with $surface ('main' or a Details page by its
# navigation label) and $tag naming the condition, for the evidence's file names.
$contrastMeasureStep = @'
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class QaContrast {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    // Whether the window under a point is the app's: a notification over the window once supplied the
    // pixels of five labels, measured as the app's (QA-Win10, 4 Oct 2026). By process, not by window,
    // because the app's tooltips and flyouts are windows of their own, and its pixels all the same.
    public static bool Owns(IntPtr root, int x, int y) {
        POINT p; p.X = x; p.Y = y; IntPtr h = WindowFromPoint(p); uint a, b;
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(root, out a); GetWindowThreadProcessId(h, out b); return a == b;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    // Another process's always-on-top window over the app's - a Windows Security prompt sat in the
    // corner of QA-Win11 through a whole run - hidden before a photograph, and named for the evidence.
    // The taskbar and the desktop stay; the VM is reverted afterwards anyway.
    public static string HideForeignTopmost(IntPtr root) {
        uint app; GetWindowThreadProcessId(root, out app); RECT a;
        if (root == IntPtr.Zero || !GetWindowRect(root, out a) || a.R - a.L < 2) return "";
        var hidden = new System.Collections.Generic.List<string>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == app || !IsWindowVisible(h) || (GetWindowLong(h, -20) & 0x8) == 0) return true;
            var c = new System.Text.StringBuilder(256); GetClassName(h, c, 256); string cls = c.ToString();
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "Progman" || cls == "WorkerW") return true;
            RECT r; GetWindowRect(h, out r);
            if (r.R <= a.L || r.L >= a.R || r.B <= a.T || r.T >= a.B || r.R - r.L < 2 || r.B - r.T < 2) return true;
            ShowWindow(h, 0); hidden.Add(cls); return true;
        }, IntPtr.Zero);
        return string.Join(", ", hidden);
    }    // What is under a point, for the evidence: the window's class, its top-level window's class, and
    // whether that top-level window is the one being measured.
    public static string Under(IntPtr root, int x, int y) {
        POINT p; p.X = x; p.Y = y; IntPtr h = WindowFromPoint(p); IntPtr top = GetAncestor(h, 2);
        var a = new System.Text.StringBuilder(256); var b = new System.Text.StringBuilder(256);
        GetClassName(h, a, 256); GetClassName(top, b, 256);
        return a + " in " + b + (top == root ? "" : " (another window)");
    }
    static double Lin(int c) { double v = c / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
    public static double Lum(int argb) { return 0.2126 * Lin((argb >> 16) & 0xFF) + 0.7152 * Lin((argb >> 8) & 0xFF) + 0.0722 * Lin(argb & 0xFF); }
    public static double Ratio(double a, double b) { return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05); }
    public static string Hex(int argb) { return string.Format("#{0:X6}", argb & 0xFFFFFF); }
    // The background is the commonest colour, to within 2 bits a channel, averaged over the pixels that
    // are it; the text is the third most contrasting pixel, so one stray pixel cannot pass a box. Ink
    // is how many pixels differ from the background by 1.5:1 or more: under a handful, nothing is drawn.
    public static object[] Measure(Bitmap bmp, int x0, int y0, int w, int h) {
        int x1 = Math.Min(bmp.Width, x0 + w), y1 = Math.Min(bmp.Height, y0 + h);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        if (x1 - x0 < 2 || y1 - y0 < 2) return null;
        var data = bmp.LockBits(new Rectangle(x0, y0, x1 - x0, y1 - y0), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int n = (x1 - x0) * (y1 - y0); var px = new int[n];
        for (int row = 0; row < y1 - y0; row++) Marshal.Copy(data.Scan0 + row * data.Stride, px, row * (x1 - x0), x1 - x0);
        bmp.UnlockBits(data);
        var counts = new Dictionary<int, int>(); int best = 0, bestKey = 0;
        foreach (int p in px) { int k = ((p >> 18) & 0x3F) << 12 | ((p >> 10) & 0x3F) << 6 | ((p >> 2) & 0x3F); int c; counts.TryGetValue(k, out c); counts[k] = ++c; if (c > best) { best = c; bestKey = k; } }
        long r = 0, g = 0, b = 0; int m = 0;
        foreach (int p in px) { int k = ((p >> 18) & 0x3F) << 12 | ((p >> 10) & 0x3F) << 6 | ((p >> 2) & 0x3F); if (k == bestKey) { r += (p >> 16) & 0xFF; g += (p >> 8) & 0xFF; b += p & 0xFF; m++; } }
        int bg = (int)(r / m) << 16 | (int)(g / m) << 8 | (int)(b / m);
        double bgL = Lum(bg);
        var ratios = new double[n]; var fgs = new int[n]; int ink = 0;
        for (int i = 0; i < n; i++) { ratios[i] = Ratio(Lum(px[i]), bgL); fgs[i] = px[i]; if (ratios[i] >= 1.5) ink++; }
        Array.Sort(ratios, fgs); Array.Reverse(ratios); Array.Reverse(fgs);
        int pick = Math.Min(2, n - 1);
        return new object[] { Hex(bg), Hex(fgs[pick]), Math.Round(ratios[pick], 2), ink, Math.Round(100.0 * m / n) };
    }
}
"@
Add-Type -AssemblyName System.Drawing
$Ae = [System.Windows.Automation.AutomationElement]

$w = Get-AppWindow
if (-not $w) { [ordered]@{ error = "no main window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
$root = $w
if ($surface -eq 'main') {
    $d = Get-AppWindowNamed 'Receiver Details' -Seconds 1
    if ($d) { try { $d.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }; Start-Sleep -Seconds 2 }
    [void][QaContrast]::MoveWindow((Get-Handle $w), 40, 40, 1800, 1280, $true)
}
else {
    $d = Get-AppWindowNamed 'Receiver Details' -Seconds 2
    if (-not $d) {
        $button = Find-Control $w -AutomationId 'DetailsButton' -Seconds 5
        if ($button) { Invoke-Control $button } else { Send-KeyTo $w '^d' }
        $d = Get-AppWindowNamed 'Receiver Details' -Seconds 20
    }
    if (-not $d) { [ordered]@{ error = "no Details window ($(Get-WindowReport))" } | ConvertTo-Json -Compress; return }
    # Inside the work area: at 1500 px its foot went behind Windows 10's taskbar.
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    [void][QaContrast]::MoveWindow((Get-Handle $d), 100, 40, 2400, [Math]::Min(1500, $work.Bottom - 50), $true)
    [void](Select-NavigationItem $d $surface)
    Start-Sleep -Seconds 4
    $root = $d
}

# Every text element under the window, in the raw view: a label inside a button is not in the control view.
function Get-Texts($element) {
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $out = New-Object System.Collections.Generic.List[object]
    $stack = New-Object System.Collections.Stack; $stack.Push($element)
    while ($stack.Count -and $out.Count -lt 3000) {
        $e = $stack.Pop()
        try {
            if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text) { $out.Add($e) }
            $c = $walker.GetFirstChild($e)
            while ($c) { $stack.Push($c); $c = $walker.GetNextSibling($c) }
        } catch { }
    }
    $out
}
# Disabled text is exempt (section 9.4.5): the nearest ancestor that is not text says whether it is.
function Test-Enabled($e) {
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $p = $e
    for ($i = 0; $i -lt 6 -and $p; $i++) {
        try { if ($p.Current.ControlType -ne [System.Windows.Automation.ControlType]::Text) { return $p.Current.IsEnabled } } catch { return $true }
        $p = $walker.GetParent($p)
    }
    $true
}
# Large text takes 3:1: 24 px regular, or 18.66 px at semibold (600) and above.
function Get-Large($e) {
    try {
        $range = $e.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange
        $size = $range.GetAttributeValue([System.Windows.Automation.TextPattern]::FontSizeAttribute)
        $weight = $range.GetAttributeValue([System.Windows.Automation.TextPattern]::FontWeightAttribute)
        if ($size -is [double]) {
            # UI Automation gives points; the floor is in pixels at 96 DPI.
            $px = $size * 96 / 72
            return [ordered]@{ px = [Math]::Round($px, 1); weight = "$weight"; large = ($px -ge 24 -or ($px -ge 18.66 -and $weight -is [int] -and $weight -ge 600)) }
        }
    } catch { }
    $h = $e.Current.BoundingRectangle.Height
    [ordered]@{ px = $null; weight = $null; large = ($h -ge 80) }
}

$handle = Get-Handle $root
$seen = @{}
$measured = New-Object System.Collections.Generic.List[object]
$covered = New-Object System.Collections.Generic.List[string]
$script:hiddenWindows = @()
function Measure-View([int]$view) {
    $h = [QaContrast]::HideForeignTopmost($handle); if ($h) { $script:hiddenWindows += $h }
    [void][QaContrast]::SetForegroundWindow($handle)
    [QaWin32]::MoveTo(2550, 1590)
    Start-Sleep -Seconds 2
    $r = $root.Current.BoundingRectangle
    $shot = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($shot); $g.CopyFromScreen([int]$r.Left, [int]$r.Top, 0, 0, $shot.Size); $g.Dispose()
    $shot.Save("C:\qa\contrast\$tag-$($surface -replace ' ', '')-$view.png", [System.Drawing.Imaging.ImageFormat]::Png)
    foreach ($t in (Get-Texts $root)) {
        try {
            $b = $t.Current.BoundingRectangle
            if ($b.IsEmpty -or $b.Width -lt 3 -or $b.Height -lt 3 -or $t.Current.IsOffscreen) { continue }
            $key = "$($t.Current.Name)|$([int]$b.Left)|$([int]($b.Top))"
            $id = ($t.GetRuntimeId() -join '.')
            if ($seen.ContainsKey($id)) { continue }
            if (-not [QaContrast]::Owns($handle, [int]($b.Left + $b.Width / 2), [int]($b.Top + $b.Height / 2))) { $covered.Add("'$($t.Current.Name)' at $([int]$b.Left),$([int]$b.Top) under $([QaContrast]::Under($handle, [int]($b.Left + $b.Width / 2), [int]($b.Top + $b.Height / 2)))"); continue }
            $m = [QaContrast]::Measure($shot, [int]($b.Left - $r.Left), [int]($b.Top - $r.Top), [int]$b.Width, [int]$b.Height)
            if (-not $m -or $m[3] -lt 6) { continue }
            $seen[$id] = $true
            # An unnamed text element that draws something is a glyph - a FontIcon, a NumberBox's spin
            # arrows - and takes the 3:1 floor for icons carrying meaning, not text's.
            $icon = -not "$($t.Current.Name)".Trim()
            $size = Get-Large $t
            $measured.Add([ordered]@{
                text = "$($t.Current.Name)"; id = "$($t.Current.AutomationId)"; view = $view
                left = [int]$b.Left; top = [int]$b.Top; width = [int]$b.Width; height = [int]$b.Height
                background = $m[0]; foreground = $m[1]; ratio = $m[2]; ink = $m[3]; backgroundShare = $m[4]
                px = $size.px; weight = $size.weight; kind = $(if ($icon) { 'icon' } elseif ($size.large) { 'large text' } else { 'text' }); floor = $(if ($icon -or $size.large) { 3.0 } else { 4.5 }); enabled = (Test-Enabled $t)
            })
        } catch { }
    }
    $shot.Dispose()
}

New-Item -ItemType Directory -Force 'C:\qa\contrast' | Out-Null
Measure-View 0
$views = 0
# A page taller than the window is measured a screen at a time, down to its foot.
if ($surface -ne 'main') {
    $scroll = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($Ae::IsScrollPatternAvailableProperty, $true))) |
        Where-Object { try { $_.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticallyScrollable } catch { $false } } |
        Sort-Object { $b = $_.Current.BoundingRectangle; - $b.Width * $b.Height } | Select-Object -First 1)
    if ($scroll) {
        $sp = $scroll[0].GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        for ($view = 1; $view -le 12 -and $sp.Current.VerticalScrollPercent -lt 99.5; $view++) {
            $sp.ScrollVertical([System.Windows.Automation.ScrollAmount]::LargeIncrement); Start-Sleep -Milliseconds 800
            Measure-View $view; $views = $view
        }
        try { $sp.SetScrollPercent(-1, 0) } catch { }
    }
}
$measured | ConvertTo-Json -Depth 3 | Set-Content "C:\qa\contrast\$tag-$($surface -replace ' ', '').json" -Encoding UTF8
$under = @($measured | Where-Object { $_.enabled -and $_.ratio -lt $_.floor } | ForEach-Object { "$($_.kind) '$($_.text)' at $($_.left),$($_.top) $($_.ratio):1 ($($_.foreground) on $($_.background), floor $($_.floor))" })
$lowest = $measured | Where-Object enabled | Sort-Object { $_.ratio / $_.floor } | Select-Object -First 1
[ordered]@{ surface = $surface; measured = $measured.Count; covered = @($covered | Select-Object -Unique); hidden = @($script:hiddenWindows | Select-Object -Unique); views = $views; under = $under; margin = $(if ($lowest) { [Math]::Round($lowest.ratio / $lowest.floor, 3) } else { 99 }); lowest = $(if ($lowest) { "$($lowest.kind) '$($lowest.text)' $($lowest.ratio):1 ($($lowest.foreground) on $($lowest.background), floor $($lowest.floor))" }) } | ConvertTo-Json -Compress -Depth 3
'@

# The app started again after the sign-out that brought 200 %, and the scaling it opened at.
$contrastLaunchStep = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class QaLaunch { [DllImport("user32.dll")] public static extern int GetDpiForWindow(IntPtr h); }
"@
Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process "shell:AppsFolder\$((Get-AppxPackage -Name WinZ3805A).PackageFamilyName)!App"
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Seconds 2; $w = Get-AppWindow -Seconds 5 } while ((Get-Date) -lt $deadline -and -not ($w -and (Find-Control $w -AutomationId 'ClockText' -Seconds 0)))
[ordered]@{ window = [bool]$w; dpi = $(if ($w) { [QaLaunch]::GetDpiForWindow((Get-Handle $w)) } else { 0 }) } | ConvertTo-Json -Compress
'@

function Test-Contrast {
    param($Vm, $Result)
    $pipe = Get-QaSimulatorPipe -Vm $Vm
    if (-not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "\\.\pipe\$pipe" -Quiet)) {
        $Result.Error = "skipped: the VM has no simulator port; run Add-QaSimulatorPort (build/qa/README.md)"
        return
    }
    $build = (Get-Facts $Vm).build
    $mica = $build -ge 22000
    if ($mica -and -not (Select-String -LiteralPath $Vm.Vmx -SimpleMatch 'mks.enable3d = "TRUE"' -Quiet)) {
        $Result.Error = "skipped: Windows 11 draws Mica only with the VM's 3D acceleration on; run Enable-Qa3dGraphics (build/qa/README.md)"
        return
    }
    Send-Zip $Vm $Offline 'candidate'
    $code = Install-Candidate $Vm 'candidate'
    Check $Result 'installed (exit 0, or 2 with the start check timing out)' ($code -in 0, 2) "exit $code"
    $simulator = Start-Process (Get-Simulator) -PassThru -WindowStyle Hidden `
        -ArgumentList '--pipe-client', $pipe, '--start', 'locked', '--speed', '2', '--control', "$pipe-control" `
        -RedirectStandardOutput (Join-Path $Result.Folder 'simulator.log') -RedirectStandardError (Join-Path $Result.Folder 'simulator.err')
    $null = Invoke-QaGuestScript $Vm -Name 'connect-com2' -Script $connectCom2
    $script:contrastCovered = New-Object System.Collections.Generic.List[string]
    $script:contrastHidden = New-Object System.Collections.Generic.List[string]
    $surfaces = 'main', 'Overview', 'Satellites', 'Position', 'Timing', 'Holdover', 'Time', 'Status Registers', 'Diagnostics', 'Settings'
    $hues = 'Red', 'Lime', 'Blue', 'Yellow', 'Cyan', 'Magenta'
    function Get-Luminance([string]$hex) {
        $c = [Convert]::ToInt32($hex.TrimStart('#'), 16)
        $lin = { param($v) $v /= 255; if ($v -le 0.03928) { $v / 12.92 } else { [Math]::Pow(($v + 0.055) / 1.055, 2.4) } }
        0.2126 * (& $lin (($c -shr 16) -band 0xFF)) + 0.7152 * (& $lin (($c -shr 8) -band 0xFF)) + 0.0722 * (& $lin ($c -band 0xFF))
    }
    # Every surface measured under the condition set, and one check for all of them.
    function Measure-Condition([string]$tag, [string]$label) {
        $under = New-Object System.Collections.Generic.List[string]; $count = 0; $lowest = $null
        foreach ($surface in $surfaces) {
            $short = $surface -replace ' ', ''
            $m = Invoke-UiStep $Vm "contrast-$tag-$short" "`$surface = '$surface'; `$tag = '$tag'" $contrastMeasureStep
            if ($m.error) { $under.Add("$($surface): $($m.error)"); continue }
            $count += $m.measured
            foreach ($u in @($m.under)) { if ($u) { $under.Add("$surface $u") } }
            foreach ($c in @($m.covered)) { if ($c) { $script:contrastCovered.Add("$label, $surface $c") } }
            foreach ($h in @($m.hidden)) { if ($h) { $script:contrastHidden.Add($h) } }
            if ($m.lowest -and (-not $lowest -or $m.margin -lt $lowest.margin)) { $lowest = [pscustomobject]@{ margin = $m.margin; text = "$surface $($m.lowest)" } }
            try { Copy-QaFile $Vm -Source "C:\qa\contrast\$tag-$short.json" -Destination (Join-Path $Result.Folder "$tag-$short.json") } catch { }
            if (@($m.under).Count) { foreach ($view in 0..$m.views) { try { Copy-QaFile $Vm -Source "C:\qa\contrast\$tag-$short-$view.png" -Destination (Join-Path $Result.Folder "$tag-$short-$view.png") } catch { } } }
        }
        Check $Result "[A11Y-4] $($label): every piece of text meets its floor" ($under.Count -eq 0 -and $count -ge 200) "$count measured; lowest against its floor: $(if ($lowest) { $lowest.text }); under: $($under -join '; ')"
    }
    try {
        $seen = Wait-AppLog $Vm 'State: LOCK' 0 120 'locked'
        Check $Result 'connected to the simulated receiver, and locked' $seen.found $seen.line
        if (-not $seen.found) { return }
        # Measured at 200 %, an effective 1280 x 800. At 100 % a 12 px glyph's stems are narrower than a
        # pixel, so no pixel is the text's own colour and every small label reads lighter than it is: the
        # sky plot's "N", 5.8:1 by its brush, measured 4.44:1 (4 Oct 2026). At 200 % the stems have cores.
        $null = Invoke-UiStep $Vm 'contrast-scale' "`$width = 2560; `$height = 1600; `$dpi = 192" $scalingSetStep
        # Notifications off for the new session: one over the window supplies pixels that are not the app's.
        $null = Invoke-QaGuestScript $Vm -Name 'contrast-quiet' -Script "Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications' -Name ToastEnabled -Value 0 -Type DWord"
        Invoke-SignOutAndIn $Vm
        $launch = Invoke-UiStep $Vm 'contrast-launch' '' $contrastLaunchStep
        $seen = Wait-AppLog $Vm 'State: LOCK' $seen.count 120 'relocked'
        Check $Result 'at 200 %, the app open and locked again' ($launch.window -and $launch.dpi -eq 192 -and $seen.found) "window $($launch.window), dpi $($launch.dpi); $($seen.line)"
        if (-not $seen.found) { return }
        foreach ($theme in 'Light', 'Dark') {
            $set = Invoke-UiStep $Vm "contrast-$theme-hues" "`$theme = '$theme'; `$wall = 'Gray'; `$scheme = ''; `$hues = '$($hues -join ',')'" $contrastSetStep
            if ($set.error) { Check $Result "[A11Y-4] $theme" $false $set.error; continue }
            $dark = (Get-Luminance $set.backdrop) -lt 0.2
            Check $Result "[A11Y-4] $($theme): the app follows Windows' theme" ($dark -eq ($theme -eq 'Dark')) "backdrop $($set.backdrop)"
            $seenHues = @($hues | ForEach-Object { $set.hues.$_ })
            $moved = @($seenHues | Where-Object { $_ -ne $set.backdrop }).Count
            if ($mica) {
                Check $Result "[A11Y-4] $($theme): Mica is drawn - the backdrop takes the wallpaper's hue" ($moved -ge 4) "grey $($set.backdrop); $(($hues | ForEach-Object { "$_ $($set.hues.$_)" }) -join ', ')"
            }
            else {
                Check $Result "[A11Y-4] $($theme): no Mica here - the app says so, and keeps the solid whatever the wallpaper" ($set.micaLog -and $moved -eq 0) "logged $($set.micaLog); grey $($set.backdrop); $(($hues | ForEach-Object { "$_ $($set.hues.$_)" }) -join ', ')"
            }
            Measure-Condition "$($theme.ToLowerInvariant())-grey" "$theme, $(if ($mica) { 'over Mica on a grey wallpaper' } else { 'on the solid backdrop' })"
            if ($mica) {
                # The hue that moves the backdrop furthest towards the text: the darkest in Light, the lightest in Dark.
                $ranked = $hues | Sort-Object { Get-Luminance $set.hues.$_ }
                $worst = if ($theme -eq 'Light') { $ranked[0] } else { $ranked[-1] }
                $set = Invoke-UiStep $Vm "contrast-$theme-$worst" "`$theme = '$theme'; `$wall = '$worst'; `$scheme = ''; `$hues = ''" $contrastSetStep
                Measure-Condition "$($theme.ToLowerInvariant())-$($worst.ToLowerInvariant())" "$theme, over Mica on a $($worst.ToLowerInvariant()) wallpaper (the hardest of six, backdrop $($set.backdrop))"
            }
        }
        $schemes = if ($build -ge 22000) { [ordered]@{ aquatic = 'High Contrast #1'; dusk = 'High Contrast #2'; nightsky = 'High Contrast Black'; desert = 'High Contrast White' } }
                   else { [ordered]@{ hc1 = 'High Contrast #1'; hc2 = 'High Contrast #2'; black = 'High Contrast Black'; white = 'High Contrast White' } }
        foreach ($name in $schemes.Keys) {
            $set = Invoke-UiStep $Vm "contrast-$name" "`$theme = ''; `$wall = 'Gray'; `$scheme = '$($schemes[$name])'; `$hues = ''" $contrastSetStep
            if ($set.error -or $set.contrast -ne $schemes[$name]) { Check $Result "[A11Y-4] $($schemes[$name]) is on ($name)" $false "$($set.error) active '$($set.contrast)'"; continue }
            Measure-Condition "hc-$name" "$($schemes[$name]) ($name)"
        }
    }
    finally {
        if ($null -ne $script:contrastCovered) { Check $Result '[A11Y-4] every element was measured on its own window''s pixels (nothing covered it)' ($script:contrastCovered.Count -eq 0) "$($script:contrastCovered.Count) covered: $(@($script:contrastCovered | Select-Object -First 12) -join '; '); other processes' windows hidden first: $(@($script:contrastHidden | Select-Object -Unique) -join ', ')" }
        $null = Invoke-QaGuestScript $Vm -Name 'contrast-off' -Script $contrastOff
        if (-not $simulator.HasExited) { $simulator.Kill() }
        $null = Save-Evidence $Vm $Result.Folder
    }
}

$scenarioTable = [ordered]@{
    'fresh-online'     = ${function:Test-FreshOnline}
    'fresh-offline'    = ${function:Test-FreshOffline}
    'upgrade-1.2.0'    = ${function:Test-Upgrade120}
    'leftover-cert'    = ${function:Test-LeftoverCert}
    'repair-damaged'   = ${function:Test-RepairDamaged}
    'app-checks'       = ${function:Test-AppChecks}
    'receiver'         = ${function:Test-Receiver}
    'connect-cancel'   = ${function:Test-ConnectCancel}
    'sign-in'          = ${function:Test-SignIn}
    'pin-compact'      = ${function:Test-PinCompact}
    'whole-layout'     = ${function:Test-WholeLayout}
    'accessibility'    = ${function:Test-Accessibility}
    'sky-export'       = ${function:Test-SkyExport}
    'history-reinstall' = ${function:Test-HistoryReinstall}
    'guide-pages'      = ${function:Test-GuidePages}
    'high-contrast'    = ${function:Test-HighContrast}
    'display-scaling'  = ${function:Test-DisplayScaling}
    'text-scaling'     = ${function:Test-TextScaling}
    'keyboard-focus'   = ${function:Test-KeyboardFocus}
    'reduced-motion'   = ${function:Test-ReducedMotion}
    'greyscale-states' = ${function:Test-GreyscaleStates}
    'contrast'         = ${function:Test-Contrast}
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
