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

Say "WinZ3805A start diagnosis, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
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

$aumid = "$($app.PackageFamilyName)!App"
$started = Get-Date
Say ''
Say "starting     $aumid"
Say "result       $([StartProbe]::Run($aumid, 20000))"

# Everything Windows' deployment and app-model logs recorded since the start, unfiltered: an attempt
# to deploy the runtime's companions would name them, not WinZ3805A.
foreach ($log in 'Microsoft-Windows-AppXDeploymentServer/Operational', 'Microsoft-Windows-AppModel-Runtime/Admin', 'Application') {
    try {
        Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $started } -ErrorAction Stop |
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
