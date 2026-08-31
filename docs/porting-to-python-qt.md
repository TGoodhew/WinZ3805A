# Porting WinZ3805A to Python and Qt

**A work plan for a port of this application to Python 3 and PySide6, so that it runs on
Linux as well as Windows.**

This document is written to be handed to someone — a person or an agent — who has not
worked on this repository. It says what to build, in what order, what the existing code
already gives you for free, and which parts are not a translation but a rewrite. Where a
decision has been made, the reasoning is here so it can be disagreed with rather than
guessed at.

---

## 0. Status, and who this is for

**Cross-platform support is a non-goal of *this project*, not a prohibition on porting.**
§3 records the non-goal and three of the six goals in §2 name Windows explicitly — a WinUI 3
surface, Microsoft Store distribution, and an application that is "native to Windows"; §6.1
fixes the stack at WinUI 3 on .NET 10. That is a statement of what the maintainers of this
repository have taken on. It says nothing about what anyone else may build.

The code is MIT-licensed. **A port is welcome, and needs no permission.** What the non-goal
does mean is that a port has to be clear about which of two things it is, because the
engineering is identical and the ownership is not:

- **An independent port** — a fork, or a new repository that borrows this one's work. It
  needs no amendment to anything here, because it is not bound by this project's goals. The
  decisions collected in part 12 become the porter's own, and this document's job is to make
  sure none of them gets made by accident.
- **A port this project adopts** — the Python application shipped as a sibling of the
  Windows one, maintained here. Then §2, §3 and §6.1 do need amending first, since that is
  where the goals and the stack are written down, and `CLAUDE.md` requires a conflict with
  `requirements.md` to be raised rather than resolved quietly.

Everything else in this document applies unchanged either way. Only who signs off on part 12
differs.

Read [`requirements.md`](requirements.md) before writing any code, on either route. It is
the best description that exists of the receiver's behaviour, the safety model and the
design intent, and almost all of it is platform-neutral in substance even where it is
Windows-specific in wording. **An independent port should carry it across and annotate where
it diverges** rather than starting from a blank document — the parts that are wrong for
Linux are named in part 7, and the rest is hard-won and still true. The port changes the
rendering technology; it does not get to change what the application means.

Two things a fork inherits whatever route it takes. §8's safety model is not a stylistic
preference — the exclusions in §8.4 exist because the commands behind them can brick a
receiver, and the allowlist architecture in §8.1 is what makes them unreachable rather than
merely discouraged. And the ten captured status screens are irreplaceable hardware output.
Both are discussed below; neither is negotiable in a port that expects to be used on real
equipment.

---

## 1. Why this is tractable

The unusual thing about this codebase is that the boundary a port needs was already drawn,
for other reasons, and is enforced by the build. `WinZ3805A.Device` may not reference
`Microsoft.UI.*`; the parser is unit-tested headlessly against captured status screens; a
large amount of view-model and service logic is compiled into the test project by explicit
`<Compile Include>` links precisely *because* it does not touch the UI.

Measured on the tree as it stands:

| Layer | Files | Lines | What the port does with it |
|---|---:|---:|---|
| `src/WinZ3805A.Device` | 43 | 8,173 | Translate. No UI references by rule. |
| App C# with no UI or Win32 reference | 90 | 17,373 | Translate. |
| App C# touching WinUI, WinRT or Win32 | 48 | 14,326 | Rewrite. |
| XAML | 26 | 7,134 | Rewrite. |
| `tests/WinZ3805A.Tests` | 100 | 23,888 | Translate. 1,317 test cases, **zero** references to `Microsoft.UI`. |
| `build/palette/*.py` | 6 | 562 | **Already Python. Use as-is.** |

Of those 90 UI-free app files, **86 are already listed in
[`WinZ3805A.Tests.csproj`](../tests/WinZ3805A.Tests/WinZ3805A.Tests.csproj)** as linked
compilation units. That list is the single most useful artefact in this repository for a
porter: it is a machine-checked statement of which logic is free of the UI, and it includes
every view model, `DeviceSessionService`, `PollingService`, `TrendStore` and `HelpDocument`.

