# The automated QA harness (#633)

Scripts that run the release QA pass on throwaway VMs, so that a person does not have to.
`docs/manual-qa.md` says what each check is; this folder is how a machine runs the ones it
can. Phase 1, the checks that need neither the bench receiver nor extra hardware, is tracked
in #633.

## Provisioning a test VM

`Install-QaVm.ps1` builds a clean Windows VM in VMware Workstation from an installation ISO,
unattended, in about eight minutes on the development machine:

```powershell
.\build\qa\Install-QaVm.ps1 -Name QA-Win10 -IsoPath <path to a Windows 10 ISO> -Edition 'Windows 10 Pro'
.\build\qa\Install-QaVm.ps1 -Name QA-Win11 -IsoPath <path to a Windows 11 ISO> -Edition 'Windows 11 Pro'
```

It leaves a VM under `Documents\Virtual Machines\QA\<Name>` with a snapshot, **QA Clean**:
Windows signed in to a local administrator `qa`, VMware Tools running, UAC set never to
prompt, the display never sleeping. Reverting to it gives a signed-in desktop in a few
seconds. The account's password is generated, stored in Windows Credential Manager as
`WinZ3805A-QA:<Name>`, and never displayed; the answer file that carried it is deleted.
Windows is installed with the generic Pro key and is not activated, which a test machine
does not need.

Two exist on the development machine: **QA-Win10** (Windows 10 22H2) and **QA-Win11**
(Windows 11 26H2, 18 minutes to provision). Both are left **powered off**: provisioning stops
its VM once the snapshot is taken, and a QA pass starts the VM it needs and stops it again,
even when a check fails.

## Driving a VM

`QaVm.psm1` is what the QA pass drives a VM with:

| Function | What it does |
|---|---|
| `New-QaVm` | Opens a VM with its stored credential |
| `Start-QaVm` | Reverts to a snapshot (*QA Clean* by default), starts without a window, waits for the desktop |
| `Stop-QaVm` | Powers it off; call it in a `finally` |
| `Copy-QaFile` | Copies a file in (`-ToGuest`) or out |
| `Invoke-QaGuest` | Runs a program on the signed-in desktop and returns its exit code |
| `Invoke-QaGuestScript` | Runs a PowerShell script there and returns its exit code and output |
| `Invoke-VmRun` | Any other `vmrun` command; guest operations with `-Guest` |

The installer runs in a guest with `Install.cmd -Unattended`, whose exit code is the outcome
(see `install.ps1`'s help). On 2 Oct 2026 v1.3.3's online zip returned 3 (installed, .NET
missing) in 17 seconds and the offline zip 0 (installed .NET, started) in 41.

## Things worth not rediscovering

Each of these cost a failed run on 1 Oct 2026.

- **Boot under BIOS, not UEFI.** A Windows ISO booted under UEFI waits at *Press any key to
  boot from CD*, and nothing can press it. Under BIOS with a blank disk it boots straight
  into setup. Windows 11's TPM, Secure Boot and RAM checks are bypassed in the answer file.
- **Declare the PCIe root ports.** Without them the e1000e adapter has no slot and
  `vmware-vmx` crashes at power-on (*No PCIe slot available for Ethernet0*).
- **Workstation 25's Tools installer is `setup.exe`**, not `setup64.exe`. Find the Tools disc
  by `VMwareToolsUpgrader.exe`: the Windows disc has a `setup.exe` too.
- **Keep commands out of the answer file.** The first-logon work is a script on the answer
  ISO, which the answer file only finds and starts.
- **The guest writes a trail to COM1**, which the VM wires to `guest-serial.log` in its folder.
  It needs neither Tools nor a login, and it is how the Tools problem above was found in one
  run after an hour of guessing.
- **`vmrun checkToolsState` says `installed` for a working guest** until something has logged
  in, so a guest login is the test of readiness, not that word.
- **A login before a restart is not readiness.** The first-logon script restarts the guest;
  a login that lands just before is accepted and the next step hangs. The install waits for
  a `ready` line that a run-once script writes after the restart.
- **A login is not a desktop either.** It succeeds while the automatic sign-in is still under
  way; `Wait-QaDesktop` waits for Explorer and no sign-in screen.
- **Ship scripts as files, not command strings.** A `-Command` string passed through `vmrun`
  ran nothing and reported nothing; `Invoke-QaGuestScript` copies a `.ps1` in, runs it with
  `-File`, and brings its output back.
- **Give `vmrun` a program's arguments as separate words.** As one string they arrive as one
  quoted argument: `cmd /c` copes, `powershell.exe` exits 1 without running anything.
- **A blank guest password cannot be used.** Windows refuses non-console logons to accounts
  with blank passwords, which is why the account has a generated one.
