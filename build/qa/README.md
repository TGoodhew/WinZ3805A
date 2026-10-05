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
| `Set-QaEvidenceFolder` | Where a guest step that passes its deadline leaves its screenshot |
| `Enable-Qa3dGraphics` | Turns on the VM's 3D acceleration, so Windows 11 draws Mica, and re-takes *QA Clean* with it, keeping the old one as *QA Clean before 3D* |
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

**3D acceleration.** The `contrast` scenario measures text over Mica, and without the VM's 3D
acceleration Windows 11 composes the desktop in software and draws Mica as a flat fallback colour.
The app still applies Mica Alt, so nothing looks wrong, but the backdrop ignores the wallpaper and the
Mica case is never measured. `Install-QaVm.ps1` turns it on for a new VM. QA-Win11 was given it on
4 Oct 2026 with `Enable-Qa3dGraphics`, the same three-minute shutdown, edit and re-snapshot as the
simulator port. It keeps the old snapshot as *QA Clean before 3D*, which
`Start-QaVm -Snapshot 'QA Clean before 3D'` reverts to. QA-Win10 doesn't need it, because Windows 10
has no Mica and the scenario checks there that the app keeps its solid. The scenario skips a
Windows 11 VM without 3D acceleration rather than pass it.

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
nothing ran. It is 3, **AWAITING JUDGEMENT**, when every check passed but photographs still need a
verdict, and 0 only when nothing is left.

**Judging the photographs.** Since 5 Oct 2026 the photographs are part of the verdict. Until then a
pass reported PASS whether or not anyone had looked at them, and v1.3.5 was tagged from such a pass.
- **What is compared.** Every photograph a scenario keeps (`QaJudging.psm1`) is compared with the same
  photograph from the last accepted pass, kept in `%LOCALAPPDATA%\WinZ3805A QA\baselines`.
- **Unchanged** means under 0.5 % of pixels differing. In two passes of the same v1.3.5 code, 162 of
  244 photographs differed by less than that.
