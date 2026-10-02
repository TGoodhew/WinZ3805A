<#
.SYNOPSIS
    Creates a Windows test VM in VMware Workstation from an ISO, with nobody at the keyboard (#633).

.DESCRIPTION
    The QA pass needs clean Windows machines it can revert to. This builds one end to end:

      1. A local account's password is generated and stored in Windows Credential Manager as
         WinZ3805A-QA:<Name>, user 'qa'. It is never printed.
      2. An answer file (autounattend.xml) is written onto a small ISO. It partitions the disk,
         installs the chosen edition with Windows' generic installation key (unactivated, which
         a test machine does not need), skips every out-of-box screen, creates the account,
         signs it in automatically, sets UAC never to prompt, and installs VMware Tools from
         Workstation's own copy.
      3. A disk is created and a .vmx written: BIOS firmware, the Windows ISO, the answer ISO and
         the VMware Tools ISO attached.
      4. The VM is started without a window, and the script waits until VMware Tools reports
         the guest running and a guest operation succeeds with the stored credential.
      5. The VM is shut down, the Windows and answer ISOs are detached, the answer ISO - which
         holds the password - is deleted, and the VM is started again and snapshotted as
         'QA Clean', running and signed in, which is what the QA pass reverts to.

    BIOS, not UEFI, is deliberate. A Windows ISO booted under UEFI waits at "Press any key to
    boot from CD or DVD", and nothing can press it that early. Under BIOS, with a blank disk, it
    boots straight into setup. Windows 11's TPM, Secure Boot and RAM checks are bypassed in the
    answer file, so no virtual TPM, and so no VM encryption password, is needed.

.PARAMETER Name
    The VM's name. It names its folder, its display name and its credential.

.PARAMETER IsoPath
    The Windows installation ISO.

.PARAMETER Edition
    The image to install, by its name in install.wim/install.esd: 'Windows 10 Pro' or
    'Windows 11 Pro' on Microsoft's consumer ISOs.

.EXAMPLE
    .\Install-QaVm.ps1 -Name QA-Win10 -IsoPath "$HOME\Downloads\en-us_windows_10_consumer_editions_version_22h2_x64_dvd_8da72ab3.iso" -Edition 'Windows 10 Pro'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,15}$')][string]$Name,
    [Parameter(Mandatory)][string]$IsoPath,
    [Parameter(Mandatory)][string]$Edition,
    [string]$VmRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Virtual Machines\QA'),
    [int]$MemoryMB = 4096,
    [int]$Processors = 2,
    [int]$DiskGB = 64,
    [string]$TimeZone = 'Pacific Standard Time',
    [int]$InstallTimeoutMinutes = 90
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'QaVm.psm1') -Force

$workstation = "${env:ProgramFiles(x86)}\VMware\VMware Workstation"
$vdisk = Join-Path $workstation 'vmware-vdiskmanager.exe'
$toolsIso = Join-Path $workstation 'windows.iso'
foreach ($required in $vdisk, $toolsIso, $IsoPath) {
    if (-not (Test-Path $required)) { throw "Not found: $required" }
}
$IsoPath = (Resolve-Path $IsoPath).Path

$folder = Join-Path $VmRoot $Name
if (Test-Path $folder) { throw "$folder already exists. Choose another -Name or remove that VM first." }
New-Item -ItemType Directory -Path $folder | Out-Null
$vmx = Join-Path $folder "$Name.vmx"
$target = "WinZ3805A-QA:$Name"

