<#
.SYNOPSIS
    Installs WinZ3805A on a machine that has never had a developer tool on it.

.DESCRIPTION
    Three things have to happen, and only one of them needs administrator rights:

      1. Trust the certificate the application is signed with. Needs elevation,
         because the certificate store it goes into belongs to the machine.
      2. Install the Windows App Runtime, if it is not already there.
      3. Install the application itself.

    Steps 2 and 3 run as the person who started this, NOT elevated, and that is
    deliberate. Installing an app is a per-user operation: elevating the whole
    script would install it for whichever administrator the UAC prompt
    authenticated, which on a shared machine is not the person at the keyboard.
    They would then see the install succeed and no application anywhere.

.NOTES
    The certificate is self-signed, and this script says so plainly rather than
    hurrying the user past it. What it grants is narrow - see the TrustedPeople
    comment below - but it is still a decision the user is entitled to make with
    their eyes open.
#>

[CmdletBinding()]
param(
    # Set when this script re-launches itself elevated to do the certificate
    # step alone. Not for a user to pass.
    [switch]$TrustCertificateOnly
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$certificate = Get-ChildItem -Path $here -Filter '*.cer' | Select-Object -First 1
$bundle = Get-ChildItem -Path $here -Filter '*.msixbundle' | Select-Object -First 1
$runtime = Get-ChildItem -Path (Join-Path $here 'Runtime') -Filter '*.msix' -ErrorAction SilentlyContinue |
    Select-Object -First 1

function Write-Step { param([string]$Text) Write-Host ''; Write-Host $Text -ForegroundColor Cyan }
function Write-Ok { param([string]$Text) Write-Host "  $Text" -ForegroundColor Green }
function Write-Info { param([string]$Text) Write-Host "  $Text" -ForegroundColor Gray }

# ---------------------------------------------------------------------------
# The elevated half: one certificate, into one store.
# ---------------------------------------------------------------------------
if ($TrustCertificateOnly) {
    # LocalMachine\TrustedPeople, never Root. The distinction is the whole
    # reason this is defensible: a certificate in TrustedPeople is trusted to
    # sign *applications you install by hand* and nothing else. It cannot vouch
    # for a website, and it cannot make arbitrary code look like it came from
    # Microsoft. Root would do both.
    certutil.exe -addstore TrustedPeople $certificate.FullName | Out-Null
    exit $LASTEXITCODE
}

Write-Host ''
Write-Host '  WinZ3805A' -ForegroundColor White
Write-Host '  Monitoring and control for HP/Symmetricom GPS-disciplined oscillators.'
Write-Host ''

if (-not $certificate) { throw 'The certificate is missing from this folder. Download the release again.' }
if (-not $bundle) { throw 'The application package is missing from this folder. Download the release again.' }

# ---------------------------------------------------------------------------
# 1. Trust
# ---------------------------------------------------------------------------
Write-Step '1 of 3  Trusting the signature'

$thumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
        -ArgumentList $certificate.FullName).Thumbprint

if (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumbprint") {
    Write-Ok 'Already trusted. Nothing to do.'
}
else {
    Write-Info 'Windows will ask for administrator permission in a moment.'
    Write-Info ''
    Write-Info 'It is being asked so this app''s signature can be added to the'
    Write-Info '"Trusted People" store, which is what lets Windows install an'
    Write-Info 'application that did not come from the Microsoft Store.'
    Write-Info ''
    Write-Info 'That store is narrow on purpose. A certificate in it can vouch'
    Write-Info 'for applications you install by hand, and for nothing else - it'
    Write-Info 'cannot vouch for a website or for code you did not choose to run.'
    Write-Info ''

    $elevated = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$($MyInvocation.MyCommand.Path)`"",
        '-TrustCertificateOnly'
    )

    if ($elevated.ExitCode -ne 0) {
        throw 'The certificate was not trusted, so the application cannot be installed. Nothing has been changed.'
    }

    Write-Ok "Trusted. Certificate $($thumbprint.Substring(0, 8))..., issued to $((New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $certificate.FullName).Subject)"
}

# ---------------------------------------------------------------------------
# 2. The runtime
# ---------------------------------------------------------------------------
Write-Step '2 of 3  Windows App Runtime'

if ($runtime) {
    Write-Info 'Installing, or confirming it is already present. This can take a minute.'
    try {
        Add-AppxPackage -Path $runtime.FullName -ErrorAction Stop
        Write-Ok 'Installed.'
    }
    catch {
        # Already present at this version or newer is the common case and is not
        # a failure. Anything else is, and is reported as it was received.
        if ($_.Exception.Message -match '0x80073D06|already installed|higher version') {
            Write-Ok 'Already present.'
        }
        else {
            throw
        }
    }
}
else {
    Write-Info 'Not included in this download; assuming it is already installed.'
}

# ---------------------------------------------------------------------------
# 3. The application
# ---------------------------------------------------------------------------
Write-Step '3 of 3  WinZ3805A'

try {
    Add-AppxPackage -Path $bundle.FullName -ErrorAction Stop
    Write-Ok 'Installed.'
}
catch {
    # Windows reports an untrusted signature as 0x80073CF0, "Package could not
    # be opened", with the real reason - 0x800B0109 - buried in the second
    # sentence. Left alone, that sends someone off to download the file again,
    # which will not help: the download is fine and the trust is not. Measured,
    # not assumed: this is exactly what installing the package without the
    # certificate produces.
    # 0x80073CFB: something is already registered under this identity. On a
    # machine that has never built the application that cannot happen — but the
    # person most likely to run this installer repeatedly is whoever is
    # developing it, and on their machine `winapp run` has registered the loose
    # build output under exactly the same identity. The package family name
    # differs only by a hash of the publisher, so the two look like separate
    # installs and are not: a packaged build cannot replace a registered one.
    if ($_.Exception.Message -match '0x80073CFB') {
        throw 'This machine already has a development registration of WinZ3805A, which a packaged ' +
              'build cannot replace. Remove it and run this installer again:' + [Environment]::NewLine +
              [Environment]::NewLine +
              '    Get-AppxPackage WinZ3805A | Remove-AppxPackage -PreserveApplicationData' + [Environment]::NewLine +
              [Environment]::NewLine +
              'The -PreserveApplicationData is not optional. Without it Windows deletes everything ' +
              'the application has stored under this identity: the remembered connection, the ' +
              'settings, the logs, and the whole trend database. Nothing in the repository is ' +
              'touched either way.'
    }

    if ($_.Exception.Message -match '0x800B0109|0x80073CF0') {
        throw 'Windows will not install the application because it does not trust the signature. ' +
              'That usually means the certificate step above was skipped or declined. ' +
              'Run this installer again and agree to the administrator prompt. ' +
              'The download itself is fine - re-downloading will not change anything.'
    }

    throw
}

Write-Host ''
Write-Host '  Done. WinZ3805A is in the Start menu.' -ForegroundColor Green
Write-Host ''
Write-Host '  To remove it later: Settings > Apps > Installed apps > WinZ3805A.' -ForegroundColor Gray
Write-Host '  Removing it does not remove the certificate; that is in' -ForegroundColor Gray
Write-Host '  certlm.msc, under Trusted People.' -ForegroundColor Gray
