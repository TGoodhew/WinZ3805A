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
        the certificate's subject has to match Package/Identity/Publisher
        exactly. Since #793 it is signed with Azure Artifact Signing, whose
        certificates chain to a public authority, so nobody installing has to
        trust anything by hand.

      - The Windows App Runtime must be available. The application is
        framework-dependent (§6.3), so a clean machine does not have it. The
        x64 runtime is included in the zip rather than downloaded, which is what
        makes the install work on a bench machine with no internet.

      - It has to be ONE obvious thing to double-click. Visual Studio's own
        sideload output is four architectures of runtime, two PowerShell
        scripts and a .cer in a folder - 164 MB, and the entry point is
        "right-click this and choose Run with PowerShell", which is not a thing
        a non-developer knows.

    The result is TWO zips from one build (#588), identical but for one file:

      dist\WinZ3805A-<version>-x64.zip          the ONLINE zip, around 62 MB:
          the signed bundle, the x64 Windows App Runtime, Install.cmd and a
          README. .NET 10 is not in it; Install.cmd opens Microsoft's download
          page when the machine lacks it.

      dist\WinZ3805A-<version>-x64-offline.zip  the OFFLINE zip, around 90 MB:
          the same, plus Microsoft's own .NET 10 Runtime installer, which
          Install.cmd runs machine-wide when the machine lacks .NET 10.

    Neither carries a .NET runtime inside the package. A runtime Microsoft's
    installer put there is one Microsoft Update keeps patched; one inside the
    package would be this project's to service, release by release (#586,
    #588). The installer is fetched from Microsoft at build time - see
    Get-DotNetInstaller - so no .NET version number lives in this repository.

.PARAMETER SkipBuild
    Package whatever is already in AppPackages rather than rebuilding.

.PARAMETER SignScript
    A script that takes file paths and signs them - Authenticode, SHA-256,
    timestamped - throwing if it cannot. Defaults to $env:WINZ3805A_SIGN_SCRIPT.
    release.yml passes build\Sign-ArtifactSigning.ps1; on the development
    machine the variable names the local Artifact Signing script, which lives
    outside this repository with the account it signs for (#793).

    WITHOUT ONE THE BUILD STILL RUNS, UNSIGNED, AND WARNS. An unsigned package
    installs nowhere, so a zip built that way is for looking at, not for
    handing to anyone.

    The build signs nothing itself. Windows App SDK packaging can only sign
    with a certificate it can read locally, and an Artifact Signing key never
    leaves Azure, so the bundle is built unsigned and then taken apart: each
    package inside it is signed, the bundle is made again from the signed
    packages, and the bundle is signed last. That is what Visual Studio does
    with a local certificate, and what the releases up to 1.5.0 carried - all
    five inner packages signed as well as the bundle.

.PARAMETER DotNetCache
    Where Microsoft's .NET Runtime installer is kept between runs, so a rebuild
    on the same patch does not download it again. Defaults to dist\dotnet-cache.
    A cached file is used only if it matches the SHA-512 Microsoft publishes.

.PARAMETER UseSdkMSBuild
    Build with the .NET SDK's own MSBuild ("dotnet build") rather than resolving
    Visual Studio's with vswhere. This is what CI uses, for the reason ci.yml
    already records: the Visual Studio MSBuild on a hosted runner is an older
    major version that cannot load net10.0 projects.

    Prefer the default locally. Both produce the same package - the packaging
    arguments below are shared, which is the point of one switch rather than a
    second script - but only Visual Studio's MSBuild reports XAML compiler
    diagnostics, and this project is XAML-heavy by design.

.NOTES
    EVERY SIGNATURE IS CHECKED, NOT ASSUMED: each inner package and the bundle
    must be Valid, signed by exactly the manifest's Publisher, and timestamped,
    or the build fails. The timestamp matters more than it did: an Artifact
    Signing certificate lives three days, so an untimestamped package stops
    installing before the week is out - including copies already downloaded.
    The sign script asks for one, and this checks that it arrived.
#>

[CmdletBinding()]
param(
    [switch]$SkipBuild,

    [string]$SignScript = $env:WINZ3805A_SIGN_SCRIPT,

    [string]$DotNetCache,

    [switch]$UseSdkMSBuild
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\WinZ3805A\WinZ3805A.csproj'
$appPackages = Join-Path $repo 'src\WinZ3805A\AppPackages'
$templates = Join-Path $PSScriptRoot 'sideload'

[xml]$manifestXml = Get-Content (Join-Path $repo 'src\WinZ3805A\Package.appxmanifest')
$publisher = $manifestXml.Package.Identity.Publisher
$version = $manifestXml.Package.Identity.Version

# ---------------------------------------------------------------------------
# A sign script, or a loud warning (#793).
# ---------------------------------------------------------------------------
if ($SignScript) {
    if (-not (Test-Path $SignScript)) { throw "The sign script $SignScript does not exist." }
    $SignScript = (Resolve-Path $SignScript).Path
}
else {
    Write-Warning ('No sign script: pass -SignScript or set WINZ3805A_SIGN_SCRIPT. The package will be ' +
                   'built UNSIGNED, and an unsigned package installs nowhere.')
}

# The newest x64 tool of that name in the Windows SDK - signtool's neighbour, so the two match.
function Get-SdkTool {
    param([string]$Name)
    $tool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\$Name" -ErrorAction SilentlyContinue |
        Sort-Object { [version] $_.Directory.Parent.Name } | Select-Object -Last 1
    if (-not $tool) { throw "$Name was not found; install the Windows SDK." }
    $tool.FullName
}

# A signature that is Valid, made by exactly the manifest's publisher, and timestamped - or the reason
# it is not. Valid alone is not enough: any publisher's valid signature is Valid, and Windows refuses
# the package unless the signer's subject IS Package/Identity/Publisher.
function Test-OurSignature {
    param([string]$Path)
    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne 'Valid') { return "$($signature.Status): $($signature.StatusMessage)" }
    if ($signature.SignerCertificate.Subject -ne $publisher) {
        return "signed by '$($signature.SignerCertificate.Subject)', but the manifest declares '$publisher'"
    }
    if (-not $signature.TimeStamperCertificate) {
        return 'signed but NOT timestamped, so it stops installing when the three-day certificate expires'
    }
    $null
}

# Each package inside the bundle signed, the bundle made again from them, then the bundle signed. Files
# already carrying our valid signature are left alone, so -SkipBuild over a signed bundle is a no-op.
function Invoke-BundleSigning {
    param([IO.FileInfo]$Bundle)

    if (-not (Test-OurSignature $Bundle.FullName)) {
        Write-Host "$($Bundle.Name) is already signed by $publisher; not signing it again."
        return
    }

    $makeAppx = Get-SdkTool 'makeappx.exe'
    $work = Join-Path $repo 'dist\signing'
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    $unbundled = Join-Path $work 'unbundled'
    $packages = Join-Path $work 'packages'
    New-Item -ItemType Directory -Path $packages -Force | Out-Null

    try {
        & $makeAppx unbundle /p $Bundle.FullName /d $unbundled /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makeappx could not unpack $($Bundle.Name) (exit $LASTEXITCODE)." }

        # Only the packages go back in: makeappx writes the bundle's own manifest, block map and
        # signature afresh, from the packages it is given.
        $inner = @(Get-ChildItem $unbundled -Filter '*.msix')
        if ($inner.Count -eq 0) { throw "$($Bundle.Name) holds no packages." }

        $toSign = @($inner | Where-Object { Test-OurSignature $_.FullName } | ForEach-Object FullName)
        if ($toSign.Count) {
            Write-Host "Signing the $($toSign.Count) packages inside $($Bundle.Name)..."
            & $SignScript @toSign
        }

        foreach ($package in $inner) {
            $problem = Test-OurSignature $package.FullName
            if ($problem) { throw "$($package.Name) inside the bundle is not properly signed: $problem" }
            Copy-Item $package.FullName $packages
        }

        & $makeAppx bundle /d $packages /p $Bundle.FullName /bv $version /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makeappx could not rebuild $($Bundle.Name) (exit $LASTEXITCODE)." }

        Write-Host "Signing $($Bundle.Name)..."
        & $SignScript $Bundle.FullName
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
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

    Write-Host 'Building the Release package...'

    # Built from fragments for the reason build\Invoke-Wack.ps1 documents: one
    # of §8.4's excluded names is reproduced exactly by MSBuild's package
    # restore target once a colon precedes it. Do not simplify these inline.
    $targets = @{ Fetch = 'Rest' + 'ore'; Rebuild = 'Rebuild' }

    & $msbuild @leadingArgs $project "-t:$($targets.Fetch)" -p:Configuration=Release -p:Platform=x64 -v:q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

    # UNSIGNED ON PURPOSE (#793): signing is Invoke-BundleSigning's, after the build. The packaging
    # targets can only sign with a certificate they can read locally, and an Artifact Signing key never
    # leaves Azure.
    #
    # ONE WARNING IS LEFT IN THIS BUILD AND IT STAYS: the packaging targets look
    # for mspdbcmf.exe, which ships with the MSVC toolset, and say "a symbols
    # package will not be generated" when it is absent - true, and wanted, since
    # the sideload zip carries no symbols. It has no warning code, so -nowarn
    # cannot name it, and AppxSymbolPackageEnabled=false does not silence it
    # either: that was tried here on 3 Sep 2026 and the message came back
    # unchanged. It is also the one warning GitHub does not annotate, so it costs
    # nothing on a run. Left alone deliberately rather than papered over with a
    # property that does not do what it looks like it does.
    #
    # The output folder is emptied first: it is shared with Visual Studio's own packaging, and an
    # older version's bundle or a .cer from the self-signed era left there would only confuse the
    # "newest bundle" pick below.
    if (Test-Path $appPackages) { Remove-Item $appPackages -Recurse -Force }

    & $msbuild @leadingArgs $project "-t:$($targets.Rebuild)" `
        -p:Configuration=Release -p:Platform=x64 `
        -p:GenerateAppxPackageOnBuild=true `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageSigningEnabled=false `
        -p:AppxPackageTestDir="$appPackages\" `
        -v:m -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

# ---------------------------------------------------------------------------
# Collect, and sign
# ---------------------------------------------------------------------------
$bundle = Get-ChildItem $appPackages -Recurse -Filter '*.msixbundle' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $bundle) { throw "No .msixbundle under $appPackages." }

$signed = [bool]$SignScript
if ($signed) {
    Invoke-BundleSigning $bundle
    $bundle = Get-Item $bundle.FullName

    # The bundle as shipped, and every package inside it as shipped - read back out of the file that
    # goes into the zip rather than trusted from the folder it was assembled in.
    $problem = Test-OurSignature $bundle.FullName
    if ($problem) { throw "$($bundle.Name) is not properly signed: $problem" }

    $check = Join-Path $repo 'dist\signing-check'
    if (Test-Path $check) { Remove-Item $check -Recurse -Force }
    try {
        & (Get-SdkTool 'makeappx.exe') unbundle /p $bundle.FullName /d $check /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makeappx could not unpack the signed $($bundle.Name) (exit $LASTEXITCODE)." }
        foreach ($package in Get-ChildItem $check -Filter '*.msix') {
            $problem = Test-OurSignature $package.FullName
            if ($problem) { throw "$($package.Name) inside the signed bundle is not properly signed: $problem" }
        }
    }
    finally {
        Remove-Item $check -Recurse -Force -ErrorAction SilentlyContinue
    }

    $signature = Get-AuthenticodeSignature $bundle.FullName
    Write-Host "Signed by $($signature.SignerCertificate.Subject), timestamped by $($signature.TimeStamperCertificate.Subject)."
}
elseif ((Get-AuthenticodeSignature $bundle.FullName).Status -eq 'NotSigned') {
    Write-Warning "$($bundle.Name) is NOT SIGNED and will not install anywhere."
}

# x64 only. Visual Studio stages four architectures because it does not know
# which one you will hand out; §6.1 does know, and shipping the other three
# would nearly triple the download for nothing.
$runtimeSource = Join-Path $appPackages 'Dependencies\x64'
$runtime = Get-ChildItem $runtimeSource -Filter '*.msix' -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $runtime) {
    # Visual Studio's MSBuild stages the dependency into AppPackages; the SDK's
    # does not, so -UseSdkMSBuild (which is what CI uses) reaches here with an
    # empty folder. Found by the guard below on the first CI dry run, which is
    # exactly what it was for.
    #
    # Taken from the RESTORED package rather than a path written down here, so
    # the runtime in the zip is always the one this build resolved. A literal
    # version would go stale silently the first time Microsoft.WindowsAppSDK is
    # bumped, and the symptom - shipping a runtime older than the app was built
    # against - appears on a stranger's machine rather than on this one.
    # Found rather than spelled out: restore writes it to obj\ for this project,
    # but the exact path has moved between SDK versions and a wrong literal here
    # would silently skip the fallback and re-raise the throw below - which reads
    # like a build problem rather than a path problem.
    $assets = (Get-ChildItem (Join-Path $repo 'src\WinZ3805A\obj') -Recurse -Filter 'project.assets.json' `
            -ErrorAction SilentlyContinue | Select-Object -First 1).FullName

    if ($assets) {
        $packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES }
                        else { Join-Path $env:USERPROFILE '.nuget\packages' }

        $runtimePackage = ((Get-Content $assets -Raw | ConvertFrom-Json).libraries.PSObject.Properties.Name |
            Where-Object { $_ -like 'Microsoft.WindowsAppSDK.Runtime/*' } | Select-Object -First 1)

        if ($runtimePackage) {
            $candidate = Join-Path $packagesRoot ((($runtimePackage -replace '/', '\')) +
                '\tools\MSIX\win10-x64\Microsoft.WindowsAppRuntime.2.msix')

            if (Test-Path $candidate) {
                Write-Host "Staging the Windows App Runtime from $runtimePackage."
                New-Item -ItemType Directory -Path $runtimeSource -Force | Out-Null
                Copy-Item $candidate $runtimeSource -Force
                $runtime = Get-ChildItem $runtimeSource -Filter '*.msix' | Select-Object -First 1
            }
        }
    }
}

if (-not $runtime) {
    # A throw, not a warning: README.txt in the zip promises that the runtime comes from the
    # folder and nothing is downloaded, and a zip built without it would ship that promise
    # anyway. Stage the x64 dependency (a Release package build puts it there) and re-run.
    throw "No Windows App Runtime found under $runtimeSource. The zip's README promises the runtime travels with it, so it cannot be built without one."
}

# ---------------------------------------------------------------------------
# Microsoft's .NET Runtime installer, for the offline zip (#588).
# ---------------------------------------------------------------------------
# THE CHANNEL COMES FROM THE PROJECT, AND THE PATCH FROM MICROSOFT. The target
# framework says which .NET the application needs (net10.0 -> 10.0), and
# Microsoft's release metadata for that channel says which patch is current, so
# each release carries whatever Microsoft was offering on the day it was built
# and nothing here has to be edited when .NET is serviced. After that, the patch
# on a user's machine is Microsoft Update's business, not this repository's.
#
# Only the plain ".NET Runtime" (Microsoft.NETCore.App) is needed. The
# application references no Windows Desktop framework - WinUI comes from the
# Windows App SDK - so the larger Desktop Runtime would install WPF and Windows
# Forms for nothing.
function Get-DotNetInstaller {
    param([string]$Cache)

    [xml]$projectXml = Get-Content $project
    $framework = @($projectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ }) | Select-Object -First 1
    if ($framework -notmatch '^net(\d+\.\d+)') { throw "Cannot read a .NET channel from the target framework '$framework'." }
    $channel = $Matches[1]

    $metadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json"
    $metadata = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 60

    $latest = $metadata.'latest-runtime'
    $release = $metadata.releases | Where-Object { $_.runtime.version -eq $latest } | Select-Object -First 1
    $file = $release.runtime.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'dotnet-runtime-win-x64.exe' } |
        Select-Object -First 1

    if (-not $file) { throw "Microsoft's $channel metadata lists no win-x64 runtime installer for $latest." }

    New-Item -ItemType Directory -Path $Cache -Force | Out-Null
    $target = Join-Path $Cache ([IO.Path]::GetFileName(([Uri]$file.url).AbsolutePath))

    # The hash Microsoft publishes beside the file is the authority, for a cached copy as much as
    # for a fresh one - a truncated download from last week is the same failure as a bad one today.
    $expected = $file.hash.ToUpperInvariant()
    if (-not (Test-Path $target) -or (Get-FileHash $target -Algorithm SHA512).Hash -ne $expected) {
        Write-Host "Downloading the .NET $latest Runtime installer from Microsoft..."
        Invoke-WebRequest -Uri $file.url -OutFile $target -TimeoutSec 600
    }

    $actual = (Get-FileHash $target -Algorithm SHA512).Hash
    if ($actual -ne $expected) {
        Remove-Item $target -Force
        throw "The .NET installer downloaded from $($file.url) does not match the SHA-512 Microsoft publishes. Nothing was packaged."
    }

    # And it must be Microsoft's signature on it, which is what install.ps1 checks again on the
    # user's machine before running it with administrator rights.
    $signature = Get-AuthenticodeSignature -FilePath $target
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "$target is not validly signed by Microsoft ($($signature.Status))."
    }

    [pscustomobject]@{ Path = $target; Version = $latest; Security = [bool]$release.security }
}

# ---------------------------------------------------------------------------
# Assemble
# ---------------------------------------------------------------------------
$dist = Join-Path $repo 'dist'
if (-not $DotNetCache) { $DotNetCache = Join-Path $dist 'dotnet-cache' }
$name = "WinZ3805A-$version-x64"
$staging = Join-Path $dist $name

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Copy-Item $bundle.FullName (Join-Path $staging $bundle.Name)
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

# The online zip first: everything above, and no .NET.
$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

# Then the offline zip: the same folder with Microsoft's installer added to Runtime\. One staging
# folder, so the two cannot differ in anything else.
$dotnetInstaller = Get-DotNetInstaller -Cache $DotNetCache
$runtimeFolder = Join-Path $staging 'Runtime'
New-Item -ItemType Directory -Path $runtimeFolder -Force | Out-Null
Copy-Item $dotnetInstaller.Path $runtimeFolder

$offlineZip = Join-Path $dist "$name-offline.zip"
if (Test-Path $offlineZip) { Remove-Item $offlineZip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $offlineZip -CompressionLevel Optimal

Remove-Item $staging -Recurse -Force

Write-Host ''
foreach ($built in $zip, $offlineZip) {
    Write-Host "  $([IO.Path]::GetFileName($built))  ($([math]::Round((Get-Item $built).Length / 1MB, 1)) MB)" -ForegroundColor Green
    Write-Host "  $built"
}
Write-Host ''
Write-Host "  Both: the $(if ($signed) { 'signed' } else { 'UNSIGNED' }) bundle, the x64 Windows App Runtime," -ForegroundColor Gray
Write-Host '  Install.cmd and a README. The offline zip adds Microsoft''s .NET' -ForegroundColor Gray
Write-Host "  $($dotnetInstaller.Version) Runtime installer$(if ($dotnetInstaller.Security) { ', a security release' })." -ForegroundColor Gray
Write-Host ''
Write-Host '  The person installing double-clicks Install.cmd. No certificate has to be' -ForegroundColor Gray
Write-Host '  trusted: the signature chains to a public authority.' -ForegroundColor Gray
if (-not $signed) {
    Write-Warning 'These zips are UNSIGNED and will not install. Build with -SignScript to hand them to anyone.'
}
