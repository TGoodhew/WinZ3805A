# Manual QA checklist

The checks that need a person, a receiver, or a machine setting — everything the CI gates and the
unit tests cannot reach.

Two places in `requirements.md` name this document and, until 28 Aug 2026, it did not exist:

- **§6.4 item 4** — *"Add an integration test to the manual QA checklist: unplug the adapter
  mid-transaction and confirm the app reports Disconnected without crashing."*
- **§9.12** — which, as it then read, said *"A11Y-3 and A11Y-4 run in CI. The rest are a release
  checklist item."*

So the work had been done and recorded in issue comments, which is not something anybody can run
before a release. This is that list.

**How to use it.** Each entry says what to do, what to look for, and what it is protecting — the last
one matters, because a check whose purpose is forgotten gets performed carelessly or dropped.

**Part of it runs by itself (since 2 Oct 2026, #633).** This document used to say that nothing here
was automated and nothing should be. That held while every check needed a person at a keyboard. It
stopped holding once a script could build a clean Windows machine, install a release on it and read
the outcome. `build/qa/Invoke-QaPass.ps1` does that. It reverts throwaway VMware VMs (Windows 10 and
Windows 11, built unattended by `build/qa/Install-QaVm.ps1`) to a clean snapshot, runs the checks
marked **automated** below against a build's two zips, powers the VMs off again, and writes a report
for the release's QA-run issue. `build/qa/README.md` says how. Everything not marked still needs a
person, the bench receiver, or hardware the harness does not have.

Screenshots the automated pass keeps are **judged by the agent running it**, not left unread, and
the verdict goes in the QA-run issue (decided 2 Oct 2026). The checks that ask for a person's eye
below — section 3's clipping, section 13's images, high contrast, section 23's ellipsis — may be judged the same way,
with the screenshots attached.

| Section | Status | How |
|---|---|---|
| 2 | **automated** | `receiver`: the simulated Z3805A on the VM's COM2 powered off for 30 s and back; the session must reach *Reconnecting*, reconnect by itself, and log a `State:` line after (#639) |
| 4 | **partly automated** | `accessibility`, with the simulated receiver: **A11Y-3**, every icon-only control in both windows named and its tooltip opened by real pointer movement; **A11Y-9**, a listener records the live-region events for a mode change, a lost connection and a tier C outcome, with their live setting (decided 2 Oct 2026: the events being raised is the check, and Narrator's speech is left to bug reports); **A11Y-10**, the medallion's state and every sky-plot marker exposed as sentences; **A11Y-11**, List showing the same satellites with the same data as the plot. The rest of the section has rows of its own: A11Y-1, -2 and -5 (`keyboard-focus`), -4 (`contrast`), -6 (`text-scaling`), -7 (section 3, `display-scaling`), -8 (`high-contrast`), -12 (`greyscale-states`) and -13 (`reduced-motion`). Still by hand: whether Narrator speaks the announcements, and a user's own contrast colours |
| 3 | **automated, judged by the agent** | `display-scaling`: 100, 150, 200 and 225 %, each after a sign-out, both windows at the display's scaling and inside the work area, their title-bar buttons clear of the caption buttons, a real drag on each title bar, and both photographed. See section 3 |
| 4 (A11Y-1, -2, -5) | **automated** | `keyboard-focus`: every surface walked with Tab alone; every focusable control reached, no unnamed stops, a ring drawn at every stop, every stop at least 32 × 32. See A11Y-5 below; since 5 Oct 2026 also reading order (A11Y-1) and the rings in Dark and High Contrast #1 (A11Y-2) |
| 4 (A11Y-4) | **automated** | `contrast`, since 4 Oct 2026: every text element on the main window and every Details page measured on screen against its §9.4.5 floor, in Light, Dark and all four contrast themes, over Mica on Windows 11. See A11Y-4 below |
| 4 (A11Y-12) | **automated, judged by the agent** | `greyscale-states`: six receiver states photographed beside their greyscale. See A11Y-12 below |
| 4 (A11Y-13) | **automated** | `reduced-motion`: page changes captured frame by frame with Windows' animation effects on (the control) and off; off, no frames in between. See A11Y-13 below |
| 4 (A11Y-6) | **automated, judged by the agent** | `text-scaling`: Windows' text size at 100, 150 and 200 % at each of §9.6.1's breakpoints, confirmed in the app, with a dialog's buttons on screen and photographs for the agent. See A11Y-6 below |
| 4 (A11Y-8) | **automated, judged by the agent** | `high-contrast`, with the simulated receiver: each of the four contrast themes switched live under the running app, confirmed by name, and the main window and the Details Overview and Satellites pages measured and photographed. See A11Y-8 below |
| 7 | **automated** | `sky-export`, with the simulated receiver: the plot saved through the app's Save dialog in Light and Dark (Windows' app mode), in high contrast (the whole desktop, through `SPI_SETHIGHCONTRAST`) and at 225 % (2880 × 1800, then a sign-out). Each file is measured: every pixel opaque, the corners the theme's page background, or the live window colour under high contrast, which must be neither page background, and under high contrast the markers in the window-text colour, not the surface (#218). The caption is read back out of the file with Windows' OCR: in UTC, with the page's elevation mask. The caption must be gone from the page after Save and after Cancel, and at 225 % the caption must still be the image's last row, so nothing is cropped from the bottom. The Details window's caption is checked on every leg; at 225 % it is wrong (#663). The card's layout is measured on every leg too, through UI Automation: the heading clear of the Plot choice, and the legend's last entry inside the card; at the width Details first opens at, both fail (#664). The saved images are kept for the agent to judge, which is how #664 was found |
| 8 | **automated** | `binary-audit`: `Test-NoBlockedCommands.ps1 -ScanBinaries` on the unpacked package |
| 10 | **automated** | `receiver`: the simulated antenna pulled and held off past the grace minute; the app must log both notifications, and the screen is photographed as the first fires, for the agent to judge. Then the app is restarted with the switch off, and the same loss must raise none |
| 11 | **automated** | `app-checks`: the guide and every image it names in the package; Ctrl+D and F1 open Details and the guide, each with its own caption |
| 12 | **automated** | `release-assets` (host), `fresh-online`, `fresh-offline` (with the runtime case each VM has, #594/#595), `unblocked-download`, `offline-no-network`, `blocked-zip`, `upgrade-1.2.0`, `upgrade-previous`, `leftover-cert`, `replace-v130` (#590), `remove-everything` (#618), `uninstall-sideload`, `companions-removed` (#625), `repair-damaged`, on Windows 10 and 11 - since 5 Oct 2026. Not reproduced: Windows 10 whose only App Runtime is a newer Store one (the newer-runtime path itself runs on QA-Win11) |
| 18 | **automated** | `app-checks`: Explorer killed; the app logs re-adding its icon and keeps running |
| 20 | **automated** | `sign-in`: the guest's user signs out and Windows signs them back in (ForceAutoLogon), so the app is started by its own startup task. The setting is changed on the Settings page through UI Automation: in the notification area (started hidden, one copy, its icon, polling, and brought forward when opened from Start), with the window open, and off (nothing starts). A receiver powered off at sign-in must be retried, said once, and connected within about 30 s of answering; the connection dialog opened mid-retry must stop it and connect. The startup task's state written as 1, which Windows reads back as disabled by the user (the state Task Manager's Disable leaves), must lock the box at Off with the note; written back as 2, it shows on again |
| 24 | **automated** | `connect-cancel`: the simulated receiver powered off, so COM2 is there and silent; an auto-detect walk is stopped by the dialog's Cancel and by Esc, through UI Automation. Each must log the press and *Disconnected. Cancelled.* within 3 s, send no probe after it, and leave the dialog open with Connect usable. Cancel with nothing running closes the dialog, and Connect connects once the receiver answers |
| 25 | **automated** | `app-checks`: a second launch typed into the Start menu over Notepad; the app must be in front, alone, and say so in its log |
| 22 | **automated** | `pin-compact`: each of the four routes changes Windows' own topmost flag, and Notepad brought to the front over the window covers it only when unpinned. Measured: the title bar stays 32 px, the pushpin ends 8 px before the minimise button, the compact medallion is a whole 64 × 64 inside the window; the pushpin's tooltip opens by real pointer movement; a title-bar drag moves the window. The window's menu and the notification area's menu are read, ticks and keys included, and used. After Exit and a restart it is compact and pinned, and the footer pin agrees |
| 23 | **automated** | `whole-layout`, with the simulated receiver connected, at 100 % and then at 150 % (the screen set to 1600 × 1200 and the scaling to 144 DPI, then a real sign-out): exited from the tray menu, `window.json` deleted, started. Every row must be on screen, the clock line whole on one line with the badge and globe button centred on it, the status line clear of the buttons, and the footer within 40 effective px of the bottom; 150 % must open at the same effective size as 100 %. At the minimum width the clock wraps onto two lines before the date, still centred; the height is then stepped down and up, and the footer, readouts and clock line must go and come back together with the clock line never clipped. *Copy value* must give one line with ordinary spaces. Screenshots of both layouts are kept for the status line's ellipsis, which UI Automation cannot see |
| 13 | **automated, judged by the agent** | `guide-pages`, with the simulated receiver locked: every page the guide illustrates photographed as `build\Capture-GuideImages.ps1` takes its images, at an 860 × 778 page area measured rather than assumed, at the top and, where the page scrolls, at the bottom, once its Refresh buttons say it has finished reading. Settings is photographed with the Advanced Console switch off, as the guide shows it, and the switch is then turned on there for the console page. Each photograph is set beside the guide's own image, and the agent judges every pair against the Pass row below and records the verdicts in the QA issue. The script checks that the right thing was photographed at the right size; only the agent can say the pictures agree |
| 21 | **automated** | `history-reinstall`, with the simulated receiver: *Export history…* through the app's Save dialog, the status line and the log giving the same count and the file a SQLite database; imported straight back, the confirmation naming this receiver with no warning, and Cancel importing nothing; then the package uninstalled (its history gone, the exported file kept), reinstalled and reconnected, and the file imported: every reading added, and a second import adding none. A file exported with no receiver connected, and the same file after the simulator's `serial` command has put a different unit on the cable, are imported by pressing Enter, so the default button is proved by what it does: Import for an unknown receiver, Cancel for a different one. The trend at 7 d is photographed for the agent to judge |
| 1 (unless the probe holds), 5, 15, 16, 17, 19 | **not applicable** | Decided 2 Oct 2026. Section 5: a pass against the simulator is sufficient. Section 1 (the adapter pulled): a VM's serial port cannot vanish the way a USB device does, so unless that can be simulated a defect there is raised as a bug. Sections 15–17 (a real talker, the lamps, a UCCM's broadcast) and 19 (a second display): raised as bugs when found, not checked per release |
| 6 | **automated** | `survey-operations`, since 5 Oct 2026: a survey after a power cycle of the simulated receiver, then Cancel survey (back to the held position) and, after another, Adopt computed position (the estimate held), each reporting in about ten seconds with no survey left running. The simulator's cancel-versus-adopt behaviour is a guess, marked so in its README |
| 9 | **automated, judged by the agent** | `screen-fields`, since 5 Oct 2026: locked and in holdover, every number on the simulator's status screen found by value in the app's windows, the timeline all but frozen while both are read. Numbers only; whether each is in the right place is for the photographs |
| 14 | **automated, opt-in** | `soak`, since 4 Oct 2026, and in `Invoke-Release.ps1`: the last release and the candidate soaked 60 minutes each on QA-Win11 against the simulator, private bytes compared |
| 1 | **not applicable** | A pulled adapter cannot be simulated in the QA VMs, so the section stays out of QA (decided 2 Oct 2026: automate it only if a removal can really be simulated). Measured 6 Oct 2026 on both VMs: with COM2 open, as it always is while the app is connected, Windows refuses to disable the device — `Disable-PnpDevice` reports *Generic failure*, `pnputil /disable-device` *pending system reboot* — and the port keeps working. A USB-serial adapter pulled out cannot be refused, which is the case this section exists for. With nothing holding it the device does disable, so the refusal is the open handle, not the VM |
| the rest | by hand | a real sign-in, a display or theme change, or a person's eye, until #633 reaches them |

---

## 1. Surprise removal and reconnect (§6.4, P0-14)

**Why.** `SerialPort` has a long-standing hazard: removing a USB-serial adapter while the port is
open can raise on an internal thread and terminate the process outright, uncatchably. The three code
mitigations are provable from source; that the process survives is only provable by pulling the plug.

| | |
|---|---|
| **Do** | With the app connected and polling, unplug the USB-serial adapter. Leave it out 60 s. Plug it back into the **same** socket. |
| **Watch** | Task Manager, or `Get-Process WinZ3805A`. |
| **Pass** | **The PID is unchanged afterwards.** The app reports the loss within 10 s and reconnects within 45 s of replug. Stale readings stay on screen with their age climbing — never blanked. |

**Also check while it is unplugged** (this is the only state that shows it): the Details window
carries an error bar reading *"Lost the connection to COM3. Retrying in N seconds."* with **Retry
now** and **Stop retrying**, and the number counts down.

> The 45 s figure was 30 s until 28 Aug 2026. §7.2's backoff caps at 30 s, so an adapter returning
> just after a failed attempt waits the full interval plus ~2.2 s to open and auto-detect — the two
> clauses could not both hold (#14).

---

## 2. Receiver power cycle (#259)

> **Automated** by the `receiver` scenario of `build/qa/Invoke-QaPass.ps1`, against the Z3805A simulator on each QA VM's COM2. It reproduces what the bench unit did on 2 Oct 2026: silence while off, then a lost first command and a first screen more than 15 s late. Do it by hand when the transport changes.

**Why.** Not the same test as pulling the adapter, and it fails differently. Removal throws
`IOException`, which the transport recognises. A power cycle throws **nothing at all** — the adapter
never leaves, the handle stays valid, and the far end simply goes quiet. That case wedged the
application twice on 28 Aug: reconnected, then never polled again.

| | |
|---|---|
| **Do** | With the app connected, power-cycle the receiver. **Leave it off 20–30 s.** |
| **Pass** | A `State:` line appears in `app.log` *after* `Session COM3 is now Connected`. |

**The duration is the test.** A short cycle lets the receiver answer again before three consecutive
timeouts accumulate, so the session never enters `Reconnecting` and the failing path is never
entered. A cycle that produces no `Reconnecting` line has proved nothing.

Log location: *Show log folder* on the Diagnostics page opens it; the path is in
[how-to-use.md](how-to-use.md#where-things-are-kept).

---

## 3. Display scaling (A11Y-7, #27)

**Why.** The layout at each scaling, and the title bar's reach, are what no test sees. Changing the
scaling from a script *and expecting it to apply at once* reports success and changes nothing; written
to the profile with a sign-out after it, it applies, which is how the QA pass does it (`display-scaling`,
below; corrected 3 Oct 2026, #633).

| | |
|---|---|
| **Do** | Set 100 %, 150 %, 200 %, 225 % in turn. Restart the app at each. |
| **Pass** | No clipping, no overlap, no text cut off. The title-bar drag region still works — grab the bar and move the window. Caption buttons stay reachable. |

Restore the original scaling afterwards and **read it back to confirm**.

> **Automated since 3 Oct 2026** (`display-scaling`). At 100, 150, 200 and 225 % in turn, each set as
> LogPixels with the screen sized to a 1280 × 800 effective desktop and applied by signing out and in,
> with no stored placements: both windows checked at the display's scaling and inside the work area, their
> title-bar buttons ending before the caption buttons UI Automation reports, a real drag on each title bar
> moving the window, and both photographed for the agent to judge clipping. Its first run found #675.
>
> **225 %, not 350 %** (amended 28 Aug 2026, #27). Windows derives its scaling list from the panel's
> size and resolution, and on the 5120 × 1440 reference display it stops at 225 %. Higher figures need
> *Custom scaling*, which is system-wide and needs a sign-out. If you are ever running this on a
> high-DPI laptop that offers 250 % or 350 % in the ordinary dropdown, check them there — the clamping
> code still handles that case, it is simply not claimed to be verified.
>
> **What to look for, rather than just "does it look right".** The caption-button clearance must come
> from the system, not a formula: it scaled 138 → 207 → 276 px across 100 / 150 / 200 %, and then
> **stopped** at 225 %, where Windows holds its caption buttons at 92 px each. An application computing
> `138 × scale` would be reserving 310 px for buttons occupying 276. The check is that the app's own
> title-bar buttons never reach the caption buttons — at 225 % they end at 3841 against a caption area
> starting at 3985.

---

## 4. Accessibility, what CI cannot reach (§9.12, P0-16)

Six of the thirteen criteria have a CI gate for the part of them a script can judge — A11Y-2
(`Test-FocusVisualCoverage.ps1`), A11Y-3 (`Test-IconOnlyButtons.ps1`), A11Y-4
(`Test-ContrastFloor.ps1`, Light and Dark only), A11Y-5 (`Test-PointerTargets.ps1`, declared floors
only), A11Y-8 (`Test-ThemeDictionaryParity.ps1`, `Test-HighContrastLegibility.ps1`) and A11Y-12
(`Test-NoColourOnlyStates.ps1`, `Test-SeriesSeparation.ps1`). The full text of every criterion and
its verification method is §9.12; this is the operator's list, one item per criterion, so a run can
record a result against each number.

- **A11Y-1 Keyboard only.** Unplug the mouse. Reach every command, every page, and every dialog. Tab
  order follows reading order.
- **A11Y-2 Focus visual.** At each focus stop, in all three themes, the ring is visible against both
  adjacent surfaces, accent-filled buttons included. The gate proves the two strokes cover the
  luminance range; a person confirms a ring is actually drawn at every stop.
- **A11Y-3 Icon-only controls.** Hover each icon-only control — among them the Details title bar's
  four icons and the globe and the pin on the main window — and confirm a tooltip appears; with Narrator on, confirm each is read by name. The gate reads XAML, so
  a control built in code is this check's alone (added 29 Sep 2026, #556: this list went from A11Y-2
  to A11Y-4).
- **A11Y-4 Contrast, where the gate cannot read.** Accessibility Insights colour-contrast pass under
  a contrast theme (its colours are the user's own `SystemColor*`), and over Mica, where the backdrop
  is a live blur of the wallpaper.
  *Automated since 4 Oct 2026* (`contrast`): every text element on the main window and every Details
  page, scrolled to its foot, measured on screen against its §9.4.5 floor - 4.5:1, 3:1 for large text
  and for icons, disabled text exempt. Each theme is measured over Mica on a grey wallpaper and on the
  hardest of six saturated ones, at 200 % so small text is measured at its own colour rather than its
  anti-aliasing; then each of the four contrast themes. On Windows 10 it confirms the solid fallback
  instead of Mica. It needs QA-Win11's 3D acceleration, without which Windows draws Mica as a flat
  colour whatever the wallpaper. What it still cannot reach is a user's own contrast colours, which
  the four built-in themes stand in for. Its first run found #697.
- **A11Y-5 Target size.** Accessibility Insights target-size check. The sky-plot markers will flag;
  §9.10.2 is the answer to that flag.
  *Automated since 3 Oct 2026, with A11Y-1 and A11Y-2* (`keyboard-focus`): the main window and every Details
  page walked with Tab alone until the cycle returns to its start. Every control UI Automation calls
  focusable must be reached (a list's items count as reached when a sibling was a stop, since a list is
  one Tab stop); no Tab may land where UI Automation cannot name it; the strip around each stop must
  change when the focus leaves it, which is a drawn ring; and each stop must be at least 32 × 32. Crops of
  any stop without a measured ring are kept for the agent. What it cannot judge is whether the order
  follows reading order - the stops are recorded in order for that. Its first runs found #681, #682 and #683.
- **A11Y-6 Text scaling.** Settings → Accessibility → Text size at 100, 150 and 200 %, at each of
  §9.6.1's breakpoints — Minimal (below 640), Compact (640–1023) and Medium (1024 and up). Nothing
  clips; dialogs scroll rather than truncate.
  *Automated since 3 Oct 2026* (`text-scaling`): TextScaleFactor at 100, 150 and 200 % with the
  accessibility broadcast Settings sends, at each breakpoint reached through the screen's size (1280 × 800,
  800 × 600, 640 × 480) at 100 % scaling, with no stored placements. The size is confirmed by the clock line
  growing with it; the Satellites page's Manage dialog must keep its buttons on screen; the main window,
  Overview, Settings and the dialog are photographed for the agent to judge clipping. Its first run found
  #678.
- **A11Y-7 Display scaling** is section 3.
- **A11Y-8 High contrast.** Switch the desktop into each of the four contrast themes. Every reading
  stays legible; no foreground is painted in the surface behind it; the medallion and the severity
  shapes are distinguishable.
  > Windows 11 renamed these. "High Contrast White" is **Desert**. `.theme` files silently no-op —
  > use the Settings UI. See #218 for what this found the first time it was actually done.
  *Automated since 3 Oct 2026* (`high-contrast`): all four themes switched live under the running app with
  `SPI_SETHIGHCONTRAST` - by their internal names on both systems, because Windows 11 asked for "Aquatic"
  applies High Contrast Black - and each confirmed by the active scheme's name. The main window and two
  Details pages are measured (the theme's window colour commonest, its text colour drawn, the title bar's
  subtitle at 3:1, no window text inside a highlighted control) and photographed for the agent to judge.
  Its first run found #672.
- **A11Y-9 Announcements.** With Narrator running, force a mode change, a connection change and a
  tier C outcome. Each is spoken; a lost connection assertively rather than politely.
  *Automated since 3 Oct 2026* (`accessibility`) at the level of the events Narrator listens for; that
  Narrator then speaks them is left to bug reports, as decided on 2 Oct 2026. The listener's first run
  found a lost connection announced politely while the app reconnects (#660).
- **A11Y-10 Automation peers.** Accessibility Insights tree: the medallion exposes its state as a
  sentence, and the sky plot exposes every marker.
- **A11Y-11 List alternate.** On the Satellites page, **List** shows the same satellites with the
  same data as the plot.
- **A11Y-12 Colour.** A greyscale screenshot of every page and state (P0-19): no state is carried by
  hue alone — severity is colour **and** shape **and** text everywhere it appears. The chart-series
  gate covers the eight series and nothing else.
  *Automated since 3 Oct 2026, judged by the agent* (`greyscale-states`): the simulated receiver put
  through locked, holdover, recovery, a failing health check, power-up and reconnecting, and the main
  window and Overview photographed in each beside their greyscale. **Judge the shapes from full-size
  crops**: they are 12 px, and in a scaled-down view a hexagon reads as a circle - two issues were filed
  and closed on 3 Oct 2026 for exactly that misreading. Every state read correctly from the grey half.
- **A11Y-13 Animations off.** Settings → Accessibility → Visual effects → Animation effects off.
  Nothing animates, and no layout differs from the animated path.
  *Automated since 3 Oct 2026* (`reduced-motion`): `SPI_SETCLIENTAREAANIMATION` on and then off, the app
  restarted each time, and three page changes between Overview and Position (pages that show stored readings, not ones that read the receiver on opening) captured as fast as the screen can be read. The
  frames unlike both the page before and the page after are the transition: with effects on there must be
  several, which proves the capture can see one, and with them off none. Measured on the first run: 6 to 9
  in-between frames on, 0 off. It does not reach the medallion's own motion or a dialog's entrance.

---

## 5. Receiver states that need the hardware moved (#185, #4)

Only run when the receiver is being moved anyway. The capture harness collects these unattended:

```
pwsh build\Capture-Fixtures.ps1 -SelfTest     # the half that needs no port; run it first
pwsh build\Capture-Fixtures.ps1 [-Port COM3]  # then leave it running; Ctrl+C when the receiver has settled
```

It needs the port to itself, so **exit the application first** — closing its window only hides it
and keeps the port open; exit from Settings → Running in the background → Exit or the notification-area icon. The
harness writes only states it has not seen, seeding what it already has from disk, so leaving it
running across a whole session is safe, and it appends a provenance line to `capture-log.md` for
every file it writes — commit the two together.

| State | How to reach it |
|---|---|
| Power-up, acquiring | Power-cycle the receiver. |
| Holdover | **Pull the antenna lead** with the receiver running. Wait several minutes — the elapsed-time and present-uncertainty fields only become meaningful with time on the clock. |
| Recovery | Plug the antenna back in. It passes through recovery on the way to lock, so this state exists only in that window. |
| Survey in progress | Power-cycle with survey-on-power-up enabled. The receiver refuses a survey command while holding a position (#229), so the power cycle is the route. |
| Health-monitor failure | Cannot be induced. The harness names it `-health-fail` and will capture it if one ever happens. |

---

## 6. Survey operations (P0-12)

Each needs a survey actually running, which needs a power cycle with survey-on-power-up enabled.

| | |
|---|---|
| **Do** | With a survey running, press **Cancel survey** and confirm. |
| **Pass** | The dialog reports success after about ten seconds; the Position page shows the previously held position, not the partial estimate; the survey card says no survey is running. |

| | |
|---|---|
| **Do** | Power-cycle again, let the survey run a few minutes, press **Adopt computed position** and confirm. |
| **Pass** | Success after about ten seconds; the position shown is the estimate as it stood; no survey is running. Adopting early leaves a poor position, and the manual entry form is the way back. |

- **Cancel** sends `:GPS:POSition LAST` and restores the previously held position — so it costs
  minutes rather than the two hours a full survey takes, and leaves the receiver where it started.
- Both take **about ten seconds** to answer. That is normal — the receiver tears down the
  accumulation before replying — and is why they have their own 30 s timeout class (#256).

## 7. Sky-plot image export (#47, §10.5)

Needs satellites on the plot, so it goes with section 5 rather than standing alone. Everything here
is a property of the *file*, which is why none of it is in CI — the rendering path leaves no trace in
source that a script could check.

- **Save image, in all three themes.** Light and Dark are app-mode settings and safe to drive from a
  script. High contrast is a whole-desktop change and takes minutes to apply and undo, so it wants a
  person who is not using the machine — but it is **not unsafe**, and an empty `High Contrast Scheme`
  is **not** a reason to skip it. That was asserted once, on 28 Aug, and was wrong: the reversibility
  round trip was performed from exactly that baseline.

  **All three legs passed on 28 Aug 2026**, measured rather than eyeballed:

  | Theme | Corner colour | Matches | Non-opaque samples |
  |---|---|---|---|
  | Light | `#F3F3F3` | `WzPageBackgroundFallbackBrush` | 0 |
  | Dark | `#202020` | same token, Dark | 0 |
  | High contrast (Desert) | `#FFFAEF` | live `GetSysColor(COLOR_WINDOW)` | 0 |

  Pick **Desert** for this leg specifically. Its cream `#FFFAEF` is distinct from both the Light and
  Dark page backgrounds, so a matching corner proves the flatten resolved the *high-contrast* token
  rather than coincidentally agreeing with one of the others. Night sky would not: its window colour
  is `#202020`, which is also the Dark fallback, and the check would pass either way.

  Also confirm the plot is not painted in the surface colour — the #218 failure. Count
  `SystemColorWindowTextColor` pixels inside the plot region; 3,303 were present against 862,623 of
  window colour on 28 Aug. All seven `WzSequential*` steps resolve to window text under high
  contrast, so signal strength is carried by **marker area alone** there. That is intended, and it is
  why §10.5 scales area with C/N rather than relying on the ramp.

  Two traps when driving it from a script: `Start-Process -ArgumentList` **splits an unquoted scheme
  name into three arguments** and the script fails without changing anything, and the `-Off` path
  **substitutes and persists** `High Contrast White` into a scheme that was empty — clear it back by
  hand and read it back, or the baseline is quietly wrong afterwards. The export is deliberately
  not theme-substituted, so each one produces a different and correct file; what is being checked is
  that none of them produces an **illegible** one. Under high contrast in particular, confirm the
  markers are not the window colour — that was #218's whole failure mode, and an exported PNG is
  where it would be least visible.
- **Open the file outside the app.** This is the check that found the export shipping
  semi-transparent (28 Aug): every corner measured `A=0` and the caption row `A=179`, because the
  card fill resolves to a stock Fluent colour and stock tokens are mostly **not opaque**. It looked
  perfect in a viewer compositing over white. The capture is now flattened onto
  `WzPageBackgroundFallbackBrush`, so the corners should measure the page background of the theme
  you exported in — `#F3F3F3` Light, `#202020` Dark — and nothing should be under `A=255`.
  **Measure it; do not eyeball it.** The whole failure mode is that it looks right.
- **Read the caption.** Time in UTC, and the elevation mask present and matching the box on the page.
  A caption that disagreed with the plot above it would be the one defect that makes the record
  actively misleading rather than merely absent.
- **Confirm the caption is gone from the screen afterwards**, including after cancelling the save
  dialog. It is shown only for the duration of the render.
- **At 225 % scaling**, check the file is not cropped. `RenderTargetBitmap` truncates rather than
  throwing when it is asked for more pixels than the hardware will give, so an over-budget capture
  produces an image that opens cleanly and is missing its bottom.

---

## 8. The shipped binary carries no excluded command (P0-7, §8.4)

> **Automated** by `build/qa/Invoke-QaPass.ps1` (`binary-audit`): the package is unpacked and `Test-NoBlockedCommands.ps1 -ScanBinaries` reads every assembly as ASCII and UTF-16. It judges only the assemblies this repository builds. On v1.3.3, two third-party assemblies had coincidental hits (markup, not SCPI) and were listed, not judged.

**Why.** P0-7's acceptance is a manual audit of the built binary, the only P0 whose stated method
is manual. `Test-NoBlockedCommands.ps1` proves the *source* holds §8.4's tokens in one file; only a
search of the *output* proves nothing else — a resource, a generated string, a dependency — carries
them. This checklist cannot spell the tokens (the gate exempts only the specification), so take
them from §8.4.

| | |
|---|---|
| **Do** | Build Release. Search every `.dll` in the package output for each §8.4 token, as text, case-insensitively. |
| **Pass** | The only matches are the regular-expression patterns compiled from `BlockedCommands.cs` in `WinZ3805A.Device.dll`. The application assembly contains none. |

## 9. Every status-screen field reaches the Details window (P0-5)

**Why.** P0-5's acceptance — *every field in the source status screen is represented somewhere in
the details UI* — has no test, because a test cannot know what "represented" means.

| | |
|---|---|
| **Do** | Take a captured screen from `tests/WinZ3805A.Tests/Fixtures/` and walk it line by line against the Details pages with the receiver in a comparable state. |
| **Pass** | Every field has a home — a readout, a table cell, a card — or a recorded reason for not having one. |

## 10. Lock notifications (P1-9, #288)

> **Partly automated** by the `receiver` scenario: the simulated antenna is pulled and held off until the app logs its notification, then reconnected until it logs the second. On screen, and the switch-off half, are still by hand.

**Why.** The notification path was rebuilt on 29 Aug 2026 after `AppNotificationManager` turned out
never to have registered on any machine, and nobody noticed for a fortnight because nothing tests
it. It rides on section 5's antenna pull.

| | |
|---|---|
| **Do** | With *Tell me when the receiver loses GPS lock* on, pull the antenna and wait. Plug it back in. Then turn the switch off and repeat. |
| **Pass** | A Windows notification about a minute after the pull, not before; another when lock returns; none at all with the switch off. |

## 11. Help in the installed package (#312)

> **Automated** by `build/qa/Invoke-QaPass.ps1` (`app-checks`), on the sideloaded package in a clean VM: the guide and every image it names are under `Help\` in the installed package, and Ctrl+D and F1 open the *Receiver Details* and *Help* windows. Windows must know each by its own caption, which is what the taskbar, Alt+Tab and Narrator name them by (#637).

**Why.** The guide and its images are linked `Content` items copied into the package; whether the
*installed* application carries `Help\how-to-use.md` and its images is checkable only there.

| | |
|---|---|
| **Do** | In the sideloaded install, press `F1` from the main window and from Details. |
| **Pass** | The guide opens in its own window with every screenshot rendered, not the fallback text. |

---

## 12. The published release installs on a machine that has never had it

> **Partly automated** by `build/qa/Invoke-QaPass.ps1`. On Windows 10 and Windows 11 VMs:
> - a fresh online install with no .NET (exit 3);
> - a fresh offline install with .NET and the start check (exit 0, one elevation);
> - replacing a used v1.2.0;
> - a leftover certificate;
> - this version installed, then damaged, and repaired by running the installer again (#600);
> - **since 5 Oct 2026**, `upgrade-previous`: the previous release installed and used, then this one over it - the in-place upgrade most users make, with history, a changed setting and the remembered port kept; and `release-assets`, on the host: the zips' version, signer and certificate, and for a published release its notes' hashes, thumbprint and .NET row;
> - **since 5 Oct 2026, the three download rows below**:
>   - `unblocked-download`: the online zip given the mark a browser leaves, unblocked, extracted by Explorer's own copy engine, then the notes' thumbprint, one prompt, the .NET page named, the Microsoft Update line against Windows' own setting, and the app started once .NET is in;
>   - `offline-no-network`: the offline zip with the VM's network adapter disconnected;
>   - `blocked-zip`: the zip left blocked, and `Install.cmd` started as a double-click starts it, through ShellExecute.
>
> Two parts of those rows are reproduced rather than performed. The browser download is reproduced by the mark it leaves. The administrator prompt is on the secure desktop, so the thumbprint is read from the installer's log, which is what was actually trusted. The row for a machine an earlier installer left unable to start the app was retired on 4 Oct 2026: #614 is closed, fixed by #619 in v1.3.3, and the Windows 10 replacement row below, automated as `upgrade-1.2.0`, is what guards that fix. The harness was shown to fail: given v1.3.2 as the candidate, the Windows 10 upgrade fails as #617 did.

**Why.** Everything else here tests the application. This tests the *download* — and it is the only
check with a stranger at the other end of it. The failure modes are all invisible from a developer
machine, because a developer machine already has the runtime, already trusts the certificate, and
never sees the mark Windows puts on a downloaded file.

Do it on the artifact from the **release page**, not on `dist\` — downloading is half of what is
being tested.

**Keep the installer's log with the result** (#592). Every run writes one to
`%LOCALAPPDATA%\WinZ3805A Installer\logs`, and it holds three things:
- what the installer found and why it removed or kept each copy and certificate;
- the machine's state before and after;
- whether the app stayed open when the installer started it.

Attach it to the release's QA-run issue. A failure should leave a log that ends in its reason.

There are **two zips** since #588. Run both, each on a machine that has never had .NET 10: a fresh virtual machine per zip is the honest way.

| | |
|---|---|
| **Do** | **Online zip**, on a machine with no Visual Studio, no Windows App SDK and no .NET, connected to the internet: download `WinZ3805A-<version>-x64.zip` from the release, right-click it → *Properties* → **Unblock**, extract, double-click `Install.cmd`. Install .NET from the page it opens, then start the app. |
| **Watch** | The certificate thumbprint in the UAC/trust prompt. |
| **Pass** | The thumbprint matches the one in the release notes. One administrator prompt, for the certificate. Step 2 says .NET 10 is not installed and opens `dotnet.microsoft.com`'s .NET 10 page. Once .NET is installed from that page, the app appears in Start and launches. The last lines say whether Microsoft Update is on, and that matches *Settings › Windows Update › Advanced options*. |

| | |
|---|---|
| **Do** | **Offline zip**, on a second such machine, **disconnected from the network**: the same steps with `WinZ3805A-<version>-x64-offline.zip`. |
| **Pass** | **One** administrator prompt, for the certificate **and** .NET together, and no other. Step 2 names the installer and reports *.NET 10.0.x installed*. The version matches the release notes' row for the offline zip. The app launches with no network and no download prompt. *Settings › Apps* lists *Microsoft .NET Runtime - 10.0.x (x64)*: an ordinary Microsoft install, which is what Microsoft Update services. v1.3.1's single zip installed and then stopped at first launch on a .NET download prompt (#586). |

| | |
|---|---|
| **Do** | Repeat *without* unblocking the zip first. |
| **Pass** | The install completes as it does from an unblocked zip. On Windows 11, Windows' own **Open File – Security Warning** comes first, naming `Install.cmd` and an unknown publisher; after **Run** it carries on. Windows 10 shows no warning. *This said the install was expected to fail legibly: measured on 5 Oct 2026 with v1.3.4 and v1.3.5 it does not fail at all, because `Install.cmd` starts PowerShell with `-ExecutionPolicy Bypass` and nothing else in the zip cares about the mark (#703).* Automated as `blocked-zip`. |

| | |
|---|---|
| **Do** | `build\Uninstall-Sideload.ps1`, then `Get-AppxPackage -Name WinZ3805A` and `certlm.msc` → *Trusted People*. |
| **Pass** | Neither the package nor the certificate is left behind. |
| **Do** | **Removing everything (#618).** On a machine where this release's `Install.cmd` replaced an earlier-identity copy (v1.2.0), so there is a saved copy in Documents and the installer's logs, and where an earlier release's `WinZ3805A.cer` was also added to the account's own *Trusted People* (double-click it and accept the wizard): download `build/Remove-WinZ3805A.ps1` from the page `docs/remove-winz3805a.md` and run it with `-ListOnly`, then without. Accept saving the data; decline deleting the saved copies. |
| **Pass** | `-ListOnly` changes nothing. The real run asks once for administrator permission. Afterwards: <ul><li>`Get-AppxPackage -Name WinZ3805A` lists nothing;</li><li>*Trusted People* holds neither `7F47E8D7…` nor `655D07E3…`, in `certlm.msc` or in `certmgr.msc`;</li><li>`%LOCALAPPDATA%\WinZ3805A Installer` is gone;</li><li>Documents has a *WinZ3805A saved data …* folder with `trend.db`, and the earlier saved copies are still there;</li><li>the Windows App Runtime and .NET are untouched;</li><li>its own *Checking* step reports nothing left, and its log is on the Desktop.</li></ul> After a restart, this release's `Install.cmd` installs and starts. |

**Last run of the removal row:** 1 Oct 2026, Tony's Windows 10 22H2 VM, on the state left by the Windows 10 replacement row. Results are attached to #618.
- **It passes.** `-ListOnly` changed nothing. The real run saved the data, then removed the app, `7F47E8D7` from the machine, `655D07E3` from the account's store, and the installer's folder. It kept the saved copy. After a restart, `Install.cmd` installed and the app started.
- **The account's certificate stores are a merged view that includes the machine's.** The first version listed the machine's certificate a second time, under the account, and reported it could not be removed there. The script now counts it once, as the machine's. Importing a certificate into the account's store while the machine already trusts it adds no copy of its own: after the machine's was removed, none was left in any store.

> **When the publisher changes, this section is not optional.** `Identity/@Name` and
> `Identity/@Publisher` together form the package family name, so a build signed by a different
> publisher installs *alongside* the old one instead of upgrading it, and `Uninstall-Sideload.ps1`
> — which finds the certificate by the manifest's *current* publisher — will not remove the old
> certificate. It happened at v1.0.1, when the placeholder `CN=AppPublisher` was replaced, and
> again at v1.3.1, when a misspelling of the company's name was corrected to
> `CN=The Schnauzer Group LLC`. Since then `Uninstall-Sideload.ps1` also removes the certificate
> of every installed copy's publisher, whatever that publisher was. **Since #590, `Install.cmd`
> replaces an earlier copy itself.** The next row checks that on a machine with a real earlier
> copy.

| | |
|---|---|
| **Do** | **Replacing an earlier copy (#590).** On a machine with **v1.3.0** installed from its own release page, with some history recorded and the app running: run this release's `Install.cmd`. |
| **Pass** | It asks for the running copy to be closed and waits. *Before anything changes* lists version 1.3.0.0 and the certificate `655D07E3…`, and only then asks, in **one** administrator prompt. Afterwards: <ul><li>`Get-AppxPackage -Name WinZ3805A` lists only this release;</li><li>`certlm.msc` → *Trusted People* holds only this release's certificate;</li><li>Documents has a *WinZ3805A earlier copy 1.3.0.0 …* folder containing `trend.db`;</li><li>the app opens with 1.3.0's history and its remembered connection.</li></ul> |
| **Do** | **Replacing an earlier copy on Windows 10 (#617).** The same as the row above, on a **Windows 10** x64 machine: an earlier-identity copy (v1.2.0 or v1.3.0) installed from its own release page, started, used for a minute, then closed. Run this release's `Install.cmd`. |
| **Pass** | *Before anything changes* says this version of Windows cannot install the two side by side, and lists save, remove, install. The log has `decide order  save and remove the earlier copy, then install: build 19045`. A *Removing the earlier copy* step saves the data to Documents and removes the copy **before** *4 of 4*. Step 4 installs **without** `0x80073CF3`, the data is moved in afterwards, and **the start check reports the app running**. The start check is the real test, because the refused install this replaces left the app unable to start (#614). |
| **Do** | **A newer Windows App Runtime already installed (#594, #595).** On a **Windows 10** x64 machine whose only `Microsoft.WindowsAppRuntime.2` is a newer, Store-serviced one (2.5.1.0 or later, typically brought by a Photos update): run `Install.cmd`. |
| **Pass** | Step 3 says the runtime is *already present (2.5.x), which is this version or newer* and installs nothing. The companion line names the newest framework and gives both companions' versions. The app installs, the start check reports it running, and its window opens. The start check is the real test here, because missing companions fail silently (#473). On Windows 11 with the zip's runtime beside a newer one, the companions follow the newer. |
| **Do** | **The runtime's companions missing (#625).** With this release installed and closed, remove them: `Get-AppxPackage MicrosoftCorporationII.WinAppRuntime.Main.2 \| Remove-AppxPackage`, and the same for `MicrosoftCorporationII.WinAppRuntime.Singleton`. Start WinZ3805A, then run `Install.cmd` again and start it once more. |
| **Pass** | It opens and works without them. `app.log` has `FAIL  Runtime  Windows App Runtime: PackageInstallFailed, 0x80070005` with the advice to run the installer again, where v1.3.3 exited silently with that code. After the rerun, both companions are back and the next start logs *its parts are in place*. |
| **Do** | **A certificate left behind.** On a machine where v1.3.0 was uninstalled from *Settings › Apps*, so its certificate is still trusted: run `Install.cmd`. |
| **Pass** | *Before anything changes* lists the certificate `655D07E3…` and no earlier copy. After the one prompt, *Trusted People* no longer holds it. |
| **Do** | **This version, damaged (#600).** With this release installed by `Install.cmd`, started once and closed, damage it from an elevated PowerShell. Take ownership of `WinZ3805A.dll` in its install folder (`takeown`, then `icacls … /grant *S-1-5-32-544:F`) and overwrite it with zeros of the same length. Confirm the app no longer starts, then run this release's `Install.cmd` again. Automated as `repair-damaged`. |
| **Pass** | *Before anything changes* says this version is already installed and how it will be repaired if it does not start. After the start check fails, *Repairing WinZ3805A* registers it again, which does not help, then saves the data to a *WinZ3805A repair backup …* folder in Documents, removes the app, installs it again and moves the data back. The start check then reports it running, the log has `repair        repaired by rung 2`, and the app opens with its history. |

**Last run of the upgrade rows:** 30 Sep 2026, Tony's clean Windows 11 Pro VM (build 26200), from **v1.2.0** rather than v1.3.0. Both share the earlier identity and certificate. The dry-run artifact of #589 + #591 + #593 + #596 was used, with the online zip. The installer log is quoted in #590's thread. Results:
- it waited for the running 1.2.0 to be closed;
- it listed 1.2.0.0 and `655D07E3…`;
- **one** administrator prompt trusted `7F47E8D7…` and removed `655D07E3…`;
- the runtime was *already present (2.5.1.0)*, the Store's, so nothing was installed: #595's case on Windows 11;
- the companions matched 2.5.1.0 (Main 2.5.1.0, Singleton 8002.5.1.0);
- 1.2.0's data was saved to Documents and moved, then 1.2.0 was removed;
- the start check reported the app running.

The previous attempt had found the parameter-collision bug that #589 fixes: a successful prompt was reported as declined. **Windows 10 and the offline zip are still to run.**

**Windows 10, 1 Oct 2026** (Tony's Windows 10 22H2 VM, build 19045, published v1.3.2 zips; the diagnosis files are attached to #614):
- **The v1.3.2 installer fails the upgrade row on Windows 10.** Step 4 is refused with `0x80073CF3`, because a package with the same name is already installed (#617).
- Uninstalling the earlier copy by hand after that refusal, then installing again, leaves an app that is listed and intact but never starts: `0x80270254` (#614). **A restart fixes it.**
- Uninstalling the earlier copy *without* a refused attempt first, then installing, works. So does a fresh install with no earlier copy.

The two Windows 10 rows above were written from those runs. Both were then run against #617's fix: the fixed `install.ps1` in the extracted v1.3.2 folder, which is what the packager ships, verbatim.
- **Replacing an earlier copy on Windows 10:** passes. The log has `decide order  save and remove the earlier copy, then install: build 19045`. 1.2.0's data was saved to Documents and the copy removed, then 1.3.2 installed with no refusal. The data was moved in, and the start check and a second launch both found the app running.
- **A machine an earlier installer left unable to start the app:** passes. After the v1.3.2 installer's refusal and a manual uninstall, the fixed installer's start check found no process. After a restart, the app started.

---

## 13. The guide's screenshots still show the application

**Why.** `docs\how-to-use.md` is also the F1 help, and its 18 page screenshots are the part of it
nothing can check. **A wrong screenshot is worse than a missing one**: drifted prose reads as prose,
but a picture is read as evidence, and a reader who sees a control in the guide that is not in the
application concludes they have the wrong version.

`Test-GuideCoverage.ps1` makes it impossible to ship an option nobody wrote about. It cannot look at
a picture.

| | |
|---|---|
| **Do** | Open each page the guide illustrates beside the guide, at the width the images were taken at, and compare. |
| **Pass** | Every control visible in the image is on the page, with the same label; nothing on the page that the surrounding text names is missing from the image. |

> **Two scripts take every image, and both were last run on 29 Sep 2026** against the bench Z3805A,
> locked, with winapp 0.7.0. Both drive a running, connected application, so the Do row above still
> applies afterwards: **look at every image**. A page that has not finished reading photographs just
> as willingly as one that has — the first run's Diagnostics pictures had the spinner turning and
> Lifetime and GPS receiver dashed, which is why the page script now waits for the page's Refresh
> button to come back before it shoots.
>
> - `build\Capture-GuideImages.ps1` takes the `page-*.png` pictures. It photographs each page's
>   content pane as an *element*, so no cropping arithmetic can be wrong about where the page is.
>   **Its `-ContentWidth` is load-bearing.** #351 flows the cards into as many columns as fit, and
>   the threshold is 864 px. The guide's prose and its "upper half" / "lower half" pairs are written
>   around a single column, so the default of 860 is what keeps the pictures matching the words.
> - `build\Capture-GuideCloseups.ps1` takes everything else: the main window and its parts, the
>   time-zone flyout, compact mode, the connection dialog, the Details title bar and its icons, the
>   navigation pane, and single controls. Each is a crop of the window's root pane to the reported
>   bounds of the controls that make it up. Photographing each part by its own name was tried first:
>   most of those names are grids, which UI Automation does not expose, and winapp quietly fell back
>   to the whole window.
>
> **The Advanced Console page exists only while its switch is on**, so turn it on in Settings before
> the page run and back off afterwards; the switches' own close-ups are then taken with it off,
> which is the default the guide's table describes.
>
> **`main-compact.png` was taken last, once #565 was fixed.** Compact mode had been drawing the TFOM
> and FFOM pills clipped under the medallion, and a picture of the defect would have illustrated the
> wrong thing.
>
> **Automated since 3 Oct 2026** (`guide-pages`, #633). The pass photographs the pages in a QA VM against the simulated receiver and puts each beside the guide's picture; the agent judges them. Expect values to differ (the time, the sky, how much history there is) and judge controls and labels. Two differences are known and are not defects: the simulator's status-register masks read 0 where the bench unit's were set, and Windows 10 draws the console's search glyph from a different icon font. **It checks the guide; it does not retake it.** When a pair disagrees, retake the guide's image with the scripts below.
>
> **One thing the run left alone, on purpose:**
>
> | Image | Why it was kept |
> |---|---|
> | Receiver-specific cards | The Z3805A has no **Disciplining loop** card (a UCCM's, #512), no **Antenna** or **Integrity** pill (#515, #516) and no **Position uncertainty** row (#516), so the pictures correctly show none. The guide's prose says which receivers have them |

---

## 14. Memory over hours (#385, #399, G1)

**Why.** Two leaks have shipped, and neither was visible in an afternoon. #385 held 2.2 GB and
pegged a core inside two hours — the kind anybody notices, eventually, after ten hours and 4.9 GB of
diagnosis. #399 was the other kind: **19 MB an hour with CPU perfectly flat, nothing in the log, the
window still responsive**, and 3.2 GB across the week G1 expects the app to survive on a second
monitor. Nothing in CI can see either. A soak needs a receiver, hours, and somebody to read it.

Run it when anything on the polling, rendering or shell-badge path has changed, and before a release
that touches any of them.

```
pwsh build\Watch-Soak.ps1 -SelfTest                       # the arithmetic; needs no receiver
pwsh build\Watch-Soak.ps1 -Label before -DurationMinutes 60
```

**A soak is read against another soak, never against a threshold.** Working set depends on the
window layout, the page on screen and how long the receiver has been locked, so a number on its own
means nothing. Hold these equal between the two runs, or the comparison is worthless:

| Hold equal | Why |
|---|---|
| The resting page | Overview reads the trend store; Status Registers does not. |
| Which windows are open | The Details window is a second render path on every reading. |
| Any navigation done first | Visiting pages allocates legitimately. Do the same visits, then let it settle. |
| Receiver state | A locked receiver and one in holdover poll differently. |
| Duration | Growth is a rate, and a rate needs the same denominator. |
| Who else is on the machine | Another process taking memory trims this one's working set. It cannot touch private bytes, which is the other reason to read those. |

**The verdict is private bytes, not working set.** Working set is what the operating system
currently keeps resident, so anything at all can move it without the application allocating or
freeing a byte — another process taking memory, the window being minimised, your own dump faulting
every page back in. On 4 Sep 2026 one three-hour run read **−16.35 MB/hour** as working set and
**−0.07 MB/hour** as private bytes, and the private figure was the true one: a peer session's bench
work had trimmed 38 MB out of residency in a single sample while committed memory did not move.
`Watch-Soak.ps1` leads with private and warns when the two disagree; if it warns, the working-set
number is not about this application and should not be quoted.

**Read it in this order**, because each instrument answers what the one before it cannot:

1. **Private bytes** — is anything growing at all, with **working set alongside** as the only
   instrument that sees a GDI, handle or native leak, none of which appear on a managed heap.
2. **The heap sizes from `dotnet-counters`** — *which* heap. #399 was 147.6 MB of large object heap
   against 4.4 MB of gen2, and that one line would have aimed the whole investigation a day earlier.
3. **The gcdump totals and type lines** — *which type*. #399's finding was 69.5 MB of
   `ManagedObjectWrapperHolder[]` against **101,861 live objects in the entire heap**. A table sized
   for eight million under a heap of a hundred thousand is not a retention bug but a *rate* bug —
   something minting wrappers, not something holding them — and no working-set curve says that.

**What to do when it grows.** Get the allocating callstack rather than reasoning about it:

```
dotnet-trace collect -p <pid> --providers Microsoft-DotNETCore-SampleProfiler --duration 00:00:02:00
```

#399's mechanism was *inferred* from reading twelve call sites, and shipped with that inference
stated as unproven. A trace would have named the caller in five minutes.

> **Attaching changes the reading.** A diagnostic session allocates its buffers inside the process
> being measured: one gcdump taken mid-soak on 4 Sep 2026 moved the working set 8 MB in the very
> next sample, and a `dotnet-dump collect --type Heap` moved it **120 MB** by faulting every page
> into residency to read it — while private bytes fell slightly, which is how you tell. That is why the script dumps at the two ends and not throughout, and why
> `-SkipDumps` exists for when another measurement is already in flight. If you attach anything by
> hand during a run, the run is contaminated — start the window again.

> **`dotnet-counters ps` does not list a packaged app**, and its silence is not evidence that the
> diagnostics IPC is unreachable. The first #399 session concluded exactly that and spent the night
> on working set alone. Check for `\\.\pipe\dotnet-diagnostic-<pid>` and attach with `-p <pid>`.
> `dotnet-gcdump` is not installed by default: `dotnet tool install -g dotnet-gcdump`.

---

## 15. A talker on the bench (#420)

**Why.** The NMEA family was written against `tools/NmeaSimulator` and shipped having never heard a
receiver (#310). A simulator only emits what its author thought of, so until 7 Sep 2026 the whole
family rested on one person's idea of what a talker says. Ten captures — five from the VK-162 and
five from a forM8N — now sit in
`tests/WinZ3805A.Tests/Nmea/Captures/` and are replayed in CI for ever — but a capture cannot be
re-taken without the hardware in front of you, which is why this is a procedure rather than a note.

Run the self-test first; it needs no port and checks the half that can be checked:

```
pwsh build\Capture-Talker.ps1                 # no -Port: lists the ports present, and stops
pwsh build\Capture-Talker.ps1 -SelfTest
pwsh build\Capture-Talker.ps1 -Port COM4 -Label <what-this-sitting-is> -DurationMinutes 30
```

**Exit the application first.** It holds the port, and closing its window only hides it.

**A USB talker's port number is not stable.** It is enumerated per socket on a bridge chip with no
unique serial, so the same receiver in a different socket can appear as a different `COM`. List the
ports before and after plugging it in rather than assuming.

**Fill in the note's "What was happening" on the day.** `NmeaCaptureReplayTests` fails a capture
whose note still carries the placeholder, so a capture committed without one breaks the build —
deliberately.

### The states, and how much they actually cost

| State | How to reach it | Cost on 7 Sep 2026 |
|---|---|---|
| Steady state | Leave it somewhere with a clear view of the sky and walk away. | Free. 30 min gave 1,800 cycles with not one dropped. |
| Power-on, acquisition | Unplug, wait several minutes, plug in. Start the capture on the port *appearing*, or the boot banner is missed. | Easy, but yielded **one** no-fix cycle: a hot start with signal reacquires in a second. |
| Fix quality ladder | Nothing. It walks `0` → `1` → `2` on its own as SBAS corrections arrive. | Free, and it is the whole `ModeDetail` ladder. |
| Weak signal | Any partial obstruction. | Easy, and more useful than it sounds — see below. |
| **Fix lost while powered** | **Needs a real enclosure — or a cold start. See the warning.** | Not by shielding. **Achieved on 11 Sep 2026 on the forM8N** with a `UBX-CFG-RST` cold start (`form8n-fix-lost`). |

> **Do not repeat the improvised shielding.** Two attempts failed and the measurements are worth
> having: an inverted metal cover gave about **5 dB** of attenuation (mean C/N 31.1 against 36.6 in
> the open) and a **microwave oven with the door shut** about **10 dB** (27.4). The oven still held
> a fix on 7 to 11 satellites in every one of 387 cycles. Adding a saucepan inside the oven changed
> nothing measurable. A GPS receiver is far more sensitive than household metalwork, so budget for a
> proper screened enclosure — or do what `form8n-fix-lost` did on 11 Sep 2026 and cold-start the
> module with `UBX-CFG-RST`, which loses the fix without touching the signal.
>
> The failure was still worth keeping: at 17% of satellites in view but untracked, against 6.6% in
> the open, that capture is the corpus's best source of weak-signal input and of satellites crossing
> the tracked boundary. **A shielding attempt that fails is a weak-signal capture that succeeded**,
> so label and keep it rather than deleting it.

### The puck is not GPS-only, and that was worth finding out (8 Sep 2026)

**This section previously said a second constellation "needs a GLONASS- or Galileo-capable module".
The VK-162 is one.** Its ROM advertises `GPS;SBAS;GLO;QZSS` and `UBX-CFG-GNSS` carries a GLONASS
block — supported, and merely disabled. Poll the receiver rather than believing a label:

```powershell
# UBX-MON-VER (0x0A 0x04) lists the constellations in the ROM.
# UBX-CFG-GNSS (0x06 0x3E) says which are enabled.
```

**Ask for what you want and let the receiver refuse.** Three attempts, three answers:

| Asked for | Answer |
|---|---|
| GPS + GLONASS concurrently | **NAK** — u-blox 7 runs one constellation at a time |
| GLONASS + SBAS + QZSS, GPS off | **NAK** — SBAS and QZSS are GPS augmentations |
| GLONASS alone, all augmentations off | **ACK** |

**Send `CFG-GNSS` to RAM only — no `CFG-CFG` save — so unplugging reverts it**, and set it back
explicitly afterwards anyway. `vk162-glonass-only.nmea` is the result, and it is the only capture in
the corpus taken with the receiver reconfigured; its note says so, because a capture whose
provenance is silent about that is misleading.

**Two things came free that shielding could not buy.** GLONASS from cold with no almanac gave
**152 consecutive no-fix cycles** — against *one* from the GPS cold start, which reacquires in a
second — so it is the corpus's longest genuine no-fix stretch and cost nothing but patience. And
with SBAS off the fix ladder stops at quality `1`, which is the **standalone** branch of
`ModeDetail` that no other capture takes.

**What is still genuinely out of reach here.** #424 needs two constellations *in one cycle* and this
receiver cannot produce them at all; it also numbers GLONASS in 65–96 exactly as NMEA 4.10 says, so
it would not collide even if it could. #429's `GNS` likewise. Both needed different hardware, and
the forM8N supplied it: `form8n-gps-beidou-*.nmea` for #424 and `form8n-gns-without-gga.nmea` for
#429. Both issues are closed (noted 29 Sep 2026, #556).

### The stationary dynamic model, and why no capture came of it

**`CFG-NAV5` sets it and u-center is not needed** — the same UBX route as `CFG-GNSS`, mask bit 0 so
every other navigation setting is left as found, and read the value back afterwards because an ACK
says the frame was accepted rather than that the value stuck.

Done on 8 Sep 2026 with a 12-minute sitting, and **the result is a negative worth recording**: the
sentence set was identical and the latitude spread was **12.04 m, against 12.98 m** for the portable
`vk162-steady-state`. Indistinguishable. The dynamic model changes the navigation filter, not what
reaches the driver.

**The capture was deliberately not committed.** It would have been the only file in the corpus with
nothing to say for itself, and a near-duplicate costs replay time in CI for no coverage. Where a
capture earns its place by holding something no other holds, this one held nothing — so the
measurement above is the artefact, and the bytes can be re-taken in twelve minutes if anyone doubts
it.

---

## 16. The front-panel lamps (#440, #462)

> **Rewritten 29 Sep 2026 (#556) for #462's two lamps**, shipped 11 Sep. The panel has two lamps the
> host may drive, and they now mean different things. **Enabled is the application's**: lit while it
> holds the link, flashed around every command. **Active is the receiver's**: lit while it is locked
> to GPS, written only when that changes. The Settings switch is *Front-panel lamps*, and Diagnostics
> has a toggle for each lamp.

**Why.** This is the one feature in the application whose entire output is a light on a piece of
metal. Nothing in CI can see it, no UI Automation assertion can see it, and the receiver's own
`:LED:ACT?` read-back **cannot see it either** — it reports the register the write landed in, not the
lamp. Everything that shipped in v1.0.14 was verified through that register. **Nobody has yet
watched the panel while the application drove it.**

Two claims therefore stand unverified, and both are one look away:

- That `:LED:ACTive` drives the lamp **labelled Active** and not some other indicator. (Enabled
  was answered by eye on 9 Sep 2026 — see the end of this section.)
- That what a person sees is what #462 designed: with the switch on, **Enabled** lit while connected
  and flickering with each command, **Active** lit while locked, and both put back as they were
  found on disconnect.

**You need to be in front of the unit.** Everything below is two minutes.

### The manual control (Diagnostics)

1. Note which lamps are lit on the front panel before starting. Write it down; the whole feature is
   about putting the panel back as it was found.
2. Details → Diagnostics → **Front panel** → toggle **Active lamp** on. (The card's **Enabled lamp**
   toggle does the same for the other lamp; repeat steps 2–4 with it.)
   **It takes about a second to answer** — the receiver services this node on its own 1 Hz tick
   (§10.9), so a pause is correct behaviour, not a hang.
3. **Look at the panel.** The *Active* lamp should be lit and nothing else should have changed.
4. Toggle it off. The lamp goes out.

### The per-session behaviour (Settings)

5. Settings → Advanced → **Front-panel lamps** → *Used*. **Enabled** should light **immediately**,
   not at the next connect, and flicker with each command — readings arrive far less often while it
   is on, which is expected (§10.9). **Active** should be lit if the receiver is locked.
6. Disconnect. Both lamps return to whatever step 1 recorded.
7. Now the case the design turns on: **set a lamp on by hand** (step 2), then connect and disconnect
   with the switch still on. That lamp must still be **on** at the end — the application borrows
   the lamps and gives back what it found, so a user's *on* survives a session. If it comes back
   off, the borrowing logic is wrong and that is the defect to file.
8. Turn the setting off (*Left alone*). Both lamps return to what they were.

### Kill the application while the lamp is lit

9. With the lamp lit by the application, end the process from Task Manager.
10. The lamp **stays lit**. This is expected and documented: there is no wire left to restore over.
    Confirm the escape hatch works — reconnect and use the Diagnostics toggle to put it out.

### `:LED:ENABled` drives the lamp labelled *Enabled* — answered 9 Sep 2026

**No longer an open question.** `:LED:ENAB 1` lights the front-panel lamp labelled **Enabled** and
`:LED:ENAB 0` puts it out, watched by eye over two runs on the bench Z3805A and recorded on video.
The other four lamp registers — `:LED:ACT?`, `:LED:ALAR?`, `:LED:GPSL?`, `:LED:HOLD?` — were
sampled every two seconds throughout both runs and **never moved**, so *Enabled* is a genuinely
separate indicator and not a second name for *Active*.

**The guide had it all along**, which is the part worth feeling slightly foolish about:
`z3801.pdf`, *Front Panel at a Glance*, item 2 — *"User-definable indicators labeled Enabled and
Active. These can be turned on through the RS-422 port."* Table 4-2 gives `:LED:ENABled` as
*"Sets or queries Enabled LED"*, word for word what it gives `:LED:ACTive`. **The panel carries six
indicators and exactly two of them belong to the host software**; Power is hardwired and Alarm, GPS
Lock and Holdover are the receiver's own, query-only.

So "connected" and "talking" are separable after all — but **the cost that killed the per-sweep idea
is unchanged**, a write still costing about a second against ~30 ms queries, so a second lamp buys
semantics rather than activity. What to do with it was a design call, **#462**, and it was made on
11 Sep 2026: Enabled became the application's lamp and Active follows lock (see the note at the top
of this section).

**Keep the do-nothing baseline in any repeat of this.** The unit had been powered up minutes
earlier, so "a lamp changed on its own while acquiring" was a live alternative explanation, and the
first run could not exclude it. Five samples with the node untouched, all identical, is what does.

> **`:LED:ALARm:USER` is not implemented on this receiver, so the Alarm lamp cannot be exercised.**
> Both spellings answer `-113,"Undefined header"` in query form and `-108,"Parameter not allowed"`
> in set form, with the ALARM register never moving and the queue cleared before each candidate so
> the error belongs to that command alone. §8's reasoning for not cataloguing it is untouched by
> this and remains the real argument; the node simply is not there to catalogue.

> **Every `:LED:` write costs about a second**, first byte to prompt, because the receiver services
> the node on its 1 Hz tick. That is measured, not estimated (#440), and it is why the lamp is lit
> once per session rather than flashed. Budget for it; do not read a one-second pause as a fault.

---

## 17. GPS − UTC from a broadcast, not a query (#481)

**Why.** Every other reading on the Time page is something the application *asked* for. This one, on
a UCCM, is not: that family answers none of §10.14's `:PTIM:LEAP` queries and states the accumulated
offset in the binary time code it broadcasts about every two seconds. So the reading depends on a
whole chain — the transport recognising a 44-byte frame in the byte stream, keeping it out of the
line reader, keeping it out of the *drain*, handing it to the driver, and the page preferring the
query where there is one — and **no part of that chain can be exercised by a receiver that does not
broadcast.** A simulator can stand in for the shapes; only hardware produces the timing.

**The timing is the whole difficulty, and it is why this is a procedure.** A broadcast lands while
the link is idle, because a transaction lasts tens of milliseconds and the broadcasts are seconds
apart. On 12 Sep 2026, 115 transactions in 40 seconds carried **not one** frame while the readings
looked fine — the frames were being discarded with the stale input, which stopped the corruption
they had been causing and so looked exactly like the fix working. **A pass here is the value
appearing, never the absence of a symptom.**

### Procedure

1. Connect to a UCCM. Details → **Time**.
2. **On the first visit**, `GPS − UTC` must show a number, not `—`. The first visit is the test: the
   page reads the queries once on navigation, and the first full-tier status can arrive after that,
   so a value that only appears on a second visit is a failure of this check even though the number
   is right.
3. The number must be the true offset — **+18 s** at the time of writing, and it changes only when
   IERS announces a leap second. A plausible-looking wrong number is the failure worth looking for:
   a misread byte gives an integer, not an error.
4. **No error line beneath it.** "The receiver did not answer the leap-second queries" is true of
   this family and must not be printed beside a figure the receiver broadcast unasked.
5. Leave it for a minute. The value must not flicker to `—` and back: a full-tier status that
   arrives before any frame has been seen would do that, and it is the shape of a regression in
   which frames are gathered only sometimes.

### Verify against a receiver that does *not* broadcast

Connect a **SmartClock** and open the same page. `GPS − UTC` must still come from
`:PTIM:LEAP:ACC?` — the query is the authority wherever it answers, and the broadcast only fills in.
Nothing about this family's behaviour may have changed, and a regression here looks like a correct
number arriving from the wrong place, which no reading of the screen can distinguish.

### What this cannot check, and still has not been

- **A leap second actually announced.** The pending flag comes from the time code's own bit and has
  only ever been seen clear. It cannot be forced, and the receiver decides when to set it; until one
  is announced the pill's caution state is unexercised on this family.
- **The offset across a power cycle**, and in **holdover** — both need §11.1's states, which are
  section 5's business and #416's.
- **Whether the offset is right**, independently. The host clock is not a reference unless its time
  service is running; on the bench machine it is stopped, so a comparison there can bound the answer
  and not settle it. The receiver's own statement is the better source, which is the point of
  reading it at all.

**Last run:** 12 Sep 2026 against `TRIMBLE,57964-80,40896646,V2.0.1.6-01` on COM3 at 57600-8-N-1 —
`+18 s` on the first visit, no error line, pill reading *None announced*, stable over a minute.
18 of 115 transactions carried a frame, spaced two seconds apart, which is every one the receiver
sent.

## 18. The tray icon survives an Explorer restart (#549)

> **Automated** by `build/qa/Invoke-QaPass.ps1` (`app-checks`): Explorer is killed in the VM, and the pass requires the log line below and the app still running.

**Why.** When Explorer restarts, every notification icon goes with it, and the shell broadcasts
`TaskbarCreated` so that each application can add its icon again. Until #549 the icon's window was
message-only, **and a message-only window receives no broadcasts**, so the icon stayed gone until
the application was restarted too. With close-to-tray on by default (§10.3.1) the icon is often the
only sign the application is running, so it looked as if it had exited while it went on polling and
holding the port. `TrayIconWindowTests` proves the window now hears a broadcast; only a real
Explorer restart proves the icon comes back.

| | |
|---|---|
| **Do** | Launch the app and close its window, so it is running in the notification area. Note the icon's shape. Task Manager → *Windows Explorer* → *Restart*. Wait for the taskbar to return. |
| **Pass** | The icon is back within a few seconds, in the same shape and with the same tooltip, without the app being touched. Clicking it opens the window, and right-clicking it shows *Open*, *Keep above other windows* and *Exit* (the middle one since #568). `app.log` has the line *Explorer restarted; adding the tray icon again.* and no warning about the tray. |

**The icon may come back in the overflow** even if it had been dragged onto the taskbar. That is
Windows' decision about a re-added icon and not a failure of this check.

**Last run:** 29 Sep 2026, against the packaged Debug build of `main` at `25358fa`, registered as a
development package. Explorer was restarted at 10:38:54.9. The application logged the re-add line at
10:38:56.1, the shell then reported its icon present (`Shell_NotifyIconGetRect` on the tray window's
handle), the log held no tray warning, and clicking and right-click → *Open* both opened the window.
**One deviation:** the window was open rather than closed, because this was the first launch after a
fresh install. The icon's window does not depend on the main window, so this does not weaken the
result, but the next run should follow the procedure as written.

Earlier the same day, before a development build could be registered, the production `TrayIcon`
class was run in a scratch harness instead. Two harnesses ran side by side through one Explorer
restart. The one built from the old message-only window lost its icon and never got it back in the
44 seconds that followed. The one built from #549's window had its icon back 1.4 s after the new
Explorer process appeared.

## 19. A hidden window reopens on a display that is still there (#553)

**Why.** The main window can be hidden to the notification area for weeks, and the display it was
on can be unplugged, undocked or taken by a remote session in the meantime. Until #553 the window
was checked against the attached displays only at launch, so reopening it from the tray put it
back where the lost display had been, entirely off screen. `WindowPlacementPolicyTests` cover the
arithmetic. Only a real second display shows whether the window actually comes back.

| | |
|---|---|
| **Do** | With a second display attached, drag the main window onto it and close the window to the notification area. Unplug or disable that display. Click the tray icon. Repeat, reopening with a second launch from Start instead of the tray. |
| **Pass** | Both times the window appears on the remaining display, centred if none of it was left there, and `app.log` has *The window was not on a display; moved from …*. With the display still attached, reopening leaves the window exactly where it was and logs nothing. |

**Windows may move a hidden window itself** when a display is removed. If it does, the window comes
back on screen with no log line, which still passes. The log line only says the application had to
do it.

**Last run:** not yet with a second display. On 29 Sep 2026 the single-display 5120 × 1440 bench
stood in for one: the hidden window was moved with `SetWindowPos` to where a lost display would have
been, then reopened. From x = 6000 via the tray and from x = −4000 via a second launch, it came back
centred at 1760,246. From x = 4500, hanging off the right, it was pulled back to 3520,300. An
ordinary close and reopen left it at 400,300, untouched, with nothing logged. Before the fix, the
same steps reopened it at 6000,300.

## 20. Starting at sign-in (#548)

**Why.** A start at sign-in is Windows launching the package's startup task, and nothing but a real
sign-in produces one: the activation kind that tells the application it was started this way cannot
be faked from a test or a script. The tests cover the choice shown for every task state, which
setting a launch follows, and the retry arithmetic. They cannot cover the launch itself.

| | |
|---|---|
| **Do** | In Settings, set *Start when I sign in to Windows* to **In the notification area**. Sign out and back in. Then set it to **With the window open**, and sign out and in again. Then set it to **Off**, and sign out and in once more. |
| **Pass** | First sign-in: no window appears, the icon is in the notification area, the receiver is polled, and `app.log` has *Started by Windows at sign-in; hidden: True*. Second: the window opens by itself. Third: nothing starts. Opening the application from Start after the first sign-in brings the window forward rather than starting a second copy. |

| | |
|---|---|
| **Do** | With it on, unplug the receiver's USB adapter, or switch the receiver off. Sign out and in. Wait a minute, then plug it in or switch it on. |
| **Pass** | `app.log` has *… did not answer; trying again every 30 s until it does* once, not every 30 s, and within about 30 s of the receiver coming back, *Connected to … after N attempts since sign-in*. The icon reads *Disconnected* until then. Opening the connection dialog while it is still retrying stops the retrying, and the dialog can connect. |

| | |
|---|---|
| **Do** | Turn it off in Task Manager → *Startup apps*, then open Settings in the application. |
| **Pass** | The control shows **Off**, cannot be changed, and says to turn it on in Windows Settings. Turning it back on there and reopening the page shows it on again. |

**Last run:** 29 Sep 2026, the Settings half only, against the packaged Debug build driven by UI
Automation. Every choice reached Windows: the task's recorded state went 0 → 2 → 2 → 0 → 2, and the
stored *hidden* preference followed. Enabling asked for no consent. With the task set to disabled by
the user, as Task Manager records it, the page showed *Off*, disabled, with the note, and the log
read *DisabledByUser*. **No real sign-in has been done yet**, so the first two tables are still owed.

## 21. The history survives a reinstall (#551)

**Why.** The history lives in the package's data, and removing the package deletes it; export and
import are the only way across. `HistoryFileTests` cover the round trip, the merge rules and every
refusal against real files, but not the pickers, the confirmation as a person reads it, or an
uninstall. An uninstall is the point, and it is also the one step here that destroys data if the
export did not work, so **check the exported file before uninstalling**.

| | |
|---|---|
| **Do** | With a receiver connected and some hours of history, Settings → *Export history…*, and save it. Open the file in any SQLite tool, or import it straight back: the confirmation must name this receiver. Cancel. Then uninstall, reinstall, reconnect the same receiver, and *Import history…* the file. |
| **Pass** | The status line says how many readings were exported, over which dates. The confirmation names the receiver connected now and warns about nothing. After the import the Overview trend at 7 d shows the history from before the uninstall, joined to what the reinstalled application has recorded since. Importing the same file again reports 0 added, the rest already here. |

| | |
|---|---|
| **Do** | Import a file exported while a *different* receiver was connected, or while none was. |
| **Pass** | The confirmation names both receivers, or says one is unknown, and says the histories can't be separated afterwards. For a **different** receiver, **Cancel** is the default button; when either side is unknown, Import is. Cancelling changes nothing. |

**Last run:** 29 Sep 2026, against the packaged Debug build connected to the Z3805A on COM3, without
an uninstall. The export was 1,875 readings in one file (`journal_mode` delete, no companions). It
passed SQLite's integrity check, had all six columns, and its manifest named
`SYMMETRICOM,Z3805A,3625A02931,1.01.03-A`. Importing it straight back through the pickers (Tony)
showed the confirmation naming this receiver, and the log read *0 samples added, 1875 already present;
receiver Same*. **The uninstall-and-restore half and the different-receiver table are still owed.**

**Automated since 3 Oct 2026** (`history-reinstall`), both tables, on QA-Win10 and QA-Win11 against the published v1.3.4. The history is a minute of readings rather than hours, which changes the counts and not the path. What it leaves out is a person reading the confirmation: it checks the sentences, not how they read.

## 22. The window can be pinned from compact mode (#568)

**Why.** The footer's pin was the only way to keep the main window on top, and compact mode collapses
the footer. So the one layout meant for a corner of the screen could not be pinned or unpinned, and a
pinned compact window looked exactly like an unpinned one. There are now four routes to one setting,
and a pushpin in the title bar. What no test can reach is whether each route really changes what
Windows does with the window, and whether the pushpin leaves the title bar and the compact layout
exactly as they were. The first version put it in the title bar control's header slot, which
thickened the bar by 16 px and clipped the compact medallion.

| | |
|---|---|
| **Do** | In the standard layout, unpinned, enter compact mode. Press `Ctrl+Shift+T`. Put another window over the corner where it sits. Then right-click the compact window and read the menu. Choose *Keep this window above others* to unpin. Right-click the notification-area icon, read its menu, and choose *Keep above other windows*. Restart the application. |
| **Pass** | After `Ctrl+Shift+T`, the other window cannot cover it and a pushpin shows at the right of its title bar, just before the minimise button, and hovering over it opens a tooltip naming the key. The title bar stays its normal height, and the compact medallion is whole, not clipped at the bottom. Dragging the title bar still moves the window. The right-click menu shows *Keep this window above others* ticked and *Compact mode* ticked, each with its key. Choosing it unpins: the pushpin goes and the other window can cover it. The tray menu shows *Keep above other windows* unticked between *Open* and *Exit*, and choosing it pins again. After the restart the window comes back compact, pinned, and carrying the pushpin. In the standard layout, the footer pin shows the same state as the other three routes. |

**Last run:** 29 Sep 2026, against the packaged Debug build with the Z3805A on COM3. The following all passed:
- `Ctrl+Shift+T` set and cleared the window's topmost flag in both layouts.
- The title bar measured 32 px, with the pushpin 8 px from the minimise button.
- The compact medallion measured 64 × 63.
- The tooltip opened above the bar.
- A drag on the title moved the window.
- The right-click menu showed both items with the right ticks.

**The tray menu and the tooltips were then confirmed by hand (Tony), on the published v1.3.0, recorded in #573.** The tray menu is a shell popup the automation could not read.

**Automated since 3 Oct 2026** (`pin-compact`). UI Automation still cannot see that popup menu, but Win32 can: the scenario opens it with the icon's own callback message, posted to the app's tray window, reads its items and ticks from the menu, and clicks the item. What it leaves out is only the shell routing a right-click to the icon.

**Hover testing is only real with real input.** `SetCursorPos` moves the pointer without a pointer event, so no tooltip opens, even on the globe button. Only `SendInput` or `mouse_event` movement proves a tooltip.

## 23. A window with nothing to restore opens at the whole layout (#578, #581)

**Why.** With no stored placement, which is every first launch and every launch after a stored
display has gone, the main window used to open at the size Windows gives any new window. On the
5120 × 1440 bench that was 3840 × 1023, around a layout 380 wide. It now opens at a size the page
calculates from its own rows: wide enough for the clock line and the status line with the controls
beside each, at the widest text either can show, and tall enough for every row (#580). The
calculation measures text, so it depends on the zone, the language and the text scale, and only
the running application can show it came out right. The display's scaling is not known
until the content loads, so the size is applied twice: once against 100 % and again at the real
scaling. Only a real display at a scaling other than 100 % shows whether the second one lands.

| | |
|---|---|
| **Do** | Exit the application from its tray menu. Delete `window.json` from `%LOCALAPPDATA%\Packages\<package family>\LocalCache\Local\WinZ3805A\`. Start the application. Repeat at 150 % display scaling. |
| **Pass** | Both times the window opens in the standard layout, with the readout row, the figures of merit, the clock line and the footer all showing. The whole clock line shows, with the badge and the globe button on screen to its right and vertically centred on the text. The whole status line shows beside Details, the pin and Connect. Nothing is clipped at the bottom, and the window is no taller than it needs to be. |

| | |
|---|---|
| **Do** | With a receiver connected, drag the window's left edge in until it stops at its minimum width. Then drag the bottom edge up slowly until the footer disappears, and back down. |
| **Pass** | At the minimum width the clock line wraps onto two lines, breaking before the date and never inside it, and the badge and globe button stay on screen beside it, centred on both lines (#581). The status line, where it no longer fits beside Details, the pin and Connect, ends in an ellipsis rather than being cut mid-character. Dragging up, the footer, readouts and clock line disappear together before the clock line is clipped; dragging down, they come back with the clock line whole. Right-click the clock line and copy it: the pasted text is one line with ordinary spaces. |

**Last run:** 30 Sep 2026, at 100 % and then 150 %, against the packaged Debug build with the
Z3805A on COM3 in Pacific time.
- **First launch:** it opened at 553 × 504, with 537 × 495 of content. The clock line (336 px), badge and globe button were all on screen, the text centred on the controls, and the status line whole beside the three buttons.
- **Minimum width, 380 of content:** at 495 to 498 of height the short layout showed; from 499 the full layout, with the clock at 244 × 36 on two lines (`09:18:54 Pacific Daylight Time ·` then `30 Sep 2026`), and both controls on screen at y 686–718 against the text's 684–720. The copy was not tried. Tony confirmed the wrap, and then the status line's ellipsis, by eye.
- **150 %, minimum width** (144 DPI, Tony at the desktop): 380 × 501 of content. The clock wrapped to 244 × 36 effective, breaking before the date. The badge and globe button were on screen, their centre at y 687 in line with the text's. The status line ended `1.3.0.0 · COM3 · 9…` before the three buttons, each 32 effective high.
- **150 %, first launch** (`window.json` renamed away): 828 × 755 physical, **537 × 495 effective**, the same content size as at 100 %. So the second pass, at the real scaling, landed; the first alone would have left about 358 wide. The clock was on one line and whole, and so was the status line (`… · updating…`) beside the buttons.

Both 150 % runs used the old 1.3.0 package registration, which points at the same build folder as the 1.3.1 one, so the code under test was the same; only the version in the status line differs.

## 24. Cancel stops a connection attempt (#585, #607)

**Why.** A ContentDialog's buttons can't be clicked from a headless test, and the defect lived in
exactly that gap. The view model's cancellation was tested and correct; the dialog never delivered
the click. While the Connect click held its deferral, the dialog ignored Cancel and Esc, so an
auto-detect walk on a silent port ran its full ninety seconds. Once Cancel worked, it left the
session reading *Connecting* with the port held (#607); that half is now a test.

| | |
|---|---|
| **Do** | Leave the USB adapter plugged in but **disconnect the serial cable from the receiver**, so the port exists and nothing answers. Open the connection dialog, choose *Auto-detect settings*, press **Connect**, and about ten seconds in press **Cancel**. Repeat, pressing **Esc** instead. Then press Cancel with nothing running. Reconnect the cable and press **Connect** once more. |
| **Pass** | Cancel stops the walk within a second or two. `app.log` has *Cancel pressed while connecting* and then *is now Disconnected. Cancelled.*, with no more `*CLS` lines after it. The dialog stays open with the port picker and Connect usable, and no error message. The main window reads *Disconnected*, not *Connecting*. Esc does the same. Cancel with nothing running closes the dialog. With the cable back, Connect finds the receiver, **the dialog closes itself**, and the main window shows it. |

**Last run:** 30 Sep 2026, packaged Debug build, Z3805A on COM3.
- **Cancel:** pressed at 17:54:18.955; the log reached *Disconnected. Cancelled.* at 17:54:19.007, and the main window read *Disconnected*.
- **Esc:** handled in 0.2 s.
- **Cancel while idle:** closed the dialog.
- **Success:** with the cable reconnected, Connect settled on 9600-8-N-1 in about 2 s, the dialog closed itself, and the main window showed *Locked to GPS*.
- **Before the fix,** a mouse click on Cancel nine seconds in, and Esc later, reached no handler, and the walk ran on.

## 25. A second launch brings the window forward (#46, #627)

> **Automated** by `build/qa/Invoke-QaPass.ps1` (`app-checks`): Notepad is put in front, *WinZ3805A* is typed into the Start menu with real keystrokes (a launch from a script would not carry the foreground right this checks), and the pass requires WinZ3805A in front, one copy running, and the log line below.

**Why.** One instance runs at a time (#46): a second launch hands its activation to the running
one and exits, and the running one brings its window back. Reopening a window hidden in the
notification area always worked, because showing a hidden window takes the foreground. A window
that was open but **covered** stayed behind on both Windows 10 and Windows 11 until #627, and it
survived because nothing checked that case: section 19 reopens a hidden window. Which window is in
front is only knowable on screen.

| | |
|---|---|
| **Do** | With the main window open, cover it with another application's window, then click **WinZ3805A** in the Start menu. Then close the window to the notification area and click it in the Start menu again. |
| **Pass** | Both times the window comes to the front, and no second copy starts. `app.log` has *Brought to the front: took the foreground.* for the covered window and *Brought to the front: it already was.* for the hidden one. *…the taskbar button flashes* means Windows refused the foreground: record it as a failure, because the window did not come forward. |

**Last run:** 1 Oct 2026, the build with #629.
- **Windows 10 22H2 VM, dry-run release build:** both pass, with the two log lines as above.
- **Windows 11, Tony's development machine, Debug build:** the covered window passes (*took the foreground*).
- **Before the fix,** the covered window stayed behind on both.

## Before a release

**First the automated pass**, on the release's dry-run build, before the tag:
`build/qa/Invoke-QaPass.ps1 -Online <zip> -Offline <zip>`, with its report posted to the QA-run
issue. It must have no FAIL and no ERROR before the tag, and since 5 Oct 2026 no photograph awaiting a verdict: every photograph that differs from the last accepted pass is judged from its triptych and the verdict recorded with `build/qa/Complete-QaRun.ps1` (see `build/qa/README.md`). Then run it again on the published zips
(`-Release <tag>`). Of the sections below, it covers 8, 11, 18, 25 and most of 12.

Then, by hand: sections 1–4, 9 and 13 in full, then **the rest of 12 on the published artifact** — which means the release
exists before the last check passes. That is the right way round: a release nobody can install is
worth catching after it is published rather than not at all, and the fix is another tag. Section 5
only if the hardware is being moved, with sections 7 and 10 alongside it since they need the same
antenna; **section 16 the first time anyone is in front of the receiver**, since the lamp shipped in
v1.0.14 verified only through a register read and the two minutes it costs are the only way anyone
will ever know it drives the right light; section 6 if survey behaviour has been touched; **section 14 if anything on the polling,
rendering or shell-badge path has changed** — it costs an hour of waiting and perhaps five minutes of
attention, and both leaks that have shipped would have been caught by it; **section 17 whenever a
broadcasting receiver is to hand and anything on the transport's byte path has changed** — it costs
one glance at one readout, and the failure it catches is a value quietly not arriving, which no
other check in this list would notice; **section 18 if the tray icon's code has changed** — it
costs half a minute; **section 22 if the pin, the tray menu or the main window's title bar has
changed**; **section 19 if window placement has changed and a second display is to hand**; **section 23 if
window placement or the main window's layout heights have changed**; **section 24 if the
connection dialog or the session's connect paths have changed**; **section 25 if single-instance
redirection or how the main window is shown or activated has changed**; **section 20 if
anything on the launch or connect-on-launch path has changed**, since only a real sign-in reaches it;
**section 21 whenever `trend.db`'s schema or the history export has changed**, since an export that
a later version cannot import loses the history of everyone who relied on it. Section 15 is not a release
gate at all: run it when a talker is to hand, because the captures it produces are permanent and the
opportunity is not.

Open a QA-run issue for the release and record each section's result there — the issues this
checklist cites track defects rather than runs, so there is no standing place otherwise — and if
something fails, file it
rather than fixing it silently: the log of what was checked is worth as much as the checking.