So roughly 25,500 lines of behaviour can be ported as a translation exercise with an
existing pass/fail oracle behind it, and roughly 21,500 lines of presentation have to be
rebuilt. Plan the effort as two projects with that shape, not as one uniform rewrite.

### The oracle

Ten captured status screens live in
[`tests/WinZ3805A.Tests/Fixtures/`](../tests/WinZ3805A.Tests/Fixtures/), with provenance
recorded in `captured/capture-log.md`. They cover power-up, GPS acquisition, locked,
stabilising, surveying, recovery and three depths of holdover. §11.1 requires that the
parser never throws and that unparseable fields become null; these files are how that is
checked. **They are hardware captures that cost a bench sitting to obtain — treat them as
irreplaceable.** The Python parser is finished when it agrees with the C# parser on all ten,
field for field, including the nulls.

---

## 2. The target stack

| Concern | Choice | Why |
|---|---|---|
| Language | **Python 3.12+** | `asyncio` maturity; `TaskGroup`; typing that `mypy --strict` can actually enforce. |
| UI toolkit | **PySide6** (Qt 6.7+) | See below. |
| Async integration | **`qasync`** | Runs the asyncio event loop on Qt's. The device layer is async top to bottom and must stay that way. |
| Serial I/O | **`pyserial` + `pyserial-asyncio`** | Replaces `System.IO.Ports`. `serial.tools.list_ports` replaces the whole registry crawl in `SerialPortEnumerator`. |
| Storage | **`sqlite3`** (stdlib) | Direct swap for `Microsoft.Data.Sqlite`; `TrendStore`'s schema ports unchanged. |
| Help rendering | **`markdown-it-py`** | Replaces Markdig. `HelpDocument` parses the guide into blocks that the UI lays out natively — keep that design, do not render HTML. |
| Types | **frozen dataclasses**, `typing.Optional` everywhere | Replaces C# records with `required` members. |
| Test runner | **pytest** | 1,317 cases to carry across. |
| Static analysis | **`mypy --strict`** and **`ruff`** | Not optional. See part 6.2. |
| Packaging | **Flatpak** (Linux), **PyInstaller/MSI** (Windows) | See part 10. |

### Why PySide6 and not something else

The application is a data-dense instrument panel with four hand-drawn custom controls, a
three-theme token system, and a written accessibility contract in §9.12. That combination
rules out most of the field:

- **PySide6 (chosen).** `QPainter` maps directly onto the existing custom controls, whose
  geometry is already separated into UI-free files. Qt exposes AT-SPI on Linux and
  UIA/MSAA on Windows, so §9.12's criteria remain expressible. `QSystemTrayIcon`,
  high-DPI scaling and per-widget stylesheets all exist. Licensed LGPL, which fits this
  repository's MIT licence.
- **PyQt6 — rejected on licensing.** Functionally equivalent, but GPL-or-commercial.
- **GTK4 / libadwaita via PyGObject — rejected.** Excellent on Linux, poor and
  awkwardly packaged on Windows. The port should not trade one single-platform toolkit
  for another.
- **Toga, Kivy, Dear PyGui, Flet — rejected.** None of them can meet §9.12. Kivy and
  Dear PyGui do not expose a platform accessibility tree at all, which makes A11Y a
  non-starter rather than a difficulty.
- **A web UI in a wrapper — rejected.** It would work, and it would discard the native
  accessibility, the native theming and the glanceable-window design that §9.1 and §10.3
  are built around.

### One consequence to accept up front

C# gives this project two guarantees the compiler enforces and Python cannot:
nullable-reference-types-as-errors, which is what makes §11.1's "the parser never throws"
survive contact with every consumer; and `TreatWarningsAsErrors`. `mypy --strict` with
`Optional` recovers most of the first and none of the second's breadth. **Budget for
`mypy --strict` in CI from the first commit, not later** — retrofitting it onto a finished
Python codebase is the same losing game as retrofitting a design-token layer onto finished
XAML, which is why §15 orders that one the way it does.

---

## 3. Proposed layout

