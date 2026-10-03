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
| `Add-QaSimulatorPort` | Gives the VM a COM2 served on a named pipe for the Z3805A simulator, and re-takes *QA Clean* with it (#639) |
| `Get-QaSimulatorPipe` | The pipe's name: `winz-qa-<VM name>` |
| `Save-QaCleanSnapshot` | Re-takes *QA Clean* from the running guest once `Wait-QaQuiet` says it has gone quiet, then powers off |

**The simulator port.** The `receiver` scenario needs a receiver, and a VM has none, so each QA VM
has a second serial port that the VM serves on `\\.\pipe\winz-qa-<name>`. The guest sees it as
COM2; COM1 is the provisioning trail. `tools/SmartClockSimulator --pipe-client <pipe>` connects to
it from the host. `Install-QaVm.ps1` gives a new VM the port. A VM provisioned before 2 Oct 2026 gets
it once, with `Add-QaSimulatorPort`, which takes about three minutes:
1. revert, and shut the guest down cleanly;
2. add the port to the `.vmx`;
3. boot to the desktop and replace *QA Clean*;
4. power off.

The snapshot has to be taken again because it holds a running machine, and a running machine cannot
gain a serial port.

The installer runs in a guest with `Install.cmd -Unattended`, whose exit code is the outcome
(see `install.ps1`'s help). On 2 Oct 2026 v1.3.3's online zip returned 3 (installed, .NET
missing) in 17 seconds and the offline zip 0 (installed .NET, started) in 41.

## Running the QA pass

```powershell
.\build\qa\Invoke-QaPass.ps1 -Online <online zip> -Offline <offline zip>   # a dry-run build
.\build\qa\Invoke-QaPass.ps1 -Release v1.3.4                               # published zips
```

Every scenario reverts a VM to *QA Clean*, does what a person would do on a fresh machine, reads
the outcome from the installer's exit code, its log and the guest's state, and powers the VM off
afterwards. The report, `report.md`, goes into the release's QA-run issue; everything collected
from the guests sits beside it. The exit code is 1 if any scenario failed or errored, and also if
nothing ran.

| Scenario | manual-qa.md section | What it requires |
|---|---|---|
| `binary-audit` | 8 | No excluded command in the assemblies this repository builds (host, no VM) |
| `fresh-online` | 12 | Online zip, no .NET: exit 3, this release installed and trusted, start check skipped |
| `fresh-offline` | 12 | Offline zip: .NET and the app in one elevation, exit 0, start check passed |
| `upgrade-1.2.0` | 12 | A used v1.2.0 replaced: the right order for the build, data saved and moved, old copy and certificate gone |
| `leftover-cert` | 12 | v1.2.0 uninstalled by hand first: its certificate still removed |
| `repair-damaged` | 12 | This version installed, its main assembly then overwritten with zeros, and the installer run again: re-registering tried first, then the reinstall repairs it, with the data saved to Documents and back in the repaired copy (#600) |
| `app-checks` | 11, 18, 25 | On the running app: the guide in the package, and Ctrl+D and F1 opening windows with their own captions, a second launch typed into the Start menu bringing a covered window forward, the tray icon surviving an Explorer restart; a screenshot is kept for the agent to judge |
| `receiver` | 2, 10 | The app against the simulated Z3805A on the VM's COM2, its settings written for connect-on-launch: it connects and locks; follows a pulled antenna into holdover (`WAIT`, #642), notifies after the grace minute, recovers and notifies again; and comes back by itself after a 30-second power cycle. The screen is photographed as the first notification fires (with Windows' notification time raised to 60 s so it is still up), and a second loss with notifications switched off must raise none. Needs the simulator port |
| `connect-cancel` | 24 | The simulated receiver powered off, so COM2 is there and silent. Through UI Automation (`guest\Ui.ps1`, the first scenario to use it): an auto-detect walk stopped by the dialog's Cancel, then by Esc, each logged and *Disconnected. Cancelled.* within 3 s with no probe after; Cancel with nothing running closes the dialog; Connect connects once the receiver is powered on. Needs the simulator port |

A candidate built before `-Unattended` existed - any release up to v1.3.3 - gets its prompts
answered with newlines and its outcome read from its log, so older releases can be candidates.
That is how the harness was shown to fail: given v1.3.2, `upgrade-1.2.0` fails on Windows 10
exactly as #617 did.

## Things worth not rediscovering

- **Take a snapshot only once the guest has gone quiet.** A desktop that has just appeared is still
  busy with updates, indexing and first runs, and every check started from that snapshot starts busy.
  One taken 30 s after a cold boot made the app's first launch take 14 s on Windows 11. That failed
  the installer's 15 s start check in every run from it, `fresh-offline` included, until the snapshot
  was taken again after four quiet minutes. `Save-QaCleanSnapshot` waits for the processor to stay
  under 10 % for 30 s. A real machine can be that busy too, so since #646 the start check waits up
  to 45 s for the log while the process is alive.

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
- **A list passed through `powershell -File` arrives as one string.** `-Scenarios a,b` became the
  single name `a,b`, matched nothing, and the run reported success having run nothing; lists are
  split, and a run with no results fails.
- **A desktop is not yet a Tools session.** On Windows 11, Explorer can be up after a revert while
  VMware Tools' user-session process is not, and `runProgramInGuest -interactive` then refuses;
  `Wait-QaDesktop` waits for that process too.
- **The state #614 found does not reproduce under `vmrun`.** A scenario that refused a
  side-by-side install, removed the earlier copy and installed again never met `0x80270254`, in
  two forms (v1.3.2's own package, then the candidate's), where the same steps taken by hand on a
  Windows 10 desktop had (#614). Why is not known — the installs here run through `vmrun`'s own
  logon rather than from the desktop, which is the obvious difference and an unproven one. A
  scenario that cannot set up its own precondition proves nothing when it passes, so it was
  removed and that row of section 12 stays by hand; the message it checked was seen in VM test 9 (#624).
- **A blank guest password cannot be used.** Windows refuses non-console logons to accounts
  with blank passwords, which is why the account has a generated one.
