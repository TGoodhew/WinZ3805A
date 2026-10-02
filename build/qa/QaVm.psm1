# QaVm.psm1 - the VMware half of the automated QA pass (#633).
#
# Drives a test VM from the host with vmrun: revert to a snapshot, run a program on the guest's
# logged-in desktop, copy files in and out. Guest passwords come from Windows Credential Manager,
# stored there by the person with cmdkey, and are never written to a log, a file or the console.
#
# vmrun takes the guest password on its command line (-gp), which is the only way it accepts one,
# so it is visible to other processes for the length of the call. This machine is the
# developer's own; that is accepted here and should not be copied anywhere it is not.

Set-StrictMode -Version Latest

$script:VmRunPath = "${env:ProgramFiles(x86)}\VMware\VMware Workstation\vmrun.exe"

if (-not ('WinZ3805AQa.CredentialStore' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace WinZ3805AQa
{
    public static class CredentialStore
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll")]
        static extern void CredFree(IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        const uint Generic = 1;
        const uint PersistLocalMachine = 2;

        // Stores a generic credential, as cmdkey /generic would, replacing any of the same target.
        public static void Write(string target, string user, string secret)
        {
            byte[] blob = System.Text.Encoding.Unicode.GetBytes(secret);
            IntPtr buffer = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, buffer, blob.Length);
                var credential = new CREDENTIAL
                {
                    Type = Generic,
                    TargetName = target,
                    UserName = user,
                    CredentialBlob = buffer,
                    CredentialBlobSize = (uint)blob.Length,
                    Persist = PersistLocalMachine
                };
                if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // Returns { user, secret } for a generic credential, or null when there is none.
        public static string[] Read(string target)
        {
            IntPtr pointer;
            if (!CredRead(target, Generic, 0, out pointer)) return null;
            try
            {
                var credential = (CREDENTIAL)Marshal.PtrToStructure(pointer, typeof(CREDENTIAL));
                string secret = credential.CredentialBlobSize == 0 ? string.Empty
                    : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
                return new[] { credential.UserName, secret };
            }
            finally { CredFree(pointer); }
        }
    }
}
'@
}

# A credential stored with cmdkey /generic:<Target>, as a PSCredential, or a terminating error that
# says how to store it. The secret goes straight into a SecureString.
function Get-QaCredential {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Target)

    $pair = [WinZ3805AQa.CredentialStore]::Read($Target)
    if (-not $pair) {
        throw "No credential '$Target' in Windows Credential Manager. Store it with: cmdkey /generic:$Target /user:<name> /pass"
    }
    $secure = New-Object System.Security.SecureString
    foreach ($ch in $pair[1].ToCharArray()) { $secure.AppendChar($ch) }
    $secure.MakeReadOnly()
    New-Object System.Management.Automation.PSCredential($pair[0], $secure)
}

# Stores a generic credential in Windows Credential Manager, replacing one of the same target.
function Set-QaCredential {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][System.Security.SecureString]$Password
    )
    $plain = (New-Object System.Management.Automation.PSCredential($UserName, $Password)).GetNetworkCredential().Password
    [WinZ3805AQa.CredentialStore]::Write($Target, $UserName, $plain)
}

# A CD image holding a folder's files, made with Windows' own disc-mastering API (IMAPI2), so no
# tool has to be installed. Used for the answer file an unattended Windows install reads.
function New-QaIso {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceFolder,
        [Parameter(Mandatory)][string]$IsoPath,
        [string]$VolumeName = 'QA'
    )

    if (-not ('WinZ3805AQa.IsoWriter' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices.ComTypes;
namespace WinZ3805AQa
{
    public static class IsoWriter
    {
        public static void Write(object image, string path, int blockSize, int blocks)
        {
            var stream = (IStream)image;
            byte[] buffer = new byte[blockSize];
            using (var file = System.IO.File.Create(path))
            {
                for (int i = 0; i < blocks; i++)
                {
                    stream.Read(buffer, blockSize, System.IntPtr.Zero);
                    file.Write(buffer, 0, blockSize);
                }
            }
        }
    }
}
'@
    }

    $image = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $image.FileSystemsToCreate = 3   # ISO 9660 and Joliet
    $image.VolumeName = $VolumeName
    $image.Root.AddTree((Resolve-Path $SourceFolder).Path, $false)
    $result = $image.CreateResultImage()
    [WinZ3805AQa.IsoWriter]::Write($result.ImageStream, $IsoPath, $result.BlockSize, $result.TotalBlocks)
}

