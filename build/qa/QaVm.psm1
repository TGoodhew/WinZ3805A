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

# Waits until the guest is at a signed-in desktop: Explorer running and no sign-in screen. A guest
# login can succeed while the automatic sign-in is still under way, which is not a state a UI check
# can run in; the first QA Clean snapshot was taken there.
function Wait-QaDesktop {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Vm, [int]$Seconds = 180)

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $processes = Invoke-VmRun $Vm listProcessesInGuest -Guest
            if ($processes -match 'explorer\.exe' -and $processes -notmatch 'LogonUI\.exe') { return }
        }
        catch { }
        Start-Sleep -Seconds 3
    }
    throw "The guest did not reach a signed-in desktop within $Seconds seconds."
}

Export-ModuleMember -Function Get-QaCredential, Set-QaCredential, New-QaIso, New-QaVm, Invoke-VmRun, Wait-QaDesktop
