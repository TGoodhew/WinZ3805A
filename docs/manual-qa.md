# Manual QA checklist

The checks that need a person, a receiver, or a machine setting — everything the eleven CI gates and
1700-odd unit tests cannot reach.

Two places in `requirements.md` name this document and, until 28 Aug 2026, it did not exist:

- **§6.4 item 4** — *"Add an integration test to the manual QA checklist: unplug the adapter
  mid-transaction and confirm the app reports Disconnected without crashing."*
- **§9.12** — *"A11Y-3 and A11Y-4 run in CI. The rest are a release checklist item."*

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

Log location: `%LOCALAPPDATA%\Packages\WinZ3805A_1z32rh13vfry6\LocalCache\Local\WinZ3805A\logs\app.log`

---

## 3. Display scaling (A11Y-7, #27)

**Why.** Not automatable. Programmatic scaling reports success, changes nothing, and leaves registry
state behind. It needs a person at **Settings → Display → Scale**.

| | |
|---|---|
| **Do** | Set 100 %, 150 %, 200 %, 250 %, 350 % in turn. Restart the app at each. |
| **Pass** | No clipping, no overlap, no text cut off. The title-bar drag region still works — grab the bar and move the window. Caption buttons stay reachable. |

Restore the original scaling afterwards and **read it back to confirm**.

---

## 4. Accessibility, the eleven not in CI (§9.12, P0-16)

A11Y-3 (icon-only controls) and A11Y-4 (contrast floors) gate CI. The rest are here. See #16 for the
full text of each; this is the operator's summary.

- **Keyboard only.** Unplug the mouse. Reach every command, every page, and every dialog. Tab order
  follows reading order; focus is always visible.
- **Screen reader.** With Narrator running, confirm the medallion announces its state, that a tier C
  outcome is spoken, and that a lost connection is announced assertively rather than politely.
- **High contrast.** Switch the desktop into a contrast theme. Every reading stays legible; no
  foreground is painted in the surface behind it.
  > Windows 11 renamed these. "High Contrast White" is **Desert**. `.theme` files silently no-op —
  > use the Settings UI. See #218 for what this found the first time it was actually done.
- **Text scaling.** Settings → Accessibility → Text size to 200 %. Nothing clips; dialogs scroll
  rather than truncate.
- **Colour.** Confirm no state is carried by hue alone — severity is colour **and** shape **and**
  text everywhere it appears.

---

## 5. Receiver states that need the hardware moved (#185, #4)

Only run when the receiver is being moved anyway. The capture harness collects these unattended:

```
winapp run src\WinZ3805A\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64 --detach   # stop this first
pwsh build\Capture-Fixtures.ps1
```

It needs COM3 to itself, so **stop the application first**. It writes only states it has not seen,
seeding what it already has from disk, so leaving it running across a whole session is safe.

| State | How to reach it |
|---|---|
| Power-up, acquiring | Power-cycle the receiver. |
| Holdover | **Pull the antenna lead** with the receiver running. Wait several minutes — the elapsed-time and present-uncertainty fields only become meaningful with time on the clock. |
| Recovery | Plug the antenna back in. It passes through recovery on the way to lock, so this state exists only in that window. |
| Survey in progress | Power-cycle with survey-on-power-up enabled. The receiver refuses a survey command while holding a position (#229), so the power cycle is the route. |
| Health-monitor failure | Cannot be induced. The harness names it `-health-fail` and will capture it if one ever happens. |

---

## 6. Survey operations (P0-12)

Each needs a survey actually running, which needs a power cycle.

- **Cancel** sends `:GPS:POSition LAST` and restores the previously held position — so it costs
  minutes rather than the two hours a full survey takes, and leaves the receiver where it started.
- **Adopt** ends the survey and takes the estimate *as it stands*. Adopting early leaves a poor
  position; the manual entry form is the way back if you do.
- Both take **about ten seconds** to answer. That is normal — the receiver tears down the
  accumulation before replying — and is why they have their own 30 s timeout class (#256).

---

## Before a release

Sections 1–4 in full. Section 5 only if the hardware is being moved; section 6 if survey behaviour
has been touched. Record the result on the relevant issue, and if something fails, file it rather
than fixing it silently — the log of what was checked is worth as much as the checking.