```
winz3805a/
├── pyproject.toml
├── docs/                        requirements.md and the guide, carried across
├── src/winz3805a_device/        the port of src/WinZ3805A.Device — NO Qt IMPORTS, EVER
│   ├── transport/               serial.py, line_protocol.py, fake.py, broadcast.py
│   ├── commands/                catalog.py, blocked.py, scpi_command.py
│   ├── parsing/                 status_screen.py, diagnostic_log.py, scalars.py
│   ├── models/                  receiver_status.py, satellite.py, coordinates.py, …
│   └── drivers/                 base.py, smartclock.py, nmea/
├── src/winz3805a/               the application
│   ├── services/                session, polling, trend_store, preferences, logging
│   ├── viewmodels/              the 30-odd view models, still Qt-free where possible
│   ├── widgets/                 the custom controls (QPainter)
│   ├── views/                   the windows and pages
│   ├── themes/                  colours.py, typography.py, spacing.py, qss/
│   └── platform/                tray.py, notifications.py, badge.py — per-OS shims
├── tests/
│   ├── fixtures/                the ten captured screens, copied verbatim
│   └── …                        mirrors the C# test tree
├── tools/nmea_simulator/        port of tools/NmeaSimulator
└── build/                       the gates, reworked; palette/ copied unchanged
```

**The one boundary that must not be crossed:** `winz3805a_device` may not import from
`PySide6` or from `winz3805a`. This is the same rule the C# tree enforces through project
references, and it is what keeps the parser testable and the driver model honest. Enforce
it with a test that walks the package's imports — it costs twenty lines and it is the
reason the current port is possible at all.

Keep `viewmodels/` Qt-free too, wherever a view model does not need to be a `QObject`. The
86-file link list in the test project is the evidence that this is achievable; losing it
would make the next port impossible and the tests slow.

---

## 4. The phases

Each phase states what "done" means. Do not start a phase before its predecessor's
done-condition holds — §15 makes the same point about the original build, and for the same
reason.

### Phase 0 — Settle the route, and set up

Decide which of part 0's two routes this is, and if it is the second, amend §2, §3 and §6.1
before anything else. If it is the first, note in the new repository's README that it is an
independent port and which commit of this one it was taken from — a fork that cannot say
what it diverged from cannot pick up a later parser fix.

Then: `pyproject.toml`, `mypy --strict`, `ruff`, `pytest`,
CI running all three on every push. Copy `build/palette/` across unchanged and confirm
`validate.py` still passes — it checks the colour maths against published figures and is
the one piece of this repository that needs no porting at all.

**Done when:** an empty package passes `mypy --strict` in CI, and `python build/palette/validate.py` is green.

### Phase 1 — Models and the parser

Port `src/WinZ3805A.Device/Models/` (16 files) and `Parsing/` (4 files). This is the single
biggest translation unit: `StatusScreenParser.cs` alone is 1,375 lines.

Work fixture-first. For each of the ten captured screens, write the assertion before the
parsing code. §11.2 documents the model; §11.3 lists the advisory strings.

Two rules from §11.1 that are easy to lose in Python:

- **The parser never raises.** Not `ValueError`, not `KeyError`, not `AttributeError`. An
  unparseable field is `None` and the UI renders `—`. Wrap every scalar conversion; port
  `ScalarParsers.cs` first and route everything through it.
- **`None` is a real value, not an error.** Every consumer handles it. `mypy --strict` is
  what makes this checkable; without it this guarantee is a comment.

**Done when:** all ten fixtures parse to the same field values the C# tests assert, and a
fuzz test that feeds the parser truncated and corrupted screens raises nothing.

### Phase 2 — Transport and the line protocol

Port `Transport/` (11 files, ~1,900 lines). See part 6.1 below for the hard part.

`FakeTransport.cs` ports early and matters more than it looks: most transport tests run
against it rather than against a port. Note the lazy-initialisation defect its history
records — build the pipe once, in the constructor, not on first use from either side.

**Done when:** the protocol tests pass against the fake, including a status screen
delivered one byte at a time and a prompt split across a read boundary.

