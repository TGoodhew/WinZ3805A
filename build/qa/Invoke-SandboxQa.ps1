<#
.SYNOPSIS
    Runs the installer's Windows 11 QA rows in Windows Sandbox: no VMware, no VMs, nothing to provision.

.DESCRIPTION
    Written for #793, when the move to Artifact Signing needed its install rows run and the QA VMs
    were not on the development machine. Each scenario gets a fresh sandbox, so each starts as a clean
    Windows 11 does: no .NET, no WinZ3805A, nothing of ours in any certificate store - and, it turned
    out, not even the Microsoft root the signature chains to, which makes the no-network scenario the
    sharpest offline test available.

    Scenarios (docs/manual-qa.md section 12):
      offline-no-network  the offline zip with the sandbox's networking disabled: the signature still
                          verifies, one prompt for .NET alone, the app starts
      fresh-online        the online zip: exit 3, no prompt, nothing in Trusted People; then the
                          offline zip over it: one prompt for .NET, the app starts
      tampered            one byte of the bundle changed: refused before anything changes, and told
                          to download again
      upgrade-previous    the previous release installed and given data, then this one: replaced
                          (new publisher) or upgraded in place (same publisher), the data kept, no
                          certificate of any release left; then Remove-WinZ3805A.ps1, -ListOnly first
      leftover-cert       the previous release removed in Settings, then this one: any certificate
                          the previous release left in Trusted People is removed

    WHAT IT CANNOT COVER, and the VM pass (Invoke-QaPass.ps1) still must: Windows 10 - the sandbox is
    always a Windows 11 build - and with it #617's order; the UAC prompt, since the sandbox has UAC
    off; the receiver simulator; anything needing a restart.

    Each sandbox opens a window on the desktop while it runs. Leave it alone; it closes itself.

.PARAMETER Online
    The candidate's online zip.

.PARAMETER Offline
    The candidate's offline zip.

.PARAMETER Previous
    The previous release's offline zip. Defaults to the newest published release older than the
    candidate, downloaded with gh.

.PARAMETER Scenarios
    Which to run. All by default.

.PARAMETER OutDir
    Where the evidence goes, one folder per scenario. Defaults to a new folder under
    %LOCALAPPDATA%\WinZ3805A QA\sandbox.

.EXAMPLE
    .\build\qa\Invoke-SandboxQa.ps1 -Online dist\WinZ3805A-2.0.0.0-x64.zip -Offline dist\WinZ3805A-2.0.0.0-x64-offline.zip
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Online,
    [Parameter(Mandatory)][string]$Offline,
    [string]$Previous,
    [ValidateSet('offline-no-network', 'fresh-online', 'tampered', 'upgrade-previous', 'leftover-cert')]
    [string[]]$Scenarios = @('offline-no-network', 'fresh-online', 'tampered', 'upgrade-previous', 'leftover-cert'),
    [string]$OutDir,
    [int]$MinutesPerScenario = 30
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not (Get-Command wsb -ErrorAction SilentlyContinue)) { throw 'Windows Sandbox (wsb) is not installed. Turn on the "Windows Sandbox" optional feature.' }
if (-not $OutDir) { $OutDir = Join-Path $env:LOCALAPPDATA ('WinZ3805A QA\sandbox\{0:yyyyMMdd-HHmmss}' -f (Get-Date)) }
$OutDir = (New-Item -ItemType Directory -Force $OutDir).FullName
$inRoot = Join-Path $OutDir 'in'
$in = (New-Item -ItemType Directory -Force (Join-Path $inRoot 'zips')).FullName

function Say([string]$Text) { Write-Host ('{0:HH:mm:ss}  {1}' -f (Get-Date), $Text) }

# Windows' publisher ID, the suffix of a package family name (see Invoke-QaPass.ps1).
function Get-PublisherId([string]$Publisher) {
    $hash = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::Unicode.GetBytes($Publisher))
    $bits = (($hash[0..7] | ForEach-Object { [Convert]::ToString($_, 2).PadLeft(8, '0') }) -join '') + '0'
    -join (0..12 | ForEach-Object { '0123456789abcdefghjkmnpqrstvwxyz'[[Convert]::ToInt32($bits.Substring($_ * 5, 5), 2)] })
}