function Say { param([string]$Text) Write-Host ('{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $Text) }

# ---------------------------------------------------------------------------
# 1. The account's password: generated, stored, never shown.
# ---------------------------------------------------------------------------
# Letters and digits only, so it needs no escaping in XML, in a command line, or in vmrun's -gp.
$alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'.ToCharArray()
$bytes = New-Object byte[] 20
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
Set-QaCredential -Target $target -UserName 'qa' -Password (ConvertTo-SecureString $password -AsPlainText -Force)
Say "Stored the account's password in Credential Manager as $target."

# ---------------------------------------------------------------------------
# 2. The answer file, on its own ISO.
# ---------------------------------------------------------------------------
$arch = 'processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State"'
# Windows' published generic key for Pro. It installs and does not activate.
$genericKey = 'VK7JG-NPHTM-C97JM-9MPGT-3V66T'
$labConfig = 'HKLM\SYSTEM\Setup\LabConfig'
# What runs at the first sign-in lives in a script on the answer ISO, and the answer file only
# finds and starts it. The first version put the VMware Tools install in the answer file as one
# PowerShell line with its switches - /S /v"/qn REBOOT=R" - quoted three layers deep, and the VM
# installed Windows, signed in, and then never reported Tools running: most likely the quotes
# arrived mangled and Tools' setup opened its interactive wizard, which a synchronous first-logon
# command waits on forever. The line below needs no quoting at all.
$firstLogonCommand = 'cmd /c for %d in (D E F G H I J) do @if exist %d:\firstlogon.ps1 powershell -NoProfile -ExecutionPolicy Bypass -File %d:\firstlogon.ps1'

# Each step it takes is written to C:\qa-firstlogon.log and to COM1, which the .vmx wires to
# guest-serial.log in the VM's folder on the host: a trail that needs neither VMware Tools nor a
# guest login, which is everything this step exists to set up.
$firstLogonScript = @'
$ErrorActionPreference = 'Continue'
function Mark([string]$Text) {
    $line = '{0:HH:mm:ss} {1}' -f (Get-Date), $Text
    Add-Content -Path C:\qa-firstlogon.log -Value $line
    try { $port = New-Object System.IO.Ports.SerialPort 'COM1', 9600; $port.Open(); $port.WriteLine($line); $port.Close() } catch { }
}
Mark 'first logon: started'
reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System /v ConsentPromptBehaviorAdmin /t REG_DWORD /d 0 /f | Out-Null
Mark "UAC never prompts: reg exit $LASTEXITCODE"
powercfg /change monitor-timeout-ac 0
powercfg /change standby-timeout-ac 0
Mark 'display and sleep timeouts off'
# The Tools disc is the one carrying VMwareToolsUpgrader.exe. Its installer is setup.exe in
# Workstation 25 (setup64.exe in older releases, which the first version looked for and never
# found); the Windows disc has a setup.exe of its own, so the name alone would pick the wrong one.
$toolsRoot = Get-PSDrive -PSProvider FileSystem | Where-Object { Test-Path (Join-Path $_.Root 'VMwareToolsUpgrader.exe') } | Select-Object -First 1 -ExpandProperty Root
$setup = foreach ($name in 'setup64.exe', 'setup.exe') { if ($toolsRoot -and (Test-Path (Join-Path $toolsRoot $name))) { Join-Path $toolsRoot $name; break } }
Mark "VMware Tools setup: $(if ($setup) { $setup } else { 'not found' })"
if ($setup) {
    # VMware's documented silent install. REBOOT=R defers the restart to the one below.
    $process = Start-Process -FilePath $setup -ArgumentList '/S', '/v"/qn REBOOT=R"' -Wait -PassThru
    Mark "VMware Tools setup exited $($process.ExitCode)"
}
# The host must not take a login that lands before this restart as the guest being ready - the
# fifth run did, then asked for a clean shutdown while Windows was already restarting, and that
# hung. qa-ready.ps1 runs once at the next sign-in and writes 'ready', which only exists after.
Copy-Item -Path (Join-Path $PSScriptRoot 'qa-ready.ps1') -Destination C:\qa-ready.ps1 -Force
reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce /v QaReady /t REG_SZ /d "powershell -NoProfile -ExecutionPolicy Bypass -File C:\qa-ready.ps1" /f | Out-Null
Mark "ready check registered for the next sign-in: reg exit $LASTEXITCODE"
Mark 'restarting so the Tools service starts cleanly'
shutdown /r /t 5
'@

# Runs once, at the first sign-in after that restart.
$readyScript = @'
$line = '{0:HH:mm:ss} ready: signed in after the restart' -f (Get-Date)
Add-Content -Path C:\qa-firstlogon.log -Value $line
try { $port = New-Object System.IO.Ports.SerialPort 'COM1', 9600; $port.Open(); $port.WriteLine($line); $port.Close() } catch { }
'@

$answer = @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
  <settings pass="windowsPE">
    <component name="Microsoft-Windows-International-Core-WinPE" $arch>
      <SetupUILanguage><UILanguage>en-US</UILanguage></SetupUILanguage>
      <InputLocale>en-US</InputLocale>
      <SystemLocale>en-US</SystemLocale>
      <UILanguage>en-US</UILanguage>
      <UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-Setup" $arch>
      <RunSynchronous>
        <RunSynchronousCommand wcm:action="add"><Order>1</Order><Path>reg add $labConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add"><Order>2</Order><Path>reg add $labConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add"><Order>3</Order><Path>reg add $labConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
      </RunSynchronous>
      <DiskConfiguration>
        <Disk wcm:action="add">
          <DiskID>0</DiskID>
          <WillWipeDisk>true</WillWipeDisk>
          <CreatePartitions>
            <CreatePartition wcm:action="add"><Order>1</Order><Type>Primary</Type><Size>500</Size></CreatePartition>
            <CreatePartition wcm:action="add"><Order>2</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
          </CreatePartitions>
          <ModifyPartitions>
            <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>NTFS</Format><Label>System</Label><Active>true</Active></ModifyPartition>
            <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>2</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
          </ModifyPartitions>
        </Disk>
      </DiskConfiguration>
      <ImageInstall>
        <OSImage>
          <InstallFrom><MetaData wcm:action="add"><Key>/IMAGE/NAME</Key><Value>$Edition</Value></MetaData></InstallFrom>
          <InstallTo><DiskID>0</DiskID><PartitionID>2</PartitionID></InstallTo>
        </OSImage>
      </ImageInstall>
      <UserData>
        <AcceptEula>true</AcceptEula>
        <ProductKey><Key>$genericKey</Key><WillShowUI>Never</WillShowUI></ProductKey>
      </UserData>
    </component>
  </settings>
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" $arch>
      <ComputerName>$Name</ComputerName>
      <TimeZone>$TimeZone</TimeZone>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" $arch>
      <InputLocale>en-US</InputLocale>
      <SystemLocale>en-US</SystemLocale>
      <UILanguage>en-US</UILanguage>
      <UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" $arch>
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
      <UserAccounts>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Name>qa</Name>
            <Group>Administrators</Group>
            <Password><Value>$password</Value><PlainText>true</PlainText></Password>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
      <AutoLogon>
        <Enabled>true</Enabled>
        <Username>qa</Username>
        <Password><Value>$password</Value><PlainText>true</PlainText></Password>
        <LogonCount>9999999</LogonCount>
      </AutoLogon>
      <FirstLogonCommands>
        <SynchronousCommand wcm:action="add"><Order>1</Order><CommandLine>$([Security.SecurityElement]::Escape($firstLogonCommand))</CommandLine><Description>firstlogon.ps1 from the answer ISO</Description></SynchronousCommand>
      </FirstLogonCommands>
    </component>
  </settings>
</unattend>
"@

# Malformed XML would leave Windows setup waiting for a person, which is the one thing this exists
# to avoid, and it would only show half an hour in. So it is parsed here first.
[void][xml]$answer

$answerFolder = Join-Path $folder 'answer'
New-Item -ItemType Directory -Path $answerFolder | Out-Null
[IO.File]::WriteAllText((Join-Path $answerFolder 'autounattend.xml'), $answer, (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $answerFolder 'firstlogon.ps1'), $firstLogonScript, (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $answerFolder 'qa-ready.ps1'), $readyScript, (New-Object Text.UTF8Encoding($false)))
$answerIso = Join-Path $folder 'answer.iso'
New-QaIso -SourceFolder $answerFolder -IsoPath $answerIso -VolumeName 'ANSWER'
Remove-Item $answerFolder -Recurse -Force
Say 'Wrote the answer file onto answer.iso.'

# ---------------------------------------------------------------------------
# 3. The disk and the .vmx.
# ---------------------------------------------------------------------------
$disk = Join-Path $folder "$Name.vmdk"
& $vdisk -c -s "${DiskGB}GB" -a lsilogic -t 0 $disk | Out-Null
if ($LASTEXITCODE -ne 0) { throw "vmware-vdiskmanager could not create $disk" }

# The PCI bridges and PCIe root ports are what Workstation's own wizard writes. Without them the
# e1000e adapter - a PCIe device - has no slot, and vmware-vmx crashed on power-on with "No PCIe
# slot available for Ethernet0" (the first run, 1 Oct 2026). uuid.action and msg.autoAnswer keep a
# start with no window from waiting on a question nobody can see: the first says the VM is new
# rather than moved or copied, the second takes each question's default.
$vmxText = @"
.encoding = "UTF-8"
config.version = "8"
uuid.action = "create"
msg.autoAnswer = "TRUE"
virtualHW.version = "21"
displayName = "$Name"
guestOS = "windows9-64"
firmware = "bios"
bios.bootOrder = "hdd,cdrom"
memsize = "$MemoryMB"
numvcpus = "$Processors"
cpuid.coresPerSocket = "$Processors"
pciBridge0.present = "TRUE"
pciBridge4.present = "TRUE"
pciBridge4.virtualDev = "pcieRootPort"
pciBridge4.functions = "8"
pciBridge5.present = "TRUE"
pciBridge5.virtualDev = "pcieRootPort"
pciBridge5.functions = "8"
pciBridge6.present = "TRUE"
pciBridge6.virtualDev = "pcieRootPort"
pciBridge6.functions = "8"
pciBridge7.present = "TRUE"
pciBridge7.virtualDev = "pcieRootPort"
pciBridge7.functions = "8"
vmci0.present = "TRUE"
hpet0.present = "TRUE"
sata0.present = "TRUE"
sata0:0.present = "TRUE"
sata0:0.fileName = "$Name.vmdk"
sata0:1.present = "TRUE"
sata0:1.deviceType = "cdrom-image"
sata0:1.fileName = "$IsoPath"
sata0:2.present = "TRUE"
sata0:2.deviceType = "cdrom-image"
sata0:2.fileName = "answer.iso"
sata0:3.present = "TRUE"
sata0:3.deviceType = "cdrom-image"
sata0:3.fileName = "$toolsIso"
serial0.present = "TRUE"
serial0.fileType = "file"
serial0.fileName = "guest-serial.log"
ethernet0.present = "TRUE"
ethernet0.connectionType = "nat"
ethernet0.virtualDev = "e1000e"
ethernet0.addressType = "generated"
usb.present = "TRUE"
ehci.present = "TRUE"
usb_xhci.present = "TRUE"
sound.present = "FALSE"
svga.autodetect = "TRUE"
tools.syncTime = "TRUE"
"@
[IO.File]::WriteAllText($vmx, $vmxText, (New-Object Text.UTF8Encoding($false)))
Say "Created $vmx ($MemoryMB MB, $Processors processors, $DiskGB GB disk)."

$vm = New-QaVm -VmxPath $vmx -GuestTarget $target

# Lines of the guest's serial trail already shown, across both waits.
$script:serialSeen = 0

# Polls until VMware Tools runs and a guest operation authenticates, or the time runs out.
function Wait-Guest {
    param([int]$Minutes, [string]$RequireTrail)
    $deadline = (Get-Date).AddMinutes($Minutes)
    $lastState = ''
    $serial = Join-Path $folder 'guest-serial.log'
    while ((Get-Date) -lt $deadline) {
        # The guest's own trail, from firstlogon.ps1 over the serial port.
        if (Test-Path $serial) {
            $lines = @(Get-Content $serial -ErrorAction SilentlyContinue | Where-Object { $_.Trim() })
            # Only when there is something new: a range from $seen past the end runs backwards.
            if ($lines.Count -gt $script:serialSeen) {
                foreach ($line in $lines[$script:serialSeen..($lines.Count - 1)]) { Say "guest: $($line.Trim())" }
                $script:serialSeen = $lines.Count
            }
        }
        $state = 'unknown'
        try { $state = Invoke-VmRun $vm checkToolsState } catch { }
        if ($state -ne $lastState) { Say "VMware Tools: $state"; $lastState = $state }
        # Workstation 25 reports a guest with Tools working and logins accepted as 'installed',
        # never 'running' (seen on the first complete install), so the login itself is the test.
        $trailOk = (-not $RequireTrail) -or ((Test-Path $serial) -and (Select-String -Path $serial -Pattern $RequireTrail -Quiet))
        if ($trailOk -and $state -match 'running|installed') {
            try {
                if ((Invoke-VmRun $vm fileExistsInGuest -Arguments 'C:\Windows\explorer.exe' -Guest) -match 'exists') { return }
            }
            catch { }
        }
        Start-Sleep -Seconds 30
    }
    throw "The guest was not ready within $Minutes minutes. Its screen is in VMware Workstation; open $vmx there to see where it stopped."
}

# ---------------------------------------------------------------------------
# 4. Install.
# ---------------------------------------------------------------------------
Say 'Starting the VM without a window; Windows setup runs unattended (typically 20-40 minutes).'
Invoke-VmRun $vm start -Arguments 'nogui' | Out-Null
Wait-Guest -Minutes $InstallTimeoutMinutes -RequireTrail 'ready: signed in after the restart'
Say 'Windows is installed, signed in, and answering guest operations with the stored credential.'

# ---------------------------------------------------------------------------
# 5. Detach the installation media, delete the answer file, snapshot.
# ---------------------------------------------------------------------------
Invoke-VmRun $vm stop -Arguments 'soft' | Out-Null
$deadline = (Get-Date).AddMinutes(5)
while ((& "$workstation\vmrun.exe" -T ws list) -match [regex]::Escape($vmx)) {
    if ((Get-Date) -gt $deadline) { throw 'The VM did not shut down within five minutes.' }
    Start-Sleep -Seconds 5
}
$vmxText = (Get-Content $vmx -Raw) -replace 'sata0:1\.present = "TRUE"', 'sata0:1.present = "FALSE"' -replace 'sata0:2\.present = "TRUE"', 'sata0:2.present = "FALSE"'
[IO.File]::WriteAllText($vmx, $vmxText, (New-Object Text.UTF8Encoding($false)))
Remove-Item $answerIso -Force
Say 'Detached the Windows and answer ISOs and deleted answer.iso.'

Invoke-VmRun $vm start -Arguments 'nogui' | Out-Null
Wait-Guest -Minutes 15
Wait-QaDesktop -Vm $vm
Invoke-VmRun $vm snapshot -Arguments 'QA Clean' | Out-Null
Say "Snapshot 'QA Clean' taken. $Name is ready: $vmx"
