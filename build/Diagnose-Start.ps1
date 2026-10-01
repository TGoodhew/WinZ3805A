# Diagnose-Start.ps1 - why WinZ3805A exits without a window.
#
# For a copy that installs cleanly and then exits without a window or an app.log (#614). The
# installer starts the app through Explorer and never sees its exit code; this holds the process
# from the first moment, so the code survives however quickly it exits. docs/diagnose-start.md is
# the page that tells a user how to download and run it.
#
# Run it in an ordinary (not administrator) PowerShell window:
#     powershell -ExecutionPolicy Bypass -File .\Diagnose-Start.ps1
# It starts WinZ3805A once, waits up to 20 seconds, and writes what happened to
# WinZ3805A-start-diagnosis.txt on the Desktop. It changes nothing on the machine.
# If WinZ3805A does open, just close it.

$ErrorActionPreference = 'Continue'
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'WinZ3805A-start-diagnosis.txt'
$lines = New-Object System.Collections.Generic.List[string]
function Say { param([string]$Text) $lines.Add($Text); Write-Host $Text }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IApplicationActivationManager
{
    [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
    [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
    [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        IntPtr itemArray, out uint processId);
}

[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
class ApplicationActivationManager { }

public static class StartProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    const uint Synchronize = 0x00100000, QueryLimited = 0x1000;

    // Starts the app and holds a handle to its process from the first moment, so the exit code
    // survives however quickly it exits.
    public static string Run(string aumid, uint waitMs)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid;
        int hr = manager.ActivateApplication(aumid, null, 0, out pid);
        if (hr < 0) return "activation failed: 0x" + hr.ToString("X8");

        IntPtr handle = OpenProcess(Synchronize | QueryLimited, false, pid);
        if (handle == IntPtr.Zero)
            return "started as process " + pid + ", which was gone before it could be opened (error " + Marshal.GetLastWin32Error() + ")";
        try
        {
            if (WaitForSingleObject(handle, waitMs) != 0)
                return "started as process " + pid + ", still running after " + (waitMs / 1000) + " s";
            uint code;
            if (!GetExitCodeProcess(handle, out code))
                return "process " + pid + " exited; exit code unreadable (error " + Marshal.GetLastWin32Error() + ")";
            return "process " + pid + " exited with code 0x" + code.ToString("X8");
        }
        finally { CloseHandle(handle); }
    }
}
'@

Say "WinZ3805A start diagnosis 2, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$os = Get-CimInstance Win32_OperatingSystem
Say "windows      $($os.Caption) $($os.Version)"

$app = Get-AppxPackage -Name 'WinZ3805A' | Select-Object -First 1
if (-not $app) { Say 'WinZ3805A is not installed for this account.'; $lines | Set-Content $out; return }
Say "winz3805a    $($app.PackageFullName), status $($app.Status), signature $($app.SignatureKind)"
foreach ($d in $app.Dependencies) {
    Say "depends on   $($d.PackageFullName), signature $($d.SignatureKind), status $($d.Status)"
}

foreach ($p in @(Get-AppxPackage -Name '*WinAppRuntime*') + @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*')) {
    $extra = ''
    if ($p.IsFramework -and $p.Name -eq 'Microsoft.WindowsAppRuntime.2') {
        $msix = Join-Path $p.InstallLocation 'MSIX'
        $extra = ", MSIX folder: $(if (Test-Path $msix) { (Get-ChildItem $msix -Name) -join ' ' } else { 'absent' })"
    }
    Say "runtime      $($p.PackageFullName), signature $($p.SignatureKind), status $($p.Status)$extra"
}

# What Windows holds for the app itself. Windows 10 has been seen refusing the launch as "not
# registered" (0x80270254) while listing the package as Ok, and re-registering it at launch to no
# effect (#614), so these are the records that answer differently from Get-AppxPackage: the
# applications the installed manifest declares, whether the Start menu knows the app, and whether
# every signed file is still on disk - antivirus quarantining a file leaves the package listed Ok.
try {
    $manifest = Get-AppxPackageManifest -Package $app.PackageFullName
    foreach ($a in $manifest.Package.Applications.Application) {
        Say "application  Id $($a.Id), executable $($a.Executable), entry point $($a.EntryPoint)"
    }
}
catch { Say "application  manifest unreadable: $($_.Exception.Message)" }

try {
    $start = @(Get-StartApps | Where-Object { $_.AppID -like "$($app.PackageFamilyName)!*" })
    Say "start menu   $(if ($start.Count) { ($start | ForEach-Object { "'$($_.Name)' as $($_.AppID)" }) -join ', ' } else { 'not listed' })"
}
catch { Say "start menu   could not be read: $($_.Exception.Message)" }

try {
    [xml]$blockMap = Get-Content -LiteralPath (Join-Path $app.InstallLocation 'AppxBlockMap.xml') -Raw -ErrorAction Stop
    $files = @($blockMap.BlockMap.File)
    $missing = New-Object System.Collections.Generic.List[string]
    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($f in $files) {
        $path = Join-Path $app.InstallLocation $f.Name
        if (-not (Test-Path -LiteralPath $path)) { $missing.Add($f.Name) }
        elseif ((Get-Item -LiteralPath $path).Length -ne [long]$f.Size) { $changed.Add($f.Name) }
    }
    Say "files        $($files.Count) in the package, $($missing.Count) missing, $($changed.Count) a different size"
    foreach ($n in ($missing | Select-Object -First 10)) { Say "missing      $n" }
    foreach ($n in ($changed | Select-Object -First 10)) { Say "changed      $n" }
}
catch { Say "files        could not be checked: $($_.Exception.Message)" }

foreach ($key in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock', 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx') {
    $values = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
    $set = foreach ($n in 'AllowAllTrustedApps', 'AllowDevelopmentWithoutDevLicense', 'BlockNonAdminUserInstall', 'AllowDeploymentInSpecialProfiles') {
        if ($values -and $null -ne $values.$n) { "$n=$($values.$n)" }
    }
    Say "policy       ${key}: $(if ($set) { $set -join ', ' } else { 'nothing set' })"
}

try {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $userProfile = Get-CimInstance Win32_UserProfile -Filter "SID='$sid'" -ErrorAction Stop
    Say "profile      status $($userProfile.Status) (0 ordinary; 1 temporary, 2 roaming, 4 mandatory, 8 corrupted)"
}
catch { Say "profile      could not be read: $($_.Exception.Message)" }

$sideloaded = @(Get-AppxPackage | Where-Object { $_.SignatureKind -eq 'Developer' -and $_.Name -ne 'WinZ3805A' -and -not $_.IsFramework -and -not $_.IsResourcePackage })
Say "dev-signed   $(if ($sideloaded.Count) { ($sideloaded | Select-Object -First 8 | ForEach-Object Name) -join ', ' } else { 'no other developer-signed apps for this account' })"

$aumid = "$($app.PackageFamilyName)!App"
$started = Get-Date
Say ''
Say "starting     $aumid"
Say "result       $([StartProbe]::Run($aumid, 20000))"

# Everything these logs recorded since the start, unfiltered: an attempt to deploy the runtime's
# companions would name them, not WinZ3805A. TWinUI is where the shell records why it refused an
# activation, and AppReadiness and StateRepository hold the per-user registration it consults.
# A second's margin, because the logs stamp whole seconds less often than Get-Date does.
$logs = 'Microsoft-Windows-AppXDeploymentServer/Operational', 'Microsoft-Windows-AppModel-Runtime/Admin',
        'Microsoft-Windows-TWinUI/Operational', 'Microsoft-Windows-AppReadiness/Admin',
        'Microsoft-Windows-AppReadiness/Operational', 'Microsoft-Windows-StateRepository/Operational',
        'Microsoft-Windows-AppxPackaging/Operational', 'Application'
foreach ($log in $logs) {
    try {
        Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $started.AddSeconds(-1) } -ErrorAction Stop |
            Select-Object -First 40 |
            ForEach-Object { Say ("event        {0} {1} {2} {3}: {4}" -f $_.TimeCreated.ToString('HH:mm:ss'), $log, $_.LevelDisplayName, $_.Id, ($_.Message -replace '\s+', ' ')) }
    }
    catch { Say "event        ${log}: none ($($_.Exception.Message))" }
}

$appLog = Join-Path $env:LOCALAPPDATA "Packages\$($app.PackageFamilyName)\LocalCache\Local\WinZ3805A\logs\app.log"
Say "app.log      $(if (Test-Path $appLog) { 'present' } else { 'absent' })"

$lines | Set-Content -Path $out -Encoding UTF8
Write-Host ''
Write-Host "Written to $out - please send that file." -ForegroundColor Green