# A test VM: its vmx, and the credentials that open it.
function New-QaVm {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$VmxPath,
        [Parameter(Mandatory)][string]$GuestTarget,
        [string]$EncryptionTarget
    )

    if (-not (Test-Path $script:VmRunPath)) { throw "vmrun not found at $script:VmRunPath" }
    if (-not (Test-Path $VmxPath)) { throw "No VM at $VmxPath" }
    [pscustomobject]@{
        Vmx        = $VmxPath
        Guest      = Get-QaCredential $GuestTarget
        Encryption = if ($EncryptionTarget) { Get-QaCredential $EncryptionTarget } else { $null }
    }
}

# Runs vmrun against the VM. Guest operations get -gu/-gp, an encrypted VM gets -vp. Output is
# returned; a failure throws with the password masked out of anything it repeats.
function Invoke-VmRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Vm,
        [Parameter(Mandatory)][string]$Command,
        [string[]]$Arguments = @(),
        [switch]$Guest
    )

    $prefix = @('-T', 'ws')
    $secrets = @()
    if ($Vm.Encryption) {
        $vp = $Vm.Encryption.GetNetworkCredential().Password
        $prefix += @('-vp', $vp)
        $secrets += $vp
    }
    if ($Guest) {
        # An account with no password is common on a test VM that signs itself in. Windows
        # PowerShell drops an empty argument when it calls a program, so -gp would swallow the
        # next word as the password; '""' reaches vmrun as the empty string. PowerShell 7 passes
        # '' correctly and would pass '""' as two quote characters.
        $gp = $Vm.Guest.GetNetworkCredential().Password
        $gpArgument = if ($gp) { $gp } elseif ($PSVersionTable.PSVersion.Major -lt 7) { '""' } else { '' }
        $prefix += @('-gu', $Vm.Guest.UserName, '-gp', $gpArgument)
        $secrets += $gp
    }

    $output = & $script:VmRunPath @prefix $Command $Vm.Vmx @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    foreach ($s in $secrets) { if ($s) { $output = $output.Replace($s, '********') } }
    if ($code -ne 0) { throw "vmrun $Command failed ($code): $($output.Trim())" }
    $output.TrimEnd()
}

# Waits until the guest is at a signed-in desktop: Explorer running, no sign-in screen, and VMware
# Tools' user-session half running as the account - the part that runs programs on the desktop. A
# guest login can succeed while the automatic sign-in is still under way, which is not a state a UI
# check can run in; the first QA Clean snapshot was taken there. And on Windows 11 the desktop can
# be up while that Tools process is not, which failed a run with "the specified guest user must be
# logged in interactively" (2 Oct 2026).
function Wait-QaDesktop {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Vm, [int]$Seconds = 180)

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $processes = Invoke-VmRun $Vm listProcessesInGuest -Guest
            # A wildcard, not a regex: the owner is DOMAIN\user, and a backslash built into a regex
            # here once came out as '\qa', an invalid escape that threw on every poll until the
            # wait timed out.
            $userTools = $processes -split "`n" | Where-Object { $_ -like "*\$($Vm.Guest.UserName), cmd=*vmtoolsd*" }
            if ($processes -match 'explorer\.exe' -and $processes -notmatch 'LogonUI\.exe' -and $userTools) { return }
        }
        catch { }
        Start-Sleep -Seconds 3
    }
    throw "The guest did not reach a signed-in desktop within $Seconds seconds."
}

