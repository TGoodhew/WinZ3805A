<#
.SYNOPSIS
    Removes everything any WinZ3805A installer has put on this machine (#618).

.DESCRIPTION
    For someone who wants a clean slate: before a fresh install, after an upgrade
    that went wrong (#614), or to stop using the application. It runs from a
    downloaded file, without a clone, on Windows PowerShell 5.1, and
    docs/remove-winz3805a.md walks through every step it takes.

    It REMOVES, after showing a list and asking:
      - every WinZ3805A package for this account, whatever its publisher - the
        placeholder identity before v1.0.1, the one v1.0.1 to v1.3.0 used, the
        current one, and development registrations - and with each package,
        everything it stored: history (trend.db), settings and logs;
      - a WinZ3805A data folder outside any package, if one exists;
      - the certificates the releases were signed with, from Trusted People and
        Trusted Root, for the machine and for this account;
      - the installer's own folder: its logs (#592) and its marker (#617).

    It OFFERS FIRST to save the history and settings to Documents, and by
    default does. It ASKS before deleting the "WinZ3805A earlier copy" folders
    the installer saved to Documents (#590), and by default keeps them.

    It NEVER REMOVES the Windows App Runtime or .NET 10. Both are shared:
    Photos brings the runtime on Windows 10 (#595), and anything built on .NET
    may use the runtime the offline zip installed. It lists them, with what
    else on this account depends on them, and leaves the decision to the person.

    One administrator prompt at most, for the machine's certificate stores. The
    rest runs as the person who started it, because packages and their data are
    per account - elevating everything would remove the administrator's copy,
    not theirs. Other Windows accounts are listed, never touched.

.PARAMETER ListOnly
    Show what would be removed and kept, write the log, and change nothing.

.NOTES
    EVERY RUN WRITES A LOG to the Desktop, WinZ3805A-removal-<date>-<time>.log -
    on the Desktop rather than beside the installer's logs, because removing
    those is one of the things this does.
#>

[CmdletBinding()]
param(
    [switch]$ListOnly,

    # Set when this script re-launches itself elevated for the machine's certificate stores, and
    # nothing else. Not for a person to pass. No parameter may share a name with a variable below,
    # in any case: PowerShell variable names ignore case, so a local of the same name IS the
    # parameter and is converted to its type (install.ps1 shipped that bug once, #588/#589).
    [switch]$AsAdministrator,
    # Store and thumbprint pairs, "Root:THUMBPRINT,TrustedPeople:THUMBPRINT".
    [string]$MachineCertificates,
    # The run's log, passed to the elevated half so both write to one file.
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# What identifies this application's certificates.
# ---------------------------------------------------------------------------
# By thumbprint for every published release, so a certificate is found even after its application
# was removed by hand - which never touches the certificate stores. By subject for the current
# publisher, which names this company and nothing else. The placeholder identity before v1.0.1,
# CN=AppPublisher, is NOT matched by subject: it is a template default other applications can
# carry too, so a certificate with it is removed only when an installed WinZ3805A was signed by it.
#
# A known certificate is described by the releases it signed, never by its subject: the earlier
# one's subject carries the misspelling of the company's name that v1.3.1 corrected, and nothing
# new repeats it.
$knownThumbprints = @{
    '7F47E8D75FB856C5A1665900293EDB491E278B3C' = 'the certificate v1.3.1 and later are signed with'
    '655D07E31BDA80CBF6AAC2F2635EA6664B9399DD' = 'the certificate v1.0.1 to v1.3.0 were signed with'
}
$knownSubjects = @('CN=The Schnauzer Group LLC')
$placeholderSubject = 'CN=AppPublisher'

# ---------------------------------------------------------------------------
# The log.
# ---------------------------------------------------------------------------
if (-not $LogPath) {
    $LogPath = Join-Path ([Environment]::GetFolderPath('Desktop')) ('WinZ3805A-removal-{0}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

function Write-Log {
    param([string]$Text)
    $prefix = if ($AsAdministrator) { '[elevated] ' } else { '' }
    try {
        Add-Content -LiteralPath $LogPath -Encoding UTF8 -ErrorAction Stop `
            -Value ('{0}  {1}{2}' -f (Get-Date -Format 'HH:mm:ss.fff'), $prefix, $Text)
    }
    catch { }
}

trap {
    Write-Log "FAILED: $($_.Exception.Message)"
    Write-Log "  at line $($_.InvocationInfo.ScriptLineNumber): $("$($_.InvocationInfo.Line)".Trim())"
    if (-not $AsAdministrator) {
        Write-Host ''
        Write-Host "  Stopped: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  A record of this run is at $LogPath" -ForegroundColor Yellow
    }
    break
}

# ---------------------------------------------------------------------------
# The elevated half: the machine's certificate stores, and a look at other accounts.
# ---------------------------------------------------------------------------
# Exit codes: 0 done; 10 a certificate could not be removed.
if ($AsAdministrator) {
    try {
        foreach ($copy in @(Get-AppxPackage -AllUsers -Name 'WinZ3805A' -ErrorAction Stop)) {
            foreach ($user in @($copy.PackageUserInformation)) {
                Write-Log "all users     $($copy.PackageFullName): $($user.UserSecurityId.Username) $($user.InstallState)"
            }
        }
    }
    catch { Write-Log "all users     could not be listed: $($_.Exception.Message)" }

    $failed = $false
    foreach ($pair in @($MachineCertificates -split ',' | Where-Object { $_ })) {
        $store, $print = $pair -split ':', 2
        $path = "Cert:\LocalMachine\$store\$print"
        try {
            if (Test-Path $path) { Remove-Item -Path $path -Force -ErrorAction Stop }
            Write-Log "removed       LocalMachine\$store $print"
        }
        catch {
            Write-Log "not removed   LocalMachine\$store ${print}: $($_.Exception.Message)"
            $failed = $true
        }
    }
    if ($failed) { exit 10 }
    exit 0
}

function Write-Step { param([string]$Text) Write-Host ''; Write-Host $Text -ForegroundColor Cyan; Write-Log "== $Text" }
function Write-Ok { param([string]$Text) Write-Host "  $Text" -ForegroundColor Green; Write-Log "ok    $Text" }
function Write-Info { param([string]$Text) Write-Host "  $Text" -ForegroundColor Gray; if ($Text) { Write-Log "info  $Text" } }
function Write-Warn { param([string]$Text) Write-Host "  $Text" -ForegroundColor Yellow; Write-Log "warn  $Text" }

function Get-FolderSize {
    param([string]$Path)
    $bytes = (Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if (-not $bytes) { $bytes = 0 }
    '{0:N1} MB' -f ($bytes / 1MB)
}

Write-Host ''
Write-Host '  Removing WinZ3805A completely' -ForegroundColor White
Write-Log "WinZ3805A removal$(if ($ListOnly) { ', list only' }), run from $PSCommandPath"
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    Write-Log "windows       $($os.Caption) $($os.Version), PowerShell $($PSVersionTable.PSVersion)"
}
catch { Write-Log "windows       could not be read: $($_.Exception.Message)" }

# ---------------------------------------------------------------------------
# 1. What is here. Nothing changes in this section.
# ---------------------------------------------------------------------------
$local = $env:LOCALAPPDATA

# Packages. Store-installed copies did not come from these installers and are left to the Store.
$allCopies = @(Get-AppxPackage -Name 'WinZ3805A' -ErrorAction SilentlyContinue)
$copies = @($allCopies | Where-Object { $_.SignatureKind -ne 'Store' })
$storeCopies = @($allCopies | Where-Object { $_.SignatureKind -eq 'Store' })
foreach ($copy in $allCopies) {
    Write-Log "package       $($copy.PackageFullName), signature $($copy.SignatureKind), development $($copy.IsDevelopmentMode), status $($copy.Status)"
}

# Data. Each package's folder goes with the package; a folder outside any package does not.
$dataFolders = @()
foreach ($copy in $copies) {
    $folder = Join-Path $local "Packages\$($copy.PackageFamilyName)\LocalCache\Local\WinZ3805A"
    if (Test-Path -LiteralPath $folder) {
        $dataFolders += [pscustomobject]@{ Label = "$($copy.Version), $($copy.PackageFamilyName)"; Path = $folder; InPackage = $true }
    }
}
$looseData = Join-Path $local 'WinZ3805A'
if (Test-Path -LiteralPath $looseData) {
    $dataFolders += [pscustomobject]@{ Label = 'outside any package'; Path = $looseData; InPackage = $false }
}
foreach ($d in $dataFolders) {
    Write-Log "data          $($d.Path): $(Get-FolderSize $d.Path), trend.db $(if (Test-Path (Join-Path $d.Path 'trend.db')) { 'present' } else { 'absent' })"
}

# Package folders a removal left behind, for families no longer installed.
$installedFamilies = @($allCopies | ForEach-Object PackageFamilyName)
$orphanFolders = @(Get-ChildItem -LiteralPath (Join-Path $local 'Packages') -Directory -Filter 'WinZ3805A_*' -ErrorAction SilentlyContinue |
    Where-Object { $installedFamilies -notcontains $_.Name } | ForEach-Object FullName)
foreach ($o in $orphanFolders) { Write-Log "left behind   $o" }

# Certificates, in the four stores a person could have put one: the installers use the machine's
# Trusted People; double-clicking WinZ3805A.cer and accepting the wizard's defaults puts it in this
# account's. Root is checked because a certificate there vouches for even more.
#
# THIS ACCOUNT'S STORES ARE A MERGED VIEW that also shows the machine's, so a certificate the machine
# trusts appears in both. It is counted once, as the machine's: removing it there removes it from
# this view too, and removing it again by the account's path then finds nothing - measured on the
# Windows 10 VM on 1 Oct 2026, where the first version of this listed 7F47E8D7 twice. A copy the
# account ALSO holds itself shows only once the machine's is gone, which is why step 6 looks again.
$copyPublishers = @($copies | ForEach-Object Publisher | Select-Object -Unique)
function Find-Certificates {
    foreach ($store in 'TrustedPeople', 'Root') {
        $machine = @(Get-ChildItem "Cert:\LocalMachine\$store" -ErrorAction SilentlyContinue)
        $machinePrints = @($machine | ForEach-Object Thumbprint)
        $account = @(Get-ChildItem "Cert:\CurrentUser\$store" -ErrorAction SilentlyContinue |
            Where-Object { $machinePrints -notcontains $_.Thumbprint })
        foreach ($pair in @(@($machine | ForEach-Object { , @('LocalMachine', $_) }) + @($account | ForEach-Object { , @('CurrentUser', $_) }))) {
            $c = $pair[1]
            $kind = $null
            if ($knownThumbprints.ContainsKey($c.Thumbprint) -or ($knownSubjects -contains $c.Subject) -or
                (($c.Subject -eq $placeholderSubject) -and ($copyPublishers -contains $placeholderSubject))) { $kind = 'ours' }
            elseif ($c.Subject -eq $placeholderSubject) { $kind = 'unclaimed' }
            if (-not $kind) { continue }
            [pscustomobject]@{
                Location = $pair[0]; Store = $store; Thumbprint = $c.Thumbprint; Kind = $kind; Expires = $c.NotAfter
                Name = $(if ($knownThumbprints.ContainsKey($c.Thumbprint)) { $knownThumbprints[$c.Thumbprint] } else { $c.Subject })
            }
        }
    }
}
$found = @(Find-Certificates)
$certificates = @($found | Where-Object { $_.Kind -eq 'ours' })
$unclaimed = @($found | Where-Object { $_.Kind -eq 'unclaimed' })
foreach ($c in $certificates) { Write-Log "certificate   $($c.Location)\$($c.Store) $($c.Thumbprint), $($c.Name): remove" }
foreach ($c in $unclaimed) { Write-Log "certificate   $($c.Location)\$($c.Store) $($c.Thumbprint), $($c.Name): keep, no installed WinZ3805A was signed by it" }

# The installer's own folder: logs (#592), and the side-by-side marker (#617).
$installerFolder = Join-Path $local 'WinZ3805A Installer'
$installerPresent = Test-Path -LiteralPath $installerFolder
if ($installerPresent) { Write-Log "installer     $installerFolder, $(@(Get-ChildItem -LiteralPath $installerFolder -Recurse -File -Force -ErrorAction SilentlyContinue).Count) file(s)" }

# Saved copies in Documents: the installer's (#590), and any an earlier run of this script made.
$documents = [Environment]::GetFolderPath('MyDocuments')
$savedCopies = @(Get-ChildItem -LiteralPath $documents -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'WinZ3805A earlier copy *' -or $_.Name -like 'WinZ3805A saved data *' })
foreach ($s in $savedCopies) { Write-Log "saved copy    $($s.FullName), $(Get-FolderSize $s.FullName)" }

# Shared components: listed, never removed.
$runtimes = @(@(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2' -ErrorAction SilentlyContinue) +
              @(Get-AppxPackage -Name 'MicrosoftCorporationII.WinAppRuntime.*' -ErrorAction SilentlyContinue))
$everyPackage = @(Get-AppxPackage -ErrorAction SilentlyContinue)
$runtimeLines = @()
foreach ($r in $runtimes) {
    $users = @($everyPackage | Where-Object {
            $_.Name -ne 'WinZ3805A' -and $_.Name -notlike 'MicrosoftCorporationII.WinAppRuntime.*' -and
            @($_.Dependencies | ForEach-Object PackageFullName) -contains $r.PackageFullName
        } | ForEach-Object Name | Select-Object -Unique)
    $line = "$($r.Name) $($r.Version) $($r.Architecture), $(if ($r.SignatureKind -eq 'Store') { 'from the Store' } else { 'installed outside the Store' })" +
        $(if ($r.IsFramework) { if ($users.Count) { "; also used by $(@($users | Select-Object -First 4) -join ', ')" } else { '; nothing else on this account declares it' } } else { '' })
    $runtimeLines += $line
    Write-Log "shared        $line"
}
$dotnetLines = @()
foreach ($framework in 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') {
    foreach ($v in @(Get-ChildItem (Join-Path $env:ProgramFiles "dotnet\shared\$framework") -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '10.*' } | ForEach-Object Name)) {
        $dotnetLines += "$framework $v"
        Write-Log "shared        $framework $v"
    }
}

# ---------------------------------------------------------------------------
# 2. The list, before anything changes.
# ---------------------------------------------------------------------------
Write-Step 'This will remove'
$nothing = ($copies.Count -eq 0) -and ($dataFolders.Count -eq 0) -and ($orphanFolders.Count -eq 0) -and
           ($certificates.Count -eq 0) -and (-not $installerPresent)
if ($nothing) { Write-Info 'Nothing - no WinZ3805A package, data, certificate or installer folder was found.' }
foreach ($copy in $copies) {
    Write-Info "WinZ3805A $($copy.Version) ($($copy.PackageFamilyName))$(if ($copy.IsDevelopmentMode) { ', a development registration' })"
}
foreach ($d in $dataFolders) { Write-Info "  data, $($d.Label): $(Get-FolderSize $d.Path) - $($d.Path)" }
foreach ($o in $orphanFolders) { Write-Info "a folder a removal left behind: $o" }
foreach ($c in $certificates) {
    Write-Info "certificate $($c.Thumbprint.Substring(0, 8))..., $($c.Name), in $($c.Location)\$($c.Store), expires $($c.Expires.ToString('d MMM yyyy'))"
}
if ($installerPresent) { Write-Info "the installer's logs and settings: $installerFolder" }

Write-Step 'This will keep'
foreach ($copy in $storeCopies) { Write-Info "WinZ3805A $($copy.Version) from the Microsoft Store - remove it in Settings if you want to" }
foreach ($c in $unclaimed) {
    Write-Info "certificate $($c.Thumbprint.Substring(0, 8))..., $($c.Name), in $($c.Location)\$($c.Store): that name is a"
    Write-Info '  template default other applications use too, and no installed WinZ3805A was signed by it'
}
foreach ($s in $savedCopies) { Write-Info "saved copy in Documents, unless you choose otherwise below: $($s.Name)" }
foreach ($line in $runtimeLines) { Write-Info "shared: $line" }
foreach ($line in $dotnetLines) { Write-Info "shared: $line" }
if ($runtimeLines.Count -or $dotnetLines.Count) {
    Write-Info 'Shared components serve other applications too, so this never removes them.'
    Write-Info 'The page this script came from says how to, if you are sure nothing needs them.'
}
Write-Info 'WinZ3805A in other Windows accounts - each account has to run this itself.'

if ($ListOnly) {
    Write-Host ''
    Write-Host '  Nothing was changed (-ListOnly).' -ForegroundColor Green
    Write-Host "  A record of this run is at $LogPath" -ForegroundColor Gray
    Write-Log 'finished      list only'
    return
}
if ($nothing -and $savedCopies.Count -eq 0) {
    Write-Host ''
    Write-Host '  This machine is already clean of WinZ3805A.' -ForegroundColor Green
    Write-Host "  A record of this run is at $LogPath" -ForegroundColor Gray
    Write-Log 'finished      nothing to do'
    return
}

# A running copy holds its own files open; asked to close, never closed.
while (Get-Process -Name 'WinZ3805A' -ErrorAction SilentlyContinue) {
    Write-Log 'waiting       WinZ3805A is running'
    Write-Host ''
    Write-Host '  WinZ3805A is running. Close it first: right-click its icon by the clock and' -ForegroundColor Yellow
    Write-Host '  choose Exit.' -ForegroundColor Yellow
    Read-Host '  Press Enter once it has closed'
}

# ---------------------------------------------------------------------------
# 3. The choices.
# ---------------------------------------------------------------------------
$saveData = $false
if ($dataFolders.Count -gt 0) {
    Write-Host ''
    $answer = Read-Host '  Save your history and settings to Documents before removing them? [Y/n]'
    $saveData = $answer -notmatch '^\s*n'
    Write-Log "choice        save data: $saveData"
}

$deleteSaved = $false
if ($savedCopies.Count -gt 0) {
    Write-Host ''
    $answer = Read-Host "  Also delete the $($savedCopies.Count) saved cop$(if ($savedCopies.Count -eq 1) { 'y' } else { 'ies' }) in Documents listed above? [y/N]"
    $deleteSaved = $answer -match '^\s*y'
    Write-Log "choice        delete saved copies: $deleteSaved"
}

Write-Host ''
Read-Host '  Press Enter to remove what is listed above, or close this window to stop'

# ---------------------------------------------------------------------------
# 4. Save the data, before anything that could delete it.
# ---------------------------------------------------------------------------
$savedTo = @()
if ($saveData) {
    Write-Step 'Saving your history and settings'
    foreach ($d in $dataFolders) {
        if (-not (Get-ChildItem -LiteralPath $d.Path -Force -ErrorAction SilentlyContinue)) { continue }
        $label = if ($d.InPackage) { ($d.Label -split ',')[0] } else { 'unpackaged' }
        $to = Join-Path $documents "WinZ3805A saved data $label $(Get-Date -Format 'yyyy-MM-dd HHmmss')"
        try {
            Copy-Item -LiteralPath $d.Path -Destination $to -Recurse -ErrorAction Stop
        }
        catch {
            throw "Your data in $($d.Path) could not be saved to Documents, so nothing has been removed: $($_.Exception.Message)"
        }
        Write-Ok "Saved to $to"
        $savedTo += $to
    }
}

# ---------------------------------------------------------------------------
# 5. The machine's certificates: the one administrator prompt.
# ---------------------------------------------------------------------------
$machineCerts = @($certificates | Where-Object { $_.Location -eq 'LocalMachine' })
if ($machineCerts.Count -gt 0) {
    Write-Step 'Certificates for the whole machine'
    Write-Info 'Windows will ask once for administrator permission to remove them.'
    $pairs = ($machineCerts | ForEach-Object { "$($_.Store):$($_.Thumbprint)" }) -join ','
    $elevated = $null
    try {
        $elevated = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
            '-AsAdministrator', '-MachineCertificates', $pairs, '-LogPath', "`"$LogPath`"")
    }
    catch { Write-Log "elevation declined or failed: $($_.Exception.Message)" }

    if (-not $elevated) { Write-Warn 'Administrator permission was not given, so the machine''s certificates are still trusted.' }
    elseif ($elevated.ExitCode -ne 0) { Write-Warn "Some certificates could not be removed (code $($elevated.ExitCode)); the record below says which." }
    else { foreach ($c in $machineCerts) { Write-Ok "Removed certificate $($c.Thumbprint.Substring(0, 8))... from $($c.Store)." } }
}

# ---------------------------------------------------------------------------
# 6. This account's: packages, data, certificates, installer folder.
# ---------------------------------------------------------------------------
Write-Step 'WinZ3805A for this account'

foreach ($copy in $copies) {
    try {
        Remove-AppxPackage -Package $copy.PackageFullName -ErrorAction Stop
        Write-Ok "Removed WinZ3805A $($copy.Version) ($($copy.PackageFamilyName)), and everything it stored."
    }
    catch { Write-Warn "WinZ3805A $($copy.Version) could not be removed: $($_.Exception.Message)" }
}

$leftovers = @($orphanFolders) + @($copies | ForEach-Object { Join-Path $local "Packages\$($_.PackageFamilyName)" })
foreach ($folder in @($leftovers | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $folder)) { continue }
    # Windows normally deletes this with the package; anything left is only what it could not.
    if (Get-AppxPackage -Name 'WinZ3805A' -ErrorAction SilentlyContinue | Where-Object { $folder -like "*\$($_.PackageFamilyName)" }) { continue }
    try { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction Stop; Write-Ok "Removed what was left in $folder" }
    catch { Write-Warn "Could not remove $folder - it may be in use until Windows restarts: $($_.Exception.Message)" }
}

if (Test-Path -LiteralPath $looseData) {
    try { Remove-Item -LiteralPath $looseData -Recurse -Force -ErrorAction Stop; Write-Ok "Removed $looseData" }
    catch { Write-Warn "Could not remove ${looseData}: $($_.Exception.Message)" }
}

# Looked for again now, not taken from the survey: with the machine's copies gone, what this
# account's view still shows is the account's own.
foreach ($c in @(Find-Certificates | Where-Object { $_.Kind -eq 'ours' -and $_.Location -eq 'CurrentUser' })) {
    try {
        Remove-Item -Path "Cert:\CurrentUser\$($c.Store)\$($c.Thumbprint)" -Force -ErrorAction Stop
        Write-Ok "Removed certificate $($c.Thumbprint.Substring(0, 8))... from this account's $($c.Store)."
    }
    catch { Write-Warn "Could not remove certificate $($c.Thumbprint) from this account's $($c.Store): $($_.Exception.Message)" }
}

if ($installerPresent) {
    try { Remove-Item -LiteralPath $installerFolder -Recurse -Force -ErrorAction Stop; Write-Ok "Removed the installer's logs and settings." }
    catch { Write-Warn "Could not remove ${installerFolder}: $($_.Exception.Message)" }
}

if ($deleteSaved) {
    foreach ($s in $savedCopies) {
        try { Remove-Item -LiteralPath $s.FullName -Recurse -Force -ErrorAction Stop; Write-Ok "Deleted $($s.FullName)" }
        catch { Write-Warn "Could not delete $($s.FullName): $($_.Exception.Message)" }
    }
}

# ---------------------------------------------------------------------------
# 7. What is left, looked for again rather than assumed.
# ---------------------------------------------------------------------------
Write-Step 'Checking'
$remaining = @()
foreach ($copy in @(Get-AppxPackage -Name 'WinZ3805A' -ErrorAction SilentlyContinue | Where-Object { $_.SignatureKind -ne 'Store' })) {
    $remaining += "package $($copy.PackageFullName)"
}
foreach ($c in @(Find-Certificates | Where-Object { $_.Kind -eq 'ours' })) {
    $remaining += "certificate $($c.Thumbprint.Substring(0, 8))... in $($c.Location)\$($c.Store)"
}
foreach ($path in @($looseData, $installerFolder)) { if (Test-Path -LiteralPath $path) { $remaining += $path } }
foreach ($r in $remaining) { Write-Warn "Still here: $r" }
if ($remaining.Count -eq 0) { Write-Ok 'Nothing WinZ3805A installed is left for this account.' }
Write-Log "finished      $($remaining.Count) item(s) left"

Write-Host ''
foreach ($to in $savedTo) { Write-Host "  Your history and settings are saved in $to" -ForegroundColor Green }
if ($savedTo.Count) {
    Write-Host '  To bring the history back after installing again: Settings > Import history...,' -ForegroundColor Gray
    Write-Host '  and choose trend.db in that folder.' -ForegroundColor Gray
}
Write-Host '  Restart Windows before installing WinZ3805A again. After an upgrade that went' -ForegroundColor Gray
Write-Host '  wrong, Windows can hold on to the old state until it restarts.' -ForegroundColor Gray
Write-Host ''
Write-Host "  A record of this run is at $LogPath" -ForegroundColor Gray