- **The rest go to the judge.** A pixel count cannot tell a changed reading (2–5 % wherever the clock
  or a reading is on screen) from a regression (#697's dot was a fraction of a percent). So each
  photograph that differs, is a different size or has no baseline is written to the run's `judging\`
  folder as **baseline | now | differences in red**, full size.
- **Recording a verdict.**
  `Complete-QaRun.ps1 -Run <folder> -Pass <wildcards> -Note 'what was looked at'` records one, or
  `-Fail` does. A verdict without a note is refused, because "pass" alone says nothing about what
  was checked.
- **The baseline.** When every difference has a verdict and nothing failed, the run is PASS, and
  `-Promote` makes it the baseline the next pass is compared with.
- **The wallpaper.** Each scenario sets a fixed solid wallpaper first. Windows 11's daily Spotlight
  picture made nine identical full-screen photographs differ by half their pixels.

**Deadlines.** Every guest step has one: 15 minutes by default, set per call with `-TimeoutSeconds`.
A step that passes it is stopped, its script killed in the guest, and the screen photographed from
the host (`deadline-<step>.png` in the scenario's folder) before the scenario reports the error. One
step once waited an hour on a Windows prompt nobody could answer.

**One VM at a time.** The pass holds a host-wide lock (`Global\WinZ3805A-QA-one-vm-at-a-time`) while
a VM is running. A second pass started meanwhile waits, and says so. Two VMs at once made
`connect-cancel`, `receiver` and `upgrade-1.2.0` fail on a working app (4–5 Oct 2026); one at a time,
all three passed.

**Known nondeterminism**, so it is not mistaken for a defect:
- **The simulator's first 30 seconds after a power cycle.** It refuses the GPS engine's identity and
  the satellite count with −230, as the bench unit does. `accessibility` sets the elevation mask
  whenever it gets there, so the mask's read-back can land in that window. The check accepts either
  outcome, because what it tests is that the outcome is announced.
- **Live readings and the clock.** These make most photographs differ from the baseline by 2–5 %;
  the judge confirms that the red is only on them.
- **The medallion's progress ring.** It animates, so its dots differ between photographs taken at
  different moments.
- **Fixed waits that remain.** Most are deliberate: the grace minute before a lock notification, a
  receiver held off for 30 s, the absence of a retry over 70 s, a short settle after a window
  opens. The ones that stood in for "the app has done X" now wait on X (`Wait-Until` in
  `guest\Ui.ps1`).

| Scenario | manual-qa.md section | What it requires |
|---|---|---|
| `binary-audit` | 8 | No excluded command in the assemblies this repository builds (host, no VM) |
| `fresh-online` | 12 | Online zip, no .NET: exit 3, this release installed and trusted, start check skipped |
| `fresh-offline` | 12 | Offline zip: .NET and the app in one elevation, exit 0, start check passed |
| `unblocked-download` | 12 | The online zip given the `Zone.Identifier` stream a browser writes (ZoneId 3), unblocked with `Unblock-File`, and extracted through `Shell.Application`, Explorer's own copy engine, so nothing is marked. Then: the certificate the log says was trusted against the notes' thumbprint (a dry run: the current certificate); one elevation, for the certificate alone; the .NET page named; the log's Microsoft Update line against Windows Update's own service manager; and, once .NET is installed from the offline zip's copy of Microsoft's installer, the app started from Start. The administrator prompt is on the secure desktop, so the thumbprint is read from the log |
| `offline-no-network` | 12 | The offline zip installed with the VM's network adapter disconnected from the host (`vmrun disconnectNamedDevice ethernet0`), confirmed in the guest: no adapter up, `dotnet.microsoft.com` unreachable. .NET from the zip at the version the notes give, one elevation, the app started, and `Microsoft .NET Runtime - 10.0.x (x64)` in the uninstall list. The adapter is reconnected in a `finally` |
| `blocked-zip` | 12 | The zip left marked, so Explorer's extraction marks all eight files, and `Install.cmd` started through ShellExecute, as a double-click starts it. **It does not fail** (#703). Windows 11 shows its own *Open File - Security Warning*, Windows 10 nothing, and after *Run* the install completes. Three things learned getting there. ShellExecute does not return until the warning is answered, so a helper process starts it. The helper must not be hidden, or the warning it owns is never shown. And Windows 11 draws the warning's buttons as panes UI Automation cannot press, so *Run* is the dialog's own Alt+R (Cancel has the focus, so not Enter). The screen is photographed in the guest and from the host |
| `upgrade-1.2.0` | 12 | A used v1.2.0 replaced: the right order for the build, data saved and moved, old copy and certificate gone |
| `leftover-cert` | 12 | v1.2.0 uninstalled by hand first: its certificate still removed |
| `repair-damaged` | 12 | This version installed, its main assembly then overwritten with zeros, and the installer run again: re-registering tried first, then the reinstall repairs it, with the data saved to Documents and back in the repaired copy (#600) |
| `app-checks` | 11, 18, 25 | On the running app: the guide in the package, and Ctrl+D and F1 opening windows with their own captions, a second launch typed into the Start menu bringing a covered window forward, the tray icon surviving an Explorer restart; a screenshot is kept for the agent to judge |
| `receiver` | 2, 10 | The app against the simulated Z3805A on the VM's COM2, its settings written for connect-on-launch: it connects and locks; follows a pulled antenna into holdover (`WAIT`, #642), notifies after the grace minute, recovers and notifies again; and comes back by itself after a 30-second power cycle. The screen is photographed as the first notification fires (with Windows' notification time raised to 60 s so it is still up), and a second loss with notifications switched off must raise none. Needs the simulator port |
| `sign-in` | 20 | Start at sign-in with real sign-outs. The scenario sets ForceAutoLogon first, because the answer file's AutoLogon signs in only at boot. `Invoke-SignOutAndIn` signs out with vmrun's `-noWait`, since vmrun otherwise waits for ever on a program whose session has ended, then `Wait-QaGuestReady` absorbs the first guest call after the sign-in, which hangs. The rest is in `docs/manual-qa.md` section 20's row. Needs the simulator port |
| `pin-compact` | 22 | Pinning from compact mode by every route, judged by Windows' own topmost flag and by Notepad brought to the front over the window, with the title bar, pushpin and medallion measured, the tooltip opened by real pointer movement, both menus read and used, and the state kept across Exit and a restart. The notification-area menu is a Win32 popup UI Automation cannot see; `Use-TrayMenu` in `guest\Ui.ps1` opens it through the app's tray window and reads it with Win32 |
| `whole-layout` | 23 | First launch with no stored placement, then the minimum width and a shrinking height, at 100 % and at 150 %. For 150 % the screen is set to 1600 × 1200 with `ChangeDisplaySettings` (VMware Tools' resolution tool changes nothing without a console) and LogPixels to 144, then the user signs out and in. Guest scripts call `SetProcessDPIAware` (in `guest\Ui.ps1`), or every rectangle they read at 150 % comes back scaled down. The clock line's wrap is read through its text pattern, one rectangle per line. Needs the simulator port |
| `accessibility` | 4 | A11Y-3, -9, -10 and -11 against the simulated receiver. Live-region events are recorded by `guest\LiveListen.ps1`, which declares the native UI Automation COM interface itself, because the managed client Windows PowerShell ships predates live regions and knows neither the event nor the property. It runs alongside the scenario, started with vmrun's `-noWait`. Tooltips need two things learned the hard way: the guest script's console hidden, because vmrun opens it in front of the app, and pointer movement sent as real input, because a cursor set with `SetCursorPos` reaches the title bar but not the app's content. Needs the simulator port |
| `sky-export` | 7 | The sky plot saved in Light, Dark, high contrast and at 225 %, each file measured with System.Drawing and its caption read by `Windows.Media.Ocr`. The Save dialog's file name field offers no value to set and refuses focus through UI Automation, so it is clicked and typed into; it is cancelled with Esc, because its Cancel button is not found reliably. At 225 % the placement stored at 100 % would restore a window without its footer, so it is deleted first, and the Details window can take more than 15 s to appear. The card's heading and legend are measured through UI Automation; the legend is Raw to assistive technology, so it is found through the raw view walker. Needs the simulator port |
| `guide-pages` | 13 | Every page the guide illustrates, photographed as `build\Capture-GuideImages.ps1` takes them: the screen set to 1600 × 1200 at 100 % (no sign-out, since the scaling does not change), the Details window resized until the page area measures 860 × 778 (one resize landed 96 px short), each page waited on until its Refresh buttons are enabled, and its scrolling pane found by its left edge and scrolled to the bottom for the lower half. Each photograph is put beside the guide's image in `pairs\` for the agent to judge. Needs the simulator port |
| `greyscale-states` | 4 (A11Y-12) | The simulator's control commands put the receiver through each state (`antenna off`, `antenna on`, `health ocxo fail`, `power-cycle`, `power off`), each confirmed from the app's log before it is photographed. The greyscale is Rec. 709 luminance through a `ColorMatrix`, drawn beside the colour image. **Judge the severity shapes from full-size crops**: they are 12 px, and two defects were filed from a scaled-down view in which the hexagons looked like circles. Needs the simulator port |
| `reduced-motion` | 4 (A11Y-13) | Animation effects are `SPI_SETCLIENTAREAANIMATION` (read back with `SPI_GETCLIENTAREAANIMATION`), and the app is restarted after each change. A frame is the page area read with `CopyFromScreen` and shrunk to 70 × 50, about 35 a second; a frame counts as in between when it is more than 6 grey levels on average from both the page before and the page after. The run with effects **on** is the control and must see a transition, or a run with them off seeing none would prove nothing. Needs the simulator port |
| `keyboard-focus` | 4 (A11Y-1, -2, -5) | Tab alone, reading `AutomationElement.FocusedElement` at each stop. The walk ends only when it comes back to its **first** stop: ending at any repeat took a page's unnamed stops for a trap. A list or the navigation is one Tab stop, so its other items count as reached when a sibling was a stop. A ring is the strip ±8 px around the element changing when the focus leaves it; ±4 was too narrow for a toggle switch's focus visual, and the satellite rows are exempt because the live list re-sorts between the two shots. Needs the simulator port |
| `text-scaling` | 4 (A11Y-6) | `TextScaleFactor` in HKCU\Software\Microsoft\Accessibility, followed by the `WM_SETTINGCHANGE` "Accessibility" broadcast Settings sends, and the app restarted. §9.6.1's breakpoints are reached through the screen's size at 100 % scaling (no sign-out), since the Details window is clamped to the display. At the Minimal breakpoint the navigation pane is closed behind its button, so the step opens it before selecting a page. Needs the simulator port |
| `display-scaling` | 3 | 100, 150, 200 and 225 % as LogPixels, with the screen sized for a 1280 × 800 effective desktop and the stored placements deleted, applied by a sign-out as `whole-layout` does. The caption buttons come from UI Automation's **Minimize** button: `DWMWA_CAPTION_BUTTON_BOUNDS` returns an empty rectangle for these windows. The drag starts just past the title text, because a point that only looks empty can be in the `TitleBar`'s content area, which passes input through - at 200 % the window did not move. The work-area check uses the visible frame (`DWMWA_EXTENDED_FRAME_BOUNDS`), not the window rectangle with its invisible borders. Needs the simulator port |
| `high-contrast` | 4 (A11Y-8) | The four contrast themes through `SPI_SETHIGHCONTRAST`, declared **Unicode** - bound to the ANSI entry point it reads the scheme name as ANSI and applies the default theme whatever is asked, which the first run did four times over - and named by their **internal** names on Windows 11 too, which shows them as Aquatic, Dusk, Night sky and Desert but applies High Contrast Black when asked for those. Each theme is checked by the active scheme's name, not its colours, since three of Windows 10's have a black window. The screen is 1920 × 1200 at 100 %, so no sign-out. Needs the simulator port |
| `contrast` | 4 (A11Y-4) | Every text element UI Automation reports, in the raw view so a button's label counts, measured on the window's pixels. Its background is its box's commonest colour, and its text is the third most contrasting pixel, so one stray pixel cannot pass a box. Disabled text is exempt, as §9.4.5 says, judged by the nearest enclosing control. An unnamed element that draws something is a glyph (a FontIcon, a NumberBox's spin arrows) and takes the icon floor of 3:1. Size comes from the text pattern's font attributes. An element is measured only if the window under its centre belongs to the app's process (`WindowFromPoint`). The test is by process, not by window, because the app's tooltips and flyouts are popup windows of their own (`PopupWindowSiteBridge`) and their pixels are still the app's. On Windows 10 a Windows Backup notification once supplied the pixels of five labels, which read as the app's at 3.79:1. Notifications are now turned off for the session. Another process's always-on-top window over the app is hidden before each photograph and named in the report; on QA-Win11 that was a Windows Security prompt, which no notification setting removes. The Details window is sized to the work area, because its foot went behind Windows 10's taskbar. Anything still covered fails a check of its own. **Measured at 200 %**, after a sign-out: at 100 % a 12 px glyph's stems are narrower than a pixel, so no pixel is the text's colour and every small label reads lighter than it is (the sky plot's "N", 5.8:1 by its brush, measured 4.44:1). Light and Dark are Windows' app theme in the registry plus the `ImmersiveColorSet` broadcast. **Mica keeps a wallpaper's hue and replaces its lightness**, so black and white wallpapers give the same backdrop. Each theme is therefore measured over grey and over the hardest of six saturated hues: the one moving the backdrop furthest towards the text. Then the four contrast themes. Windows 11 needs the VM's 3D acceleration (above). On Windows 10 it checks the app logs that Mica is unsupported and keeps its solid whatever the wallpaper. About 30 minutes on Windows 11. Needs the simulator port |
| `soak` | 14 | **Not in the default list**; ask for it by name. The app left running for `-SoakMinutes` (60) against the simulated receiver, locked, with the main window and Details on Overview open and nothing touched, measured by `build\Watch-Soak.ps1` running in the guest. The guest has only the .NET runtime, so the scenario copies in the standalone, Microsoft-signed `dotnet-counters` and `dotnet-gcdump` (`aka.ms/<tool>/win-x64`, cached and checked for a valid signature). The soak is started with vmrun's `-noWait` and polled until it writes `C:\qa\soak\done`. Its samples, counters, gcdumps and summary come back in `soak\`. **A soak is read against another soak**: run it for the last release (`-Release`) and for the candidate on the same VM, and compare private bytes. Needs the simulator port |
| `history-reinstall` | 21 | The history exported through the app's Save dialog, the package removed with `Remove-AppxPackage` and reinstalled, and the file imported through the Open dialog, whose file-name box is AutomationId 1148 (the Save dialog's is 1001). Which button a confirmation defaults to is read from the keyboard focus when it opens and then proved by pressing Enter. The different receiver is the simulator's `serial <number>` control command followed by a power cycle, so the session asks the new unit who it is. Needs the simulator port |
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
