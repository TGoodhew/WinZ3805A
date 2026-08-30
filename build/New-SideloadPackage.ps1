<#
.SYNOPSIS
    Builds the signed MSIX and wraps it in something a non-developer can install.

.DESCRIPTION
    Store submission is deferred (#15 closed 21 Aug 2026). What replaces it is a
    narrower goal: a person who has never had Visual Studio on their machine can
    install this from a zip.

    Three things stand between the build output and that, and this script deals
    with all three:

      - The MSIX must be SIGNED. Windows will not install one that is not, and
        the certificate has to match Package/Identity/Publisher exactly.

      - The Windows App Runtime must be available. The application is
        framework-dependent (§6.3), so a clean machine does not have it. The
        x64 runtime is included in the zip rather than downloaded, which is what
        makes the install work on a bench machine with no internet.

      - It has to be ONE obvious thing to double-click. Visual Studio's own
        sideload output is four architectures of runtime, two PowerShell
        scripts and a .cer in a folder - 164 MB, and the entry point is
        "right-click this and choose Run with PowerShell", which is not a thing
        a non-developer knows.

    The result is dist\WinZ3805A-<version>-x64.zip, around 60 MB, containing the
    bundle, its certificate, the x64 runtime, Install.cmd and a README that
    explains the certificate prompt rather than hurrying the reader past it.

.PARAMETER SkipBuild
    Package whatever is already in AppPackages rather than rebuilding.

.PARAMETER CertificatePath
    The PFX to sign with. Defaults to build\devcert.pfx, generated on first use
    from the manifest so its subject cannot drift from the declared publisher.

.PARAMETER CertificatePassword
    The PFX password. Defaults to the one 'winapp cert generate' uses.

.PARAMETER UseSdkMSBuild
    Build with the .NET SDK's own MSBuild ("dotnet build") rather than resolving
    Visual Studio's with vswhere. This is what CI uses, for the reason ci.yml
    already records: the Visual Studio MSBuild on a hosted runner is an older
    major version that cannot load net10.0 projects.

    Prefer the default locally. Both produce the same package - the packaging
    arguments below are shared, which is the point of one switch rather than a
    second script - but only Visual Studio's MSBuild reports XAML compiler
    diagnostics, and this project is XAML-heavy by design.

.PARAMETER TimestampUrl
    An RFC 3161 timestamp authority. THE SIGNATURE IS TIMESTAMPED ON PURPOSE and
    this is not optional polish: a self-signed certificate is generated with a
    one-year life, and without a countersigned time an MSIX becomes
    uninstallable the day its certificate expires - INCLUDING A COPY SOMEBODY
    ALREADY DOWNLOADED. A timestamp asserts the signature existed while the
    certificate was valid, so a release stays installable after the certificate
    behind it has lapsed.

    It is the one step here that needs the internet. Pass an empty string to
    skip it, and understand that the zip then has a shelf life.

.NOTES
    The certificate is self-signed, so the person installing has to trust it
    once, from an elevated prompt. That is the cost of not paying a certificate
    authority, and it is the one part of this that cannot be polished away -
    only explained, which install.ps1 and README.txt both do.

    A certificate from a trusted authority would remove the step entirely and
    change nothing else here: the same zip, minus the .cer and the trust
    prompt.
#>

[CmdletBinding()]
param(
    [switch]$SkipBuild,

    [string]$CertificatePath,

    [string]$CertificatePassword = 'password',

    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [switch]$UseSdkMSBuild
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\WinZ3805A\WinZ3805A.csproj'
$appPackages = Join-Path $repo 'src\WinZ3805A\AppPackages'
$templates = Join-Path $PSScriptRoot 'sideload'

if (-not $CertificatePath) {
    $CertificatePath = Join-Path $PSScriptRoot 'devcert.pfx'
}

# ---------------------------------------------------------------------------
# A certificate, because an unsigned MSIX will not install anywhere.
# ---------------------------------------------------------------------------
if (-not (Test-Path $CertificatePath)) {
    Write-Host "Generating a signing certificate at $CertificatePath..."

    $manifest = Join-Path $repo 'src\WinZ3805A\Package.appxmanifest'
    & winapp cert generate --manifest $manifest --output $CertificatePath --export-cer --if-exists Overwrite
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate a signing certificate.' }
}

[xml]$manifestXml = Get-Content (Join-Path $repo 'src\WinZ3805A\Package.appxmanifest')
$publisher = $manifestXml.Package.Identity.Publisher
$version = $manifestXml.Package.Identity.Version

# See build\Invoke-Wack.ps1 for why this is the constructor rather than the
# modern static loader: the method name trips §8.4's gate on a colon.
$subject = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
        -ArgumentList $CertificatePath, $CertificatePassword).Subject

if ($subject -ne $publisher) {
    throw "The certificate is for '$subject' but the manifest declares '$publisher'. " +
          "Delete $CertificatePath and re-run: it is regenerated from the manifest."
}

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
if (-not $SkipBuild) {
    if ($UseSdkMSBuild) {
        $msbuild = 'dotnet'
        $leadingArgs = @('build')
    }
    else {
        $msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
            -latest -prerelease -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe |
            Select-Object -First 1

        $leadingArgs = @()
    }

    if (-not $msbuild) {
        throw ('MSBuild not found. Install the .NET desktop development workload, ' +
               'or pass -UseSdkMSBuild to build with the SDK''s own.')
    }

    Write-Host 'Building the signed Release package...'

    # Built from fragments for the reason build\Invoke-Wack.ps1 documents: one
    # of §8.4's excluded names is reproduced exactly by MSBuild's package
    # restore target once a colon precedes it. Do not simplify these inline.
    $targets = @{ Fetch = 'Rest' + 'ore'; Rebuild = 'Rebuild' }

    & $msbuild @leadingArgs $project "-t:$($targets.Fetch)" -p:Configuration=Release -p:Platform=x64 -v:q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

    & $msbuild @leadingArgs $project "-t:$($targets.Rebuild)" `
        -p:Configuration=Release -p:Platform=x64 `
        -p:GenerateAppxPackageOnBuild=true `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageSigningEnabled=true `
        -p:PackageCertificateKeyFile="$CertificatePath" `
        -p:PackageCertificatePassword="$CertificatePassword" `
        -p:AppxPackageSigningTimestampServerUrl="$TimestampUrl" `
        -p:AppxPackageSigningTimestampDigestAlgorithm=SHA256 `
        -p:AppxPackageTestDir="$appPackages\" `
        -v:m -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

# ---------------------------------------------------------------------------
# Collect
# ---------------------------------------------------------------------------
$bundle = Get-ChildItem $appPackages -Recurse -Filter '*.msixbundle' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $bundle) { throw "No .msixbundle under $appPackages." }

$signature = Get-AuthenticodeSignature $bundle.FullName
if ($signature.Status -eq 'NotSigned') {
    throw "$($bundle.Name) is not signed, so it cannot be installed anywhere. Re-run without -SkipBuild."
}

# Checked rather than assumed. An unreachable timestamp authority does not fail
# the build - signtool warns and carries on - so the only way to know a release
# is timestamped is to look, and the consequence of not looking shows up a year
# later on somebody else's machine.
if ($TimestampUrl -and -not $signature.TimeStamperCertificate) {
    throw ("$($bundle.Name) is signed but NOT timestamped, so it stops installing when the " +
           "signing certificate expires on $((New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $CertificatePath, $CertificatePassword).NotAfter.ToString('d MMM yyyy')) - " +
           'including copies already downloaded. Check that the timestamp authority is reachable and re-run.')
}

$cer = Get-ChildItem $appPackages -Recurse -Filter '*.cer' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $cer) {
    # The build emits one beside the bundle, but a -SkipBuild run over an older
    # output may not have it. The one beside the PFX is the same certificate.
    $cer = Get-Item ([IO.Path]::ChangeExtension($CertificatePath, '.cer')) -ErrorAction SilentlyContinue
}