### Phase 3 — Commands, safety, drivers

Port `Commands/` and `Drivers/`. §8.1 requires the catalog to be an **allowlist**; §8.4's
exclusions are not entries with a flag, they do not exist as data. `BlockedCommands.py` is
the only file in the repository where those patterns appear, reached solely through an
`is_blocked(candidate) -> bool` predicate that cannot be enumerated or iterated.

Port both drivers. The NMEA driver is the cheaper one to prove because
[`tools/NmeaSimulator`](../tools/NmeaSimulator/) can be run against it — port the simulator
in this phase too, or shell out to the existing C# one.

**Done when:** the catalog tests pass, `is_blocked` rejects every §8.4 pattern, no test
fixture or docstring anywhere contains one, and both drivers parse their sample streams.

### Phase 4 — Session, polling, storage

Port `DeviceSessionService`, `PollingService`, `TrendStore`, `ReceiverStateStore`,
`FileLogWriter` and the preference stores. §7.3's tiered schedule, §7.4's rollover
compensation and the single-transaction-at-a-time discipline all live here.

Two constraints carried from `CLAUDE.md`:

- **Inject the clock.** No `datetime.now()` or `time.monotonic()` anywhere in the device
  package or these services. Take a `Clock` protocol and call it. The C# tree bans the
  equivalents at compile time through an analyser; reproduce that with a `ruff` rule or a
  small AST check, and test the check against a deliberate violation — a rule file that
  matches nothing is a rule that enforces nothing.
- **`DeviceSessionService` is per-device, never a singleton.** v1 creates one. §12 requires
  that this not be baked in.

**Done when:** a fake clock can drive a full poll schedule deterministically, and the trend
store round-trips and compacts. Note that the store's cadence follows the poll schedule and
not a wall clock — any analysis that assumes uniform sampling will be wrong by orders of
magnitude.

### Phase 5 — The design system

**Before any window is built.** §15 step 5 puts the token layer ahead of the pages, and
retrofitting it is where design systems die.

Port `Themes/Colors.xaml`, `Typography.xaml`, `Spacing.xaml` into a Python token module
plus generated QSS. Keep the current arrangement where the colour table is the single
source and code reads it rather than restating it — `ThemePalette.cs` does this today by
reading the embedded XAML, and the Python equivalent should read one data file.

Light and Dark port directly. **The third theme does not** — see part 7.

**Done when:** a swatch page renders every token in Light and Dark, the contrast gate
passes against the new surface colours, and no hex literal exists outside the token file.

### Phase 6 — Custom widgets

Port the four hand-drawn controls to `QPainter`. Their maths is already in separate,
tested, UI-free files — `SkyPlotGeometry`, `MedallionRingMath`, `TrendDecimation`,
`AllanDeviation`, `SignalStrengthScale`, `ColourDifference` — so port the maths in Phase 1
or 4 and let these files do nothing but draw.

| Control | Lines | Notes |
|---|---:|---|
| `SkyPlotControl` | 639 | Markers are **real focusable child objects**, not painted geometry, so the accessibility tree is correct by construction (§9.10.2). Reproduce that with `QAccessible` child items — do not paint dots and bolt on a hand-written peer. |
| `TrendChart` | 580 | Draws decimated columns, min-to-max per column. No charting library: §6.1 records that one was measured at 1.65 GB for this series length. Do not reintroduce one. |
| `StatusMedallion` | 509 | The §9.10.2 signature control. |
| `AllanDeviation` | 319 | Maths already separated and tested. |

**Done when:** each widget renders the fixture data correctly in both themes, and the sky
plot's satellites are reachable and named in an AT-SPI inspector.

### Phase 7 — Windows and pages

Rebuild the 18 views. §10.2 is the window inventory; §10.3 through §10.14 specify each
surface; §9.6.1 gives the breakpoints and §9.6.2 the minimum sizes.

Do the main window first and completely — it is the one a user leaves open for weeks and
the one G1 is measured against. Then the details window and its pages.

**Done when:** every page reaches parity with its section of the specification, and the state matrix in §9.11
is exercised in both themes.

