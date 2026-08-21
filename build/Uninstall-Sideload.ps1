<#
.SYNOPSIS
    Undoes a sideload install, so the installer can be tested again from clean.

.DESCRIPTION
    Testing an installer means installing, and installing changes the machine.
    This is the other half: it puts back what New-SideloadPackage.ps1 and the
    zip it produces between them changed.

        the application package, and everything it has stored
        the signing certificate, from LocalMachine\TrustedPeople
        optionally, the zip and any folder it was extracted to

    Written because the alternative is remembering four commands and a
    certificate thumbprint, and the thumbprint changes whenever the certificate
    is regenerated.

.PARAMETER IncludeArtifacts
    Also delete dist\ and any WinZ3805A-install folder on the Desktop.

.PARAMETER IncludeCertificateFile
    Also delete build\devcert.pfx and build\devcert.cer. The next build
    regenerates them - as a NEW certificate, with a new thumbprint, which
    everyone who installed the old one has to trust again. Off by default for
    that reason.

.NOTES
    The Windows App Runtime is deliberately left alone. It is a shared framework
    that other applications may depend on, it may well have been on the machine
    before any of this, and removing it to make one test more realistic is a
    poor trade. A truly clean test of that step wants a spare machine.
#>

[CmdletBinding()]
param(
    # Set when this script re-launches itself elevated to remove the
    # certificate alone. Not for a user to pass.
    [string]$RemoveCertificateOnly,

    [switch]$IncludeArtifacts,

    [switch]$IncludeCertificateFile
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Write-Did { param([string]$Text) Write-Host "  removed  $Text" -ForegroundColor Green }
function Write-Skip { param([string]$Text) Write-Host "  already  $Text" -ForegroundColor DarkGray }
function Write-Kept { param([string]$Text) Write-Host "  kept     $Text" -ForegroundColor Gray }

# ---------------------------------------------------------------------------
# The elevated half.
# ---------------------------------------------------------------------------
if ($RemoveCertificateOnly) {
    $path = "Cert:\LocalMachine\TrustedPeople\$RemoveCertificateOnly"
    if (Test-Path $path) { Remove-Item $path -Force }
    exit 0
}

Write-Host ''
Write-Host '  Undoing the sideload install' -ForegroundColor White
Write-Host ''

# ---------------------------------------------------------------------------
# 1. The application
# ---------------------------------------------------------------------------
$installed = Get-AppxPackage -Name 'WinZ3805A' -ErrorAction SilentlyContinue

if ($installed) {
    foreach ($package in $installed) {
        # No -PreserveApplicationData. The point of this script is a machine
        # that looks like it has never seen the application, and a preferences
        # file left behind would make the next first-run test a second-run one.
        Remove-AppxPackage -Package $package.PackageFullName
        Write-Did "$($package.PackageFullName)"
    }
}
else {
    Write-Skip 'no WinZ3805A package installed'
}

# ---------------------------------------------------------------------------
# 2. The certificate
# ---------------------------------------------------------------------------
# Found by subject rather than by a remembered thumbprint: the certificate is
# regenerated whenever the publisher changes, and a hard-coded thumbprint would
# then quietly remove nothing.
[xml]$manifest = Get-Content (Join-Path $repo 'src\WinZ3805A\Package.appxmanifest')
$publisher = $manifest.Package.Identity.Publisher

$trusted = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $publisher })

if ($trusted.Count -gt 0) {
    foreach ($certificate in $trusted) {
        Write-Host "  Windows will ask for administrator permission to remove $($certificate.Thumbprint.Substring(0, 8))..." -ForegroundColor Gray

        $elevated = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', "`"$($MyInvocation.MyCommand.Path)`"",
            '-RemoveCertificateOnly', $certificate.Thumbprint
        )

        if ($elevated.ExitCode -ne 0) {
            Write-Warning "The certificate was not removed. It is $($certificate.Thumbprint) under Trusted People."
        }
        else {
            Write-Did "certificate $($certificate.Thumbprint.Substring(0, 8))..., $publisher"
        }
    }
}
else {
    Write-Skip "no certificate for $publisher in Trusted People"
}

# ---------------------------------------------------------------------------
# 3. What was built and where it was unpacked
# ---------------------------------------------------------------------------
if ($IncludeArtifacts) {
    $dist = Join-Path $repo 'dist'
    if (Test-Path $dist) {
        Remove-Item $dist -Recurse -Force
        Write-Did 'dist\'
    }
    else {
        Write-Skip 'no dist\ folder'
    }

    $extracted = Join-Path ([Environment]::GetFolderPath('Desktop')) 'WinZ3805A-install'
    if (Test-Path $extracted) {
        Remove-Item $extracted -Recurse -Force
        Write-Did $extracted
    }
    else {
        Write-Skip 'nothing extracted to the Desktop'
    }
}

if ($IncludeCertificateFile) {
    foreach ($file in @('devcert.pfx', 'devcert.cer')) {
        $path = Join-Path $PSScriptRoot $file
        if (Test-Path $path) {
            Remove-Item $path -Force
            Write-Did "build\$file"
        }
    }

    Write-Host ''
    Write-Host '  The next build generates a NEW certificate with a new thumbprint.' -ForegroundColor Yellow
    Write-Host '  Anyone who trusted the old one has to trust that one too.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# What was deliberately not touched
# ---------------------------------------------------------------------------
Write-Host ''
Write-Kept 'the Windows App Runtime - shared, and other applications may need it'

if (-not $IncludeArtifacts) { Write-Kept 'dist\ and the extracted folder - pass -IncludeArtifacts' }
if (-not $IncludeCertificateFile) { Write-Kept 'build\devcert.pfx - pass -IncludeCertificateFile' }

Write-Host ''
Write-Host '  The development registration is gone too, since it shares the identity.' -ForegroundColor Gray
Write-Host '  `winapp run` recreates it on the next launch; nothing needs doing.' -ForegroundColor Gray
Write-Host ''