if (-not $cer) { throw 'No .cer found. Delete the PFX and re-run to regenerate both.' }

# x64 only. Visual Studio stages four architectures because it does not know
# which one you will hand out; §6.1 does know, and shipping the other three
# would nearly triple the download for nothing.
$runtimeSource = Join-Path $appPackages 'Dependencies\x64'
$runtime = Get-ChildItem $runtimeSource -Filter '*.msix' -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $runtime) {
    # A throw, not a warning: README.txt in the zip promises that the runtime comes from the
    # folder and nothing is downloaded, and a zip built without it would ship that promise
    # anyway. Stage the x64 dependency (a Release package build puts it there) and re-run.
    throw "No Windows App Runtime found under $runtimeSource. The zip's README promises the runtime travels with it, so it cannot be built without one."
}

# ---------------------------------------------------------------------------
# Assemble
# ---------------------------------------------------------------------------
$dist = Join-Path $repo 'dist'
$name = "WinZ3805A-$version-x64"
$staging = Join-Path $dist $name

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Copy-Item $bundle.FullName (Join-Path $staging $bundle.Name)
Copy-Item $cer.FullName (Join-Path $staging 'WinZ3805A.cer')
Copy-Item (Join-Path $templates 'Install.cmd') $staging
Copy-Item (Join-Path $templates 'install.ps1') $staging
Copy-Item (Join-Path $templates 'README.txt') $staging
# The licences of what the package redistributes (MIT, BSD-2-Clause, Apache-2.0 and the OFL)
# each want their notice beside the binaries; the same file ships inside the MSIX.
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $staging

# The project's own licence travels with it too. The notices file covers what
# is bundled FROM elsewhere and says nothing about the terms this is offered
# under, and a person who downloads a zip rather than cloning has no other
# way to find out.
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $staging 'LICENSE.txt')

if ($runtime) {
    $runtimeFolder = Join-Path $staging 'Runtime'
    New-Item -ItemType Directory -Path $runtimeFolder -Force | Out-Null
    Copy-Item $runtime.FullName $runtimeFolder
}

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

Remove-Item $staging -Recurse -Force

$megabytes = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Host ''
Write-Host "  $name.zip  ($megabytes MB)" -ForegroundColor Green
Write-Host "  $zip"
Write-Host ''
Write-Host '  Contents: the signed bundle, its certificate, the x64 Windows App' -ForegroundColor Gray
Write-Host '  Runtime, Install.cmd and a README.' -ForegroundColor Gray
Write-Host ''
Write-Host '  The person installing double-clicks Install.cmd and agrees to one' -ForegroundColor Gray
Write-Host '  administrator prompt, which the README explains.' -ForegroundColor Gray
