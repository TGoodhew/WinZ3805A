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

**How to use it.** Nothing here is automated and nothing here should be. Each entry says what to do,
what to look for, and what it is protecting — the last one matters, because a check whose purpose is
forgotten gets performed carelessly or dropped.

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

**Why.** Not automatable. Programmatic scaling reports success, changes nothing, and leaves registry
state behind. It needs a person at **Settings → Display → Scale**.

| | |
|---|---|
| **Do** | Set 100 %, 150 %, 200 %, 225 % in turn. Restart the app at each. |
| **Pass** | No clipping, no overlap, no text cut off. The title-bar drag region still works — grab the bar and move the window. Caption buttons stay reachable. |

Restore the original scaling afterwards and **read it back to confirm**.

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
- **A11Y-4 Contrast, where the gate cannot read.** Accessibility Insights colour-contrast pass under
  a contrast theme (its colours are the user's own `SystemColor*`), and over Mica, where the backdrop
  is a live blur of the wallpaper.
- **A11Y-5 Target size.** Accessibility Insights target-size check. The sky-plot markers will flag;
  §9.10.2 is the answer to that flag.
- **A11Y-6 Text scaling.** Settings → Accessibility → Text size at 100, 150 and 200 %, at each of
  §9.6.1's breakpoints — Minimal (below 640), Compact (640–1023) and Medium (1024 and up). Nothing
  clips; dialogs scroll rather than truncate.
- **A11Y-7 Display scaling** is section 3.
- **A11Y-8 High contrast.** Switch the desktop into each of the four contrast themes. Every reading
  stays legible; no foreground is painted in the surface behind it; the medallion and the severity
  shapes are distinguishable.
  > Windows 11 renamed these. "High Contrast White" is **Desert**. `.theme` files silently no-op —
  > use the Settings UI. See #218 for what this found the first time it was actually done.
- **A11Y-9 Announcements.** With Narrator running, force a mode change, a connection change and a
  tier C outcome. Each is spoken; a lost connection assertively rather than politely.
- **A11Y-10 Automation peers.** Accessibility Insights tree: the medallion exposes its state as a
  sentence, and the sky plot exposes every marker.
- **A11Y-11 List alternate.** On the Satellites page, **List** shows the same satellites with the
  same data as the plot.
- **A11Y-12 Colour.** A greyscale screenshot of every page and state (P0-19): no state is carried by
  hue alone — severity is colour **and** shape **and** text everywhere it appears. The chart-series
  gate covers the eight series and nothing else.
- **A11Y-13 Animations off.** Settings → Accessibility → Visual effects → Animation effects off.
  Nothing animates, and no layout differs from the animated path.

---

## 5. Receiver states that need the hardware moved (#185, #4)

Only run when the receiver is being moved anyway. The capture harness collects these unattended:

```
pwsh build\Capture-Fixtures.ps1 -SelfTest     # the half that needs no port; run it first
pwsh build\Capture-Fixtures.ps1 [-Port COM3]  # then leave it running; Ctrl+C when the receiver has settled
```

It needs the port to itself, so **exit the application first** — closing its window only hides it
and keeps the port open; exit from Settings → Advanced → Exit or the notification-area icon. The
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

**Why.** The notification path was rebuilt on 29 Aug 2026 after `AppNotificationManager` turned out
never to have registered on any machine, and nobody noticed for a fortnight because nothing tests
it. It rides on section 5's antenna pull.

| | |
|---|---|
| **Do** | With *Tell me when the receiver loses GPS lock* on, pull the antenna and wait. Plug it back in. Then turn the switch off and repeat. |
| **Pass** | A Windows notification about a minute after the pull, not before; another when lock returns; none at all with the switch off. |

## 11. Help in the installed package (#312)

**Why.** The guide and its images are linked `Content` items copied into the package; whether the
*installed* application carries `Help\how-to-use.md` and its images is checkable only there.

| | |
|---|---|
| **Do** | In the sideloaded install, press `F1` from the main window and from Details. |
| **Pass** | The guide opens in its own window with every screenshot rendered, not the fallback text. |

---

## 12. The published release installs on a machine that has never had it

**Why.** Everything else here tests the application. This tests the *download* — and it is the only
check with a stranger at the other end of it. The failure modes are all invisible from a developer
machine, because a developer machine already has the runtime, already trusts the certificate, and
never sees the mark Windows puts on a downloaded file.

Do it on the artifact from the **release page**, not on `dist\` — downloading is half of what is
being tested.

| | |
|---|---|
| **Do** | On a machine with no Visual Studio and no Windows App SDK: download the zip from the release, right-click it → *Properties* → **Unblock**, extract, double-click `Install.cmd`. |
| **Watch** | The certificate thumbprint in the UAC/trust prompt. |
| **Pass** | The thumbprint matches the one in the release notes. One administrator prompt and no others. The app appears in Start and launches. |

| | |
|---|---|
| **Do** | Repeat *without* unblocking the zip first. |
| **Pass** | It fails, and the failure is readable — the window stays open and says something a
non-developer can act on. This is expected to fail; the check is that it fails *legibly*, because it is the most common way an install goes wrong and the mark is never mentioned by Windows' own error. |

| | |
|---|---|
| **Do** | `build\Uninstall-Sideload.ps1`, then `Get-AppxPackage -Name WinZ3805A` and `certlm.msc` → *Trusted People*. |
| **Pass** | Neither the package nor the certificate is left behind. |

> **When the publisher changes, this section is not optional.** `Identity/@Name` and
> `Identity/@Publisher` together form the package family name, so a build signed by a different
> publisher installs *alongside* the old one instead of upgrading it, and `Uninstall-Sideload.ps1`
> — which finds the certificate by the manifest's *current* publisher — will not remove the old
> certificate. It happened once, at v1.0.1, when the placeholder `CN=AppPublisher` was replaced.
> Uninstall the older build by hand first.

---

## 13. The guide's screenshots still show the application

**Why.** `docs\how-to-use.md` is also the F1 help, and its 17 page screenshots are the part of it
nothing can check. **A wrong screenshot is worse than a missing one**: drifted prose reads as prose,
but a picture is read as evidence, and a reader who sees a control in the guide that is not in the
application concludes they have the wrong version.

`Test-GuideCoverage.ps1` makes it impossible to ship an option nobody wrote about. It cannot look at
a picture.

| | |
|---|---|
| **Do** | Open each page the guide illustrates beside the guide, at the width the images were taken at, and compare. |
| **Pass** | Every control visible in the image is on the page, with the same label; nothing on the page that the surrounding text names is missing from the image. |

> **Re-taken 30 Aug 2026, and there is a script for it now.** `build\Capture-GuideImages.ps1`
> drives a running, connected application and photographs each page's content pane as an *element*,
> so no cropping arithmetic can be wrong about where the page is. Run it, then **look at every
> image**: it will photograph a page that failed to load just as willingly as one that worked, and
> two of the first run's images were of a page still reading from the receiver.
>
> **Its `-ContentWidth` is load-bearing.** #351 flows the cards into as many columns as fit, and the
> threshold is 864 px. The guide's prose and its "upper half" / "lower half" pairs are written
> around a single column, so the default of 860 is what keeps the pictures matching the words.

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

## Before a release

Sections 1–4, 8, 9, 11 and 13 in full, then **12 on the published artifact** — which means the release
exists before the last check passes. That is the right way round: a release nobody can install is
worth catching after it is published rather than not at all, and the fix is another tag. Section 5
only if the hardware is being moved, with sections 7 and 10 alongside it since they need the same
antenna; section 6 if survey behaviour has been touched; **section 14 if anything on the polling,
rendering or shell-badge path has changed** — it costs an hour of waiting and perhaps five minutes of
attention, and both leaks that have shipped would have been caught by it.

Open a QA-run issue for the release and record each section's result there — the issues this
checklist cites track defects rather than runs, so there is no standing place otherwise — and if
something fails, file it
rather than fixing it silently: the log of what was checked is worth as much as the checking.
