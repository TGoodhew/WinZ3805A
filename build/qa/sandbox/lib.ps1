# Dot-sourced by every sandbox job. Windows PowerShell 5.1 syntax only: that is what Install.cmd runs.
$ErrorActionPreference = 'Stop'
$zips = 'C:\qa\in\zips'
$out = 'C:\qa\out'
$work = 'C:\work'

# A zip extracted to C:\work\<label>, as a person would extract it.
function Expand-Release([string]$Zip, [string]$Label) {
    $to = Join-Path $work $Label
    if (Test-Path $to) { Remove-Item $to -Recurse -Force }
    New-Item -ItemType Directory -Force $to | Out-Null
    Expand-Archive (Join-Path $zips $Zip) -DestinationPath $to
    $to
}

# Install.cmd -Unattended from that folder. Returns its exit code; its console and the run's installer
# log are copied to C:\qa\out as <tag>-console.txt and <tag>-install.log.
#
# The whole /c command is wrapped in one more pair of quotes on purpose: cmd strips the first and last
# quote of a /c line that starts with one, and without the outer pair that broke the quoted path and
# the redirection, so Install.cmd never ran and every scenario "failed" with exit 1 (22 Nov 2026).
function Invoke-Install([string]$Folder, [string]$Tag) {
    $logs = Join-Path $env:LOCALAPPDATA 'WinZ3805A Installer\logs'
    $before = @(Get-ChildItem $logs -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object FullName)
    $p = Start-Process cmd.exe -Wait -PassThru -WindowStyle Hidden -ArgumentList '/c', "`"`"$Folder\Install.cmd`" -Unattended > `"$out\$Tag-console.txt`" 2>&1`""
    $new = Get-ChildItem $logs -Filter *.log -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.FullName } |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if ($new) { Copy-Item $new.FullName (Join-Path $out "$Tag-install.log") }
    $p.ExitCode
}

# The machine as far as WinZ3805A is concerned, to C:\qa\out\<tag>-facts.json.
function Save-Facts([string]$Tag) {
    $root = 'F40042E2E5F7E8EF8189FED15519AECE42C3BFA2'   # Microsoft Identity Verification Root CA 2020
    [ordered]@{
        packages    = @(Get-AppxPackage -Name WinZ3805A | ForEach-Object { $_.PackageFamilyName })
        versions    = @(Get-AppxPackage -Name WinZ3805A | ForEach-Object { "$($_.Version)" })
        machineTP   = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople | ForEach-Object Thumbprint)
        userTP      = @(Get-ChildItem Cert:\CurrentUser\TrustedPeople | ForEach-Object Thumbprint)
        idvRoot     = [bool](Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\AuthRoot, Cert:\CurrentUser\Root -ErrorAction SilentlyContinue | Where-Object Thumbprint -eq $root)
        dotnet      = @(Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
        data        = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'WinZ3805A_*' -ErrorAction SilentlyContinue | ForEach-Object {
                           $d = Join-Path $_.FullName 'LocalCache\Local\WinZ3805A'
                           [ordered]@{ family = $_.Name; files = @(Get-ChildItem $d -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Substring($d.Length + 1) }) } })
        savedCopies = @(Get-ChildItem ([Environment]::GetFolderPath('MyDocuments')) -Directory -Filter 'WinZ3805A*' -ErrorAction SilentlyContinue | ForEach-Object Name)
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out "$Tag-facts.json")
}

# The app closed, as a person closes it before installing again; the installer refuses to while it runs.
function Stop-App { Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 3 }