### Phase 8 — Shell integration, then the guide

Tray icon, notifications, window placement, single-instance. All of it goes behind
`platform/` with a per-OS implementation and a no-op fallback, because these are the parts
that differ most between Linux desktops. See part 7.

Last, the guide. `docs/how-to-use.md` is both the repository's guide **and** the
application's F1 help, and every screenshot in it must be retaken. `Capture-GuideImages.ps1`
is the Windows script that took them; the Linux equivalent has to be written.

---

## 5. Translation reference

| C# / WinUI | Python / Qt | Notes |
|---|---|---|
| `record` with `required` members | `@dataclass(frozen=True, slots=True)` | |
| `TimeProvider` / `GetUtcNow()` | injected `Clock` protocol | Never the module-level call. |
| `PeriodicTimer` | `asyncio` loop with `await asyncio.sleep()` on an injected clock | Must be fake-able. |
| `Channel<T>` | `asyncio.Queue` | Single consumer for the transaction queue. |
| `PipeReader` / `SequenceReader<byte>` | a buffer class you write | See part 6.1. |
| `FrozenDictionary` | `types.MappingProxyType` over a dict | |
| `INotifyPropertyChanged` | Qt `Signal` | The view models raise everything together already, so one `changed` signal per view model matches the existing design better than per-property signals. |
| `DispatcherQueue.TryEnqueue` | `QMetaObject.invokeMethod` / queued signal | |
| `{ThemeResource}` | token lookup + `setStyleSheet` on theme change | Must re-resolve on theme change; the XAML rule against `StaticResource` has the same motive. |
| `AutomationProperties.Name` | `QWidget.setAccessibleName` | |
| `x:Load` / `Visibility` | `QWidget.setVisible` | |
| `NavigationView` | `QStackedWidget` + a list/sidebar | |
| `SettingsCard` (toolkit) | hand-built row widget | No Qt equivalent; ~80 lines. |
| `ContentDialog` | `QDialog` | |
| `RenderTargetBitmap` → PNG | `QWidget.grab()` → `QPixmap.save()` | For the plot export. |
| `SerialPort.GetPortNames` + registry | `serial.tools.list_ports.comports()` | Gives description and hwid on Linux, Windows and macOS. A clear simplification. |

---

## 6. The hard parts

### 6.1 The line protocol

`LineProtocol.cs` (697 lines) is the piece §15 puts first, and its docstring explains why in
detail. Three properties have to survive the port:

1. **Echo is detected, never assumed.** The manual's default echoes every character; the
   bench unit echoes nothing. Compare the first line received against the line transmitted.
   A session that assumes either way is broken on half the hardware.
2. **The terminator is a prompt, not a newline** — `scpi > ` or `E-nnn> `. This is what
   makes a setter (prompt only) and a 1,900-byte status screen the same shape of read. No
   `readline()`-based design can express it.
3. **The prompt straddles reads.** At 9600 baud the screen arrives in dozens of chunks and
   the sentinel lands across a boundary.

C# solves (3) with `System.IO.Pipelines`, distinguishing bytes *consumed* from bytes merely
*examined*. Python has no equivalent, and `asyncio.StreamReader.readuntil` takes a fixed
separator, which the prompt grammar is not.

**Write a small accumulating buffer.** Append each chunk; after each append, test only the
last ~32 bytes against the prompt grammar, because anything longer is an unfinished response
line and testing it only wastes the decode. Keep the whole buffer until the prompt is found,
then split. This is maybe 80 lines and it is the single highest-value thing to unit-test:
feed it a captured screen one byte at a time, and again in two chunks split at every offset
inside the prompt.

Note also that the prompt reports the state of the **error queue**, not the outcome of the
last command — an `E-nnn>` prompt means the queue is non-empty, which may be from an earlier
command. Do not attribute it to the transaction that just returned.

### 6.2 Losing the compiler

Stated in part 2 and repeated because it is the biggest single risk: the "parser never throws"
guarantee and the whole null-handling contract are currently held up by the C# compiler.