# The family and version a zip installs, read from the bundle inside it.
function Get-ZipIdentity([string]$Zip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $memory = New-Object IO.MemoryStream
        $stream = ($archive.Entries | Where-Object Name -like '*.msixbundle' | Select-Object -First 1).Open()
        try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
        $bundle = New-Object IO.Compression.ZipArchive($memory)
        try {
            $reader = New-Object IO.StreamReader($bundle.GetEntry('AppxMetadata/AppxBundleManifest.xml').Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        finally { $bundle.Dispose() }
    }
    finally { $archive.Dispose() }
    $id = $manifest.Bundle.Identity
    [pscustomobject]@{ Family = "$($id.Name)_$(Get-PublisherId $id.Publisher)"; Version = $id.Version }
}

# The zips, under fixed names the jobs use.
Copy-Item $Online (Join-Path $in 'candidate-online.zip')
Copy-Item $Offline (Join-Path $in 'candidate-offline.zip')
$candidate = Get-ZipIdentity $Offline
if (-not $Previous -and ($Scenarios -contains 'upgrade-previous' -or $Scenarios -contains 'leftover-cert')) {
    function Full([string]$v) { $p = @(($v -replace '^v', '') -split '\.'); while ($p.Count -lt 4) { $p += '0' }; [version]($p[0..3] -join '.') }
    $tag = @(gh release list --repo TGoodhew/WinZ3805A --limit 30 --json tagName,isDraft,isPrerelease -q '.[] | select(.isDraft | not) | select(.isPrerelease | not) | .tagName') |
        Where-Object { $_ -match '^v\d+(\.\d+){1,3}$' -and (Full $_) -lt (Full $candidate.Version) } | Sort-Object { Full $_ } -Descending | Select-Object -First 1
    if (-not $tag) { throw "No published release older than $($candidate.Version) to upgrade from." }
    Say "previous release: $tag"
    gh release download $tag --repo TGoodhew/WinZ3805A -p '*-offline.zip' -D (Join-Path $OutDir 'download') --clobber
    $Previous = (Get-ChildItem (Join-Path $OutDir 'download') -Filter '*-offline.zip' | Select-Object -First 1).FullName
}
if ($Previous) {
    Copy-Item $Previous (Join-Path $in 'previous-offline.zip')
    $previousIdentity = Get-ZipIdentity $Previous
}
Copy-Item (Join-Path $PSScriptRoot 'sandbox\*.ps1') $inRoot
Copy-Item (Join-Path $repo 'build\Remove-WinZ3805A.ps1') $inRoot

# Every certificate a release ever asked people to trust. A candidate passes only if none is left.
$retired = @('7F47E8D75FB856C5A1665900293EDB491E278B3C', '655D07E31BDA80CBF6AAC2F2635EA6664B9399DD')

# ---------------------------------------------------------------------------
# The scenarios: the jobs each runs in its sandbox, in order.
# ---------------------------------------------------------------------------
$jobs = [ordered]@{
    'offline-no-network' = [ordered]@{
        '01-install' = @'
"network $(try { [bool](Invoke-WebRequest -UseBasicParsing https://dotnet.microsoft.com -TimeoutSec 10) } catch { 'unreachable' })"
Save-Facts 'before'
"exit $(Invoke-Install (Expand-Release 'candidate-offline.zip' 'offline') 'install')"
Save-Facts 'after'
'@
    }
    'fresh-online' = [ordered]@{
        '01-online' = @'
"exit $(Invoke-Install (Expand-Release 'candidate-online.zip' 'online') 'online')"
Save-Facts 'online'
'@
        '02-offline-over' = @'
Stop-App
"exit $(Invoke-Install (Expand-Release 'candidate-offline.zip' 'offline') 'offline')"
Save-Facts 'offline'
'@
    }
    'tampered' = [ordered]@{
        '01-tampered' = @'
$f = Expand-Release 'candidate-online.zip' 'tampered'
$b = Get-ChildItem $f -Filter *.msixbundle | Select-Object -First 1
$bytes = [IO.File]::ReadAllBytes($b.FullName); $i = [int]($bytes.Length / 2); $bytes[$i] = $bytes[$i] -bxor 0xFF; [IO.File]::WriteAllBytes($b.FullName, $bytes)
"signature $((Get-AuthenticodeSignature $b.FullName).Status)"
"exit $(Invoke-Install $f 'install')"
Save-Facts 'after'
'@
    }
    'upgrade-previous' = [ordered]@{
        '01-previous' = @'
"exit $(Invoke-Install (Expand-Release 'previous-offline.zip' 'previous') 'previous')"
Start-Sleep -Seconds 10
Stop-App
$pkg = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$d = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\WinZ3805A"
New-Item -ItemType Directory -Force $d | Out-Null
'written under the previous release' | Set-Content (Join-Path $d 'qa-marker.txt')
Save-Facts 'previous'
'@
        '02-candidate' = @'
"exit $(Invoke-Install (Expand-Release 'candidate-online.zip' 'candidate') 'candidate')"
Save-Facts 'candidate'
'@
        '03-remove' = @'
Stop-App
$r = Start-Process powershell.exe -Wait -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','C:\qa\in\Remove-WinZ3805A.ps1','-ListOnly' -RedirectStandardOutput "$out\listonly.txt"
"list-only exit $($r.ExitCode)"
Save-Facts 'listed'
# Save the data: y. Delete the saved copies: n. Then Enter to go ahead, and spares for any other prompt.
$answers = Join-Path $work 'answers.txt'; @('y', 'n', '', '', '', '') | Set-Content $answers
$p = Start-Process cmd.exe -Wait -PassThru -WindowStyle Hidden -ArgumentList '/c', "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\qa\in\Remove-WinZ3805A.ps1 < `"$answers`" > `"$out\remove.txt`" 2>&1"
"remove exit $($p.ExitCode)"
Save-Facts 'removed'
'@
    }
    'leftover-cert' = [ordered]@{
        '01-leftover' = @'
"previous exit $(Invoke-Install (Expand-Release 'previous-offline.zip' 'previous') 'previous')"
Start-Sleep -Seconds 5
Stop-App
Get-AppxPackage -Name WinZ3805A | Remove-AppxPackage
Save-Facts 'between'
"exit $(Invoke-Install (Expand-Release 'candidate-online.zip' 'candidate') 'candidate')"
Save-Facts 'after'
'@
    }
}

# Every running sandbox stopped - its window closed first, because a window left open on a stopped
# sandbox sits on the desktop asking the person to reconnect, which they need not and should not do.
#
# AND ITS SERVER ENDED TOO. WindowsSandboxServer outlives its sandbox by several minutes after `wsb
# stop`, and a sandbox launched while one lingers opens a window and never signs in: every failed
# launch of 22 Nov 2026 had one still running from the sandbox before, and every launch made after it
# had gone signed in within seconds. Once wsb lists no sandbox there is nothing for it to serve.
function Stop-Sandbox {
    Get-Process -Name WindowsSandboxRemoteSession -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    foreach ($running in @((wsb list --raw | ConvertFrom-Json).WindowsSandboxEnvironments)) { wsb stop --id $running.Id | Out-Null }
    $quiet = (Get-Date).AddSeconds(30)
    while (@((wsb list --raw | ConvertFrom-Json).WindowsSandboxEnvironments).Count -and (Get-Date) -lt $quiet) { Start-Sleep -Seconds 2 }
    if (-not @((wsb list --raw | ConvertFrom-Json).WindowsSandboxEnvironments).Count) {
        Get-Process -Name WindowsSandboxServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# One sandbox: mapped folders, the agent at sign-in, wait for every job, stop.
# ---------------------------------------------------------------------------
function Invoke-Box([string]$Name, $Jobs, [switch]$NoNetwork) {
    $box = New-Item -ItemType Directory -Force (Join-Path $OutDir $Name)
    $jobDir = New-Item -ItemType Directory -Force (Join-Path $box 'jobs')
    $boxOut = New-Item -ItemType Directory -Force (Join-Path $box 'out')
    foreach ($job in $Jobs.Keys) { Set-Content (Join-Path $jobDir "$job.ps1") (". C:\qa\in\lib.ps1`r`n" + $Jobs[$job]) -Encoding utf8 }
    $wsb = Join-Path $box 'sandbox.wsb'
    @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>$(if ($NoNetwork) { 'Disable' } else { 'Default' })</Networking>
  <MappedFolders>
    <MappedFolder><HostFolder>$inRoot</HostFolder><SandboxFolder>C:\qa\in</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$jobDir</HostFolder><SandboxFolder>C:\qa\jobs</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$boxOut</HostFolder><SandboxFolder>C:\qa\out</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File C:\qa\in\agent.ps1</Command></LogonCommand>
</Configuration>
"@ | Set-Content $wsb -Encoding utf8

    if (@((wsb list --raw | ConvertFrom-Json).WindowsSandboxEnvironments).Count) { throw 'A Windows Sandbox is already running, and only one can run at a time.' }
    Say "$Name$(if ($NoNetwork) { ', networking disabled' })"
    # A LAUNCH CAN FAIL SILENTLY: the window opens on a black screen and the session never signs in,
    # so the agent never runs. THE CAUSE WAS THE VIRTUAL GPU: on 22 Nov 2026 nine launches in ten hung
    # that way, and the same configuration with <VGpu>Disable</VGpu> - which the .wsb above now sets,
    # since nothing here needs the GPU - signed in within seconds. Before that was found, a lingering
    # WindowsSandboxServer looked like the cause (see Stop-Sandbox), so every launch still
    # first clears the last sandbox completely and waits for its processes to exit; and in case
    # there is another cause, a launch whose agent has not reported in three minutes is stopped and
    # tried again, three times in all.
    $signedIn = { Test-Path (Join-Path $boxOut 'agent.txt') }
    for ($attempt = 1; $attempt -le 3 -and -not (& $signedIn); $attempt++) {
        if ($attempt -gt 1) { Say "  the sandbox did not sign in; starting it again (attempt $attempt)" }
        Stop-Sandbox
        $gone = (Get-Date).AddMinutes(2)
        while ((Get-Process -Name vmmemWindowsSandbox, WindowsSandboxServer, WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) -and (Get-Date) -lt $gone) { Start-Sleep -Seconds 3 }
        Start-Sleep -Seconds 10
        Start-Process (Join-Path $env:windir 'System32\WindowsSandbox.exe') -ArgumentList "`"$wsb`""
        $signIn = (Get-Date).AddMinutes(3)
        while (-not (& $signedIn) -and (Get-Date) -lt $signIn) { Start-Sleep -Seconds 5 }
    }
    if (-not (& $signedIn)) {
        Stop-Sandbox
        throw "The sandbox for $Name never signed in, in three attempts."
    }
    $deadline = (Get-Date).AddMinutes($MinutesPerScenario)
    try {
        while ((Get-Date) -lt $deadline -and @($Jobs.Keys | Where-Object { -not (Test-Path (Join-Path $boxOut "$_.done")) }).Count) { Start-Sleep -Seconds 10 }
    }
    finally {
        Stop-Sandbox
        $gone = (Get-Date).AddMinutes(2)
        while ((Get-Process -Name vmmemWindowsSandbox -ErrorAction SilentlyContinue) -and (Get-Date) -lt $gone) { Start-Sleep -Seconds 3 }
    }
    $boxOut.FullName
}

# Reading the evidence back.
function Read-Facts([string]$Dir, [string]$Tag) { Get-Content (Join-Path $Dir "$Tag-facts.json") -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json }
function Read-Text([string]$Dir, [string]$Name) { (Get-Content (Join-Path $Dir $Name) -ErrorAction SilentlyContinue) -join "`n" }
function Get-ExitCode([string]$Dir, [string]$Job) { if ((Read-Text $Dir "$Job.txt") -match 'exit (-?\d+)\s*$') { [int]$Matches[1] } else { $null } }
function Test-NoRetired($Facts) { -not (@($Facts.machineTP) + @($Facts.userTP) | Where-Object { $retired -contains $_ }) }

$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$Scenario, [string]$Label, [bool]$Ok, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ Scenario = $Scenario; Ok = $Ok; Label = $Label; Detail = $Detail })
    Write-Host ('          {0}  {1}{2}' -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Label, $(if ($Detail) { " - $Detail" })) -ForegroundColor $(if ($Ok) { 'Green' } else { 'Red' })
}

# ---------------------------------------------------------------------------
# Run, and judge.
# ---------------------------------------------------------------------------
foreach ($name in $Scenarios) {
    # A sandbox that never signs in fails its own scenario, not the run: the rest still tell you
    # something, and the one that failed can be run again on its own with -Scenarios.
    try { $o = Invoke-Box $name $jobs[$name] -NoNetwork:($name -eq 'offline-no-network') }
    catch { Check $name 'the sandbox started and signed in' $false "$($_.Exception.Message) Run it again with -Scenarios $name."; continue }
    switch ($name) {
        'offline-no-network' {
            $after = Read-Facts $o 'after'; $log = Read-Text $o 'install-install.log'
            Check $name 'the network really was off' ((Read-Text $o '01-install.txt') -match 'network unreachable')
            Check $name 'exit 0, and the start check found the app running' ((Get-ExitCode $o '01-install') -eq 0 -and $log -match 'started ok: True')
            Check $name 'the signature verified with no network' ($log -match 'signing\s+Valid,') ((Read-Facts $o 'before').idvRoot -replace '^', 'Microsoft root in the store beforehand: ')
            Check $name 'one administrator prompt, for .NET alone' (([regex]::Matches($log, 'elevating for:')).Count -eq 1 -and $log -match '(?m)elevating for: \.NET\s*$')
            Check $name "installed as $($candidate.Family), nothing in Trusted People" (@($after.packages) -join ',' -eq $candidate.Family -and -not @($after.machineTP).Count) (@($after.packages) -join ', ')
        }
        'fresh-online' {
            $onlineFacts = Read-Facts $o 'online'; $log = Read-Text $o 'online-install.log'; $log2 = Read-Text $o 'offline-install.log'
            Check $name 'online zip: exit 3 (installed, .NET missing)' ((Get-ExitCode $o '01-online') -eq 3)
            Check $name 'online zip: no administrator prompt' (-not ($log -match 'elevating for:'))
            Check $name "online zip: installed as $($candidate.Family), nothing in Trusted People" (@($onlineFacts.packages) -join ',' -eq $candidate.Family -and -not @($onlineFacts.machineTP).Count)
            Check $name 'offline zip over it: exit 0, one prompt for .NET, the app running' ((Get-ExitCode $o '02-offline-over') -eq 0 -and $log2 -match '(?m)elevating for: \.NET\s*$' -and $log2 -match 'started ok: True')
        }
        'tampered' {
            $after = Read-Facts $o 'after'
            Check $name 'the changed bundle fails its signature check' ((Read-Text $o '01-tampered.txt') -match 'signature HashMismatch')
            Check $name 'exit 1, with nothing installed' ((Get-ExitCode $o '01-tampered') -eq 1 -and -not @($after.packages).Count)
            Check $name 'the person is told to download again' ((Read-Text $o 'install-console.txt') -match 'not the file that was signed')
        }
        'upgrade-previous' {
            $prev = Read-Facts $o 'previous'; $cand = Read-Facts $o 'candidate'; $listed = Read-Facts $o 'listed'; $removed = Read-Facts $o 'removed'
            $log = Read-Text $o 'candidate-install.log'
            $replaced = $previousIdentity.Family -ne $candidate.Family
            Check $name "the previous release ($($previousIdentity.Version)) installed, with the marker in its data" ((Get-ExitCode $o '01-previous') -eq 0 -and (@($prev.data | Where-Object { $_.files -contains 'qa-marker.txt' }).Count -eq 1))
            Check $name 'this release: exit 0, the app running' ((Get-ExitCode $o '02-candidate') -eq 0 -and $log -match 'started ok: True')
            Check $name "one copy, $($candidate.Family) $($candidate.Version)" ((@($cand.packages) -join ',') -eq $candidate.Family -and (@($cand.versions) -join ',') -eq $candidate.Version) (@($cand.packages) -join ', ')
            Check $name 'the marker came across into the new copy' (@($cand.data | Where-Object { $_.family -eq $candidate.Family -and $_.files -contains 'qa-marker.txt' }).Count -eq 1)
            if ($replaced) { Check $name 'a change of publisher: the earlier copy saved aside and replaced' ($log -match 'Replacing the earlier copy|Removing the earlier copy' -and @($cand.savedCopies -like 'WinZ3805A earlier copy*').Count -ge 1) }
            else { Check $name 'the same publisher: upgraded in place, nothing saved aside' (-not ($log -match 'Replacing the earlier copy|Removing the earlier copy')) }
            Check $name 'no certificate of any release in Trusted People' (Test-NoRetired $cand) ((@($cand.machineTP) + @($cand.userTP)) -join ', ')
            Check $name 'Remove-WinZ3805A -ListOnly changes nothing' ((@($listed.packages) -join ',') -eq (@($cand.packages) -join ',') -and (@($listed.savedCopies) -join ',') -eq (@($cand.savedCopies) -join ','))
            Check $name 'Remove-WinZ3805A: no package left, the data saved to Documents' (-not @($removed.packages).Count -and @($removed.savedCopies -like 'WinZ3805A saved data*').Count -ge 1) (@($removed.savedCopies) -join ', ')
        }
        'leftover-cert' {
            $between = Read-Facts $o 'between'; $after = Read-Facts $o 'after'
            $left = @(@($between.machineTP) | Where-Object { $retired -contains $_ })
            Check $name 'the previous release removed in Settings' (-not @($between.packages).Count) "left in Trusted People: $(if ($left) { $left -join ', ' } else { 'nothing' })"
            Check $name 'this release: exit 0' ((Get-ExitCode $o '01-leftover') -eq 0)
            Check $name 'no certificate of any release left in Trusted People' (Test-NoRetired $after) ((@($after.machineTP) + @($after.userTP)) -join ', ')
        }
    }
}

$failed = @($results | Where-Object { -not $_.Ok })
$results | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutDir 'results.json')
Say "$($results.Count - $failed.Count) of $($results.Count) checks passed. Evidence: $OutDir"
if ($failed.Count) { exit 1 }
