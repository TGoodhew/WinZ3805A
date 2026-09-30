<#
.SYNOPSIS
    Measures how a sideloaded WinZ3805A behaves when it is reinstalled, re-registered, repaired,
    reset, or deliberately broken - the facts #597's repair design depends on.

.DESCRIPTION
    #597 asks how the installer should rectify a broken install. Its plan's first step is to measure
    rather than assume, because four things the design turns on are Windows' behaviour, not this
    project's, and none of them is written down anywhere this project can cite:

      1. What installing the SAME version again does - nothing, or a real redeploy.
      2. What Settings > Apps > Advanced options > Repair and Reset do for a sideloaded package,
         and whether Reset deletes the history.
      3. Whether re-registering from the package's own folder keeps its data.
      4. Which broken states the installer's 15-second start check (#592) actually catches -
         in particular whether a missing .NET, which leaves the process alive on Windows' own
         prompt, reads as "running".

    FOR A TEST MACHINE ONLY. It removes a runtime companion and uninstalls .NET 10 to see what
    breaks, and puts both back; its last step RESETS the app, which deletes its data. Run it on a
    VM holding a packaged WinZ3805A installed by Install.cmd - not a development registration -
    from an ELEVATED PowerShell (the .NET step needs administrator rights).

    Each scenario records the package's identity and status, whether a marker file in the app's
    data folder survived, and a start check: launch, wait, and see whether the process is alive,
    whether it has a window, and whether it wrote to its own log after the launch. The last of
    those is the honest signal - a process can be alive on an error dialog.

    Everything goes to a report on the Desktop, which is what to attach to #597.

.PARAMETER Bundle
    The .msixbundle from an extracted release zip, of the SAME version as the one installed. Used
    to reinstall it. Without it, scenario B is skipped.

.PARAMETER DotNetInstaller
    Microsoft's dotnet-runtime-<version>-win-x64.exe - the one in the offline zip's Runtime folder -
    used to put .NET back after scenario F. Without it, scenario F asks before uninstalling, and
    .NET has to be put back by hand from dotnet.microsoft.com.

.PARAMETER Skip
    Scenario letters to leave out, for example -Skip F,G.

.NOTES
    Written 30 Sep 2026 for #597. Committed as run, like the other harnesses under build/, because
    what it measured is only as good as what it did.
#>
[CmdletBinding()]
param(
    [string]$Bundle,
    [string]$DotNetInstaller,
    [string[]]$Skip = @()
)

$ErrorActionPreference = 'Stop'

# powershell.exe -File passes "-Skip C,D" as ONE string, "C,D", not an array - it does not parse
# arguments as PowerShell would - so a run meant to skip five scenarios skipped none (30 Sep 2026).
$Skip = @($Skip | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ })

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = Join-Path ([Environment]::GetFolderPath('Desktop')) "repair-measurements-$stamp.txt"

function Write-Report {
    param([string]$Text = '')
    Write-Host $Text
    Add-Content -LiteralPath $report -Value $Text -Encoding UTF8
}

function Get-App {
    Get-AppxPackage -Name 'WinZ3805A' | Where-Object { $_.SignatureKind -ne 'Store' } | Select-Object -First 1
}

function Get-DataFolder {
    param($App)
    Join-Path $env:LOCALAPPDATA "Packages\$($App.PackageFamilyName)\LocalCache\Local\WinZ3805A"
}

function Write-Package {
    param([string]$Label)
    $app = Get-App
    if (-not $app) { Write-Report "  [$Label] package: NOT INSTALLED"; return }
    $manifest = Join-Path $app.InstallLocation 'AppxManifest.xml'
    Write-Report ("  [$Label] package: {0}, status {1}, signature {2}, development {3}" -f
        $app.PackageFullName, $app.Status, $app.SignatureKind, $app.IsDevelopmentMode)
    Write-Report ("  [$Label] install folder created {0}, manifest written {1}" -f
        (Get-Item $app.InstallLocation).CreationTime.ToString('HH:mm:ss'),
        (Get-Item $manifest).LastWriteTime.ToString('HH:mm:ss'))
}

function Set-Marker {
    $app = Get-App
    $data = Get-DataFolder $app
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $data 'repair-marker.txt') -Value "written $(Get-Date -Format o)"
    $trend = Join-Path $data 'trend.db'
    Write-Report "  marker written; trend.db present: $(Test-Path $trend)"
}

function Test-Marker {
    param([string]$Label)
    $app = Get-App
    if (-not $app) { Write-Report "  [$Label] data: package gone"; return }
    $data = Get-DataFolder $app
    Write-Report ("  [$Label] data: marker {0}, trend.db {1}, files {2}" -f
        $(if (Test-Path (Join-Path $data 'repair-marker.txt')) { 'KEPT' } else { 'GONE' }),
        $(if (Test-Path (Join-Path $data 'trend.db')) { 'present' } else { 'absent' }),
        @(Get-ChildItem $data -Recurse -File -ErrorAction SilentlyContinue).Count)
}

