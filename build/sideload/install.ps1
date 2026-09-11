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
    Write-Info "The certificate is $($certificate.Name) in this folder. Double-click it"
    Write-Info 'if you would rather look at it first.'
    Write-Info ''

    # Wait for the reader before raising the prompt. Without this the UAC dialog
    # appears over the explanation within a fraction of a second, so the text is
    # on screen and unread — which is worse than not writing it, because it
    # looks like consent was informed when it could not have been. Reported by
    # the first person to run this.
    Read-Host '  Press Enter to continue, or close this window to stop'

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
# The runtime's two companion packages, which are not optional (#473).
#
# THE FRAMEWORK PACKAGE ON ITS OWN IS NOT ENOUGH, and the way it fails is the
# reason this block is worth its length: the application installs, launches, and
# exits about 200 ms later with no window, no crash dump, no error dialog and
# nothing in its log. It is not crashing. The Windows App SDK runs a deployment
# check as a module initializer - before Main, and long before the application
# has composed anything that could write a log line - and that check gives up
# when WinAppRuntime.Main and WinAppRuntime.Singleton are absent.
#
# They ship INSIDE the framework package, so nothing is downloaded here. A
# machine with no internet connection can still do this, which is the promise
# the zip makes.
#
# DONE ON EVERY RUN, not only when the framework was just installed. The
# companions must match the framework's version, and a machine whose Windows App
# Runtime is serviced independently of this zip ends up with a newer framework
# beside older companions - at which point an application that had been working
# for weeks starts exiting silently, with the same signature and no clue.
# Measured on 11 Sep 2026: framework 2.4.0.0 against companions 2.3.1.0.
# ---------------------------------------------------------------------------
# THE FAMILY IS IN THE NAME, NOT IN THE VERSION, and sorting on the version picks the wrong
# runtime on any machine with more than one. Microsoft.WindowsAppRuntime.1.8 carries version
# 8000.946.1701.0 while Microsoft.WindowsAppRuntime.2 carries 2.4.0.0, so "newest version" is
# 1.8 by a factor of four thousand. Measured, on the machine this was written on: the first
# version of this block deployed 1.8's companions, left Main.2 absent, and DOWNGRADED the shared
# Singleton — leaving the application in exactly the state this fix exists to prevent.
#
# The runtime shipped in this zip names its own package, so when it is here it is the authority.
$frameworkName = if ($runtime) { [IO.Path]::GetFileNameWithoutExtension($runtime.Name) } else { $null }

$installed = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' -ErrorAction SilentlyContinue |
    Where-Object { $_.Architecture -eq 'X64' }

$framework = if ($frameworkName) {
    $installed | Where-Object { $_.Name -eq $frameworkName } | Select-Object -First 1
}
else {
    # No runtime in the download, so the family has to be guessed from what is installed. The
    # suffix after "Microsoft.WindowsAppRuntime." IS the family — 1.7, 1.8, 2 — and comparing
    # those as versions orders them the way the SDK numbers them.
    $installed |
        Sort-Object { [version]($_.Name -replace '^Microsoft\.WindowsAppRuntime\.', '') } -Descending |
        Select-Object -First 1
}

if (-not $framework) {
    throw 'The Windows App Runtime is not installed, so its companion packages cannot be found. ' +
          'Install the runtime from this folder and run this installer again.'
}

$companions = Join-Path $framework.InstallLocation 'MSIX'

foreach ($name in 'Main.msix', 'Singleton.msix') {
    $package = Join-Path $companions $name
    if (-not (Test-Path $package)) {
        # A runtime laid out differently to the one this was written against. Say so rather than
        # carrying on: the application would install and then exit without explaining itself.
        throw "The Windows App Runtime at $($framework.InstallLocation) does not carry $name, " +
              'so the application cannot be made to start. Report this with the runtime version: ' +
              "$($framework.Version)."
    }

    try {
        Add-AppxPackage -Path $package -ErrorAction Stop
    }
    catch {
        # Already there at this version is the ordinary case on a re-run. A version mismatch is the
        # case this block exists for, and it needs the force: the companion has to be replaced by
        # the one belonging to the framework now installed, which may be a downgrade.
        if ($_.Exception.Message -match '0x80073D06|already installed|higher version') {
            continue
        }

        Add-AppxPackage -Path $package -ForceUpdateFromAnyVersion -ErrorAction Stop
    }
}

Write-Ok "Runtime companions match the runtime ($($framework.Version))."

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
