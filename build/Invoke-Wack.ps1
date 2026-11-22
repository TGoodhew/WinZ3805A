<#
.SYNOPSIS
    Builds the Release MSIX and runs the Windows App Certification Kit over it.

.DESCRIPTION
    P0-15 (#15) is verified by a clean WACK run on x64. This wraps the build and
    the certification run so it is repeatable rather than a sequence someone
    reconstructs from memory once a release.

    Must be run **elevated**: appcert.exe refuses to start otherwise, and it
    installs, launches, drives and uninstalls the package as part of the run.
    Expect it to take over the desktop for ten to twenty minutes and to leave the
    machine alone afterwards.

        # from an elevated PowerShell
        pwsh build/Invoke-Wack.ps1

.PARAMETER Platform
    x64, the only architecture §6.1 has and the only value this accepts. The
    parameter is kept rather than removed because the packaging commands below
    read it, and a script that names its architecture is clearer than one that
    assumes it.

    Worth knowing if a second architecture is ever proposed: **WACK cannot
    cross-test.** It installs and runs the package it certifies, so certifying a
    package for another architecture needs a machine of that architecture, and
    there is no host flag that changes it.

.PARAMETER SkipBuild
    Certify the package already in AppPackages rather than rebuilding.

.PARAMETER SignScript
    A script that takes file paths and signs them, as New-SideloadPackage.ps1
    takes. Defaults to $env:WINZ3805A_SIGN_SCRIPT (#793).

    **appcert installs the package it certifies, and Windows will not install an
    unsigned MSIX.** This script used to pass AppxPackageSigningEnabled=true
    without naming a certificate, which produces a bundle with no signature at
    all - the build succeeds, and the certification run then fails at the install
    step for a reason that has nothing to do with the application.

    So the package is built and signed by New-SideloadPackage.ps1 - one signing
    path, the one releases take, rather than a second one here that could
    drift from it. Without a sign script that builds an unsigned bundle, and
    this stops before appcert rather than failing four minutes into it.

.NOTES
    The package this produces is signed exactly as a release is. It is for
    certification; hand people the release zips, not this file.
#>

[CmdletBinding()]
param(
    [ValidateSet('x64')]
    [string]$Platform = 'x64',

    [switch]$SkipBuild,

    [string]$ReportPath,

    [string]$SignScript = $env:WINZ3805A_SIGN_SCRIPT
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

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

# WACK installs and runs what it certifies, so certifying an x64 package from a
# Windows-on-ARM host measures the emulator rather than the machine a reviewer
# will use. Worth a warning rather than a refusal: the run still completes and
# most of its checks are still meaningful.
$hostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($hostArch -ne 'X64') {
    Write-Warning "Certifying an x64 package on a $hostArch host. WACK installs and runs the package, so the result reflects emulation - see the .PARAMETER Platform notes."
}

if (-not $SkipBuild) {
    # Built and signed the way a release is (#793). The packager uses Visual Studio's MSBuild by
    # default, which is the one that reports XAML diagnostics; its zips are a by-product here.
    & (Join-Path $PSScriptRoot 'New-SideloadPackage.ps1') -SignScript $SignScript
}

$bundle = Get-ChildItem (Join-Path $repo 'src\WinZ3805A\AppPackages') -Recurse -Filter '*.msixbundle' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $bundle) { throw 'No .msixbundle found under src\WinZ3805A\AppPackages.' }

# Signed, and signed by something this machine trusts - which, with Artifact Signing chaining to a
# public root, is the same question (#793).
$signature = Get-AuthenticodeSignature $bundle.FullName
if ($signature.Status -eq 'NotSigned') {
    throw "$($bundle.Name) is not signed, so appcert cannot install it. Re-run with -SignScript, or set WINZ3805A_SIGN_SCRIPT."
}

if ($signature.Status -ne 'Valid') {
    throw "$($bundle.Name) is signed but Windows does not accept the signature ($($signature.Status): $($signature.StatusMessage)), so appcert cannot install it."
}

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