function Stop-App {
    Get-Process -Name 'WinZ3805A' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# The installer's start check, and the stronger signals beside it.
function Test-Start {
    param([string]$Label)
    Stop-App
    $app = Get-App
    if (-not $app) { Write-Report "  [$Label] start: nothing to start"; return }

    $appLog = Join-Path (Get-DataFolder $app) 'logs\app.log'
    $launched = Get-Date
    try {
        Start-Process "shell:AppsFolder\$($app.PackageFamilyName)!App"
    }
    catch {
        Write-Report "  [$Label] start: could not launch - $($_.Exception.Message)"
        return
    }
    Start-Sleep -Seconds 15

    $processes = @(Get-Process -Name 'WinZ3805A' -ErrorAction SilentlyContinue)
    $logged = (Test-Path $appLog) -and ((Get-Item $appLog).LastWriteTime -gt $launched)
    $windows = @($processes | Where-Object { $_.MainWindowHandle -ne 0 } | ForEach-Object { "'$($_.MainWindowTitle)'" })

    # Anything else on screen that could be Windows' own .NET prompt for this app.
    $dialogs = @(Get-Process | Where-Object { $_.MainWindowTitle -match '\.NET|WinZ3805A' -and $_.ProcessName -ne 'WinZ3805A' } |
        ForEach-Object { "$($_.ProcessName): '$($_.MainWindowTitle)'" })

    Write-Report ("  [$Label] start: installer's check would say {0}; processes {1}; windows {2}; app.log written since launch {3}{4}" -f
        $(if ($processes.Count) { 'RUNNING' } else { 'NOT RUNNING' }),
        $processes.Count,
        $(if ($windows.Count) { $windows -join ', ' } else { 'none' }),
        $logged,
        $(if ($dialogs.Count) { "; other windows: $($dialogs -join ', ')" } else { '' }))
    Stop-App
}

function Invoke-Scenario {
    param([string]$Letter, [string]$Title, [scriptblock]$Body)
    Write-Report ''
    Write-Report "== $Letter. $Title"
    if ($Skip -contains $Letter) { Write-Report '  skipped'; return }
    try { & $Body }
    catch { Write-Report "  FAILED: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------------------
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$os = Get-CimInstance Win32_OperatingSystem

Write-Report "WinZ3805A repair measurements, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
Write-Report "windows $($os.Caption) $($os.Version) build $($os.BuildNumber); powershell $($PSVersionTable.PSVersion); elevated $admin"
Write-Report "report $report"

if (-not (Get-App)) { throw 'No sideloaded WinZ3805A is installed. Install one with Install.cmd first.' }
if ((Get-App).IsDevelopmentMode) { throw 'This is a development registration. Measure a copy installed by Install.cmd.' }

Invoke-Scenario 'A' 'Baseline' {
    Write-Package 'before'
    Set-Marker
    Test-Start 'baseline'
}

Invoke-Scenario 'B' 'Installing the same version again' {
    if (-not $Bundle) { Write-Report '  skipped: no -Bundle given'; return }
    Stop-App
    foreach ($variant in @(@{ Name = 'plain'; Args = @{} }, @{ Name = 'ForceUpdateFromAnyVersion'; Args = @{ ForceUpdateFromAnyVersion = $true } })) {
        $arguments = $variant.Args
        try {
            Add-AppxPackage -Path $Bundle @arguments -ErrorAction Stop
            Write-Report "  $($variant.Name): Add-AppxPackage returned without error"
        }
        catch {
            Write-Report "  $($variant.Name): Add-AppxPackage threw: $($_.Exception.Message -replace '\s+', ' ')"
        }
        Write-Package $variant.Name
        Test-Marker $variant.Name
    }
    Test-Start 'after reinstall'
}

Invoke-Scenario 'C' 'Re-registering from the package''s own folder' {
    Stop-App
    $app = Get-App
    Add-AppxPackage -Register (Join-Path $app.InstallLocation 'AppxManifest.xml') -DisableDevelopmentMode -ForceApplicationShutdown -ErrorAction Stop
    Write-Report '  Add-AppxPackage -Register ... -DisableDevelopmentMode returned without error'
    Write-Package 'after'
    Test-Marker 'after'
    Test-Start 'after re-register'
}

Invoke-Scenario 'D' 'Settings > Apps > Advanced options: Repair (by hand)' {
    Stop-App
    Set-Marker
    $app = Get-App
    try { Start-Process "ms-settings:appsfeatures-app?$($app.PackageFamilyName)" } catch { }
    Write-Host ''
    Write-Host '  Settings should now show WinZ3805A''s Advanced options. If not: Settings > Apps >' -ForegroundColor Yellow
    Write-Host '  Installed apps > WinZ3805A > ... > Advanced options.' -ForegroundColor Yellow
    $shows = Read-Host '  Does it offer Repair and Reset? (both / repair / reset / neither)'
    Write-Report "  offered: $shows"
    if ($shows -match 'both|repair') {
        Read-Host '  Click Repair, wait for the tick, then press Enter here'
        Write-Report '  Repair clicked'
        Write-Package 'after Repair'
        Test-Marker 'after Repair'
        Test-Start 'after Repair'
    }
}

Invoke-Scenario 'E' 'Runtime companion missing (Main)' {
    Stop-App
    $main = Get-AppxPackage -Name 'MicrosoftCorporationII.WinAppRuntime.Main.2' | Where-Object Architecture -eq 'X64' | Select-Object -First 1
    if (-not $main) { Write-Report '  no Main.2 companion installed to remove'; return }
    Write-Report "  removing $($main.PackageFullName) ($($main.SignatureKind))"
    Remove-AppxPackage -Package $main.PackageFullName -ErrorAction Stop
    Write-Report "  removed; Main.2 present now: $([bool](Get-AppxPackage -Name 'MicrosoftCorporationII.WinAppRuntime.Main.2'))"
    Test-Start 'companion missing'

    # Put it back from the newest framework's own copy, as install.ps1 does.
    $framework = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2' | Where-Object Architecture -eq 'X64' |
        Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1
    Add-AppxPackage -Path (Join-Path $framework.InstallLocation 'MSIX\Main.msix') -ForceUpdateFromAnyVersion -ErrorAction Stop
    Write-Report "  restored from $($framework.Version); Main.2 now $((Get-AppxPackage -Name 'MicrosoftCorporationII.WinAppRuntime.Main.2' | Where-Object Architecture -eq 'X64').Version)"
    Test-Start 'companion restored'
}

Invoke-Scenario 'F' '.NET 10 missing' {
    if (-not $admin) { Write-Report '  skipped: needs an elevated PowerShell'; return }
    Stop-App
    # Microsoft's runtime installer registers a BUNDLE, whose BundleCachePath is a cached copy that
    # uninstalls with /uninstall. .NET that came with Visual Studio or the SDK has only MSI entries,
    # keyed by product code, which msiexec /x removes. The bundle is preferred when both exist.
    $entry = Get-ChildItem 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
                         'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        Get-ItemProperty -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'Microsoft .NET Runtime - 10.*(x64)*' } |
        Sort-Object { [bool]$_.BundleCachePath } -Descending |
        Select-Object -First 1
    if (-not $entry) { Write-Report '  no .NET 10 runtime installation found to remove'; return }
    if (-not $DotNetInstaller) {
        $go = Read-Host "  No -DotNetInstaller given, so .NET will have to be reinstalled by hand. Uninstall '$($entry.DisplayName)' anyway? (y/n)"
        if ($go -ne 'y') { Write-Report '  skipped at the prompt'; return }
    }
    Write-Report "  uninstalling $($entry.DisplayName) ($(if ($entry.BundleCachePath) { 'bundle' } else { "msi $($entry.PSChildName)" }))"
    $removal = if ($entry.BundleCachePath) {
        Start-Process -FilePath $entry.BundleCachePath -ArgumentList '/uninstall', '/quiet', '/norestart' -Wait -PassThru
    }
    else {
        Start-Process -FilePath 'msiexec.exe' -ArgumentList '/x', $entry.PSChildName, '/quiet', '/norestart' -Wait -PassThru
    }
    Write-Report "  uninstaller exited $($removal.ExitCode); runtimes left: $((Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.NETCore.App" -Directory -ErrorAction SilentlyContinue).Name -join ', ')"
    Test-Start '.NET missing'

    if ($DotNetInstaller) {
        $install = Start-Process -FilePath $DotNetInstaller -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        Write-Report "  reinstalled .NET: exit $($install.ExitCode); runtimes: $((Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.NETCore.App" -Directory).Name -join ', ')"
        Test-Start '.NET restored'
    }
    else {
        Write-Report '  .NET NOT restored: install it from https://dotnet.microsoft.com/download/dotnet/10.0'
    }
}

Invoke-Scenario 'G' 'Reset-AppxPackage (deletes the app''s data)' {
    Stop-App
    Set-Marker
    $app = Get-App
    Reset-AppxPackage -Package $app.PackageFullName -ErrorAction Stop
    Write-Report '  Reset-AppxPackage returned without error'
    Write-Package 'after Reset'
    Test-Marker 'after Reset'
    Test-Start 'after Reset'
}

Write-Report ''
Write-Report 'Done. Attach this file to #597.'
Write-Host ''
Write-Host "  Report: $report" -ForegroundColor Green
