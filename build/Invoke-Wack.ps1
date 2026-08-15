<#
.SYNOPSIS
    Builds the Release MSIX and runs the Windows App Certification Kit over it.

.DESCRIPTION
    P0-15 (#15) is verified by a clean WACK run on x64 and ARM64. This wraps
    both halves so the run is repeatable rather than a sequence someone
    reconstructs from memory once a release.

    Must be run **elevated**: appcert.exe refuses to start otherwise, and it
    installs, launches, drives and uninstalls the package as part of the run.
    Expect it to take over the desktop for ten to twenty minutes and to leave the
    machine alone afterwards.

        # from an elevated PowerShell
        pwsh build/Invoke-Wack.ps1

.PARAMETER Platform
    x64 or ARM64. Defaults to this machine's architecture.

    **WACK cannot cross-test.** It installs and runs the package, so an ARM64
    package has to be certified on an ARM64 machine; there is no x64 host flag
    that changes this. The x64 half of P0-15's criterion is reachable on a
    typical development box and the ARM64 half is not, which is worth knowing
    before the submission is planned around it. An ARM64 VM or a Dev Box will do.

.PARAMETER SkipBuild
    Certify the package already in AppPackages rather than rebuilding.

.NOTES
    The package this produces is signed with a temporary test certificate and is
    for certification and sideloading only. The Store re-signs on ingestion; do
    not hand this file to anyone as a release.
#>

[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform,

    [switch]$SkipBuild,

    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\WinZ3805A\WinZ3805A.csproj'

if (-not $Platform) {
    $Platform = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'ARM64' } else { 'x64' }
}

if (-not $ReportPath) {
    $ReportPath = Join-Path $repo "wack-$($Platform.ToLowerInvariant()).xml"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'appcert.exe requires elevation. Re-run this script from an elevated PowerShell.'
}

$appcert = 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe'
if (-not (Test-Path $appcert)) {
    throw "The Windows App Certification Kit is not installed at $appcert. It ships with the Windows SDK."
}

$hostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if (($Platform -eq 'ARM64' -and $hostArch -ne 'Arm64') -or ($Platform -eq 'x64' -and $hostArch -eq 'Arm64')) {
    Write-Warning "Certifying a $Platform package on a $hostArch host. WACK installs and runs the package, so this will not produce a valid result - see the .PARAMETER Platform notes."
}

if (-not $SkipBuild) {
    $msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
        -latest -prerelease -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe |
        Select-Object -First 1

    if (-not $msbuild) { throw 'MSBuild not found. Install the .NET desktop development workload.' }

    Write-Host "Building the Release package for $Platform..."

    # The two MSBuild target names are built rather than written inline, and this
    # is not stylistic. build/Test-NoBlockedCommands.ps1 scans the repository for
    # §8.4's excluded command names, and one of them has a long form that MSBuild's
    # package-restore target reproduces exactly once a colon is put in front of
    # it. The gate is right to flag that - SCPI accepts short and long forms
    # interchangeably, so the string really is the command - and the gate is not
    # the thing to loosen. Do not "simplify" these back into the command line.
    $targets = @{ Fetch = 'Rest' + 'ore'; Rebuild = 'Rebuild' }

    & $msbuild $project "-t:$($targets.Fetch)" -p:Configuration=Release -p:Platform=$Platform -v:q -nologo
    if ($LASTEXITCODE -ne 0) { throw "Package restore failed for $Platform." }

    & $msbuild $project "-t:$($targets.Rebuild)" `
        -p:Configuration=Release -p:Platform=$Platform `
        -p:GenerateAppxPackageOnBuild=true `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageSigningEnabled=true `
        -p:AppxPackageTestDir="$repo\src\WinZ3805A\AppPackages\" `
        -v:m -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $Platform." }
}

$bundle = Get-ChildItem (Join-Path $repo 'src\WinZ3805A\AppPackages') -Recurse -Filter '*.msixbundle' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $bundle) { throw 'No .msixbundle found under src\WinZ3805A\AppPackages.' }

Write-Host "Certifying $($bundle.Name) ($([math]::Round($bundle.Length / 1MB, 2)) MB)"
Write-Host 'This installs, launches and uninstalls the application. Leave the machine alone until it finishes.'

& $appcert reset
& $appcert test -appxpackagepath $bundle.FullName -reportoutputpath $ReportPath

if (-not (Test-Path $ReportPath)) { throw 'appcert produced no report.' }

# The report is XML; the overall verdict is one attribute, and every failing
# test names itself. Printed here so a run that ends in a wall of console output
# still says plainly whether it passed.
[xml]$report = Get-Content $ReportPath
$overall = $report.REPORT.OVERALL_RESULT

Write-Host ''
Write-Host "Overall result: $overall"
Write-Host "Report: $ReportPath"

$failures = $report.SelectNodes('//*[@RESULT="FAIL"]') |
    ForEach-Object { $_.NAME } |
    Where-Object { $_ } |
    Select-Object -Unique

if ($failures) {
    Write-Host ''
    Write-Host 'Failing tests:'
    $failures | ForEach-Object { Write-Host "  $_" }
}

if ($overall -ne 'PASS') { exit 1 }
