<#
.SYNOPSIS
    Installs WinZ3805A on a machine that has never had a developer tool on it.

.DESCRIPTION
    Four things have to happen, and at most two of them need administrator rights:

      1. Trust the certificate the application is signed with. Needs elevation,
         because the certificate store it goes into belongs to the machine.
      2. Make sure .NET 10 is installed (#588). The OFFLINE zip carries
         Microsoft's own runtime installer and runs it here, machine-wide, which
         needs elevation too - asked for in the same prompt as step 1, so there
         is still only one. The ONLINE zip carries no runtime: it opens
         Microsoft's download page instead. Either way .NET ends up installed the
         ordinary way, by Microsoft's installer, which is what lets Microsoft
         Update keep it patched; this application never carries or services a
         runtime of its own (#586, #588).
      3. Install the Windows App Runtime, if it is not already there.
      4. Install the application itself.
      5. Replace any EARLIER COPY that this version cannot upgrade (#590). A
         release signed under a different publisher is a different package
         family to Windows, so it installs alongside rather than over the old
         one - as v1.3.1 did over every release from v1.0.1 to v1.3.0, and as
         the Store identity will again. Its data is saved to Documents and moved
         into the new copy, then the earlier copy is removed; the certificates
         earlier releases were signed with come out of Trusted People in the
         same administrator prompt as step 1. A release under the SAME publisher
         needs none of this: Windows upgrades it in place.

    Steps 3 to 5 run as the person who started this, NOT elevated, and that is
    deliberate. Installing an app is a per-user operation: elevating the whole
    script would install it for whichever administrator the UAC prompt
    authenticated, which on a shared machine is not the person at the keyboard.
    They would then see the install succeed and no application anywhere.

.NOTES
    EVERY RUN WRITES A LOG (#592), to
    %LOCALAPPDATA%\WinZ3805A Installer\logs\install-<date>-<time>.log, and says
    where at the end and on any failure. It records what was found, what was
    removed or kept and why, the state of the machine before and after, and
    whether the application then started - so "it didn't work" arrives with
    the file that says why.

    The certificate is self-signed, and this script says so plainly rather than
    hurrying the user past it. What it grants is narrow - see the TrustedPeople
    comment below - but it is still a decision the user is entitled to make with
    their eyes open.
#>

[CmdletBinding()]
param(
    # Set when this script re-launches itself elevated to do the steps that need
    # administrator rights, and nothing else. Not for a user to pass.
    #
    # NO PARAMETER MAY SHARE A NAME WITH A VARIABLE BELOW, IN ANY CASE. PowerShell
    # variable names ignore case, so a local $elevated IS the parameter $Elevated,
    # and assigning to it converts to the parameter's type. That shipped once in a
    # test build: the prompt's Process was assigned to a [switch] and threw after
    # the elevated half had already succeeded, and the local $dotnetInstaller - a
    # file - became a [string] holding its bare name, so its path was empty. Found
    # on a clean VM on 30 Sep 2026 (#588). Hence names nothing else would use.
    [switch]$AsAdministrator,
    [switch]$TrustCertificate,
    [string]$DotNetInstallerPath,
    # Thumbprints, comma-separated, of certificates earlier releases were signed with.
    [string]$RemoveCertificates,
    # The run's log, passed to the elevated half so both write to one file.
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------------------------------------------------------------------------
# The log (#592). Set up before anything that can fail, so a failure is in it.
# ---------------------------------------------------------------------------
# Under the person's own local application data rather than beside Install.cmd: the extracted
# folder is often deleted straight after installing, and the log is wanted afterwards, when the
# application will not start. This script is not packaged, so the path is not redirected.
if (-not $LogPath) {
    $logFolder = Join-Path $env:LOCALAPPDATA 'WinZ3805A Installer\logs'
    New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
    $LogPath = Join-Path $logFolder ('install-{0}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

# Never allowed to fail the install: a log that cannot be written is a lost line, not a lost run.
function Write-Log {
    param([string]$Text)
    $prefix = if ($AsAdministrator) { '[elevated] ' } else { '' }
    try {
        Add-Content -LiteralPath $LogPath -Encoding UTF8 -ErrorAction Stop `
            -Value ('{0}  {1}{2}' -f (Get-Date -Format 'HH:mm:ss.fff'), $prefix, $Text)
    }
    catch { }
}

# Every terminating error, wherever it is thrown, with where it came from. The friendly messages
# below are written for the person at the keyboard; this is written for whoever reads the log.
trap {
    Write-Log "FAILED: $($_.Exception.Message)"
    Write-Log "  at line $($_.InvocationInfo.ScriptLineNumber): $("$($_.InvocationInfo.Line)".Trim())"
    if ($_.ScriptStackTrace) { Write-Log "  $($_.ScriptStackTrace -replace '\r?\n', ' <- ')" }
    if (-not $AsAdministrator) {
        try { Write-State 'at failure' } catch { }
        Write-Host ''
        Write-Host "  A record of this install is at $LogPath" -ForegroundColor Yellow
        Write-Host '  Please include it when reporting the problem.' -ForegroundColor Yellow
    }
    break
}
$certificate = Get-ChildItem -Path $here -Filter '*.cer' | Select-Object -First 1
$bundle = Get-ChildItem -Path $here -Filter '*.msixbundle' | Select-Object -First 1
$runtime = Get-ChildItem -Path (Join-Path $here 'Runtime') -Filter '*.msix' -ErrorAction SilentlyContinue |
    Select-Object -First 1
# Present in the offline zip only, and that presence is the whole difference between the two.
$dotnetInstaller = Get-ChildItem -Path (Join-Path $here 'Runtime') -Filter 'dotnet-runtime-*.exe' -ErrorAction SilentlyContinue |
    Select-Object -First 1

# Where Microsoft's page for the runtime is. The ONLINE zip opens it; nothing is downloaded by
# this script, so what the person installs is exactly what Microsoft is offering that day.
$dotnetPage = 'https://dotnet.microsoft.com/download/dotnet/10.0'

# The Microsoft Update service, as Windows Update's service manager names it. Registered with
# automatic updates exactly when "Receive updates for other Microsoft products" is on, which is
# the one setting that decides whether Windows patches .NET by itself.
$microsoftUpdateId = '7971f918-a847-4430-9279-4a52d1efe18d'

# Certificates published releases were signed with and no longer are (#590). Listed by thumbprint,
# which names one certificate exactly, so a leftover is found even when the application it came
# with was removed by hand long ago - which is how it is usually left behind, since removing an
# app in Settings never touches Trusted People. Measured on the development machine on
# 30 Sep 2026: the first entry was still trusted there, a day after its application had gone.
$retiredThumbprints = @(
    # Every release from v1.0.1 to v1.3.0, under the earlier publisher identity.
    '655D07E31BDA80CBF6AAC2F2635EA6664B9399DD'
)

# What the person sees also goes in the log, so the log reads as the run did.
function Write-Step { param([string]$Text) Write-Host ''; Write-Host $Text -ForegroundColor Cyan; Write-Log "== $Text" }
function Write-Ok { param([string]$Text) Write-Host "  $Text" -ForegroundColor Green; Write-Log "ok    $Text" }
function Write-Info { param([string]$Text) Write-Host "  $Text" -ForegroundColor Gray; if ($Text) { Write-Log "info  $Text" } }

# The newest .NET 10 runtime installed machine-wide, or nothing. Read from the folder the .NET host
# itself loads from - its location is what the installer records in the registry - because a
# runtime the host cannot find is no runtime at all, whatever the registry lists. Any 10.x will
# do: an application built for 10.0 rolls forward to the newest 10.x patch installed.
function Get-DotNet10 {
    $location = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64' -ErrorAction SilentlyContinue).InstallLocation
    if (-not $location) { $location = Join-Path $env:ProgramFiles 'dotnet' }

    $folder = Join-Path $location 'shared\Microsoft.NETCore.App'
    if (-not (Test-Path $folder)) { return $null }

    Get-ChildItem $folder -Directory |
        Where-Object { $_.Name -match '^10\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1 -ExpandProperty Name
}

# Where a package family's copy of this application keeps everything it stores: the history
# (trend.db), the settings files and the logs. Every release since v1.0.1 writes only under
# %LOCALAPPDATA%\WinZ3805A, which Windows redirects into the package's own LocalCache - checked
# against every tag - so this one folder is the whole of a copy's data.
function Get-DataFolder {
    param([string]$Family)
    Join-Path $env:LOCALAPPDATA "Packages\$Family\LocalCache\Local\WinZ3805A"
}

# The machine as far as this application is concerned, into the log. Called before anything
# changes, after everything has, and on failure. Publishers are logged by Windows' short publisher
# ID and by whether they are this release's, never by name.
function Write-State {
    param([string]$Label)

    Write-Log "---- state: $Label ----"

    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        Write-Log "windows       $($os.Caption) $($os.Version) build $($os.BuildNumber), $($os.OSArchitecture)"
    }
    catch { Write-Log "windows       could not be read: $($_.Exception.Message)" }

    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Log "powershell    $($PSVersionTable.PSVersion), $(if ($admin) { 'elevated' } else { 'not elevated' })"

    $copies = @(Get-AppxPackage -Name 'WinZ3805A' -ErrorAction SilentlyContinue)
    if ($copies.Count -eq 0) { Write-Log 'winz3805a     none installed for this account' }
    foreach ($copy in $copies) {
        Write-Log ("winz3805a     {0} {1}, signature {2}, development {3}, publisher id {4}{5}, status {6}" -f
            $copy.PackageFamilyName, $copy.Version, $copy.SignatureKind, $copy.IsDevelopmentMode,
            $copy.PublisherId, $(if ($copy.Publisher -eq $publisher) { ' (this release)' } else { ' (another)' }), $copy.Status)
    }

    foreach ($package in @(Get-AppxPackage -Name '*WinAppRuntime*' -ErrorAction SilentlyContinue) +
                         @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' -ErrorAction SilentlyContinue)) {
        Write-Log "app runtime   $($package.Name) $($package.Version) $($package.Architecture)"
    }

    $dotnetFolder = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'
    $runtimes = @(Get-ChildItem $dotnetFolder -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
    Write-Log "dotnet        $(if ($runtimes.Count) { $runtimes -join ', ' } else { 'no Microsoft.NETCore.App runtimes' })"

    $publishers = @($copies | ForEach-Object Publisher)
    foreach ($trusted in @(Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue)) {
        $role = if ($trusted.Thumbprint -eq $thumbprint) { 'this release' }
            elseif ($retiredThumbprints -contains $trusted.Thumbprint) { 'an earlier release' }
            elseif ($publishers -contains $trusted.Subject) { 'an installed copy''s publisher' }
            else { $null }
        if ($role) { Write-Log "certificate   $($trusted.Thumbprint), expires $($trusted.NotAfter.ToString('yyyy-MM-dd')), $role" }
    }

    foreach ($folder in @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Filter 'WinZ3805A_*' -Directory -ErrorAction SilentlyContinue)) {
        $data = Get-DataFolder $folder.Name
        $trend = Join-Path $data 'trend.db'
        Write-Log ("data          {0}: {1}" -f $folder.Name, $(
            if (Test-Path $trend) { "trend.db {0:N1} MB" -f ((Get-Item $trend).Length / 1MB) }
            elseif (Test-Path $data) { 'data folder, no trend.db' }
            else { 'no data folder' }))
    }

    Write-Log "ms update     $(switch (Test-MicrosoftUpdate) { $true { 'on' } $false { 'off' } default { 'unknown' } })"
}

# A package file's identity, read from its own manifest rather than guessed from its file name.
function Get-PackageIdentity {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $reader = New-Object IO.StreamReader($archive.GetEntry('AppxManifest.xml').Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally {
        $archive.Dispose()
    }

    [pscustomobject]@{
        Name    = $manifest.Package.Identity.Name
        Version = [version]$manifest.Package.Identity.Version
    }
}

# The newest installed x64 package of that name, at the given version or above, or nothing.
function Get-InstalledAtLeast {
    param([string]$Name, [version]$Minimum)

    Get-AppxPackage -Name $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.Architecture -eq 'X64' -and [version]$_.Version -ge $Minimum } |
        Sort-Object { [version]$_.Version } -Descending |
        Select-Object -First 1
}

# $true when Microsoft Update is on, $false when it is off, $null when Windows would not say.
function Test-MicrosoftUpdate {
    try {
        $manager = New-Object -ComObject Microsoft.Update.ServiceManager
        foreach ($service in $manager.Services) {
            if ($service.ServiceID -eq $microsoftUpdateId) { return [bool]$service.IsRegisteredWithAU }
        }
        return $false
    }
    catch {
        return $null
    }
}

# ---------------------------------------------------------------------------
# The elevated half: one certificate into one store, and - offline only - one
# Microsoft installer, and the certificates earlier releases left behind.
# Exit codes: 10, the certificate; 20, the runtime installer failed; 21, the
# runtime installer is not signed by Microsoft; 30, an earlier certificate
# could not be removed.
# ---------------------------------------------------------------------------
if ($AsAdministrator) {
    if ($TrustCertificate) {
        # LocalMachine\TrustedPeople, never Root. The distinction is the whole
        # reason this is defensible: a certificate in TrustedPeople is trusted to
        # sign *applications you install by hand* and nothing else. It cannot vouch
        # for a website, and it cannot make arbitrary code look like it came from
        # Microsoft. Root would do both.
        $certutil = certutil.exe -addstore TrustedPeople $certificate.FullName
        Write-Log "certutil -addstore exited $LASTEXITCODE"
        if ($LASTEXITCODE -ne 0) { Write-Log "certutil said: $($certutil -join ' ')"; exit 10 }
    }

    if ($RemoveCertificates) {
        # Only ever from TrustedPeople, only by exact thumbprint, and only thumbprints the
        # unelevated half chose and showed the person before asking - see step 1.
        foreach ($stale in ($RemoveCertificates -split ',')) {
            $path = "Cert:\LocalMachine\TrustedPeople\$($stale.Trim())"
            if (Test-Path $path) {
                try { Remove-Item $path -Force -ErrorAction Stop; Write-Log "removed certificate $($stale.Trim())" }
                catch { Write-Log "could not remove certificate $($stale.Trim()): $($_.Exception.Message)"; exit 30 }
            }
            else { Write-Log "certificate $($stale.Trim()) was already gone" }
        }
    }

    if ($DotNetInstallerPath) {
        # Checked again here, with administrator rights, immediately before it runs: this is the
        # one file this script executes elevated, and it must be Microsoft's and unmodified.
        $signature = Get-AuthenticodeSignature -FilePath $DotNetInstallerPath
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            Write-Log "refused $DotNetInstallerPath - signature $($signature.Status), signer $($signature.SignerCertificate.Subject)"
            exit 21
        }

        # Quiet and machine-wide, exactly as the installer from dotnet.microsoft.com does it when
        # double-clicked - which is the point: Microsoft Update recognises this install and keeps
        # it patched. 3010 is "installed; a restart would finish it", and is success.
        $log = Join-Path $here 'dotnet-install.log'
        $process = Start-Process -FilePath $DotNetInstallerPath -Wait -PassThru `
            -ArgumentList '/install', '/quiet', '/norestart', '/log', "`"$log`""
        Write-Log "$([IO.Path]::GetFileName($DotNetInstallerPath)) exited $($process.ExitCode); its own log is $log"
        if ($process.ExitCode -notin 0, 3010) { exit 20 }
    }

    exit 0
}

Write-Host ''
Write-Host '  WinZ3805A' -ForegroundColor White
Write-Host '  Monitoring and control for HP/Symmetricom GPS-disciplined oscillators.'
Write-Host ''

$runStarted = Get-Date
Write-Log "WinZ3805A installer, run from $here"
Write-Log ("download      {0}, {1}" -f $(if ($bundle) { $bundle.Name } else { 'NO BUNDLE' }),
    $(if ($dotnetInstaller) { "offline, with $($dotnetInstaller.Name)" } else { 'online, no .NET installer' }))

if (-not $certificate) { throw 'The certificate is missing from this folder. Download the release again.' }
if (-not $bundle) { throw 'The application package is missing from this folder. Download the release again.' }

$signingCertificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
    -ArgumentList $certificate.FullName
$thumbprint = $signingCertificate.Thumbprint

# The package's publisher IS the certificate's subject - New-SideloadPackage.ps1 refuses to build
# otherwise - so this is the identity being installed, read off what is in the folder.
$publisher = $signingCertificate.Subject
Write-Log "signing       $thumbprint, expires $($signingCertificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-State 'before'

# ---------------------------------------------------------------------------
# Earlier copies (#590). Found now, acted on after the new copy is installed.
# ---------------------------------------------------------------------------
# An EARLIER COPY is WinZ3805A under any other publisher: a different package family, which this
# install would sit beside rather than replace. Read from Windows, never named here. Store-signed
# copies are never touched by a sideload installer. Other Windows accounts are not looked at -
# that needs rights this half does not have, and their copies are theirs.
$allCopies = @(Get-AppxPackage -Name 'WinZ3805A' -ErrorAction SilentlyContinue)
$earlier = @($allCopies | Where-Object { $_.Publisher -ne $publisher -and $_.SignatureKind -ne 'Store' } |
    Sort-Object { [version]$_.Version } -Descending)
$keptPublishers = @($allCopies | Where-Object { $earlier.PackageFullName -notcontains $_.PackageFullName } |
    ForEach-Object Publisher)

# A certificate is stale if a published release was signed with it, or it vouches for an earlier
# copy being removed - and in neither case if it is the one being installed, or anything left
# installed still carries its publisher.
$staleCertificates = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Thumbprint -ne $thumbprint -and
        $keptPublishers -notcontains $_.Subject -and
        (($retiredThumbprints -contains $_.Thumbprint) -or ($earlier.Publisher -contains $_.Subject))
    })

# Each decision, with its reason, so the log says what was left as clearly as what was removed.
foreach ($copy in $allCopies) {
    $decision = if ($copy.SignatureKind -eq 'Store') { 'keep: installed from the Store' }
        elseif ($copy.Publisher -eq $publisher) { 'upgrade in place: this release''s publisher' }
        else { 'replace: an earlier publisher identity, which cannot be upgraded in place' }
    Write-Log "decide copy   $($copy.PackageFamilyName) $($copy.Version): $decision"
}
foreach ($trusted in @(Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue)) {
    $related = ($trusted.Thumbprint -eq $thumbprint) -or ($retiredThumbprints -contains $trusted.Thumbprint) -or
        (@($allCopies | ForEach-Object Publisher) -contains $trusted.Subject)
    if (-not $related) { continue }
    $decision = if ($trusted.Thumbprint -eq $thumbprint) { 'keep: this release''s certificate' }
        elseif ($staleCertificates.Thumbprint -contains $trusted.Thumbprint) {
            if ($retiredThumbprints -contains $trusted.Thumbprint) { 'remove: an earlier release was signed with it' }
            else { 'remove: it vouches for a copy being replaced' } }
        else { 'keep: a copy that stays installed still uses its publisher' }
    Write-Log "decide cert   $($trusted.Thumbprint): $decision"
}

# A running copy holds the serial port and its own files, and one that lives in the notification
# area for weeks is very likely running. Asked to close, never closed: it may be mid-survey.
while (Get-Process -Name 'WinZ3805A' -ErrorAction SilentlyContinue) {
    Write-Log 'waiting       WinZ3805A is running; asked the person to close it' 
    Write-Host ''
    Write-Host '  WinZ3805A is running. Close it before installing:' -ForegroundColor Yellow
    Write-Host '  right-click its icon by the clock and choose Exit, or in its Details window,' -ForegroundColor Yellow
    Write-Host '  Settings > Exit WinZ3805A.' -ForegroundColor Yellow
    Read-Host '  Press Enter once it has closed'
}

if ($earlier.Count -gt 0 -or $staleCertificates.Count -gt 0) {
    Write-Step 'Before anything changes'

    foreach ($copy in $earlier) {
        Write-Info "An earlier WinZ3805A is installed: version $($copy.Version) ($($copy.PackageFamilyName))."
    }

    if ($earlier.Count -gt 0) {
        Write-Info 'It was signed under an earlier publisher identity, so Windows treats this'
        Write-Info 'version as a different application and cannot upgrade it. After installing,'
        Write-Info 'this installer will:'
        Write-Info '  - save its data - history, settings and logs - to your Documents folder;'
        Write-Info '  - move that data into the new copy, if the new copy has none yet;'
        Write-Info '  - then remove the earlier copy.'
        Write-Info 'Nothing is removed until the data has been saved.'
    }

    foreach ($stale in $staleCertificates) {
        Write-Info "An earlier release's certificate will be removed from Trusted People:"
        Write-Info "  $($stale.Thumbprint.Substring(0, 8))..., expires $($stale.NotAfter.ToString('d MMM yyyy'))."
    }

    if ($staleCertificates.Count -gt 0) {
        Write-Info 'It is no longer needed, and left in place it would go on vouching for'
        Write-Info 'anything signed with it.'
    }
}

# ---------------------------------------------------------------------------
# 1. Trust
# ---------------------------------------------------------------------------
Write-Step '1 of 4  Trusting the signature'

$trustNeeded = -not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumbprint")

if (-not $trustNeeded) {
    Write-Ok 'Already trusted. Nothing to do.'
}
else {
    Write-Info 'Windows will ask for administrator permission once, after the steps'
    Write-Info 'below have said what they need.'
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
}

# ---------------------------------------------------------------------------
# 2. .NET 10
# ---------------------------------------------------------------------------
Write-Step '2 of 4  .NET 10'

$dotnet = Get-DotNet10
$dotnetNeeded = (-not $dotnet) -and $dotnetInstaller

if ($dotnet) {
    Write-Ok "Already installed ($dotnet)."
}
elseif ($dotnetInstaller) {
    # Checked before asking for anything, so a tampered download stops here rather than at a
    # prompt the person has already agreed to. The elevated half checks again before running it.
    $signature = Get-AuthenticodeSignature -FilePath $dotnetInstaller.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "$($dotnetInstaller.Name) in the Runtime folder is not validly signed by Microsoft, so it will not be run. Download the release again."
    }

    Write-Info 'Not installed. This download includes Microsoft''s own .NET 10 Runtime'
    Write-Info "installer ($($dotnetInstaller.Name)), and it will run from this"
    Write-Info 'folder - nothing is downloaded. It installs .NET for the whole machine,'
    Write-Info 'exactly as the installer from dotnet.microsoft.com does, so Windows can'
    Write-Info 'keep it patched once this machine is online.'
}
else {
    Write-Info 'Not installed. WinZ3805A needs the free .NET 10 Runtime from Microsoft.'
    Write-Info ''
    Write-Info "Opening $dotnetPage"
    Write-Info 'On that page, under ".NET Runtime", choose the Windows x64 installer and'
    Write-Info 'run it. You can finish installing WinZ3805A first; it will start once'
    Write-Info '.NET is installed.'
    Write-Info ''
    Write-Info 'No internet connection on this machine? Use the offline download'
    Write-Info '(the zip whose name ends in -offline), which includes the installer.'

    try { Start-Process $dotnetPage } catch { Write-Info 'The page could not be opened; the address is above.' }
}

# ---------------------------------------------------------------------------
# The one administrator prompt, for whichever of the above need it.
# ---------------------------------------------------------------------------
$elevationNeeded = $trustNeeded -or $dotnetNeeded -or ($staleCertificates.Count -gt 0)

if (-not $elevationNeeded -and $earlier.Count -gt 0) {
    # Nothing needs administrator rights, but an earlier copy is about to be replaced, and that is
    # not something to do without the person having read what it means.
    Write-Host ''
    Read-Host '  Press Enter to continue, or close this window to stop'
}

if ($elevationNeeded) {
    Write-Host ''
    Write-Info 'Windows will now ask once for administrator permission, for everything'
    Write-Info 'above that needs it.'
    Write-Info ''

    # Wait for the reader before raising the prompt. Without this the UAC dialog
    # appears over the explanation within a fraction of a second, so the text is
    # on screen and unread — which is worse than not writing it, because it
    # looks like consent was informed when it could not have been. Reported by
    # the first person to run this.
    Read-Host '  Press Enter to continue, or close this window to stop'

    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$($MyInvocation.MyCommand.Path)`"",
        '-AsAdministrator'
    )
    if ($trustNeeded) { $arguments += '-TrustCertificate' }
    if ($dotnetNeeded) { $arguments += @('-DotNetInstallerPath', "`"$($dotnetInstaller.FullName)`"") }
    if ($staleCertificates.Count -gt 0) { $arguments += @('-RemoveCertificates', (($staleCertificates | ForEach-Object Thumbprint) -join ',')) }
    $arguments += @('-LogPath', "`"$LogPath`"")

    if ($dotnetNeeded) { Write-Info 'Installing .NET 10 can take a minute or two.' }

    Write-Log "elevating for:$(if ($trustNeeded) { ' certificate' })$(if ($dotnetNeeded) { ' .NET' })$(if ($staleCertificates.Count) { ' earlier certificates' })"
    try {
        $elevation = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList $arguments
    }
    catch {
        # Declining the prompt surfaces here, as an exception rather than an exit code - and it is
        # the only thing that may be reported as a decline. Anything else is this script's fault, and
        # saying "declined, nothing changed" about it would be false twice over: #588's test build did
        # exactly that after the elevated half had already done its work.
        Write-Log "elevation did not start: $($_.Exception.Message)"
        if ($_.Exception.Message -match 'cancel') {
            throw 'The administrator prompt was declined, so nothing was changed. Run this installer again and agree to it.'
        }
        throw "The administrator step could not be started: $($_.Exception.Message)"
    }
    Write-Log "elevated half exited $($elevation.ExitCode)"

    switch ($elevation.ExitCode) {
        0 { }
        10 { throw 'The certificate was not trusted, so the application cannot be installed. Nothing else has been changed.' }
        21 { throw "$($dotnetInstaller.Name) is not validly signed by Microsoft, so it was not run. Download the release again." }
        20 { throw ".NET 10 did not install. Microsoft's installer wrote what happened to dotnet-install.log in this folder. You can also install it from $dotnetPage and run this again." }
        30 { throw 'An earlier release''s certificate could not be removed from Trusted People. Nothing else was changed; run this installer again, or remove it in certlm.msc.' }
        default { throw 'The administrator step did not complete - most likely the permission prompt was declined. Run this installer again and agree to it.' }
    }

    if ($trustNeeded) {
        Write-Ok "Trusted. Certificate $($thumbprint.Substring(0, 8))..., issued to $((New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $certificate.FullName).Subject)"
    }

    if ($dotnetNeeded) {
        $dotnet = Get-DotNet10
        if (-not $dotnet) {
            throw "Microsoft's installer reported success, but .NET 10 cannot be found. See dotnet-install.log in this folder."
        }
        Write-Ok ".NET $dotnet installed."
    }

    foreach ($stale in $staleCertificates) {
        Write-Ok "Removed the earlier certificate $($stale.Thumbprint.Substring(0, 8))... from Trusted People."
    }
}

# ---------------------------------------------------------------------------
# 2. The runtime
# ---------------------------------------------------------------------------
Write-Step '3 of 4  Windows App Runtime'

# THE ZIP'S RUNTIME IS A MINIMUM, NOT A REQUIREMENT (#595). The package declares
# MinVersion, and in Windows App SDK 2.x the family name carries only the major
# version, so any 2.x at or above it satisfies the application. A newer one
# arrives on its own - on the reporter's Windows 10 machine it came with a Store
# update to Photos, which then refuses to let it go - and on Windows 10 installing
# the older one beside it is refused as a downgrade. That refusal stopped the
# install before the application was attempted, though the application would have
# installed against the newer runtime unchanged.
#
# So: look first, and install only when nothing good enough is there. And when
# installing fails, judge by what is installed afterwards, not by the words of the
# error - those are localised, and a pattern of English phrases never matched a
# machine running Windows in another language.
if ($runtime) {
    $zipRuntime = Get-PackageIdentity $runtime.FullName
    Write-Log "zip runtime   $($zipRuntime.Name) $($zipRuntime.Version)"

    $present = Get-InstalledAtLeast -Name $zipRuntime.Name -Minimum $zipRuntime.Version

    if ($present) {
        Write-Ok "Already present ($($present.Version)), which is this version or newer."
    }
    else {
        Write-Info 'Installing. This can take a minute.'
        try {
            Add-AppxPackage -Path $runtime.FullName -ErrorAction Stop
            Write-Ok "Installed ($($zipRuntime.Version))."
        }
        catch {
            Write-Log "Add-AppxPackage (runtime) said: $($_.Exception.Message)"

            $present = Get-InstalledAtLeast -Name $zipRuntime.Name -Minimum $zipRuntime.Version
            if ($present) {
                Write-Ok "Present ($($present.Version)), which is this version or newer."
            }
            else {
                throw "The Windows App Runtime could not be installed, and no version $($zipRuntime.Version) or newer is present. Windows said: $($_.Exception.Message)"
            }
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

# THE NEWEST OF THE FAMILY, because that is the one the application binds to, and so the one the
# companions must match (#594). Several versions of one family install side by side - the zip's
# 2.3.1.0 beside a Store-serviced 2.5.1.0, on the 30 Sep 2026 test VM - and "the first one Windows
# lists" was the older there, so the companions were checked against the wrong framework and the
# match reported was not one.
$framework = if ($frameworkName) {
    $installed | Where-Object { $_.Name -eq $frameworkName } |
        Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1
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

Write-Log "framework     $($framework.Name) $($framework.Version), signature $($framework.SignatureKind)"

# A Store-serviced framework carries its companions too - checked 30 Sep 2026 on a machine with the
# Store's 2.5.1.0: MSIX\Main.msix and MSIX\Singleton.msix are both there - so the same folder serves
# whichever framework is newest.
$companions = Join-Path $framework.InstallLocation 'MSIX'
$matched = @()

foreach ($file in 'Main.msix', 'Singleton.msix') {
    $package = Join-Path $companions $file
    if (-not (Test-Path $package)) {
        # A runtime laid out differently to the one this was written against. Say so rather than
        # carrying on: the application would install and then exit without explaining itself.
        throw "The Windows App Runtime at $($framework.InstallLocation) does not carry $file, " +
              'so the application cannot be made to start. Report this with the runtime version: ' +
              "$($framework.Version)."
    }

    # What this framework's companion is, from its own manifest, and what is installed under that
    # name. Equal is done; anything else is installed from the framework's own copy - forced, since
    # the companion must belong to this framework even where that is a downgrade.
    $wanted = Get-PackageIdentity $package
    $current = Get-AppxPackage -Name $wanted.Name -ErrorAction SilentlyContinue |
        Where-Object { $_.Architecture -eq 'X64' } | Select-Object -First 1
    Write-Log "companion     $($wanted.Name): wanted $($wanted.Version), installed $(if ($current) { $current.Version } else { 'none' })"

    if (-not $current -or [version]$current.Version -ne $wanted.Version) {
        try {
            Add-AppxPackage -Path $package -ErrorAction Stop
        }
        catch {
            Write-Log "Add-AppxPackage ($file) said: $($_.Exception.Message)"
            Add-AppxPackage -Path $package -ForceUpdateFromAnyVersion -ErrorAction Stop
        }

        $current = Get-AppxPackage -Name $wanted.Name -ErrorAction SilentlyContinue |
            Where-Object { $_.Architecture -eq 'X64' } | Select-Object -First 1
    }

    # Reported from what is installed now, never asserted.
    if (-not $current -or [version]$current.Version -ne $wanted.Version) {
        throw "$($wanted.Name) should be $($wanted.Version) to match the Windows App Runtime " +
              "$($framework.Version), but is $(if ($current) { $current.Version } else { 'not installed' }). " +
              'The application would install and then close without a window.'
    }

    $matched += "$([IO.Path]::GetFileNameWithoutExtension($file)) $($current.Version)"
}

Write-Ok "Runtime companions match the runtime $($framework.Version): $($matched -join ', ')."

# ---------------------------------------------------------------------------
# 3. The application
# ---------------------------------------------------------------------------
Write-Step '4 of 4  WinZ3805A'

try {
    Add-AppxPackage -Path $bundle.FullName -ErrorAction Stop
    Write-Ok 'Installed.'
}
catch {
    Write-Log "Add-AppxPackage (application) failed: $($_.Exception.Message)"

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

# ---------------------------------------------------------------------------
# The earlier copy (#590), now that the new one is safely installed.
# ---------------------------------------------------------------------------
# In this order and no other: save the data, move it across, and only then remove the package -
# removing a package deletes everything it stored, and Tony's own move off the earlier identity
# carried 22,277 samples of history (#584). If the save fails, the earlier copy stays.
if ($earlier.Count -gt 0) {
    Write-Step 'Replacing the earlier copy'

    $newCopy = Get-AppxPackage -Name 'WinZ3805A' -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -eq $publisher } | Select-Object -First 1
    $newRoot = if ($newCopy) { Join-Path $env:LOCALAPPDATA "Packages\$($newCopy.PackageFamilyName)" } else { $null }
    $newData = if ($newCopy) { Get-DataFolder $newCopy.PackageFamilyName } else { $null }
    $moved = $false

    foreach ($copy in $earlier) {
        $oldData = Get-DataFolder $copy.PackageFamilyName
        $saved = $null

        if ((Test-Path $oldData) -and (Get-ChildItem $oldData -Force -ErrorAction SilentlyContinue)) {
            $documents = [Environment]::GetFolderPath('MyDocuments')
            $saved = Join-Path $documents "WinZ3805A earlier copy $($copy.Version) $(Get-Date -Format 'yyyy-MM-dd HHmm')"

            try {
                Copy-Item -Path $oldData -Destination $saved -Recurse -ErrorAction Stop
            }
            catch {
                Write-Log "save failed: $oldData -> $saved : $($_.Exception.Message)"
                Write-Host "  Could not save the earlier copy's data to $saved, so it has been left installed." -ForegroundColor Yellow
                Write-Host "  $($_.Exception.Message)" -ForegroundColor Yellow
                continue
            }

            Write-Ok "Saved its data to $saved"

            # Moved in only if the new copy has no history of its own, and even then without
            # replacing any file it already has: robocopy's /XC /XN /XO copy only files absent at
            # the destination. The newest earlier copy wins; any other stays in Documents.
            # Windows creates the package's own folders when it installs it; if they are not there,
            # nothing is created by hand under a package's folder.
            if (-not $moved -and $newRoot -and (Test-Path $newRoot) -and
                -not (Test-Path (Join-Path $newData 'trend.db'))) {
                New-Item -ItemType Directory -Path $newData -Force | Out-Null
                robocopy.exe $oldData $newData /E /XC /XN /XO /NFL /NDL /NJH /NJS /NP | Out-Null
                Write-Log "robocopy $oldData -> $newData exited $LASTEXITCODE (below 8 is success)"
                if ($LASTEXITCODE -lt 8) {
                    $moved = $true
                    Write-Ok 'Moved its history and settings into the new copy.'
                }
            }

            if (-not $moved) {
                Write-Log "not moved: new root $newRoot exists=$(if ($newRoot) { Test-Path $newRoot } else { 'n/a' }), new trend.db exists=$(if ($newData) { Test-Path (Join-Path $newData 'trend.db') } else { 'n/a' })"
                Write-Info 'The new copy already has history of its own, so the earlier one was not'
                Write-Info 'copied over it. To add it: Settings > Import history..., and choose'
                Write-Info "  $(Join-Path $saved 'trend.db')"
            }
        }

        try {
            Remove-AppxPackage -Package $copy.PackageFullName -ErrorAction Stop
            Write-Ok "Removed the earlier copy, version $($copy.Version)."
        }
        catch {
            Write-Log "Remove-AppxPackage $($copy.PackageFullName) failed: $($_.Exception.Message)"
            Write-Host "  The earlier copy could not be removed: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host '  Remove it in Settings > Apps; its data is saved as above.' -ForegroundColor Yellow
        }
    }
}

Write-State 'after'

# ---------------------------------------------------------------------------
# Does it start? (#592) The failures worth catching here are the silent ones:
# #473's application installed, launched and exited in about 200 ms with no
# window and nothing in its own log. Only possible once .NET is installed.
# ---------------------------------------------------------------------------
$installedCopy = Get-AppxPackage -Name 'WinZ3805A' -ErrorAction SilentlyContinue |
    Where-Object { $_.Publisher -eq $publisher } | Select-Object -First 1
$startedOk = $null

if ($dotnet -and $installedCopy) {
    Write-Step 'Starting WinZ3805A to check it opens'

    # PROOF IS THE APP'S OWN LOG, NOT A LIVE PROCESS (#599). With .NET missing the process stays
    # alive - to show Windows' "You must install .NET" prompt - so counting processes passed exactly
    # the broken install this exists to catch. Measured on a clean VM on 30 Sep 2026 (#597): app.log
    # was written after the launch in every healthy run and in neither broken one. So it passes only
    # when the app wrote its log after being started AND is still running 15 s later.
    $appLog = Join-Path (Get-DataFolder $installedCopy.PackageFamilyName) 'logs\app.log'
    $launchedAt = Get-Date
    $running = @()
    $logged = $false
    try {
        Start-Process "shell:AppsFolder\$($installedCopy.PackageFamilyName)!App"
        Start-Sleep -Seconds 15
        $running = @(Get-Process -Name 'WinZ3805A' -ErrorAction SilentlyContinue |
            Where-Object { "$($_.Path)" -like "$($installedCopy.InstallLocation)*" })
        $logged = (Test-Path $appLog) -and ((Get-Item $appLog).LastWriteTime -ge $launchedAt)
        $startedOk = ($running.Count -gt 0) -and $logged
    }
    catch {
        Write-Log "could not start it: $($_.Exception.Message)"
        $startedOk = $false
    }
    $windows = @($running | Where-Object { $_.MainWindowHandle -ne 0 } | ForEach-Object { "'$($_.MainWindowTitle)'" })
    Write-Log "start check   processes $($running.Count), windows $(if ($windows.Count) { $windows -join ', ' } else { 'none' }), app.log written since launch $logged"

    if ($startedOk) {
        Write-Ok "It is running (process $($running[0].Id))."
    }
    elseif ($running.Count -gt 0) {
        # Alive but silent: most often Windows' own .NET prompt, which is a window of this process.
        Write-Log 'ALIVE BUT NOT STARTED: the process is up and has written no log since launch. What Windows recorded:'
        Write-Host ''
        Write-Host '  WinZ3805A did not get going: it is showing a window but has not started.' -ForegroundColor Yellow
        Write-Host '  If Windows is asking for .NET, install .NET 10 from' -ForegroundColor Yellow
        Write-Host "  $dotnetPage and start it again." -ForegroundColor Yellow
    }
    else {
        Write-Log 'NOT RUNNING 15 s after being started. What Windows recorded:'
    }

    if (-not $startedOk) {

        # Errors from the run as a whole for deployment, and since the launch for everything else.
        $sources = @(
            @{ LogName = 'Application'; StartTime = $launchedAt },
            @{ LogName = 'Microsoft-Windows-AppModel-Runtime/Admin'; StartTime = $launchedAt },
            @{ LogName = 'Microsoft-Windows-AppXDeploymentServer/Operational'; StartTime = $runStarted; Level = 1, 2, 3 }
        )
        foreach ($filter in $sources) {
            try {
                Get-WinEvent -FilterHashtable $filter -ErrorAction Stop |
                    Where-Object { $_.Message -match 'WinZ3805A' } | Select-Object -First 20 |
                    ForEach-Object { Write-Log ("event         {0} {1} {2} {3}: {4}" -f $_.TimeCreated.ToString('HH:mm:ss'), $filter.LogName, $_.ProviderName, $_.Id, ($_.Message -replace '\s+', ' ')) }
            }
            catch { Write-Log "event         $($filter.LogName): none, or unreadable ($($_.Exception.Message))" }
        }

        if (Test-Path $appLog) {
            Write-Log "app.log       last lines of $appLog"
            Get-Content $appLog -Tail 40 | ForEach-Object { Write-Log "  | $_" }
        }
        else { Write-Log "app.log       none at $appLog - it stopped before it could write one" }

        Write-Host ''
        if ($running.Count -eq 0) { Write-Host '  WinZ3805A did not stay open.' -ForegroundColor Yellow }
        Write-Host '  Please report it with the install record named below.' -ForegroundColor Yellow
    }
}
elseif (-not $dotnet) {
    Write-Log 'start check   skipped: .NET 10 is not installed yet'
}

Write-Host ''
Write-Host '  Done. WinZ3805A is in the Start menu.' -ForegroundColor Green
Write-Host ''

if (-not $dotnet) {
    Write-Host '  Before starting it, install .NET 10 from the page that opened:' -ForegroundColor Yellow
    Write-Host "  $dotnetPage" -ForegroundColor Yellow
    Write-Host ''
}

# .NET is Microsoft's to keep patched, not this application's (#588) - but Windows only does it
# for someone who has asked it to, and the setting is off on many machines. Said, not changed:
# it applies to every Microsoft product on the machine, which is not this installer's decision.
switch (Test-MicrosoftUpdate) {
    $true {
        Write-Host '  Windows is set to keep .NET up to date (Microsoft Update is on).' -ForegroundColor Gray
    }
    default {
        Write-Host '  Windows keeps .NET patched only when "Receive updates for other' -ForegroundColor Gray
        Write-Host '  Microsoft products" is on:' -ForegroundColor Gray
        Write-Host '    Windows 11: Settings > Windows Update > Advanced options' -ForegroundColor Gray
        Write-Host '    Windows 10: Settings > Update & Security > Windows Update > Advanced options' -ForegroundColor Gray
        Write-Host $(if ($null -eq $_) { '  This installer could not tell whether it is on.' } else { '  It is off on this machine.' }) -ForegroundColor Gray
    }
}
Write-Host ''
Write-Host '  To remove it later: Settings > Apps > Installed apps > WinZ3805A.' -ForegroundColor Gray
Write-Host '  Removing it does not remove the certificate; that is in' -ForegroundColor Gray
Write-Host '  certlm.msc, under Trusted People.' -ForegroundColor Gray
Write-Host ''
Write-Host "  A record of this install is at $LogPath" -ForegroundColor Gray
Write-Log "finished      started ok: $(if ($null -eq $startedOk) { 'not checked' } else { $startedOk })"
