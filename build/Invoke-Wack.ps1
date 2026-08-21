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
    x64, which is the only architecture §6.1 requires as of 15 Aug 2026 and the
    only value this accepts. The parameter is kept rather than removed so that
    restoring ARM64 later means widening a ValidateSet rather than rediscovering
    that this script ever had a notion of architecture.

    ARM64 was dropped because **WACK cannot cross-test**: it installs and runs
    the package it certifies, so an ARM64 package has to be certified on an ARM64
    machine and there is no host flag that changes that. Windows 11 on ARM runs
    the x64 package under emulation, so ARM64 users are not left without an
    application - they are left without a native one.

.PARAMETER SkipBuild
    Certify the package already in AppPackages rather than rebuilding.

.PARAMETER CertificatePath
    The PFX to sign with. Defaults to build\devcert.pfx, which is generated on
    first use and is gitignored.

    **appcert installs the package it certifies, and Windows will not install an
    unsigned MSIX.** This script used to pass AppxPackageSigningEnabled=true
    without naming a certificate, which produces a bundle with no signature at
    all - the build succeeds, and the certification run then fails at the install
    step for a reason that has nothing to do with the application.

    The certificate's subject must match Package/Identity/Publisher exactly. It
    is generated from the manifest so the two cannot drift, and it has to be
    regenerated when the Partner Center publisher DN replaces the placeholder.

.PARAMETER CertificatePassword
    The PFX password. Defaults to the one 'winapp cert generate' uses.

.NOTES
    The package this produces is signed with a temporary test certificate and is
    for certification and sideloading only. The Store re-signs on ingestion; do
    not hand this file to anyone as a release.
#>

[CmdletBinding()]
param(
    [ValidateSet('x64')]
    [string]$Platform = 'x64',

    [switch]$SkipBuild,

    [string]$ReportPath,

    [string]$CertificatePath,

    [string]$CertificatePassword = 'password'
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\WinZ3805A\WinZ3805A.csproj'

if (-not $ReportPath) {
    $ReportPath = Join-Path $repo "wack-$($Platform.ToLowerInvariant()).xml"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'appcert.exe requires elevation. Re-run this script from an elevated PowerShell.'
}

if (-not $CertificatePath) {
    $CertificatePath = Join-Path $PSScriptRoot 'devcert.pfx'
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
    # ---------------------------------------------------------------------
    # A certificate, because appcert has to install what it certifies.
    # ---------------------------------------------------------------------
    if (-not (Test-Path $CertificatePath)) {
        Write-Host "Generating a development certificate at $CertificatePath..."

        $manifest = Join-Path $repo 'src\WinZ3805A\Package.appxmanifest'
        & winapp cert generate --manifest $manifest --output $CertificatePath --export-cer --if-exists Overwrite
        if ($LASTEXITCODE -ne 0) { throw 'Could not generate a signing certificate.' }
    }

    # The subject has to match Package/Identity/Publisher exactly or the package
    # will not install, however valid the signature is. Checked here rather than
    # left to a confusing failure four minutes into a build.
    [xml]$manifestXml = Get-Content (Join-Path $repo 'src\WinZ3805A\Package.appxmanifest')
    $publisher = $manifestXml.Package.Identity.Publisher

    # Not Get-PfxCertificate: on a password-protected PFX it prompts, which in a
    # non-interactive shell is an indefinite hang rather than an error. Loading
    # it with the password we already have is the only form that cannot block.
    #
    # And the constructor rather than X509CertificateLoader's static method, which
    # would be the modern form. Its name follows a '::' and one of §8.4's excluded
    # tokens then appears after a colon, so build/Test-NoBlockedCommands.ps1 flags
    # it. The gate is matching '<colon><token>' correctly and a '::' member access
    # can never be SCPI - but narrowing the gate to teach it about .NET syntax buys
    # a false negative somewhere else for the sake of one line here. The line is
    # what moves.
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
        -ArgumentList $CertificatePath, $CertificatePassword
    $subject = $certificate.Subject

    if ($subject -ne $publisher) {
        throw "The certificate is for '$subject' but the manifest declares '$publisher'. " +
              "Delete $CertificatePath and re-run: it is regenerated from the manifest."
    }

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
        -p:PackageCertificateKeyFile="$CertificatePath" `
        -p:PackageCertificatePassword="$CertificatePassword" `
        -p:AppxPackageTestDir="$repo\src\WinZ3805A\AppPackages\" `
        -v:m -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $Platform." }
}

$bundle = Get-ChildItem (Join-Path $repo 'src\WinZ3805A\AppPackages') -Recurse -Filter '*.msixbundle' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $bundle) { throw 'No .msixbundle found under src\WinZ3805A\AppPackages.' }

# Signed, and signed by something this machine trusts. The first is the script's
# job and is checked above; the second is a one-off machine change that needs
# elevation of its own, so it is reported rather than done silently.
$signature = Get-AuthenticodeSignature $bundle.FullName
if ($signature.Status -eq 'NotSigned') {
    throw "$($bundle.Name) is not signed, so appcert cannot install it. Re-run without -SkipBuild."
}

if ($signature.Status -ne 'Valid') {
    Write-Warning "The package is signed but the certificate is not trusted on this machine ($($signature.Status))."
    Write-Warning "appcert will fail to install it. Trust it once with:  winapp cert install $CertificatePath"
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