# Puts the VM in the state a check starts from: reverted to a snapshot, started without a window,
# and at a signed-in desktop. A QA pass calls Stop-QaVm when it is done, so nothing is left running.
function Start-QaVm {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Vm, [string]$Snapshot = 'QA Clean')

    Invoke-VmRun $Vm revertToSnapshot -Arguments $Snapshot | Out-Null
    $running = & $script:VmRunPath -T ws list
    if (-not ($running -match [regex]::Escape($Vm.Vmx))) { Invoke-VmRun $Vm start -Arguments 'nogui' | Out-Null }
    Wait-QaDesktop -Vm $Vm
}

# Powers the VM off. Hard, because whatever a check left behind is discarded by the next revert
# anyway, and a soft stop can wait on a guest that is mid-install.
function Stop-QaVm {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Vm)

    $running = & $script:VmRunPath -T ws list
    if ($running -match [regex]::Escape($Vm.Vmx)) { Invoke-VmRun $Vm stop -Arguments 'hard' | Out-Null }
}

# The named pipe a VM's second serial port is served on. The guest sees it as COM2: COM1 is the
# provisioning trail to guest-serial.log.
function Get-QaSimulatorPipe {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Vm)
    'winz-qa-' + [IO.Path]::GetFileNameWithoutExtension($Vm.Vmx)
}

# Gives the VM a second serial port, served by the VM on a named pipe, for the Z3805A simulator to
# connect to (#639): SmartClockSimulator --pipe-client <Get-QaSimulatorPipe>. The VM is the pipe's
# server so the simulator can come and go between checks without the VM noticing.
#
# The QA Clean snapshot holds a running machine, and a running machine cannot gain a serial port,
# so the snapshot is taken again: revert, shut the guest down cleanly, add the port, boot to the
# desktop, replace the snapshot, power off. A VM that already has the port is left alone.
function Add-QaSimulatorPort {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Vm, [string]$Snapshot = 'QA Clean')

    $pipe = '\\.\pipe\' + (Get-QaSimulatorPipe -Vm $Vm)
    if (Select-String -LiteralPath $Vm.Vmx -SimpleMatch "serial1.fileName = `"$pipe`"" -Quiet) {
        Write-Host "$($Vm.Vmx) already has the simulator port."
        return
    }

    Start-QaVm -Vm $Vm -Snapshot $Snapshot
    Invoke-VmRun $Vm stop -Arguments 'soft' | Out-Null
    $deadline = (Get-Date).AddMinutes(3)
    while ((& $script:VmRunPath -T ws list) -match [regex]::Escape($Vm.Vmx)) {
        if ((Get-Date) -gt $deadline) { throw 'The guest did not shut down within three minutes.' }
        Start-Sleep -Seconds 2
    }

    # Read only now. Reverting moves the VM onto a new disk delta and rewrites the vmx to name it, so
    # a copy read before the revert names a delta that no longer exists; the first version of this
    # function wrote one back and the VM would not start (2 Oct 2026).
    $vmx = Get-Content -LiteralPath $Vm.Vmx
    $kept = @($vmx | Where-Object { $_ -notmatch '^serial1\.' })
    $port = @(
        'serial1.present = "TRUE"'
        'serial1.fileType = "pipe"'
        "serial1.fileName = `"$pipe`""
        'serial1.pipe.endPoint = "server"'
        'serial1.tryNoRxLoss = "TRUE"'
        'serial1.startConnected = "TRUE"'
    )
    Set-Content -LiteralPath $Vm.Vmx -Value ($kept + $port) -Encoding ascii

    Invoke-VmRun $Vm start -Arguments 'nogui' | Out-Null
    Wait-QaDesktop -Vm $Vm
    Invoke-VmRun $Vm deleteSnapshot -Arguments $Snapshot | Out-Null
    Invoke-VmRun $Vm snapshot -Arguments $Snapshot | Out-Null
    Stop-QaVm -Vm $Vm
}