Mitigations, in order of value:

1. `mypy --strict` in CI from Phase 0. No `Any`, no untyped defs.
2. A fuzz test over the parser: every fixture, truncated at every offset, and with random
   bytes substituted. It must never raise.
3. `ruff` with the banned-symbol equivalents of `BannedSymbols.txt` — the clock calls first.

### 6.3 Threading

The device layer is async; Qt is signal-driven and single-threaded for widgets. Use `qasync`
to run one loop. Serial reads happen in `asyncio`, the store is updated there, and the UI is
notified by a queued signal. **Do not** run the serial read in a `QThread` and marshal by
hand, and do not poll from a `QTimer` — §6.4 records why the equivalent shortcuts were
banned in the C# tree, and the reasoning is not language-specific.

### 6.4 The token system in QSS

QSS is not XAML resources: it has no theme dictionaries and no live resource resolution.
Generate the stylesheet from the token table at startup and on theme change, and
re-apply it to the whole application. Keep the token table as data — a single file the
gates can read — so that the "no hex literal outside the token file" rule stays checkable.

Custom widgets do not get their colours from QSS at all; give them a palette object and
repaint on theme change. That mirrors what `ThemePalette.cs` does today.

### 6.5 Accessibility

§9.12 is a written acceptance list and most of it ports: accessible names, focus visuals,
keyboard traversal, live regions (`QAccessible.updateAccessibility` with an alert event),
pointer target floors, and the rule that colour is never the only channel.

What changes is the tooling. The Windows UIA harness this repository uses does not exist on
Linux; use **Accerciser** or **dogtail** against AT-SPI, and expect to write the automation
from scratch. Budget for it — several of the current gates exist because a criterion was
signed off as passing while breaches sat in the primary window, and the thing that found
them was someone trying to use the app.

---

## 7. What does not cross the platform boundary

These are the parts where the answer is not "port it" but "decide what to do instead". Each
changes what the application promises a user, so each belongs in whatever document the port
treats as its specification — an amendment to §2, §3 and §6.1 on the adopted route, or the
fork's own equivalent. None of them should be settled by whoever reaches the file first.

| Windows feature | Where it is used | Linux situation |
|---|---|---|
| **MSIX / Microsoft Store** | §6.3, and G5 in §2 | Gone. Flatpak or AppImage. G5 becomes a Windows-only goal or is dropped. |
| **`Package.Current.DisplayName`** | title bars, per §6.3's rule against hard-coding the name | No package identity. Read the name from one module-level constant instead — but keep the rule that it is read from one place, because the reason for it survives. |
| **Mica Alt backdrop** | §9.2 | No equivalent. The existing solid-colour fallback becomes the only path, which the code already handles correctly. |
| **Windows High Contrast** | §9.2, §9.4.1, and two CI gates | **The largest single gap.** See below. |
| **Stock Fluent colours** | §9.4.1, `build/fluent-stock-colours.txt` | Those values were measured from a running Windows app. The contrast baseline must be re-derived against Qt's palette. |
| **Segoe UI Variable** | §9.5.1 | Not present on Linux. Pick a bundled variable face, or use the system UI font and re-check §9.4.5's contrast and §9.5.2's ramp against it. Cascadia Mono is already bundled and is OFL — it ports as-is. |
| **Taskbar overlay badge** | the shell-mode surfaces | No cross-desktop equivalent. The Unity launcher D-Bus API works on some desktops and not others. Likely dropped. |
| **Tray icon** | the keep-running behaviour in §10.3 | `QSystemTrayIcon` works, but GNOME needs a shell extension. The "close to tray" design in §10.3.1 needs a fallback for desktops with no tray at all. |
| **Windows notifications** | the lock-loss notifier | `notify-send` / the D-Bus `org.freedesktop.Notifications` interface. Straightforward, but note the history here: the App SDK notification path in this project never worked and was fixed by *removing* it. Keep the `IToastSink` seam and its no-op fallback. |
| **Windows accent colour** | §9.4.2's guard against using it as brand | There is no single equivalent. The guard becomes unnecessary; the brand ramp stays. |

