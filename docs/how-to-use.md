# How to use

This is for the person with the application installed and a receiver on a serial port. It says
what each window shows, what every control does, which key does what, and how to get the window
back when it has gone. It does not explain the receiver itself — the manuals do that — and it does
not explain how to build or change the application; the README and the specification do.

Every screenshot here was taken from the running application in the Light theme at 100 % display
scaling, with a Z3805A connected, so the numbers are real ones.

---

## The main window

![The main window: a large circular medallion with a green tick and a ring of small green marks, "Locked to GPS" beside it, then a satellites readout of 6, a 1 PPS TI readout of 1.5 ns, TFOM 3 and FFOM 0 pills, the receiver's time and date, and a footer with the port, "updated 2 seconds ago", Details, a pin and Disconnect](images/how-to-use/main-window.png)

The main window is meant to be left open — on a second monitor, in a corner, for weeks. Everything
on it answers one of two questions: *what state is the receiver in*, and *how well is it doing*.

### The medallion

![A 160 px circle with a green tick in the centre and a dotted green ring, with "Locked to GPS" to its right](images/how-to-use/main-medallion-and-mode.png)

The circle is the one thing to look at. Its **colour**, its **centre symbol** and the **words
beside it** all say the same thing, so you never have to read the colour alone:

| Words beside it | Colour | Centre | What it means |
|---|---|---|---|
| **Locked to GPS** | green | tick | Disciplining the oscillator to GPS. The normal state. |
| **Recovering** | amber | refresh arrows | Coming back from holdover; not yet locked. |
| **Waiting to recover** | amber | pause | Ready to recover but held back — the Holdover page says why. |
| **Holdover** | red | warning | Lost GPS and running on the oscillator alone. Time error grows the longer this lasts. |
| **Power-up** | grey | clock | Just switched on; nothing to report yet. |
| **Diagnostic / off** | grey | power symbol | The receiver is in a diagnostic mode or its outputs are off. |
| **Disconnected** | grey | disconnected drive | The application is not talking to a receiver. |

The **ring** around the centre is the last sixty seconds of the receiver's 1 PPS time interval —
how far its pulse is from GPS, one mark per second. A smooth ring is a calm receiver; a ring that
grows teeth is one whose loop is hunting, and you will see that before any of the numbers change.
It is a shape, not a chart: the actual figure is the 1 PPS TI readout below.

Under Windows high contrast the ring is a plain circle and the centre symbol and words carry the
state; in the compact layout (below) the ring is uniform and the centre shows the satellite count.

### The readouts

![Two readouts side by side: "satellites 6" and "1 PPS TI 1.3 ns"](images/how-to-use/main-readouts.png)

- **satellites** — how many satellites the receiver is tracking right now. If the receiver is locked
  but tracking **none**, an amber **coasting** pill appears beside it: the receiver is coasting on a
  1 PPS it can no longer verify. That combination almost always means an antenna or bias-tee
  fault, and it is the single most useful thing this window can tell you.
- **1 PPS TI** — the time interval between the receiver's pulse and GPS, in nanoseconds. A few
  nanoseconds either side of zero is normal for a locked Z3805A.

A value the receiver has not reported, or reported in a form the application could not read, is
shown as **—**. An old value is never blanked; the footer says how old it is.

### The figures of merit

![Two pills: a green dot with "TFOM 3" and a green dot with "FFOM 0"](images/how-to-use/main-figures-of-merit.png)

**TFOM** is the receiver's own estimate of its time error, on a scale where lower is better —
TFOM 3 means the error is between 100 ns and 1 µs. **FFOM** is the state of the frequency loop;
FFOM 0 means the PLL has stabilised. The Overview page spells both out in words next to the
number. The pills use colour, a shape and text together: green circle for good, amber triangle for
caution, red hexagon for critical.

### The clock line

![The receiver's time, "15:54:43 Pacific Daylight Time · 29 Aug 2026", followed by a small circled-i badge and a globe button](images/how-to-use/main-clock-line.png)

The time and date the receiver is reporting, shown in the time zone you have chosen (see the globe
button below). Three things can sit after it:

- **The ⓘ badge** appears when the receiver's own calendar has wrapped. GPS counts weeks in a
  field that rolls over about every 19.6 years, and a receiver of this age reports a date roughly
  two decades behind — 2007 for 2026. The application corrects the date and shows the corrected
  one; hover the badge to see the raw date the receiver actually reported.
- **A caution badge** appears while the time is still the receiver's power-up default, not yet
  corrected from GPS — it may be wrong by any amount until the first satellite is tracked. Hover
  it to read exactly that.
- **The globe button** opens the time-zone flyout:

  ![A flyout titled "Time zone" with a toggle "Use this computer's time zone" switched on, a time-zone picker, and the note that this changes what is displayed only](images/how-to-use/main-time-zone-flyout.png)

  Leave *Use this computer's time zone* on to see the receiver's time in your local zone, or turn
  it off and pick a zone. This changes what is **displayed** only — the receiver keeps its own
  time-zone setting.

### The footer

![The footer: "1.0.6.0 · COM3 · 9600-8-N-1 · updated just now" on the left; Details, a pin button and a blue Disconnect button on the right](images/how-to-use/main-footer.png)

- **The version** comes first, and it is the version of WinZ3805A you have installed — the one
  to quote in a bug report. It is read from the package itself, so it is never out of step with
  what is running; the number in the picture above is whichever version that screenshot was
  taken from, and yours will differ.
- **The status text** then names the port and serial settings in use and how long ago the last
  reading arrived — *updated just now*, *updated 47 seconds ago* — so an old value is never
  mistaken for a current one.
- **A pill appears beside it when a reading is late**, and it says which of the two it is:
  **overdue** after 15 seconds without one, **stale** after 60. Overdue means the receiver has not
  answered when it should have, which happens briefly and often comes back on its own; stale means
  the reading is old enough that you should not act on it. The pill carries an amber caution
  triangle or a red critical hexagon as well as the word, so it reads the same if the colours do
  not. **There is no pill while readings are fresh** — a window meant to sit on a second monitor
  for weeks should not carry a badge that never goes out, so silence here is the good state.
- **Details** opens the Receiver Details window (`Ctrl+D`).
- **The pin** keeps this window above every other window. It stays pinned across restarts.
- **Connect / Disconnect** opens the connection dialog, or drops the connection (`Ctrl+Shift+C`).

### Compact mode

![A small window, 396 by 152 pixels: a 64 px circle with a uniform dotted green ring and a large green 6 in the centre, and "Locked to GPS" beside it](images/how-to-use/main-compact.png)

Compact mode is for a corner of the screen: the medallion shrinks, the satellite count moves into
its centre, and everything else goes. **The window itself shrinks to its compact size when you
enter compact mode, and comes back to the size it had when you leave.** Enter and leave it by
**double-clicking the medallion** or pressing **`Ctrl+Shift+M`**; **`Esc`** also leaves it. The
application remembers that you were in compact mode and reopens that way.

If you make the standard window very short without entering compact mode, the readouts, figures of
merit, clock line and footer are removed rather than squashed, and the satellite count moves into
the medallion's centre there too — the count is always on the screen somewhere.

---

## Connecting

![The Connect to receiver dialog: a Port picker with Refresh, Auto-detect settings selected over Manual, Reconnect automatically and Connect to this device on launch switched on, and Connect and Cancel buttons](images/how-to-use/connection-dialog.png)

The first time the application runs it shows a **Connect your receiver** panel with one button,
**Choose a port**. Afterwards the dialog is reached from the main window's **Connect** button, from
`Ctrl+Shift+C`, or from the **status pill** in the Details window's title bar.

- **Port** lists the serial ports Windows can see; **Refresh** re-scans after plugging an adapter
  in. If no ports appear at all, the adapter's driver is the likely cause — check it in Device
  Manager.
- **Auto-detect settings** tries the likely baud rates and framings — listening first for a
  receiver that talks by itself, then asking for an identity — until one is recognised. Use it
  unless you know the settings. **Manual** exposes baud, data bits, parity and stop bits — the
  Z3805A's factory setting is 9600-8-N-1; a Z3801A leaves the factory at 19200-7-O-1, though
  some units in the field are set to even parity.
- **Reconnect automatically** keeps trying after the link drops — the adapter unplugged, the
  receiver power-cycled — with a growing pause between attempts.
- **Connect to this device on launch** does what it says; with it on, the application connects
  itself every time it starts.

Handshaking is always off. The application asserts DTR and RTS when it opens the port, which some
adapters need.

### Other receivers

The application also connects to any GPS receiver that speaks **NMEA 0183** — a u-blox module, a
marine receiver — through the same dialog: auto-detect hears it talking and chooses the NMEA driver
by itself. Such a receiver has no disciplined oscillator, so the application shows only what NMEA
carries: the mode (**Locked to GPS** with a fix, **Power-up** without one), the satellites being
tracked and the sky plot, the position, and the time and date. The 1 PPS TI, TFOM and FFOM, the
outputs, holdover, EFC, cable delay, status registers, self test, receiver log and error queue have
nothing behind them and show **—**, and the Advanced Console offers reads only; nothing is ever
sent to the receiver.

**Anything the receiver cannot do is greyed out with a line saying why** — *"This receiver does not
support an elevation mask. The NMEA 0183 driver has no command for it."* The controls are not
hidden: a page that quietly lost half of itself would look either broken or, worse, complete, and a
button that is visible and silently does nothing is the one thing that must not happen. So the
pages stay as they are, with dashes where a reading would be and a reason where a command would be.

---

## Receiver Details

Open it with the **Details** button or `Ctrl+D`. It is one window; opening it again brings the
existing one forward.

### The title bar

![The Details title bar: a green "Connected · COM3" pill in the centre, and Refresh, Export, Settings and Help icon buttons at the right](images/how-to-use/details-title-bar.png)

- **The status pill** says whether the receiver is connected and on which port. Clicking it opens
  the connection dialog.
- **Refresh full status** (`F5`) asks the receiver for its complete status now rather than waiting
  for the next scheduled read.
- **Export current view** (`Ctrl+E`) saves what the current page is showing as a file. It is
  disabled on pages that have nothing exportable.
- **Settings** (`Ctrl+,`) goes to the Settings page.
- **Help** (`F1`) opens this guide in its own window. `F1` does the same from the main window.

### The pages

![The navigation pane: Overview, Satellites, Position, Timing, Holdover, Time, Status Registers, Diagnostics, with Settings and Advanced Console at the bottom](images/how-to-use/details-nav.png)

The eight pages in the main list are reachable with `Ctrl+1` to `Ctrl+8` in the order shown;
Settings and the Advanced Console sit in the footer and have no number. In a narrower window the
pane collapses to a rail of icons, and the button at its top opens it.

Every page's cards **flow into as many columns as the width allows** — one in a narrow window, two
or three when you widen it, each card going to whichever column is currently shortest. Widening the
window is therefore worth doing on the busier pages; nothing is hidden at any width.

#### Overview — `Ctrl+1`

![The Overview page: a Synchronization card with a 96 px medallion, Outputs valid, TFOM and FFOM pills with their meanings, and 1 PPS TI; a Holdover uncertainty card; a Health monitor card with six green pills and a Run test button; and an Oscillator control (EFC) card with the current value and a trend](images/how-to-use/page-overview.png)

Everything from the main window, with the words behind the numbers:

- **Synchronization** — the medallion and mode, whether the receiver's **outputs are valid**, TFOM
  and FFOM each with what the value means, and the 1 PPS TI relative to GPS.
- **Holdover uncertainty** — the receiver's own prediction of how far its time would drift in 24
  hours of holdover, the threshold that prediction is compared against, and how long it has been in
  holdover if it is. The threshold is **not** the point at which the receiver enters holdover;
  the Holdover page explains what it is and what the separate, settable duration limit does.
- **Health monitor** — one pill per subsystem the receiver checks (self test, internal power,
  oven power, the oscillator, its control voltage, the GPS receiver), and **Run test** to run the
  receiver's self-test now — it asks first, because the receiver stops doing other things while it
  tests itself. A failing subsystem turns its pill red; the summary line above says whether
  everything passes.
- **Oscillator control (EFC)** — the voltage the receiver is applying to steer its oscillator, as a
  percentage of its range, with a trend over the last **1 h**, **6 h**, **24 h** or **7 d**. A
  slowly drifting EFC is the oscillator ageing; a sudden change is worth a look.

#### Satellites — `Ctrl+2`

![The Satellites page: "Tracking 6 · not tracking 4", a sky plot with satellites as filled dots at their positions, a Plot / List toggle and Save image, and below it a Tracked table with PRN, elevation, azimuth and signal strength](images/how-to-use/page-satellites.png)

- **The sky plot** is the sky as the antenna sees it, north up, the horizon at the rim and straight
  up at the centre. Each dot is a satellite at its position; its fill shows signal strength, and a
  dashed circle marks the **elevation mask** — satellites below it are not used. Arrow keys move
  between satellites, `Home` and `End` jump to the first and last, and `Enter` or `Space` selects
  one, which highlights it in the table too. **List** swaps
  the plot for a plain table; **Save image** writes the plot to a file.

  ![The sky plot alone: a circle with north at the top, a dashed ring marking the elevation mask, teal dots for the satellites being tracked, stronger signals drawn as larger and bolder dots, hollow rings for those not being tracked, and a legend reading tracked, predicted, acquiring, below mask, ignored, elevation mask](images/how-to-use/satellites-sky-plot.png)

- **Tracked** and **Not tracked** tables list every satellite the receiver knows about with its PRN,
  elevation, azimuth and, for tracked ones, carrier-to-noise ratio — 35 and above is good.

  ![The Tracked table: one row per satellite with its PRN, elevation, azimuth and a bar showing carrier-to-noise ratio](images/how-to-use/satellites-tracked-table.png)

![The lower half of the Satellites page: the Not tracked table, an Elevation mask card with a New mask (degrees) box, a slider across the 0-90 range beneath it and Apply mask, and a Which satellites card with a Manage… button](images/how-to-use/page-satellites-2.png)

- **Elevation mask** — satellites below this elevation are ignored. The box **opens on the mask the
  receiver is using**, not on a default, so you can see what it is set to before changing it. Type a
  new angle or drag the **slider** beneath — they are two ways to move one value, not two values —
  and press **Apply mask**. It asks for confirmation first, because a mask that is too high can
  leave the receiver with too few satellites. Once you have typed, the number is yours: the
  once-a-second sweep will not overwrite it mid-edit.
- **Which satellites** — **Manage…** opens a dialog that reads the receiver's inclusion and
  exclusion lists and offers the commands that change them: track only the selected satellites,
  track all, clear exclusions, track none, exclude all. Each is confirmed before it is sent and
  nothing is staged, so closing the dialog sends nothing. Tracking none, or excluding them all,
  drives the receiver into holdover — the dialog says so.

#### Position — `Ctrl+3`

![The Position page: Latitude, Longitude and Height readouts with a Copy button, and a Survey card with Start survey, Adopt computed position, Cancel survey and Survey on power-up](images/how-to-use/page-position.png)

- **Latitude, longitude, height** — the antenna position the receiver is using for its timing
  solution. **Copy** puts it on the clipboard.
- **Survey** — a timing receiver needs to know exactly where its antenna is, and it can work that
  out by averaging fixes for about two hours. **Start survey** begins that; **Adopt computed
  position** stops early and takes the average so far; **Cancel survey** goes back to the last held
  position; **Survey on power-up** makes the receiver survey every time it is switched on. All of
  these are confirmed before they are sent.

![The lower half of the Position page: a Set position manually card with Degrees, Minutes and Seconds boxes for latitude and longitude, a Height (metres) box, Fill from the receiver and Apply position](images/how-to-use/page-position-2.png)

- **Set position manually** — if you know the antenna position better than a survey would, enter it
  here. **Fill from the receiver** copies the current position into the boxes to edit; **Apply
  position** sends it, after confirmation, and cancels any survey in progress. A wrong position
  degrades timing, which is why the confirmation says so.

#### Timing & antenna — `Ctrl+4`

![The Timing & antenna page: an Antenna cable delay card showing the delay in use now, with Enter delay directly and Calculate from cable options and an Apply delay button](images/how-to-use/page-timing.png)

- **Antenna cable delay** — the receiver corrects its 1 PPS for the time the signal spends in the
  antenna cable, and it needs to be told how long that is. Either **enter the delay directly** in
  nanoseconds, or **calculate it from the cable**: pick a **named cable** type, or give a **custom
  velocity factor**, and the **cable length** in metres. **Apply delay** is confirmed first — the
  receiver may drop into holdover for a moment while it re-establishes lock at the new delay, and
  the *Before applying* note says so.

![The lower half of the Timing page: a 1 PPS time interval trend with 1 h, 6 h, 24 h and 7 d ranges, an Oscillator control (EFC) trend, an Oscillator drift figure, and a Stability (Allan deviation) card with averaging time τ, σy(τ) and the number of differences averaged](images/how-to-use/page-timing-2.png)

- **1 PPS time interval** and **Oscillator control (EFC)** — the same two quantities the main
  window and the Overview show, as trends over the range you choose.

  ![The 1 PPS time interval trend: a mark for each slice of the window, on an axis running from +50 ns at the top through 0 ns in the middle to −50 ns at the bottom, drawn in orange above the zero line and teal below it](images/how-to-use/timing-ti-trend.png)

  Each mark spans everything the receiver reported in that slice of time, so a tall one is a
  moment that moved. **Orange is above the zero line and teal below it**, and the further from
  zero, the bolder the mark; a mark that crosses zero is grey, because within that slice the
  interval was on both sides of it.
- **Oscillator drift** — how the EFC is trending, which is the oscillator's ageing rate.
- **Stability (Allan deviation)** — the standard measure of an oscillator's short-term stability,
  computed from the recorded 1 PPS readings for a range of averaging times τ. The **differences
  averaged** column matters: a σy(τ) computed from a handful of differences is a rough number, and
  one from thousands is a firm one.

#### Holdover — `Ctrl+5`

![The Holdover page: Predicted 24 h uncertainty, Present time error, Duration and Waiting reason readouts, and a Thresholds card with a read-only Uncertainty threshold, whether it is currently exceeded, and an editable Holdover duration limit in seconds with an Apply duration limit button](images/how-to-use/page-holdover.png)

Holdover is what the receiver does when it loses GPS: it keeps its oscillator running on the
corrections it had learned, and its time error grows from there.

- **Predicted 24 h uncertainty**, **present time error**, **duration** and — while it is *waiting to
  recover* — the **waiting reason** the receiver gives.
- **Thresholds** — there are two here, they measure different things, and **only the second can be
  changed**. The page says so beside each; this is the short version.

  - **Uncertainty threshold** is what the *predicted 24 h uncertainty* above is compared against.
    *Currently exceeded* means that if the receiver lost GPS now and ran on its own oscillator for a
    day, its time error would be expected to pass this figure. It is a **prediction about the
    oscillator, not a fault** — a receiver that has not been locked for long has not yet learned
    enough to promise better — and it is **read only**: the receiver's command set has exactly one
    settable threshold and this is not it.
  - **Holdover duration limit** is that one. It is how long the receiver may stay in holdover before
    it *raises a flag* — and nothing else: it does not end holdover, and it changes no output. The
    receiver simply starts reporting that holdover has run longer than you allowed, in the
    Questionable status register, which is what the state above reads. Enter a new value in seconds
    and **Apply duration limit**, after confirmation. The factory value is 86 400 seconds, one day;
    the setting is stored in the receiver and survives a power cycle.

  The two are easy to confuse, and the confusion has a symptom worth naming: change the duration
  limit and the uncertainty reading above it will never move, because they are not the same
  quantity.

![The lower half of the Holdover page: a Manual control card with a Before forcing holdover note, the time since power-up, and Force holdover, Recover now and Ignore recovery limit buttons](images/how-to-use/page-holdover-2.png)

- **Manual control** — **Force holdover** puts the receiver into holdover deliberately, for testing
  or before pulling the antenna; **Recover now** asks it to leave holdover; **Ignore recovery limit**
  overrides the check that keeps it waiting. Each is confirmed, and the *Before forcing holdover*
  note is worth reading once: a receiver that has not been powered up for long has not finished
  learning its oscillator, and forcing holdover then proves little.

  The **time since power-up** beside it carries a verdict, and one of its values needs explaining
  because it is the one you will usually see. **Unverified** does not mean anything is wrong: the
  application can only bound the power-up time *from below*, because it knows when **it** started
  watching and not when the **receiver** was switched on — which is why the figure reads *at least*.
  A receiver that has been running for a week looks exactly like one switched on a minute ago. It
  becomes a definite answer once the application has watched 24 hours pass, or you can confirm the
  uptime yourself and disregard it.

#### Time — `Ctrl+6`

![The Time page: a Receiver clock card with the time, a Show times in picker, the time scale, the date reported by the receiver, the week rollover correction and the power-up time](images/how-to-use/page-time.png)

- **Receiver clock** — the receiver's time in the zone you pick with **Show times in**, the **time
  scale** it is keeping (UTC, GPS time, or local time derived from either), the date it
  **reported** as against the corrected one, the
  **week rollover correction** the application is applying, and when the receiver was **powered
  up**.

![The lower half of the Time page: a Leap second card with GPS − UTC and Announced for, and a Time code output card with the format](images/how-to-use/page-time-2.png)

- **Leap second** — **GPS − UTC** is how far GPS time has run ahead of UTC since 1980 (whole seconds;
  GPS does not take leap seconds). **Announced for** shows a pending leap second, which is a step
  the 1 PPS will take and which anything downstream that counts seconds needs to expect.
- **Time code output** — the receiver sends a message about half a second before each 1 PPS naming
  the time that pulse will carry. The two **formats** say the same thing in different notations,
  so whatever decodes it has to be told which one the receiver is set to.

#### Status registers — `Ctrl+7`

![The Status registers page: a Register picker and Refresh, then a table of bits with Cond, Event, Enab, PTr and NTr columns and a Meaning column, and Discard changes and Apply mask changes buttons](images/how-to-use/page-status-registers.png)

The receiver's SCPI status registers, bit by bit, with what each bit means. The **Cond**ition and
**Event** columns are what the receiver reports; **Enab**le and the positive and negative
**tr**ansition columns are the masks you can change. Registers are **read on demand** — use
**Refresh** — and mask edits are staged until **Apply mask changes** (confirmed) or **Discard
changes**. This page is for finding out why a summary bit is set; most people never need it.

#### Diagnostics — `Ctrl+8`

![The Diagnostics page: a Refresh button at the top, then a Self test card with a Subsystem picker, a Run all tests button and one row per subsystem, and the start of the receiver's log with a Filter box and Export and Clear log buttons](images/how-to-use/page-diagnostics.png)

- **Self test** — pick one **subsystem** or all of them and press the button, which names what it
  is about to do (**Run all tests**, or **Test** followed by the subsystem) because the receiver
  drops what it is doing while it tests itself. Testing one subsystem fills in that subsystem's row;
  **Run all tests** returns a result for every subsystem and fills in all of them from the one
  sweep, against a shared timestamp. **Refresh** at the top of the page re-reads the last self-test
  result and the receiver's log.

  An `ALL` sweep is not free: on a Z3805A it takes about eleven seconds, and the receiver leaves
  GPS lock while it runs — expect a couple of minutes back to lock and a few more before the time
  figure of merit recovers. The confirmation says so before it runs.
- **The receiver's log** — the entries the receiver itself keeps, filterable, with **Export** to save
  what is shown (a filter applies to the file too) and **Clear log**, which removes the entries
  from the receiver after asking — export first if you want them. The list **scrolls inside its own
  card** rather than stretching the page, so a receiver with hundreds of entries does not bury
  everything below it. Timestamps are on the receiver's own time scale and are subject to the week
  rollover.

![The lower half of the Diagnostics page: the end of the Lifetime card, an Error queue card with Read errors, and an Undocumented queries card listing six queries each with a Run button](images/how-to-use/page-diagnostics-2.png)

- **Application log** — what the application saw: the port opening, the settings auto-detect settled
  on, every connection change, and the receiver's mode and satellite count whenever they move.
  **Show log folder** opens it in Explorer. This is the place to look for a fault that comes and
  goes while nobody is watching the window.
- **Status screen parsing** — whether the last full status screen was understood completely. A
  field the application could not read becomes an em dash rather than a guess, and this card is
  where it says so, naming the line it could not parse. It is the first place to look if a reading
  is dashed when it should not be — usually a firmware revision printing something slightly
  different.
- **Lifetime** — **power-on hours**, how long the receiver has run in total. An oven-controlled
  oscillator ages with running time, so this is the figure behind the drift the Timing page reports.
- **Error queue** — **Read errors** reads the receiver's error queue. Reading it empties it: each
  read removes the entry it returns, so what is shown is what was read.
- **Undocumented queries** — present only when *Undocumented read-only queries* is on in Settings.
  Six read-only queries that exist in the receiver's firmware but not in its manual, each run only
  when you click **Run**. They may return errors or nonsense; none of them changes a setting, and
  nothing can be typed here.

#### Settings

![The top of the Settings page: an Advanced section with the Advanced Console and Undocumented read-only queries switches, each with a paragraph explaining it, and an Appearance section with the Windows accent colour switch](images/how-to-use/page-settings.png)

Every setting has its explanation next to it on the page; this is the short version.

| Setting | Off | On |
|---|---|---|
| ![Advanced Console switch, Shown](images/how-to-use/settings-consoleswitch.png) **Advanced Console** | The console page is not shown. | Adds the **Advanced Console** page below Settings — see below. It changes what is *reachable*, never what is *permitted*. |
| ![Undocumented read-only queries switch, Hidden](images/how-to-use/settings-experimentalswitch.png) **Undocumented read-only queries** | The Diagnostics page shows no undocumented queries. | The six read-only queries appear on the Diagnostics page. Nothing can be typed, and no setting can be changed through them. |
| ![Use the Windows accent colour switch, This app's own](images/how-to-use/settings-systemaccentswitch.png) **Use the Windows accent colour** | Selected items, buttons and links use the application's own teal. | They follow the accent you chose in Windows. The colours that mean caution and critical never change, so if your Windows accent is close to one of them you will be told. |
| ![Tell me when the receiver loses GPS lock switch, On](images/how-to-use/settings-locknotificationsswitch.png) **Tell me when the receiver loses GPS lock** | No notifications. | A Windows notification when the receiver stops disciplining to GPS, and another when it starts again. A loss has to last a minute before it is reported, so a brief drop-out is never mentioned. |
| ![Keep running when I close the window switch, On](images/how-to-use/settings-keeprunningswitch.png) **Keep running when I close the window** | Closing the window exits, and monitoring stops with it. | Closing the window hides it and the receiver keeps being polled — see *When the window has gone*. This is the default. |
| ![Start in the notification area switch, Off](images/how-to-use/settings-startminimisedswitch.png) **Start in the notification area** | The window opens at launch. | The application starts with no window, monitoring from the notification area. |

![The bottom of the Settings page: the Alerts and Running in the background sections, an Exit button, and an Elsewhere for now note](images/how-to-use/page-settings-2.png)

![The Exit button](images/how-to-use/settings-exitbutton.png) **Exit** quits the application
outright — the exit that needs no notification-area icon. Poll cadences, the display time zone
and units are not on this page; the time zone is chosen from the main window's clock line, and the
poll cadences are fixed.

#### Advanced Console

![The Advanced console page: a Filter box, a Command picker, a "Will send" line, a Send button, and a Transcript with Show poll traffic, Clear and Export](images/how-to-use/page-advanced-console.png)

Shown when *Advanced Console* is on in Settings. It is a **picker, not a terminal**: choose a
command from the list (the **filter** narrows it by mnemonic or name), see exactly what **will be
sent**, and press **Send**. Commands that change something ask for confirmation with the
consequence spelled out, exactly as the pages do. The **transcript** shows everything sent and
received, the last 500 exchanges; **Show poll traffic** includes the application's own scheduled
reads, which arrive every second and would otherwise swamp what you sent; **Clear** empties the
transcript and **Export** saves it.

Nothing can be typed as a command, and the commands this application will not send — the ones that
can damage the receiver's calibration or firmware — are not in the list. They are not hidden
behind a warning; they are absent.

---

## When the window has gone

By default, **closing the main window does not exit the application**. It hides, the receiver
keeps being polled, the trend keeps filling, and lock notifications keep arriving. The first time
this happens a dialog says so — *Still running and still polling* — with **Keep running** and
**Exit now**, and it is not shown again.

What remains is an icon in the notification area — and **Windows 11 does not show a new icon on the
taskbar**. It goes into the hidden overflow behind the **`^`** chevron at the right of the taskbar
until you move it, and an application is not allowed to move it for you. So:

1. **Launch the application again.** It is single-instance: a second launch brings the existing
   window back rather than starting another copy. This needs no icon at all, and it is the route
   to reach for first.
2. **Or click the `^` chevron** (*Show hidden icons*) at the right of the taskbar; the icon is in
   that flyout. Click it to bring the window back, or right-click it for **Open** and **Exit**.
3. **To keep the icon visible**, drag it from the flyout onto the taskbar, or go to *Settings ›
   Personalization › Taskbar › Other system tray icons* and turn it on there.

To exit, right-click the icon and choose **Exit**, or use the **Exit** button on the Settings
page, which depends on nothing in the notification area. There is no confirmation: polling is not
a transaction and the trend is saved as it goes, so there is nothing to lose by stopping.

Two settings control all of this — *Keep running when I close the window* and *Start in the
notification area* — both on the Settings page.

**Lock notifications** are ordinary Windows notifications. If you have them on and none appear,
check *Settings › System › Notifications* for the application, and that Do Not Disturb is off; a
notification that was delivered while the banner was missed is still in the notification centre
(`Win+N`).

---

## Keyboard shortcuts

The main window and the Details window register different keys; the key works in the window that
has focus.

| Where | Key | Does |
|---|---|---|
| Main window | `Ctrl+D` | Open Receiver Details, or bring it forward |
| Main window | `Ctrl+Shift+C` | Connect, or disconnect if connected |
| Main window | `Ctrl+Shift+M` | Enter or leave compact mode (double-clicking the medallion does the same) |
| Main window | `Esc` | Leave compact mode |
| Details | `Ctrl+1` … `Ctrl+8` | Overview, Satellites, Position, Timing & antenna, Holdover, Time, Status registers, Diagnostics |
| Details | `F5` | Refresh full status now |
| Details | `Ctrl+E` | Export the current view |
| Details | `Ctrl+,` | Settings |
| Anywhere | `F1` | Open this guide |
| Anywhere | `Esc` | Cancel a dialog, close a flyout |
| Anywhere | `Tab`, `Shift+Tab`, arrows | Move between controls; on the sky plot, arrows move between satellites, `Home` and `End` jump to the ends, and `Enter` or `Space` selects |

In the Details window, every title-bar button's tooltip shows its shortcut. There is no `Ctrl+9`: Settings and the
Advanced Console live in the pane's footer and are reached with `Ctrl+,` and by clicking.

---

## Where things are kept

The application stores everything under its own folder in your profile —
`%LOCALAPPDATA%\Packages\<package family name>\LocalCache\Local\WinZ3805A\`, where the family
name is `WinZ3805A_` followed by a hash of the publisher — which is what *Show log folder* on the
Diagnostics page opens, so you never need to find it by hand. In it: the application log
(`logs\app.log`, rolled at 1 MB with four older files kept), the recorded trend (`trend.db`), the
remembered connection, and the window positions and settings. Uninstalling removes all of it; to
keep the trend across a reinstall,
uninstall from PowerShell with `Get-AppxPackage WinZ3805A | Remove-AppxPackage
-PreserveApplicationData`.

The application collects nothing and sends nothing anywhere. It has no network code at all;
everything it knows, it learned from the serial port.

**Further reading.** What each figure the receiver reports means is in the receiver's own user
and programming guides; how the application reads them, field by field, is §11 of the
specification in the repository.