# Copies a file into the guest (-ToGuest) or out of it.
function Copy-QaFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Vm,
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [switch]$ToGuest
    )
    $command = if ($ToGuest) { 'CopyFileFromHostToGuest' } else { 'CopyFileFromGuestToHost' }
    Invoke-VmRun $Vm $command -Arguments $Source, $Destination -Guest | Out-Null
}

# Runs a command on the guest's signed-in desktop and returns its exit code. -interactive is what
# puts it in the signed-in session, where an installer's start check can open a window; without it
# the program runs in a session nobody sees. vmrun reports a non-zero exit as a failure of its own,
# so the code is read out of that message rather than treated as an error.
#
# The program's arguments go to vmrun as separate words. Given as one string, vmrun passed them as a
# single quoted argument: cmd /c coped, because it re-parses its whole command line, but
# powershell.exe received one argument starting "-NoProfile -Exec..." and exited 1 without running
# anything (the second guest runner, 2 Oct 2026).
function Invoke-QaGuest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Vm,
        [Parameter(Mandatory)][string]$Program,
        [string[]]$Arguments = @()
    )
    # A guest whose Tools user session is still starting refuses with "must be logged in
    # interactively"; that is waited out briefly rather than failed.
    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-VmRun $Vm runProgramInGuest -Arguments (@('-activeWindow', '-interactive', $Program) + $Arguments) -Guest | Out-Null
            return 0
        }
        catch {
            if ($_.Exception.Message -match 'exit code:\s*(-?\d+)') { return [int]$Matches[1] }
            if ($_.Exception.Message -match 'logged in interactively' -and $attempt -lt 10) { Start-Sleep -Seconds 5; continue }
            throw
        }
    }
}

# Runs a PowerShell script on the guest's signed-in desktop and returns its exit code and output.
# The script travels as a file and runs with -File, so nothing has to survive being quoted through
# vmrun and a command line: the first guest runner passed -Command strings, and an Expand-Archive
# exited 1 with no way to see why. Everything it writes is captured to a file and brought back.
function Invoke-QaGuestScript {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Vm,
        [Parameter(Mandatory)][string]$Script,
        [string]$Name = 'step'
    )

    $stamp = '{0}-{1:HHmmssfff}' -f $Name, (Get-Date)
    $local = Join-Path ([IO.Path]::GetTempPath()) "qa-$stamp.ps1"
    $wrapped = "`$ErrorActionPreference = 'Stop'`r`n& {`r`n$Script`r`n} *> C:\qa\$stamp.log`r`nexit `$LASTEXITCODE"
    [IO.File]::WriteAllText($local, $wrapped, (New-Object Text.UTF8Encoding($true)))
    try {
        Invoke-VmRun $Vm createDirectoryInGuest -Arguments 'C:\qa' -Guest -ErrorAction SilentlyContinue | Out-Null
    }
    catch { }   # already there
    Copy-QaFile $Vm -Source $local -Destination "C:\qa\$stamp.ps1" -ToGuest
    Remove-Item $local -Force

    $code = Invoke-QaGuest $Vm 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "C:\qa\$stamp.ps1")

    $output = ''
    $logLocal = Join-Path ([IO.Path]::GetTempPath()) "qa-$stamp.log"
    try {
        Copy-QaFile $Vm -Source "C:\qa\$stamp.log" -Destination $logLocal
        $output = Get-Content $logLocal -Raw
        Remove-Item $logLocal -Force
    }
    catch { $output = '(the script left no output)' }
    [pscustomobject]@{ ExitCode = $code; Output = "$output".TrimEnd() }
}

Export-ModuleMember -Function Get-QaCredential, Set-QaCredential, New-QaIso, New-QaVm, Invoke-VmRun, Wait-QaDesktop, Start-QaVm, Stop-QaVm, Get-QaSimulatorPipe, Add-QaSimulatorPort, Copy-QaFile, Invoke-QaGuest, Invoke-QaGuestScript