### High contrast is the real problem

Two CI gates — theme-dictionary parity and high-contrast legibility — exist because
Windows High Contrast resolves tokens to the *user's own* `SystemColor*` choices, so a
token that is merely defined can still be illegible. The legibility gate was written after
a real defect where satellite markers were painted in the page background colour.

Linux has no equivalent system-wide contract. GNOME has a high-contrast preference; it is
not a per-token API and it is not universal. So the port faces a genuine choice:

- **(a) Ship a hand-authored high-contrast theme** as a third token set the app owns. Then
  both gates keep working, but the app is asserting its own contrast rather than deferring
  to the user's — which is a weaker promise than the current one, and should be written down
  as such.
- **(b) Ship Light and Dark only**, and amend §9.2 and §9.4.1 to say so.

**(a) is the better answer for this audience**, but it is a promise about accessibility
rather than a detail of theming, so make it deliberately — see part 12. Do not let it be
settled by whoever writes the theme file first.

---

## 8. The CI gates

Fourteen scripts run in CI today. They are the accumulated record of defects that review
missed, and losing them silently would be the worst outcome of this port. Triage:

| Gate | Fate |
|---|---|
| `Test-NoBlockedCommands.ps1` | **Ports nearly unchanged.** Reads its tokens from the one file that holds them and scans the tree. Highest-value gate in the set; port it in Phase 3. |
| `Test-DocumentReferences.ps1` | **Ports unchanged.** It checks documents, not source. Keep it running against `docs/` from day one. |
| `Test-GuideCoverage.ps1` | **Ports, with one rewrite.** Its "interactive control carrying a literal label" rule currently parses XAML; against Python it becomes an AST walk for `setText`/`setTitle` on interactive widgets. The allowlist mechanism — a redirection whose target phrasing is itself required to be present — should be kept exactly as it is. |
| `Test-NoHexLiterals.ps1` | **Ports.** Scan `.py` and generated QSS instead of XAML. |
| `Test-SpacingScale.ps1` | **Ports.** Same idea against layout margins in Python. |
| `Test-ContrastFloor.ps1` | **Ports, after the baseline is re-derived** (part 7). The maths is reusable; the input table is not. |
| `Test-SeriesSeparation.ps1` | **Already Python.** `build/palette/` holds the derivation and `validate.py` checks it. Copy across; drop only its high-contrast arm. |
| `Test-NoColourOnlyStates.ps1` | **Rewrite.** The rule (a visual state group must set at least one non-brush property) is sound and worth keeping; the XAML `VisualStateGroup` it parses has no Qt equivalent, so the check has to be re-expressed against however the port models states. |
| `Test-IconOnlyButtons.ps1` | **Rewrite.** Same rule — an icon-only button needs both an accessible name and a tooltip — against Python widget construction. |
| `Test-PointerTargets.ps1` | **Rewrite.** Keep the lesson: the floor must be **declared**, not inherited from whatever a stock style happens to supply, and the check must cover non-button widgets. That is the defect it was written for. |
| `Test-FocusVisualCoverage.ps1` | **Rewrite or drop.** Its trick — two strokes spanning the luminance range, so no accent colour can hide the ring — still applies if the port keeps a custom focus visual. |
| `Test-ThemeDictionaryParity.ps1` | **Depends on part 7's choice.** Meaningless with two themes that are both exercised daily. |
| `Test-HighContrastLegibility.ps1` | **Depends on part 7's choice.** |
| `Capture-Fixtures.ps1 -SelfTest` | **Ports.** The fixture-capture harness matters more than it looks: the states it captures happen only while the hardware is being moved, so the half that needs no serial port is checked on every push. |

---

## 9. Testing

Port the tests **with** the code they cover, phase by phase, not afterwards. 1,317 cases is
a lot to carry, but they are mostly small and table-driven, and `pytest.mark.parametrize`
maps onto `[Theory]` cleanly.

Two things worth building that do not exist today:

- **A parity harness.** For the parser and the drivers, run the C# implementation and the
  Python one over the same fixture and diff the resulting models field by field. This is
  cheap — dump both to JSON — and it turns "did I translate 1,375 lines correctly" from a
  review question into a test.
- **A fuzz test** over the parser, per part 6.2.

One warning from this repository's history: the flaky tests it has had were all the same
shape — a late delivery arriving outside the task being awaited. When a test is
intermittent, force the ordering; do not re-run it to see if it reproduces.

---

## 10. Packaging

- **Linux:** Flatpak is the best fit for the audience — bundles Qt, sandboxed, and serial
  access is a declared device permission (`--device=all` or a udev rule; the user will
  also need to be in `dialout`). AppImage is a reasonable second. Document the serial
  permission prominently; it is the first thing that will go wrong for a new user.
- **Windows:** PyInstaller one-folder plus an MSI, or a Store submission if G5 is retained.
  Note that packaging a Python Qt app for the Store is materially harder than packaging the
  current MSIX, which is a real cost of this port.
- **macOS:** falls out nearly free from PySide6, but nothing here has been thought through
  for it. Do not claim support without a machine to test on.

---

## 11. Effort

Rough, for one competent developer working steadily. The translation half is predictable;
the presentation half is not.

| Phase | Scale |
|---|---|
| 0 — setup | days |
| 1 — models and parser | 2–3 weeks; the parser dominates |
| 2 — transport | 1–2 weeks; the buffer is the risk |
| 3 — commands, safety, drivers | 1–2 weeks |
| 4 — session, polling, storage | 2 weeks |
| 5 — design system | 1–2 weeks, plus the part 7 decisions |
| 6 — custom widgets | 2–3 weeks |
| 7 — windows and pages | 6–10 weeks; this is the bulk |
| 8 — shell, guide, screenshots | 2–3 weeks |

Call it four to six months to reach parity, with the caveat that "parity" includes retaking
every screenshot in the guide and re-establishing an accessibility test rig that does not
currently exist for Linux.

### The alternative worth pricing first

If the goal is **Linux** rather than **Python**, price **Avalonia UI** before starting this
plan. `WinZ3805A.Device` is already `net10.0` with no UI references, so it compiles
unchanged; the 100-file test project runs unchanged; the 90 UI-free app files compile
unchanged; and XAML translates rather than being rewritten. That keeps roughly 40% of the
repository and 100% of the test oracle.

Everything in part 7 above remains true under Avalonia — those are platform facts, not language
facts — but parts 1 to 6 and part 9 of this document largely evaporate. The Python route is the right
one only if Python itself is a goal.

---

## 12. Decisions that must not be made by accident

Each of these changes what the application promises a user, and each has a default that
looks harmless from inside a single pull request. **On an independent port they are the
porter's own calls — this list is not a request for permission, it is a list of the things
worth deciding on purpose and writing down.** On the adopted route they belong to whoever
owns `requirements.md`.

1. **Which route this is**, per part 0 — an independent port, or one this project adopts and
   amends §2, §3 and §6.1 for. Decide it once, at the start; it determines who owns the rest
   of this list and whether a fix flows back.
2. **G5 (Microsoft Store).** Retained as a Windows-only goal, or dropped? An independent
   Linux-first port will almost certainly drop it, which is fine, but it should be stated
   rather than left to lapse.
3. **High contrast** — option (a) or (b) in part 7. This is the one to be most careful
   about. It is an accessibility promise, the current one is stronger than a hand-authored
   theme can be, and the difference is invisible to anyone not relying on it.
4. **The type face**, per part 7, and the contrast re-derivation that follows from it.
   Changing the face without re-running the contrast figures silently invalidates §9.4.5.
5. **Tray and notification behaviour** on desktops that have neither. §10.3.1's
   close-to-tray design assumes a tray exists, and on GNOME it does not without an extension.
6. **What the relationship between the two implementations is.** If both exist, §8's safety
   model exists twice and can diverge; §8.1's allowlist architecture is the part that must
   not. A fork that intends to track this repository should say so and keep the exclusion
   list synchronised; one that intends to diverge should say that too, so nobody assumes a
   fix here reaches users there.
