# WinZ3805A — Requirements Specification

**Version:** 1.0
**Date:** 11 August 2026
**Status:** Ready for implementation
**Audience:** Implementing engineer (Claude Code)

---

## 1. Problem Statement

The HP/Symmetricom SmartClock family of GPS-disciplined oscillators (Z3805A, Z3801A, 58503A/B, 59551A, Z3816A) is widely used in home and small labs as a 10 MHz frequency and 1 PPS time reference. The devices expose a rich SCPI command set over RS-232, but the only monitoring tools available are Windows-9x-era applications (SatStat, Z38XX, GPSCon) that require serial-port shims, look badly out of place on modern Windows, and in several cases expose destructive firmware commands directly next to harmless queries.

Users currently either screen-scrape `:SYST:STAT?` in a terminal emulator or run abandoned software under compatibility shims. There is no modern, safe, Store-distributable option.

**Reference device:** HP Z3805A. All behaviour in this spec is written against the Z3805A and the Symmetricom *58503B/59551A Operating and Programming Guide* (097-58503-13, Iss. 1, Mar 2000), which documents the shared command set.

---

## 2. Goals

| # | Goal | Measured by |
|---|---|---|
| G1 | A glanceable primary window showing lock mode and tracked-satellite count, suitable for leaving open on a second monitor all day | Main window ≤ 420 × 260 px, mode and count legible at 2 m, updates ≤ 2 s after state change |
| G2 | Full receiver status presented as a modern WinUI 3 surface, **not** a reproduction of the 80×24 terminal screen | No fixed-pitch reproduction of the source screen anywhere in the details UI; `WzMonoTextStyle` used only for device-literal text per §9.5.1; all data rendered through the §9.10 component inventory |
| G6 | The app has a visual identity of its own while remaining native to Windows | The §9 token set is implemented in full; §9.13 anti-patterns audit passes; no `SystemAccentColor` used as brand |
| G3 | Complete coverage of the safe, documented SCPI surface through task-oriented dialogs | Every command in §8 tier S/C reachable from the UI |
| G4 | Destructive commands are unreachable — not merely warned about | Blocked commands (§8.3) absent from the shipped command catalog; no free-text command path can emit them |
| G5 | Ships to the Microsoft Store as an MSIX package | Passes Windows App Certification Kit; installs from Store on a clean x64 machine |

---

## 3. Non-Goals

| Non-goal | Rationale |
|---|---|
| Firmware upload / flash erase | The entire class of destructive operations. Explicitly excluded per G4. Users needing this have the vendor tooling. |
| A general-purpose serial terminal | Any free-text path defeats the safety model. The Advanced Console (§10.11) is allowlist-validated, not free-text. |
| NTP server / time service | Large separate concern with security surface. Out of scope for v1. |
| Multi-device simultaneous monitoring | One receiver per app instance in v1. Architecture must not preclude it (§12). |
| Motorola binary engine access | The Z3805A's internal Oncore engine is not exposed on Port 1. Not reachable. |
| GPS week-rollover *correction* on the device | Cannot be fixed via SCPI. The app **detects and compensates in its own display only** (§7.4). |
| Cross-platform (Linux/macOS) | WinUI 3 is Windows-only by definition. |

---

## 4. Target Users

- **Primary — the time-nut / metrology hobbyist.** Owns one or more surplus GPSDOs, runs a lab bench, wants EFC and 1 PPS TI trends and satellite health at a glance. Comfortable with SCPI but does not want to type it.
- **Secondary — the calibration technician.** Uses the GPSDO as the house frequency standard; needs to confirm lock state and holdover uncertainty before a cal run, and to read the diagnostic log after an alarm.
- **Tertiary — the surplus-equipment buyer.** Just acquired a unit, needs to confirm it works, set antenna delay, and kick off a position survey without reading a 300-page manual.

---

## 5. User Stories

**Monitoring**

1. As a lab user, I want a small always-visible window showing lock mode and satellite count, so that I can confirm my reference is healthy without switching windows.
2. As a lab user, I want the window to turn amber/red when the unit drops to holdover, so that I notice degradation immediately.
3. As a technician, I want to see predicted 24-hour holdover uncertainty before starting a calibration, so that I know whether my reference is trustworthy if GPS drops.
4. As a time-nut, I want to see EFC and 1 PPS time interval plotted over hours, so that I can assess oscillator aging and loop behaviour.
5. As a time-nut, I want a polar sky plot of tracked and predicted satellites with signal strength, so that I can diagnose antenna siting and obstructions.

**Setup and control**

6. As a new owner, I want to enter my antenna cable type and length and have the app compute and set the antenna delay, so that I do not have to look up propagation delay tables.
7. As a new owner, I want to start a position survey and watch percent-complete, so that I know when the unit will transition to position hold.
8. As a technician, I want to place the unit into manual holdover and take it out again, so that I can perform holdover characterisation.
9. As a technician, I want to read and clear the diagnostic log, so that I can see what happened while I was away.
10. As a user with a known surveyed position, I want to enter coordinates directly, so that I can skip the two-hour survey.

**Safety and error states**

11. As any user, I want the app to refuse to send anything that could brick my receiver, so that I can explore the interface without fear.
12. As any user, I want a clear message when the serial port cannot be opened or the device does not respond, so that I can distinguish a cable problem from a dead unit.
13. As an owner of a pre-1999 unit, I want the app to tell me the displayed date is affected by GPS week rollover and show me the corrected date, so that I am not confused by a 2006 timestamp.

---

## 6. Technical Foundation

### 6.1 Stack

| Component | Version / choice | Notes |
|---|---|---|
| UI framework | **WinUI 3**, shipped in **Windows App SDK 2.3.1** | Current stable as of Aug 2026. WinAppSDK 2.x uses SemVer with the NuGet version matching the SDK version. |
| Runtime | **.NET 10 (LTS)** — modern .NET, *not* .NET Framework 4.x | WinUI 3 cannot run on .NET Framework, so this is forced. Use the **LTS** release, not the newest STS or preview: Store apps have long tails and LTS gives 3 years of servicing. Do not adopt .NET 11 (Nov 2026, STS) on release. |
| TFM | `net10.0-windows10.0.26100.0` | Confirm against the VS template default at project creation and prefer the template's value if it differs. |
| Min platform | `10.0.17763.0` (Windows 10 1809) | WinUI 3's floor. Verify WinAppSDK 2.3.1 does not raise it; if it does, follow the SDK. |
| Architectures | **x64 only** | No AnyCPU — Windows App SDK is native. Omit x86.<br>**Amended 15 Aug 2026.** This row required x64 *and* ARM64 and said to ship both in the bundle. It now requires x64 alone, for three reasons that compound. First, **WACK cannot cross-test**: it installs and runs the package it certifies, so an ARM64 submission has to be certified on ARM64 hardware, and there is none on this project. Shipping an architecture nobody can run the certification kit against is shipping an unverified binary. Second, **Windows 11 on ARM runs x64 applications under emulation**, so ARM64 machines still get a working application from the x64 package — the cost of dropping the native build is performance on a device this application spends its life idle on, waiting a second at a time for a serial reply. Third, the caveat this row already carried argued against the native build on its own: `System.IO.Ports` runs fine on ARM64, but third-party **USB-serial drivers frequently lack ARM64 builds** (Prolific PL2303 and CH340 clones especially; FTDI is generally fine), so a native ARM64 build is of limited use precisely where it would be used.<br>The driver caveat still applies under emulation, because it is a kernel-mode problem and the emulated application is not what is missing. The connection dialog must fail gracefully with a message naming the likely cause when zero ports enumerate on an ARM64 machine.<br>Restoring ARM64 is a small change — one entry in `Platforms`, one publish profile, one CI matrix row — and should be made when hardware to certify on exists, not before. |
| IDE | Visual Studio 2026, *.NET desktop development* workload + Windows App SDK extension | |
| MVVM | **Hand-written `INotifyPropertyChanged`** | **Amended 15 Aug 2026.** This row specified `CommunityToolkit.Mvvm` and its source-generated `ObservableProperty` / `RelayCommand`. The package was referenced and, across every one of the eleven view models built in §15 steps 7-10, **never used once** — the divergence was found by #125 rather than decided, and is settled here in favour of the code.<br>The reason it never got used is structural rather than accidental. These view models do not own their state: they project a `ReceiverStateStore` that is replaced wholesale on each poll, so each one raises *every* property together from a `RaiseAll()`, and again on a one-second staleness tick. `[ObservableProperty]` generates a property per backing field with per-property change detection, which is precisely the thing there is no use for here — there are no backing fields to generate from, and nothing to detect. Commands are the same story: tier C commands go through `CommandConfirmation.RunAsync` and a `CommandInvoker` built in `OnNavigatedTo`, not through an `ICommand` bound in XAML.<br>Adopting the toolkit would mean rewriting eleven working, tested view models to gain generated code for a pattern they do not use. The package is removed.<br>This is not a rule against the library. If a view model ever *does* own editable state per field — the P1-3 satellite manage dialog (#51) is the likely first — reintroducing it there is a one-line reference, and better than hand-writing what a generator does well. |
| DI | `Microsoft.Extensions.DependencyInjection` | **Amended 15 Aug 2026:** `Microsoft.Extensions.Hosting` removed, zero usages. §12's composition root builds a `ServiceProvider` directly in `App.Compose()` and never a host. The generic host exists to own configuration, logging and a hosted-service lifetime against a process it starts; a WinUI application's lifetime belongs to `Application.OnLaunched` and its windows, so the host would be a second lifetime model beside the real one. |
| Logging | `Microsoft.Extensions.Logging` → rolling file under `Environment.SpecialFolder.LocalApplicationData` | **Amended 15 Aug 2026, and the destination was dangerous as written.** This row said `ApplicationData.Current.LocalFolder`. **Reading `ApplicationData.Current` terminates this process uncatchably** — no managed exception, no first-chance notification, the window simply never appears. It cost a debugging session to find, and every preference store in the application was moved to a plain file under `LocalApplicationData` because of it. A specification that keeps pointing at that API will send the next reader into the same hole, so the destination is corrected here even though the feature behind it does not exist yet.<br>**The feature does not exist yet.** `ILogger` is injected into `SerialTransport`, `LineProtocol`, `DeviceSessionService` and `PollingService`, and `Transport/TransportLog.cs` holds real `LoggerMessage` source-generated instrumentation — but **nothing ever registers a provider**, `ILoggerFactory` is resolved with `GetService` and comes back null, and every one of those call sites resolves to `NullLogger`. The plumbing is real and the log is thrown away. On a tool whose whole job is a serial protocol, that is a gap rather than a tidy-up; it is filed as **#127** rather than fixed here, because adding a logging provider is a feature and #125 was a removal. |
| Serial I/O | `System.IO.Ports` (NuGet) | Works in a full-trust packaged desktop app. |
| Charts (P1) | `LiveChartsCore.SkiaSharpView.WinUI` | Verify current WinAppSDK 2.x compatibility before committing; fall back to a hand-drawn `Canvas` renderer if it lags. |
| Sky plot | Hand-drawn `Canvas` / `Path` geometry | No extra dependency. Win2D acceptable if antialiasing quality demands it. |
| Tests | xUnit for parser and command-catalog logic | Parser and safety classifier must be in a UI-independent library so they are testable headlessly. |

### 6.2 Repository location and solution layout

**Working copy:** `C:\Users\Tony\source\WinZ3805A`
**Repository name:** `WinZ3805A`
**Specification path within the repository:** `docs/requirements.md` — this document. All `§` references in code comments, commit messages, and issue bodies resolve against it.

```
C:\Users\Tony\source\WinZ3805A\
├── WinZ3805A.sln
├── CLAUDE.md                          Agent conventions; points at docs/requirements.md
├── docs/
│   └── requirements.md                This document
├── src/
│   ├── WinZ3805A/                     WinUI 3 app, single-project MSIX
│   │   ├── Views/                     XAML pages, windows, dialogs
│   │   ├── ViewModels/
│   │   ├── Controls/                  StatusMedallion, SkyPlotControl, ReadoutTile,
│   │   │                              SeverityPill, SatelliteStrengthBar,
│   │   │                              ConnectionStatusPill, TrendChart
│   │   ├── Themes/                    Colors.xaml, Typography.xaml, Spacing.xaml,
│   │   │                              Motion.xaml, Controls.xaml — §9 token set,
│   │   │                              each with Light/Dark/HighContrast dictionaries
│   │   ├── Services/                  DeviceSessionService, PollingService,
│   │   │                              SettingsService, WzMotionService
│   │   ├── Assets/                    Store logos, custom PathIcon geometry
│   │   │   └── Fonts/                 CascadiaMono.ttf (OFL 1.1) + licence notice
│   │   └── Package.appxmanifest
│   └── WinZ3805A.Device/              Class library — NO UI references
│       ├── Transport/                 SerialTransport, ITransport, LineProtocol
│       ├── Commands/                  ScpiCommand, CommandCatalog, SafetyTier
│       ├── Parsing/                   StatusScreenParser, ScalarParsers
│       └── Models/                    ReceiverStatus, Satellite, Position, HealthState
└── tests/
    └── WinZ3805A.Tests/               xUnit; captured .txt status screens as fixtures
```

The `Device` library must have zero dependency on `Microsoft.UI.*`. All parsing and safety classification lives there and is unit-tested against captured status-screen text files.

**Naming conventions, so nothing drifts:**

| Thing | Value |
|---|---|
| Repository, solution, root namespace | `WinZ3805A` |
| App assembly | `WinZ3805A.exe` |
| Design token prefix | `Wz` — `WzAccentFillBrush`, `WzSpaceMd`, `WzReadoutLargeTextStyle` |
| MSIX package identity name | `WinZ3805A` (final value issued by Partner Center — see §6.3) |
| Store display name | See §6.3; **not** necessarily identical to the package name |

### 6.3 Microsoft Store packaging

- **Single-project MSIX.** Create from the *Blank App, Packaged (WinUI 3 in Desktop)* template. Do not use a separate Windows Application Packaging Project.
- **Framework-dependent** deployment of the Windows App SDK, not self-contained. The Store handles the framework package dependency and this keeps the submission small.
- **Capabilities in `Package.appxmanifest`:**
  ```xml
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
  ```
  This is the template default and is what permits `System.IO.Ports` (which opens `\\.\COMn` via Win32). `runFullTrust` is a restricted capability requiring justification at submission; it is routinely approved for desktop apps. Justification text to use: *"Desktop application requiring Win32 serial port access to communicate with user-attached RS-232 laboratory instruments."*
- **Do not** declare the `serialcommunication` DeviceCapability. That is for the UWP `Windows.Devices.SerialCommunication` API, which this app does not use. Declaring unnecessary capabilities adds certification friction.
- **Port enumeration** uses `SerialPort.GetPortNames()`, optionally enriched with friendly names read from `HKLM\HARDWARE\DEVICEMAP\SERIALCOMM` and WMI `Win32_PnPEntity`. Registry read is the primary path; treat WMI as best-effort and never block the UI on it.
- **Identity:** `Package/Identity/@Name`, `@Publisher`, and `Properties/PublisherDisplayName` must be replaced with the values Partner Center issues. Leave clear `TODO:` markers in the manifest.
- **Privacy policy:** the app collects and transmits no user data. State this plainly; a privacy policy URL is still required by Store policy for most listings — leave a `TODO:`.
- **Trademark position.** The listing name, package name, and display name must **not** contain "HP", "Hewlett-Packard", "Agilent", "Keysight", or "Symmetricom" — those are company marks and using one implies affiliation. `WinZ3805A` contains a *model designation* rather than a company mark, which is a materially weaker claim and is defensible as nominative descriptive use: the app genuinely is for that device and there is no concise way to say so otherwise. That is the position this project takes.

  Two practical hedges follow from it:

  1. **Describe compatibility in the listing body, never in the name.** Body text such as *"works with HP and Symmetricom SCPI GPS receivers including the Z3805A, Z3801A, 58503A/B, and 59551A"* is descriptive use and is fine. The name stays a bare model reference.
  2. **Keep display name and package identity separable.** `Package/Identity/@Name` is effectively permanent — changing it is a new app with a new listing and no upgrade path for existing users. `Properties/DisplayName` and the Store listing title are one-line changes at any time. If a reviewer objects to `WinZ3805A`, changing the display name to something like *"GPSDO Monitor for Z3805A"* costs nothing, so **do not couple the two in code, resources, or tests**. Read the display name from the manifest; never hard-code the product name in a XAML string.

  See OQ-8.
- **No telemetry** in v1. If added later it requires a Store listing disclosure and an in-app opt-in.

### 6.4 Modern .NET platform usage

The device is plain RS-232 with no vendor driver SDK, no COM interop, and no P/Invoke surface beyond what `System.IO.Ports` already wraps. There is therefore **no legacy anchor** on this project — unlike instrument applications that are pinned to .NET Framework by NI-VISA, IVI-COM, or a vendor DLL. Use current-generation .NET idioms throughout rather than porting older patterns forward.

**Use these:**

| Feature | Where | Why |
|---|---|---|
| `System.IO.Pipelines` | `LineProtocol` read loop | The core parsing problem — scan an incoming byte stream for a prompt sentinel that can straddle buffer boundaries (§7.2 gives its grammar — it is not a fixed string), without copying — is precisely what Pipelines exists for. Use `PipeReader` over `SerialPort.BaseStream` and `SequenceReader<byte>` to find delimiters. Do **not** hand-roll a growing `byte[]` with manual compaction. |
| ~~`SearchValues<byte>`~~ span `IndexOfAny` | Delimiter scanning | **Corrected 21 Aug 2026 (#78).** This row and §7.2’s `SequenceReader<byte>` requirement cannot both be met: .NET 10 ships no `SequenceReader<T>` overload accepting a `SearchValues<T>`. `SequenceReader` wins, because §7.2 needs it for the straddling-buffer case. Nothing is lost — `SearchValues<T>` earns its keep by amortising set preprocessing across a large set, and a two-value CR/LF `IndexOfAny` is already vectorised. `LineProtocol` uses `TryReadToAny` with a two-byte delimiter span, and says so at the call site. |
| `System.Threading.Channels` | Command queue in `DeviceSessionService` | Single-consumer `Channel<PendingCommand>` enforces the one-transaction-at-a-time constraint (§7.2) with no locks. |
| `PeriodicTimer` | Fast and full poll cadences | Async-native, no reentrancy hazard, cancels cleanly. Replaces `DispatcherTimer` / `System.Timers.Timer`. |
| `TimeProvider` (abstract, injected) | Poller, staleness calculation, week-rollover detection | **Important for testability.** The rollover logic in §7.4 compares device time to system time; injecting `TimeProvider` lets the fixture tests assert the 2006→2026 correction deterministically instead of depending on wall clock. Use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` in tests. |
| `FrozenDictionary` / `FrozenSet` | `CommandCatalog` lookups | The catalog is built once and read constantly. Frozen collections give the best read performance and make the immutability of the allowlist structural rather than conventional. |
| `IAsyncEnumerable<T>` | Streaming multi-line responses, log paging | |
| Records, `required` members, primary constructors, collection expressions | Models throughout | `ReceiverStatus` (§11.2) is a `record` with `init` accessors by design. |
| **Nullable reference types enabled, warnings as errors** | Entire solution | Directly serves the "parser never throws" requirement — unparseable fields are typed `null`, and the compiler enforces that every consumer handles it. Set `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props`. |
| Source generators: `CommunityToolkit.Mvvm`, `System.Text.Json` (`JsonSerializerContext`), `LoggerMessage` | ViewModels, settings, logging | Reflection-free, and keeps startup fast. |
| `System.Diagnostics.Metrics` | Transport instrumentation | Counters for transactions, timeouts, parse warnings. Surfaced in Diagnostics; not exported anywhere.<br>**Not built. Amended 15 Aug 2026** to say so, because this table is read as a description of the code. Deferred at §15 step 1 and never picked back up; zero occurrences in the source. It shares a cause with §6.1's logging row — both are observability, and neither survived the first implementation pass. See #127. |

**Do not use these, despite being available:**

| Feature | Why not |
|---|---|
| **Native AOT** | WinUI 3 / XAML apps do not support NativeAOT publishing. Do not attempt it. |
| **Trimming** (`PublishTrimmed`) | WinUI 3 apps trim poorly — XAML type resolution is reflection-driven and trimming produces runtime `MissingMethodException`s that only surface in the packaged build. Leave trimming off. |
| `SerialPort.DataReceived` event | See §7.2. The event model is the source of most `SerialPort` reliability complaints. Read the `BaseStream` asynchronously instead. |
| `Task.Run` around serial reads | `SerialPort.BaseStream` supports genuine async I/O. Wrapping sync calls in `Task.Run` just burns thread-pool threads. |

**`System.IO.Ports` reliability note.** `SerialPort` has a long-standing hazard, inherited from .NET Framework and still present in modern .NET: **removing a USB-serial adapter while the port is open can raise an exception on an internal thread**, and in the `DataReceived` event model this can terminate the process. This directly threatens acceptance criterion P0-14 (unplug/replug). Mitigations, all required:

1. Never subscribe to `DataReceived`, `ErrorReceived`, or `PinChanged`. Read `BaseStream` asynchronously via `PipeReader`.
2. Wrap every read and write in `try/catch` for `IOException`, `UnauthorizedAccessException`, `InvalidOperationException`, and `ObjectDisposedException` — all four are reachable on surprise removal.
3. Dispose the `SerialPort` on a dedicated path that tolerates already-faulted state; do not assume `Close()` succeeds.
4. Add an integration test to the manual QA checklist: unplug the adapter mid-transaction and confirm the app reports Disconnected without crashing. The checklist is [`docs/manual-qa.md`](manual-qa.md); this is section 1, and section 2 is the receiver power cycle, which fails differently (#259).

---

## 7. Device Communication

### 7.1 Serial parameters

| Parameter | Default | Range |
|---|---|---|
| Baud | 9600 | 1200 / 2400 / 9600 / 19200 |
| Data bits | 8 | 7 / 8 |
| Parity | None | None / Even / Odd |
| Stop bits | 1 | 1 / 2 |
| Handshake | None | None only |
| DTR / RTS | Assert both on open | — |

Z3805A ships 9600-8-N-1. Sibling units differ — the Z3801A is commonly 19200-7-E-1 — so all parameters must be user-settable, and the connection dialog must offer an **Auto-detect** that walks the eight most likely combinations sending `*IDN?` until a valid identity string returns.

### 7.2 Line protocol

This is the fiddliest part of the implementation. Get it right before building any UI.

> **⚠ Corrected 21 Aug 2026 (#78).** Everything below was rewritten against
> `SYMMETRICOM,Z3805A,3625A02931,1.01.03-A` at 9600-8-N-1. The original text described a receiver
> that does not exist, and three of its statements stop a client dead rather than degrading it: the
> prompt was given as a literal that never matches, echo was given as always-on when this unit
> echoes nothing, and the connect sequence omitted both a banner and a discarded first command.
> Anyone implementing this section from the previous text wrote a client that could not connect.

- **Transmit:** command text followed by `CR LF`. The receiver terminates on the `CR`; a trailing
  `LF` is harmless. `CR`, `LF` and `CR LF` all work — verified against a drained error queue, and
  none of the three leaves anything behind.
- **Echo:** **both duplex settings occur in the field, and neither may be assumed.** The manual says
  the receiver defaults to `FDUPlex ON` and echoes every character; the bench Z3805A echoes nothing.
  Detect echo by comparing the first received line to the line transmitted, and discard it only when
  it matches. A client that assumes echo-on eats the first line of every response the day it meets a
  unit with echo off; one that assumes echo-off reads its own command back as the answer.
- **Response terminator:** responses end `CR LF`.
- **Response values carry a leading space.** `:SYNC:TFOM?` answers `␣+3`, not `+3`. Trim before
  parsing rather than treating the space as part of the field. This affects every scalar parse
  in §11.
- **Prompt sentinel — a grammar, not a literal.** The prompt marks end-of-transaction and carries
  **no trailing newline**. It has two forms:

  ```
  prompt := "scpi > "            the error queue is empty
          | "E-" digits "> "     the error queue is not empty, e.g. "E-113> "
  ```

  **The two forms space differently**: `scpi > ` has a space before the `>` and the error form does
  not. The only reliable invariant is that a prompt ends with `>` followed by a space. Matching the
  literal `scpi> ` that this section used to specify never terminates a transaction at all, so every
  command runs to its full timeout.

  A command the receiver rejects answers with **the prompt and nothing else**. There is no error
  body on the wire; the reason must be fetched separately with `:SYST:ERR?`.

- **The prompt reflects the ERROR QUEUE, not the command that just ran.** This is the correction
  most easily got wrong, because the wrong reading is plausible and stays self-consistent for as
  long as the queue happens to be empty.

  Measured with one error queued, then three *successful* commands issued:

  | Command | Response body | Prompt |
  |---|---|---|
  | `*IDN?` (queue empty) | `SYMMETRICOM,Z3805A,3625A02931,1.01.03-A` | `scpi > ` |
  | *one bad command queues* `-113` | | |
  | `*IDN?` | `SYMMETRICOM,Z3805A,3625A02931,1.01.03-A` | **`E-113> `** |
  | `:SYNC:STAT?` | `LOCK` | **`E-113> `** |
  | `:GPS:SAT:TRAC:COUN?` | `+2` | **`E-113> `** |

  Every one of those succeeded and returned correct data under an error prompt. **A client that
  infers "this command failed" from an error prompt is wrong** — and wrong in the direction that
  reports a working command as broken. §7.3.1 records the instance of exactly that.

  Two further details, each measured in both orders:

  - **The prompt names the *newest* queued error; `:SYST:ERR?` returns the *oldest* first.** They
    read opposite ends of the same FIFO. Queue `-113` and then `-109`, and the prompt reads
    `E-109> ` while the first `:SYST:ERR?` answers `-113`.
  - **The prompt returns to `scpi > ` only once the queue is fully drained**, and it reflects the
    queue as of the end of the transaction — the read that empties the queue already comes back
    with `scpi > `.

  Read correctly the prompt is a free signal: it says whether the queue is non-empty without
  spending a round trip. It never says which command put something in it.

- **Connect sequence — three steps, in order, before any command whose answer matters.** Asserting
  DTR on open has two separate effects, and skipping either step corrupts the session silently
  rather than failing:

  1. **Absorb the banner.** The receiver emits its identity string and a prompt with nothing asked
     of it. The announcement arrives late enough to land *after* a first command has gone out, so a
     client that transacts immediately reads the banner as its first response and every reply
     afterwards is one behind — with nothing reporting an error, because every transaction still
     completes.
  2. **Spend the framing glitch.** The same DTR assertion reaches the receiver as a character. It
     answers the next thing it is asked with `-362`, "Framing error in program message", having
     dropped that command unexecuted. The first command after opening must therefore be one whose
     response nobody wants. During auto-detect the alternative is losing the identity query that
     decides whether a receiver is present at all.
  3. **Then transact.**

  The glitch is directly observable: open the port and send `*IDN?` first and it answers `E-362> `
  with no identity string; send anything else first and `*IDN?` answers normally.
- **Read strategy:** a transaction completes when the stream yields the prompt sentinel, or on timeout. Never rely on `ReadLine()` alone — `:SYST:STAT?` is a multi-line block of ~1900 bytes that will span many reads. Implement with `PipeReader` over `SerialPort.BaseStream` and `SequenceReader<byte>` for sentinel detection (§6.4); this handles the straddling-buffer case for free and avoids the manual compaction bugs that plague hand-rolled versions.
- **Timeouts:** 3000 ms default; 15000 ms for `:SYST:STAT?` (≈1900 bytes at 9600 baud ≈ 2 s of wire time, plus device latency); 30000 ms for `*TST?` and `:DIAG:TEST?`.
- **Concurrency:** the device is strictly one-transaction-at-a-time. `DeviceSessionService` owns a single-consumer `Channel<PendingCommand>`; all callers `await` their turn. No exceptions to this.
- **Error checking:** after every command in safety tier **C** (§8), automatically issue `:SYST:ERR?` and surface any non-zero error to the user. Do not do this after tier-S queries — it doubles traffic for no benefit.
- **Reconnect:** on `IOException` or timeout ×3 consecutive, transition to `Disconnected`, close the port, and retry with exponential backoff (2 s, 4 s, 8 s, capped 30 s) while the user has "stay connected" enabled.

### 7.3 Polling schedule

Two independent cadences:

| Tier | Interval (default, user-settable) | Commands |
|---|---|---|
| **Fast** | 1 s | `:SYNC:STAT?`, `:SYNC:TFOM?`, `:SYNC:FFOM?`, `:SYNC:TINT?`, `:DIAG:ROSC:EFC:REL?`, `:GPS:SAT:TRAC:COUN?` |
| **Full** | 10 s | `:SYST:STAT?` |

Rationale: the satellite El/Az/(C-N) table has **no individual query** — it exists only inside `:SYST:STAT?`. Everything else has a cheap scalar query. Fast tier drives the main window and trend charts; full tier drives the satellite table, position, and health sections.

At 9600 baud the full screen consumes ~2 s of the 10 s window. The scheduler must never let the two tiers overlap — they share the same command channel, so the fast tier will naturally stall behind a full-screen fetch. That is acceptable; do not attempt to interleave.

#### 7.3.1 A reading the receiver will not give

> **⚠ Added 20 Aug 2026** (#155). The sweep above was a fixed list asked unconditionally. It is now
> conditional in one place, for a reason that had to be found on hardware.

**`:SYNC:TINT?` has no answer while the receiver is unlocked.** There is no GPS 1 PPS to measure
against, so the receiver answers no data at all and puts `E-230` — *data corrupt or stale* — in the
prompt. That is the correct answer to the question; the question is the mistake.

Asked once a second it is a mistake with consequences. On the bench receiver, an unlocked spell
filled the error queue until the receiver began answering **`E-350`, queue overflow**, and the
Diagnostics page could not empty it because the sweep refilled it faster than the page drained it.
Real errors were being discarded to make room for poll noise.

**The rule.** When the receiver refuses a fast-tier reading, that reading is not asked for again
until `:SYNC:STAT?` reports a different state.

- **Keyed on the state, not on a list of states.** Nothing in the application decides which sync
  states support which reading; the receiver is asked once and believed. This makes no claim about a
  sibling model whose firmware may answer where this one does not, and it costs at most one error per
  state transition instead of one per second.
- **It self-clears.** A receiver that regains lock is asked again on the next sweep, because its
  state changed.
- **Only a refusal counts.** A timeout or a dropped link says nothing about whether the receiver
  would have answered, and suppressing a reading because a cable was unplugged would keep it
  suppressed after the cable was plugged back in.
- **`:SYNC:STAT?` must stay first in the sweep.** The rule depends on knowing the state before the
  rest of the tier is asked, which the order above already provides.

**§7.2's error-queue check is why this matters beyond tidiness.** That rule reads the queue after
every tier C command and surfaces anything non-zero, which assumes the queue holds *that command's*
error. Filled with poll noise it does not: a user applying an antenna delay while the receiver was
unlocked was told about a time-interval poll instead — a fault reported that did not happen, and one
that did hidden behind it. **So the tier C path drains the queue before the command as well as
reading it afterwards**, bounded, discarding what it finds without reporting it. Those entries
pre-date the user's action and attributing them to it is the defect.

### 7.4 GPS week rollover detection

Compute `delta = SystemUtcNow - DeviceReportedUtc`. If `delta` is within ±7 days of a multiple of 1024 weeks (7168 days), set `ReceiverStatus.WeekRolloverEpochs = round(delta / 7168 days)` and expose:

- `DeviceReportedDate` — as returned.
- `CorrectedDate` — device date + (epochs × 7168 days).

The UI shows the corrected date prominently with an info badge explaining the offset, and the raw device date in the tooltip. **Do not** silently substitute — the user must be able to see what the hardware actually said. Time-of-day and the 1 PPS itself are unaffected; the badge text must say so, because users reasonably panic when they see the wrong year.

---

## 8. Command Catalog and Safety Model

### 8.1 Architecture requirement

The command catalog is an **allowlist**, implemented as a static readonly collection in `WinZ3805A.Device.Commands.CommandCatalog`. Every SCPI string the application can emit originates from this catalog. There is no code path that constructs a command string from arbitrary user input.

```csharp
public enum SafetyTier { Safe, Confirm, Blocked }

public sealed record ScpiCommand(
    string Mnemonic,          // ":GPS:SAT:TRAC:EMANgle"
    string ShortForm,         // ":GPS:SAT:TRAC:EMAN"
    SafetyTier Tier,
    bool IsQuery,
    string DisplayName,
    string Description,
    IReadOnlyList<ParameterSpec> Parameters,
    ResponseFormat ResponseFormat,
    string? ConfirmationText = null,
    string? SuccessText = null,          // §9.7.4
    bool RequiresAcknowledgement = false,// §9.7.4’s four "I understand" commands
    bool IsExperimental = false,         // §8.5, off by default and never polled
    string? ValueLabel = null);          // names a composite value in a confirmation
```

> **⚠ Corrected 21 Aug 2026 (#85).** The record carries four more members than this section originally declared, and three are safety-relevant. `RequiresAcknowledgement` keeps §9.7.4’s four strong-variant commands in the catalog rather than as a hard-coded list in the dialog layer — a second home for a safety fact, in the layer least able to test it. `IsExperimental` keeps §8.5’s opt-in queries out of `CommandCatalog.Safe`, so they cannot reach a poll timer or the everyday command surface. The remaining two carry §9.7.4’s success text and §10.6’s composite value label.

**Blocked commands are not present in the catalog at all.** They are not entries with a flag; they do not exist as data. The `SafetyTier.Blocked` value exists only so the validator in the Advanced Console can reject a user-typed string by pattern match and log the attempt. A blocked command must never appear in any list, picker, autocomplete, help text, or log the user can see.

### 8.2 Tier S — Safe (execute on click, no confirmation)

All queries plus non-disruptive actions.

```
*IDN?                                        *CLS
*ESE?                *ESR?                   *SRE?              *STB?
:SYST:STAT?          :SYST:STAT:LENG?        :SYST:ERR?
:SYST:DATE?          :SYST:TIME?             :SYST:COMM?
:SYNC:STAT?          :SYNC:FFOM?             :SYNC:TFOM?        :SYNC:TINT?
:SYNC:HOLD:DUR?      :SYNC:HOLD:DUR:THR?     :SYNC:HOLD:DUR:THR:EXC?
:SYNC:HOLD:TUNC:PRED?  :SYNC:HOLD:TUNC:PRES?  :SYNC:HOLD:WAIT?
:GPS:REF:VAL?        :GPS:REF:ADEL?
:GPS:POS?            :GPS:POS:ACT?           :GPS:POS:HOLD:LAST?
:GPS:POS:HOLD:STAT?  :GPS:POS:SURV:PROG?     :GPS:POS:SURV:STAT?
:GPS:POS:SURV:STAT:POW?
:GPS:SAT:TRAC?       :GPS:SAT:TRAC:COUN?     :GPS:SAT:TRAC:EMAN?
:GPS:SAT:TRAC:IGN?   :GPS:SAT:TRAC:IGN:COUN?  :GPS:SAT:TRAC:IGN:STAT? <PRN>
:GPS:SAT:TRAC:INCL?  :GPS:SAT:TRAC:INCL:COUN? :GPS:SAT:TRAC:INCL:STAT? <PRN>
:GPS:SAT:VIS:PRED?   :GPS:SAT:VIS:PRED:COUN?
:PTIM:TCOD?          :PTIM:TCOD:FORM?        :PTIM:DATE?        :PTIM:TIME?
:PTIM:TIME:STR?
:PTIM:TZON?
:PTIM:LEAP:ACC?      :PTIM:LEAP:DATE?        :PTIM:LEAP:DUR?    :PTIM:LEAP:STAT?
:LED:ALAR?           :LED:GPSL?              :LED:HOLD?
:DIAG:ROSC:EFC:REL?  :DIAG:LIF:COUN?         :DIAG:QUER:RESP?
:DIAG:LOG:COUN?      :DIAG:LOG:READ?         :DIAG:LOG:READ? <n>
:DIAG:LOG:READ:ALL?  :DIAG:TEST:RES?
:STAT:OPER:COND?     :STAT:OPER:EVEN?        :STAT:OPER:ENAB?
:STAT:OPER:NTR?      :STAT:OPER:PTR?
:STAT:OPER:HARD:COND?    :STAT:OPER:HARD:EVEN?    :STAT:OPER:HARD:ENAB?
:STAT:OPER:HARD:NTR?     :STAT:OPER:HARD:PTR?
:STAT:OPER:HOLD:COND?    :STAT:OPER:HOLD:EVEN?    :STAT:OPER:HOLD:ENAB?
:STAT:OPER:HOLD:NTR?     :STAT:OPER:HOLD:PTR?
:STAT:OPER:POW:COND?     :STAT:OPER:POW:EVEN?     :STAT:OPER:POW:ENAB?
:STAT:OPER:POW:NTR?      :STAT:OPER:POW:PTR?
:STAT:QUES:COND?         :STAT:QUES:EVEN?         :STAT:QUES:ENAB?
:STAT:QUES:NTR?          :STAT:QUES:PTR?
:SYNC:HOLD:REC:INIT
:SYNC:HOLD:REC:LIM:IGN
```

`:SYNC:HOLD:REC:INIT` and `:SYNC:HOLD:REC:LIM:IGN` are classed Safe: they move the unit *toward* lock, which is the desired state, and cannot damage anything.

### 8.3 Tier C — Confirm (modal confirmation with explicit consequence text)

| Command | Confirmation text |
|---|---|
| `:SYST:PRESet` | "Reset all receiver settings to factory defaults? Antenna delay, position, elevation mask, and satellite selections will be lost. Serial port settings are not affected." |
| `:SYST:COMM:SER1:BAUD`<br>`:SYST:COMM:SER1:BITS`<br>`:SYST:COMM:SER1:PARity`<br>`:SYST:COMM:SER1:SBITs`<br>`:SYST:COMM:SER1:PACE`<br>`:SYST:COMM:SER1:FDUPlex` | "Change serial port settings? The connection will drop and the app will attempt to reconnect with the new settings. **These persist through power cycling** — if reconnection fails you will need to try each setting manually." |
| `:SYST:COMM:SER1:PRESet` | "Restore serial port to factory defaults (9600-8-N-1)? The connection will drop and reconnect." |
| `:GPS:REF:ADELay <s>` | "Set antenna delay to *n* ns? Changing this while locked can push the receiver into holdover." |
| `:GPS:POSition <coords>` | "Set fixed antenna position? This cancels any survey in progress and the receiver will use these coordinates for all timing solutions. An incorrect position degrades timing accuracy." |
| `:GPS:POSition LAST` | "Cancel survey and restore the last held position?" |
| `:GPS:POSition SURVey` | "Stop surveying and adopt the computed average position?" |
| `:GPS:POSition:SURVey:STATe ONCE` | "Start a position survey? This takes approximately two hours with four or more satellites tracked." |
| `:GPS:POS:SURV:STAT:POWerup <ON\|OFF>` | "Change power-up behaviour?" |
| `:GPS:INIT:DATE` / `:GPS:INIT:TIME` / `:GPS:INIT:POSition` | "Send initial acquisition aid? Only valid before the first satellite is tracked; the receiver will return error −221 otherwise." |
| `:GPS:SAT:TRAC:EMANgle <deg>` | "Set elevation mask to *n*°? Values above 15° during survey may prevent position determination; above 40° severely limits availability." |
| `:GPS:SAT:TRAC:IGNore <PRN…>` / `:IGN:ALL` / `:IGN:NONE` | "Exclude the selected satellites from tracking?" — `:IGN:ALL` gets a stronger variant: "Exclude **all** satellites? The receiver will lose lock and enter holdover." |
| `:GPS:SAT:TRAC:INCLude <PRN…>` / `:INCL:ALL` / `:INCL:NONE` | "Update the tracking inclusion list?" — `:INCL:NONE` gets the strong variant. |
| `:SYNC:HOLDover:INITiate` | "Force manual holdover? The receiver will stop disciplining to GPS until you explicitly recover. **Do not do this within the first 24 hours after power-up** — it corrupts SmartClock oscillator learning." |
| `:SYNC:HOLD:DUR:THReshold <s>` | "Set holdover threshold?" |
| `:SYNC:IMMediate` | "Force immediate resynchronisation? This causes a step change in the 1 PPS output." |
| `:PTIM:TZONe <h>,<m>` | "Change time zone offset? All reported times change, including the timecode output." |
| `:DIAG:LOG:CLEar` | "Clear the diagnostic log? This cannot be undone." |
| `:STAT:PRESet:ALARm` | "Reset alarm masks to defaults?" |
| `:STAT:*:ENABle` / `:NTRansition` / `:PTRansition` setters | "Change status register mask?" |
| `:STAT:QUES:COND:USER <SET\|CLEar>`<br>`:STAT:QUES:EVEN:USER <PTR\|NTR>` | "Change user-defined questionable status bit?" |
| `*ESE` / `*SRE` setters | "Change event/service-request enable mask?" |
| `*TST?` | "Run the receiver's full self-test? **The receiver will drop out of lock and re-acquire**, so the 1 PPS output is degraded for several minutes. The test itself takes up to 30 seconds." |
| `:DIAG:TEST? <subsystem>` | "Run the *subsystem* diagnostic? **The receiver will drop out of lock and re-acquire**, so the 1 PPS output is degraded for several minutes. The test itself takes up to 30 seconds." |

> **⚠ Amended 28 Aug 2026 (#53).** Both lines previously said the test *"may briefly interrupt normal
> operation"*. Measured against the live Z3805A, that materially understates it:
>
> ```
> 18:20:39  LOCK, TFOM 3, 9 satellites     <- before
>    ... the twelve subsystem tests ...
> 18:24:38  POW,  TFOM 9, 8 satellites     <- after
> ```
>
> The receiver left lock entirely and returned to **power-up with TFOM 9**, then re-acquired over
> several minutes. On an instrument whose entire purpose is a disciplined 1 PPS, that is the
> difference between a shrug and a real outage, and §8's tier C exists precisely so the user is told
> **before** they commit rather than discovering it afterwards. The old wording would have been read
> as "a second or two".
>
> The 30 second figure is retained and confirmed: individual subsystems returned in 2.4–5.4 s, `ALL`
> in 12.4 s, and `GPS` reached **24.0 s** — inside §7.2's 30 000 ms class with six seconds to spare,
> so a shorter timeout would fail a healthy receiver.

**Dialog construction, button roles, styling and focus behaviour are specified in §9.7.4**, which is the authority for them. The rule that stood here previously — destructive action on the *secondary* button, styled `AccentButtonStyle` — was superseded by §9.7.4 and is removed rather than left 450 lines from its own correction (#84). It mattered: §9.7.4 puts the destructive action on the **PrimaryButton** styled `WzDestructiveButtonStyle` with Cancel as the `CloseButton` and `DefaultButton`, precisely because *accent means the safe thing to do next*. Anyone reading §8 in order and implementing from it would have built the opposite.

The four commands needing an additional "I understand" tick are named in §9.7.4 and carried in the catalog by `RequiresAcknowledgement` (see §8.1), not by a list held in the dialog layer. **§8.3’s confirmation-text table below is untouched by that amendment** — §9.7.4 changes button roles only.

### 8.4 Tier B — Blocked (absent from catalog; never displayed)

```
:DIAGnostic:DOWNload            Flash firmware via S-record — can brick the unit
:DIAGnostic:ERASe               Erases application flash, leaves boot loader only
:DIAGnostic:ERASe?
:SYSTem:LANGuage "INSTALL"      Switches to firmware-install mode
:SYSTem:LANGuage "PRIMARY"      Paired with the above; blocked to keep the pair atomic
```

Plus, categorically:

- **Any undocumented node in set form.** The Z3801A firmware string table contains parser keywords with no published documentation (`TCOefficient`, `PSTARTUP`, `DOUTput`, `RESTricted`, `OUTPut:PINS:PIN1..PIN8`, `SOURce`, `IREFerence`, `EGRESPONSE`, and others). Query forms of a small subset may be enabled per §8.5. **Set forms are permanently blocked with no override.**
- `:SYSTem:LANGuage?` — query only, harmless, but omitted anyway so the `LANGuage` node never appears in any UI surface. Its value is not useful to the target users.

The blocked list is **private to the `WinZ3805A.Device` assembly**, held in one file as regex patterns, and reachable from outside only as a predicate — `CommandCatalog.IsBlocked(string)`, which answers one bool about one candidate. Nothing can enumerate it, bind to it, or render it into a list. Its only caller is the Advanced Console validator, which rejects a typed string and logs the attempt.

> **⚠ Corrected 21 Aug 2026 (#85).** This paragraph previously named the list `CommandCatalog.BlockedPatterns` and then required, in the same sentence, that it "must not be enumerable through any public API that a view binds to". A public member of that name is enumerable by definition. **The requirement wins over the name.** `build/Test-NoBlockedCommands.ps1` enforces that the one file stays the only place these names occur.

### 8.5 Experimental queries (opt-in, query-only)

Off by default. Enabled by a toggle in Settings → Advanced, with the text: *"Enable undocumented read-only queries. These are present in the receiver's command parser but absent from the published manual. They may return errors or nonsense. No setting is changed."*

When enabled, the Diagnostics window shows an additional read-only card offering exactly:

| Query | Z3801A | Z3805A, firmware `1.01.03-A` |
|---|---|---|
| `:DIAG:ROSC:EFC:ABSolute?` | in the string dump | **`+436061`** |
| `:DIAG:ROSC:EFC:TCOefficient?` | in the string dump | `E-113` |
| `:SYST:STAT:SLOG?` | in the string dump | `E-113` |
| `:DIAG:STACk?` | in the string dump | `E-113` |
| `:DIAG:PROCess?` | in the string dump | `E-113` |
| `:DIAG:MEMory?` | in the string dump | `E-113` |

Each runs on explicit click, never on a poll timer. Results shown as raw text. Any SCPI error is displayed rather than swallowed. This list is fixed — no free-text entry into it.

> **⚠ Amended 20 Aug 2026** (#152). The list above was previously six mnemonics with no indication
> of where they came from or which models have them. **Both columns are now stated**, because the two
> are not the same claim and the difference matters to anyone reading a result.
>
> The keywords come from the **Z3801A** firmware string dump named in §16 — a sibling model. Being in
> that dump means the node exists in *that* firmware's parser, and says nothing about any other. Run
> against the bench **Z3805A** on 20 Aug 2026 through the §10.11 console and the §8.5 card, five of the
> six answered `E-113` and the receiver's error queue held exactly five entries afterwards.

**`E-113` is an answer, not a failure.** It is SCPI's *undefined header*: the node is not in this
firmware's parser. For a card whose entire purpose is asking undocumented questions, "this receiver
does not have that one" is a result, and the most useful one available for five of the six. It is
displayed like any other, and it is **not** an error in the application, in the transport or in the
receiver.

**The list stays fixed at six on every model.** It is not filtered to what the connected receiver
supports, for three reasons: the application would have to probe all six to know, which is what the
card does anyway; a list that changed shape by model would make the specification's "exactly" untrue;
and a user who opted into asking undocumented questions is owed the answer rather than a shorter
list. §8.6 governs model-specific *behaviour*; this list is model-independent by construction.

**Where a query does answer, the answer is not documented either.** `:DIAG:ROSC:EFC:ABSolute?`
returns `+436061` on this receiver while the documented `:DIAG:ROSC:EFC:RELative?` returns
`-16.83` per cent at the same moment. Nothing states the units of the first, and nothing may assume
them: it is shown as raw text and no part of the application computes anything from it.

### 8.6 Model-specific commands

The following exist in the shared command set but are **59551A-only hardware features**. Detect model from `*IDN?` and hide these entirely on a Z3805A:

```
:PULSe:*                        Programmable pulse output
:SENSe:TSTamp<n>:*              Event time stamping
:SENSe:DATA:*
:FORMat:DATA
:PTIM:PPS:EDGE                  1 PPS edge polarity
:SYST:COMM:SER2:*               Second serial port
```

On a Z3805A, Port 2 is a time-of-day broadcast that does not accept commands. Probe `:SYST:COMM:SER2:BAUD?` once at connect; if it errors, mark SER2 unsupported and hide.

---

## 9. Design System

This section is normative for all visual and interaction decisions. Where it conflicts with a functional requirement in §7, §8, or §10, **the functional requirement wins** and the conflict is flagged in §9.14. Every surface described in §10 is built from the tokens and components defined here; §10 no longer describes appearance in its own terms.

### 9.1 Design thesis

**Who and when.** The people in §4 are not "using" this app most of the time — they are *glancing at* it. A time-nut has it docked on a second monitor beside a spectrum analyser display for weeks at a stretch. A calibration technician looks at it for four seconds before a cal run to answer one question: *can I trust this reference right now?* The surplus buyer is the only user with a long first session, and that session is roughly forty minutes of setup that happens once. Session length is bimodal: **thousands of two-second glances, punctuated by rare half-hour diagnostic sessions.**

That inverts the usual desktop-app priority. Optimising for the interactive session at the expense of the ambient one would be optimising for the rarer case.

> **Design thesis — *the instrument face*.**
> This is not a dashboard about a device; it is the front panel the device never had. It must be readable across a bench in peripheral vision, and it must be so quiet when everything is fine that the moment something changes, you notice without looking.

Three consequences follow, and they govern everything below:

1. **Colour is a signal, not a surface.** Chrome is near-neutral. Saturated colour appears only where it carries state. If the app is showing red, something is actually wrong. This is why the palette is small and why the accent is deliberately not the Windows default blue — a blue that means "selected" everywhere else in Windows cannot also mean "nominal" here.
2. **Data does not move.** Numeric readouts snap to their new value. Nothing counts up, eases, or tweens. A smoothed instrument display is a lie about the measurement, and the audience for this app is precisely the audience that would notice.
3. **The window recedes.** Mica Alt shows through the base layer so the app reads as part of the desktop when nothing needs attention, and the state medallion is the only thing that pulls the eye.

**Signature moment — the state medallion.**

The main window's primary element is a circular medallion: a mode glyph at the centre, wrapped by a ring that is a **live 60-second radial sparkline of 1 PPS time interval**. One object answers both questions a glance is asking — *what state is it in* (discrete, from the glyph and colour) and *how well is it behaving* (continuous, from whether the ring is smooth or ragged). A calm ring means a calm loop. A ring that suddenly grows teeth means the loop is hunting, and you see it before TFOM changes.

The ring is **qualitative by design**. It is not a chart and must never be read for values; the precise TI figure is always set adjacent to it in `WzReadoutMedium`. Circles are reserved exclusively for the medallion — every other surface in the app uses a 4 px or 8 px radius (§9.3). That reservation is what makes it read as an instrument face rather than one card among many.

**Thesis sanity check.** The first direction was a conventional metrics dashboard: KPI tiles, system accent, a line chart above the fold. That is what this brief would produce for a server monitor, a battery analyser, or a network appliance — the subject was doing no work. Three things were changed and are load-bearing:

| Changed from | Changed to | Why |
|---|---|---|
| Rectangular KPI tile for lock state | Circular medallion with radial TI sparkline | The subject is a *phase-locked loop*. Loop behaviour is periodic, so a radial trace is the honest form. It also makes the one element you glance at carry two dimensions of information instead of one. |
| System accent colour | Fixed brand teal `#0B6C74` / `#3FB8C4`, system accent opt-in | If accent follows the system, a user with a red or amber system accent gets an app whose chrome is indistinguishable from its alarm states. Semantic colour must be structurally protected from user configuration. |
| Segoe UI throughout | Segoe UI Variable for UI, **Cascadia Mono for all device-literal text** | Every string the receiver actually emits — SCPI mnemonics, raw register values, log entries, transcript — is set in Cascadia Mono. The typographic split makes "what the machine said" visually distinct from "what the app says about it," which matters in an app whose whole job is faithful reporting. |

### 9.2 Materials, layering, and elevation

**Backdrop.** The window uses **Mica Alt** (`MicaBackdrop` with `Kind="BaseAlt"`), not Mica and not Acrylic.

- Mica Alt's stronger tint gives the layered card surfaces more separation from the backdrop than base Mica, which matters because this app is mostly cards on a backdrop with little else.
- Acrylic is wrong: it is for transient surfaces, and it samples what is *behind* the window, which for a bench user is often another instrument display — visual noise directly behind status data.
- **Windows 10 degradation:** Mica is unavailable below Windows 11 build 22000. Fall back to `WzPageBackgroundFallbackBrush` (a solid, defined per theme in §9.4). Detect with `MicaController.IsSupported()` (`Microsoft.UI.Composition.SystemBackdrops`); never let an unsupported backdrop produce a transparent or black window. `MicaBackdrop` itself exposes only `Kind` and `KindProperty` — verified against the shipped WinAppSDK 2.3.1 assemblies — so the check cannot be made on the backdrop object.

**Layer hierarchy.**

| Layer | Surface | Fill | Stroke | Shadow |
|---|---|---|---|---|
| **L0** | Window backdrop | Mica Alt (or fallback solid) | none | none |
| **L1** | Page content region | `WzLayerFillBrush` (`LayerFillColorDefaultBrush`) | none | none |
| **L2** | Card | `WzCardFillBrush` (`CardBackgroundFillColorDefaultBrush`) | 1 px `WzStrokeSubtleBrush` | none |
| **L2h** | Card, hover / selected | `CardBackgroundFillColorSecondaryBrush` | 1 px `WzStrokeDefaultBrush` | none |
| **L3** | Transient: `ContentDialog`, `Flyout`, `TeachingTip`, `ToolTip`, `MenuFlyout` | `WzOverlayFillBrush` (`SolidBackgroundFillColorBase`) | 1 px `SurfaceStrokeColorFlyoutBrush` | `ThemeShadow` |

**Elevation rule, stated so it is testable:** *a surface casts a shadow if and only if it can be dismissed.* Dialogs, flyouts, tips, tooltips and menus have `ThemeShadow`. Cards, headers, panes, and the medallion never do. A reviewer can verify this by searching the XAML for `Shadow` and checking each hit is a transient surface.

Depth values are WinUI stock: dialog 32, flyout 16, tooltip 8. Do not invent intermediate depths.

**Mica must show through.** L1 page regions use `LayerFillColorDefault`, which is translucent. Do not place an opaque `SolidColorBrush` panel spanning the content area — that defeats the backdrop and is called out in §9.13.

**Stroke behaviour across themes.** In light theme, card strokes read as a slightly darker hairline than the fill and do most of the separation work. In dark theme, `CardStrokeColorDefault` is a *lighter* value than the fill — this is correct and intentional, and implementers must not "fix" it by inverting. In high contrast, all strokes resolve to `SystemColorWindowTextColor` at 1 px and become the *only* separator, since fills collapse to the system window colour.

### 9.3 Corner radius

| Token | Value | Applies to |
|---|---|---|
| `WzControlCornerRadius` | **4** | Buttons, text inputs, combo boxes, checkboxes, chips, pills, progress bars, list item highlights |
| `WzCardCornerRadius` | **8** | Cards, `Expander`, `InfoBar`, page section containers, the sky plot frame |
| `WzOverlayCornerRadius` | **8** | Dialogs, flyouts, teaching tips, menus, tooltips (WinUI stock `OverlayCornerRadius`) |
| — | **circle** | The state medallion only |

Nothing else. No 2, no 6, no 12, no 16. Window corners are OS-managed — do not set them.

The single circle is the point: it is the one shape the eye can find without focusing, which is exactly the job the medallion has to do.

### 9.4 Colour

All colour is declared in `Themes/Colors.xaml` as three `ResourceDictionary` entries under `ThemeDictionaries`: `Light`, `Dark`, `HighContrast`. **No literal hex value appears in any control style, page, or code-behind.** Every brush below is defined once and referenced by key with `{ThemeResource}` — never `{StaticResource}`, which would not re-resolve on theme change.

#### 9.4.1 Surface and text tokens

| Token key | Light | Dark | High contrast | Role |
|---|---|---|---|---|
| `WzPageBackgroundFallbackBrush` | `#F3F3F3` | `#202020` | `SystemColorWindowColor` | Solid backdrop when Mica is unavailable |
| `WzLayerFillBrush` | → `LayerFillColorDefaultBrush` | → same | `SystemColorWindowColor` | L1 page region |
| `WzCardFillBrush` | → `CardBackgroundFillColorDefaultBrush` | → same | `SystemColorWindowColor` | L2 card |
| `WzCardFillSecondaryBrush` | → `CardBackgroundFillColorSecondaryBrush` | → same | `SystemColorWindowColor` | L2 card, recessed row |
| `WzOverlayFillBrush` | → `SolidBackgroundFillColorBaseBrush` | → same | `SystemColorWindowColor` | L3 transient |
| `WzStrokeSubtleBrush` | → `CardStrokeColorDefaultBrush` | → same | `SystemColorWindowTextColor` | Card hairline |
| `WzStrokeDefaultBrush` | → `ControlStrokeColorDefaultBrush` | → same | `SystemColorWindowTextColor` | Input borders, dividers |
| `WzTextPrimaryBrush` | → `TextFillColorPrimaryBrush` | → same | `SystemColorWindowTextColor` | Readouts, headings |
| `WzTextSecondaryBrush` | → `TextFillColorSecondaryBrush` | → same | `SystemColorWindowTextColor` | Labels, units, captions |
| `WzTextTertiaryBrush` | `#8C000000` — **owned, see below** | → `TextFillColorTertiaryBrush` | `SystemColorGrayTextColor` | Footers, timestamps, staleness |
| `WzTextDisabledBrush` | → `TextFillColorDisabledBrush` | → same | `SystemColorGrayTextColor` | Disabled |

Mapping to stock WinUI resources rather than redefining them is deliberate: it means the app inherits any future Fluent refinement for free, and the custom layer is only where the app genuinely differs. The custom names still exist so that a later change has one place to happen.

> **⚠ One text token is owned rather than inherited, decided 22 Aug 2026 (#176).**
>
> `WzTextTertiaryBrush` in **Light** is a literal `#8C000000` — 54.9% black — and not stock
> `TextFillColorTertiary`. Dark is unchanged and still inherits.
>
> **Why.** Stock tertiary is 45% black in Light, which measures **3.28 : 1** on the layer fill
> against §9.4.5's 4.5 : 1 floor. Fluent chose that value for de-emphasised, non-essential text;
> this application uses it as the standard caption colour in **117 places, 114 of them at 12 px**,
> which is a job it was not chosen for. The mismatch is this project's, so the fix is this
> project's too.
>
> **What it measures now**, composited over the opaque page background:
>
> | Surface | Was | Is |
> |---|---|---|
> | `WzLayerFillBrush` | 3.28 : 1 | **4.70 : 1** |
> | `WzCardFillBrush` | 3.29 : 1 | **4.72 : 1** |
> | `WzCardFillSecondaryBrush` | 3.30 : 1 | **4.68 : 1** |
> | `WzOverlayFillBrush` | 3.28 : 1 | **4.60 : 1** |
>
> **What it costs.** `WzTextSecondaryBrush` is 62% black, so the gap between secondary and
> tertiary narrows from 17 percentage points to 7. §9.5.2's three text levels survive and stay
> ordered, but they are closer together in Light than Fluent draws them. That is the price of the
> floor and it is paid knowingly: text nobody can read is not a hierarchy.
>
> **How it stays honest.** `build/Test-ContrastFloor.ps1` requires every other §9.4.5 text and
> surface token to resolve to a stock Fluent colour and fails the day one stops. This token is
> exempt from that check by name, in the gate's `$ownedTokens` table, carrying this issue number
> and this reason. Ownership is a decision someone made, never something that quietly happened.
> An owned token gets **no** contrast exemption — it still has to clear the floor, and does.

#### 9.4.2 Brand accent ramp

**Strategy: brand accent by default, system accent as an explicit opt-in** (Settings → Appearance → "Use my Windows accent colour", default off).

> **⚠ Amended 28 Aug 2026 (#45, OQ-D4 resolved).** The accent is **kept**: `#0E7C86`. OQ-D4 recorded the
> constraint to preserve as "≥ 60° hue separation from Windows blue and from every semantic colour"
> **without naming a colour space**, and the answer depends entirely on which one is meant:
>
> | Accent vs | HSV | **CIE LCH** |
> |---|---|---|
> | Windows blue `#0078D4` | 21° | **68°** |
> | `WzSuccess` light `#0F7B3C` | 40° | **61°** |
> | `WzSuccess` dark `#4CC38A` | 34° | **51°** |
> | `WzCaution`, `WzCritical` | 149–175° | 134–178° |
>
> **The separation is measured in CIE LCH**, which is the perceptual space and the one consistent with
> §9.4.4's use of ΔE₀₀ elsewhere in this document. Under HSV the constraint reads as failed against
> three colours; under LCH it holds against everything except `WzSuccess` in the dark theme, at 51°.
>
> **That one shortfall is accepted rather than overlooked.** The accent and the semantic palette never
> carry the same meaning — accent marks the safe thing to do next (§9.7.4), severity marks instrument
> state — and §9.4.3 requires severity to render as colour **plus shape plus text** everywhere it
> appears, so nothing depends on hue alone to separate them. §9.4.4's series-separation gate already
> enforces clearance between the *chart* palette and the severity colours, which is where a hue
> collision would actually mislead.
>
> Recorded because the figure was unmeasurable as written: a constraint stated in degrees with no
> space named cannot be checked, and the next person choosing a colour would have had this argument
> again from the start.

Rationale, and this is a hard constraint rather than a preference: the semantic palette (§9.4.3) must remain unambiguous. A user whose Windows accent is red would otherwise get an app where "selected navigation item" and "critical alarm" are the same colour. When the opt-in is enabled, the app substitutes `SystemAccentColor*` into the ramp **and** shows a one-time `TeachingTip` if the resolved accent falls within ΔE₀₀ < 20 of `WzCritical` or `WzCaution`, offering to revert. Semantic brushes are never derived from accent under any setting.

| Token key | Light theme | Dark theme | Use |
|---|---|---|---|
| `WzAccentDark3` | `#052F33` | `#052F33` | Pressed on light |
| `WzAccentDark2` | `#08474D` | `#08474D` | Hover on light |
| `WzAccentDark1` | `#0B6C74` | `#0B6C74` | **Light-theme base** |
| `WzAccentBase` | `#0E7C86` | `#0E7C86` | Reference hue |
| `WzAccentLight1` | `#189AA6` | `#189AA6` | Hover on dark |
| `WzAccentLight2` | `#3FB8C4` | `#3FB8C4` | **Dark-theme base** |
| `WzAccentLight3` | `#7FD4DC` | `#7FD4DC` | Pressed on dark, sparkline |
| `WzAccentFillBrush` | `WzAccentDark1` | `WzAccentLight2` | Resolved accent |

High contrast: `WzAccentFillBrush` → `SystemColorHighlightColor`, accent text → `SystemColorHighlightTextColor`.

Measured contrast (WCAG relative luminance, computed not estimated):

- `#0B6C74` on `#FFFFFF` → **6.16 : 1** ✓ passes AA for body text
- `#3FB8C4` on `#272727` → **6.31 : 1** ✓ passes AA for body text

Both exceed the 4.5:1 floor with margin, so accent-coloured text is permitted at body size. Accent as a *fill* behind white text: `#0B6C74` with `#FFFFFF` foreground = 6.16:1 ✓.

#### 9.4.3 Semantic colours and severity shapes

| Token | Light | Dark | Shape | Glyph | Meaning in this app |
|---|---|---|---|---|---|
| `WzSuccessBrush` | `#0F7B3C` | `#4CC38A` | ● circle | `\uE73E` CheckMark | Locked, valid, test passed |
| `WzCautionBrush` | `#8A5300` | `#F2B155` | ▲ triangle | `\uE7BA` Warning | Recovering, waiting, reduced accuracy, stale data |
| `WzCriticalBrush` | `#B22B2B` | `#FF6B6B` | ⬢ hexagon | `\uEA39` ErrorBadge | Holdover, hardware failure, disconnected with error |
| `WzInfoBrush` | `WzAccentDark1` | `WzAccentLight2` | ⬤ circle-i | `\uE946` Info | Neutral advisory, rollover notice |
| `WzNeutralBrush` | `#616161` | `#9A9A9A` | ○ ring | `\uE7BA`/none | Unknown, power-up, not applicable |

**Meaning is never carried by colour alone.** Every severity indication is a triple: **colour + shape + text label**. The shape channel is what makes the app usable under deuteranopia and protanopia, where `WzSuccessBrush` and `WzCriticalBrush` converge — a circle and a hexagon do not. This is implemented once in the `SeverityPill` control (§9.10) and every severity surface uses that control rather than hand-rolling a coloured dot.

The four severity shapes are drawn as `Path` geometry, not as glyphs from a font, so they render identically under high contrast where they resolve to `SystemColorWindowTextColor` outlines with a hairline fill distinction.

#### 9.4.4 Data visualisation palette

Charting colour is a separate concern from UI colour and must not reuse semantic tokens — a trace coloured `WzCriticalBrush` implies an alarm that is not being asserted.

**Categorical** (satellite traces, up to 8 series). Re-derived 22 Aug 2026 for #87 and #177, replacing a ramp that had been described as derived from Okabe–Ito:

| Index | Light | Dark |
|---|---|---|
| 1 | `#BD5572` rose | `#DD7F97` |
| 2 | `#B4684E` terracotta | `#E97E59` |
| 3 | `#766110` olive | `#EAC96A` |
| 4 | `#455530` moss | `#B5C79C` |
| 5 | `#109180` teal | `#07AE9A` |
| 6 | `#45849F` steel | `#7CB9D6` |
| 7 | `#085AA6` blue | `#719BEA` |
| 8 | `#4A4A4A` graphite | `#C4C4C4` |

Assign by index in a stable order (PRN ascending), never by hash — a satellite must keep its colour across sessions. A hue belongs to an index in **both** themes, so series 5 is the teal one wherever it is drawn; a satellite must not change identity when the desktop theme does.

> **⚠ Why this ramp replaced the Okabe–Ito one, 22 Aug 2026 (#87, #177).**
>
> **The old ramp was not Okabe–Ito.** Three of its eight entries — olive, violet and graphite — had been substituted for values that read better as thin lines. That was a defensible change, and it silently gave up the one property Okabe–Ito had been chosen for: **series 1 and 7 measured 4.5 ΔE₀₀ apart under deuteranopia**, which is not two colours. Two entries were also under §9.4.5's 3:1 chart-line floor. Both defects survived three months of review because nothing in the repository computed either number.
>
> **The two were solved together**, because they share the same eight values and fixing either alone re-breaks the other.
>
> | | old | this ramp |
> |---|---|---|
> | Light — worst pair, all three vision models | 2.8 | **10.6** |
> | Dark — worst pair | 3.1 | **10.5** |
> | Light — worst contrast, all four §9.4.1 surfaces | 2.23, two failing | **3.52, all passing** |
> | Dark — worst contrast | 2.73, one failing | **5.08, all passing** |
> | Closest approach to a §9.4.3 semantic colour | 6.4 | 10.2 |
>
> **The Light ramp is darker and more muted than the one it replaces, and that is forced.** A colour light enough to look bright on a near-white card cannot reach 3:1 against it — the two Light entries that were failing, `#56B4E9` and `#CC79A7`, failed *because* they were light. Any palette that clears the floor here is a palette of darker colours.

**Constraints this palette is held to.** Four, and three of them exist because a first attempt got them wrong:

1. **Every pair separates**, not merely adjacent entries — all 28, under normal vision, deuteranopia and protanopia, in both themes. Adjacency was the old wording and it is the wrong test: up to eight traces are on one chart *simultaneously*, so every pair has to hold.
2. **≥3:1 against the worst §9.4.1 surface**, not only against the card. The layer, secondary card and overlay differ by a few points of luminance, and a candidate cleared 3.50 on the card while sitting at 3.26 on the Light overlay.
3. **A minimum hue gap.** Maximising the smallest colour difference, on its own, produces a ramp with two browns and two purples separated by lightness. That satisfies the arithmetic and fails a person asked which trace is which; categorical colours have to be *nameable*.
4. **Clear of §9.4.3 by ≥10 ΔE₀₀** — the opening sentence of this section is a perceptual claim, not only a naming one, so it is measured. **The neutral series is exempt**: series 8 is grey and `WzNeutralBrush` is grey, and both mean *nothing is being asserted*.

**Sequential** — signal strength (C/N or SS). Single-hue teal ramp anchored on the brand:
`#DFF1F3` → `#A8DDE3` → `#6FC5CE` → `#3FB8C4` → `#189AA6` → `#0B6C74` → `#08474D`

Its adjacent steps measure low under simulation (4.4 ΔE₀₀ protanopia) and that is correct rather than a defect: a sequential ramp is read by lightness, and the simulated ramp stays monotonic. Neighbouring steps of a ramp are meant to be similar.

**Diverging** — 1 PPS time interval, zero-anchored (negative / zero / positive):
`#08474D` ← `#3FB8C4` ← `#DDE4E5` → `#F0A882` → `#B23A00`

The neutral midpoint must map to exactly 0 ns, not to the data midpoint. A TI chart whose colour break drifts with the data is misleading.

**The diverging ramp therefore applies only to an axis that contains zero**, which in practice means the 1 PPS chart alone. §10.7.1's oscillator-control chart is framed on its own data and does not reach zero, so there is nothing for the neutral midpoint to map to; anchoring it on the window mean instead would make the same colour break mean *"the receiver is on time"* on one chart and *"near where the oscillator has lately been"* on the other. That series takes a single stroke from the categorical palette above and carries **no colour-borne value at all** — its diagnostic content is entirely in the shape of the trace, which satisfies A11Y-12 by having nothing to encode.

**Verification.** `build/Test-SeriesSeparation.ps1` gates CI on the categorical palette: all 28 pairs, both themes, three vision models, plus the hue-gap and semantic-clearance rules above. `build/palette/` holds the derivation, and `build/palette/validate.py` checks the colour maths against the figures published on #87 before anything trusts it.

**HighContrast is not checked and cannot be.** Its series alternate between `SystemColorWindowTextColor` and `SystemColorHighlightColor` — the user's own two colours — so **eight traces cannot be separated by colour there at all.** A pass from the gate is not a statement that the chart is accessible. A second channel (dash pattern plus direct labelling) is required for HighContrast regardless of what this ramp contains, and is tracked on #87.

#### 9.4.5 Contrast floor

Enforced in every theme, no exceptions:

| Element | Minimum |
|---|---|
| Body text, labels, units | **4.5 : 1** |
| Text ≥ 18.66 px semibold or ≥ 24 px regular | **3 : 1** |
| Non-text UI: strokes, icons carrying meaning, chart lines, medallion ring | **3 : 1** |
| Focus visual against both its adjacent surfaces | **3 : 1** |

> **⚠ Two clarifications from measurement, 21 Aug 2026 (#24).**
>
> **Disabled text is exempt.** WCAG 1.4.3 excludes text in an inactive control, and a disabled
> field meeting the same floor as an enabled one stops reading as disabled — the reduced contrast
> *is* the affordance. `WzTextDisabledBrush` is therefore not checked.
>
> **"Strokes" is broader here than WCAG is, and the gate follows WCAG.** This row lists strokes
> under the 3:1 non-text floor without qualification, but §9.4.1 maps `WzStrokeSubtleBrush` and
> `WzStrokeDefaultBrush` onto stock Fluent hairlines that measure **1.06:1 to 1.24:1** — they are
> 6% black by design. WCAG 1.4.11 applies to UI components and *meaningful* graphics, and a card
> border that is nobody’s only indicator of anything is neither. Taken literally this row would
> require abandoning Fluent’s card styling wholesale. **Unresolved rather than quietly dropped:**
> the gate checks meaningful non-text — severity colours, chart series — and does not check
> decorative strokes. If a stroke ever becomes the sole carrier of meaning, it moves into scope.

### 9.5 Typography

#### 9.5.1 Faces

| Role | Face | Fallback chain | Notes |
|---|---|---|---|
| UI and prose | **Segoe UI Variable Text** | Segoe UI Variable → Segoe UI → system default | Optical size handles small text; do not use Display below 18 px |
| Headings ≥ 20 px | **Segoe UI Variable Display** | as above | |
| Numeric readouts | **Segoe UI Variable Display**, tabular figures | as above | §9.5.3 |
| Device-literal text | **Cascadia Mono** | Consolas → Courier New | SCPI mnemonics, transcript, raw register values, log entries, `*IDN?` string |

**Cascadia Mono licensing and packaging.** Cascadia is SIL OFL 1.1, which permits redistribution in the MSIX. It ships with Windows Terminal (inbox on Windows 11, not guaranteed on Windows 10), so it must be **embedded in the package** and referenced as `ms-appx:///Assets/Fonts/CascadiaMono.ttf#Cascadia Mono` rather than assumed present. Include the OFL licence text in the third-party notices. Consolas is the fallback and is present on all supported versions.

No display or brand face is introduced. The type personality comes from the **Segoe / Cascadia split** and from the readout scale, not from a third family. A decorative face here would fight the instrument thesis.

#### 9.5.2 Ramp

| Token key | Face | Size / line height | Weight | Tracking | Use |
|---|---|---|---|---|---|
| `WzCaptionTextStyle` | Variable Text | 12 / 16 | Regular | 0 | Timestamps, units, footers, helper text |
| `WzBodyTextStyle` | Variable Text | 14 / 20 | Regular | 0 | Default UI text, prose |
| `WzBodyStrongTextStyle` | Variable Text | 14 / 20 | Semibold | 0 | Field labels, table headers, emphasis |
| `WzSubtitleTextStyle` | Variable Display | 20 / 28 | Semibold | 0 | Card headers |
| `WzTitleTextStyle` | Variable Display | 28 / 36 | Semibold | −0.01 em | Page headers |
| `WzTitleLargeTextStyle` | Variable Display | 40 / 52 | Semibold | −0.015 em | Empty-state headline |
| `WzDisplayTextStyle` | Variable Display | 68 / 80 | Semibold | −0.02 em | Reserved; not used in v1 |

Each maps to a WinUI stock style where one exists (`BodyTextBlockStyle`, `SubtitleTextBlockStyle`, `TitleTextBlockStyle`) with only the documented deltas applied.

**`WzCaptionTextStyle` is small text, and that decides its colour.** At 12 px it is under every
threshold in §9.4.5's 3 : 1 row, so it carries the 4.5 : 1 floor wherever it appears — including
the timestamps, units and helper text it is named for. That floor, not appearance, is why
`WzTextTertiaryBrush` is a value this project owns in Light rather than one it inherits; §9.4.1
records the measurement and the cost. **Caption text may still be tertiary.** The point of owning
the token was to keep it legible at that size, so nothing here needs moving to secondary.

#### 9.5.3 Numeric and tabular treatment

This is the part that separates a careful instrument app from a sloppy one, and it is where most data-dense Windows apps fail.

| Token key | Size / line height | Weight | Use |
|---|---|---|---|
| `WzReadoutLargeTextStyle` | 56 / 56 | Semibold | Medallion centre value, satellite count |
| `WzReadoutMediumTextStyle` | 32 / 36 | Semibold | TFOM, FFOM, 1 PPS TI, EFC % |
| `WzReadoutSmallTextStyle` | 20 / 24 | Semibold | Card-level figures, table numerics |
| `WzMonoTextStyle` | Cascadia Mono 13 / 18 | Regular | Transcript, SCPI strings, log lines, raw register values |

**Rules, all verifiable by inspection:**

1. **Tabular figures everywhere a number can change in place.** Set `Typography.NumeralAlignment="Tabular"` on every readout and every numeric column. Without this, a value stepping from `-33.1` to `-9.8` shifts horizontally and, at a glance across a bench, reads as motion where there is none. See OQ-D1 for verification of this attached property in WinUI 3.
2. **Numerals never reflow.** Reserve width for the maximum expected string, including sign. A field that can show `-999.9` reserves six characters even while showing `0.0`.
3. **Units are typeset, not concatenated.** The unit is a separate `Run` in `WzCaptionTextStyle` / `WzTextSecondaryBrush`, separated by a hair space (`\u200A`), never bolded, never part of the numeric string. `−33.1` is `WzReadoutMedium`; ` ns` is caption-secondary.
4. **Minus sign, not hyphen.** Use U+2212 MINUS SIGN in readouts. A hyphen is optically too short and sits too high next to lining figures. Format with a custom `NumberFormatInfo` where `NegativeSign = "\u2212"`. Raw SCPI text in `WzMonoTextStyle` is exempt — it is reproduced verbatim.
5. **Right-align numeric table columns**, left-align text columns, and align on the decimal separator where fractional digits vary.
6. **Fixed decimal places per quantity**, never variable: TI 1 dp, EFC 1 dp, holdover uncertainty 1 dp, C/N integer, percentages 1 dp, **EFC drift rate, scatter and diurnal amplitude 1 dp in ppm of control range** (§10.7.1). A column that changes its precision row to row is unreadable. Where a figure is too small to survive its quantity's precision, **change the unit, not the number of decimals** — the rule is about a reader comparing two lines, and it does not stop applying because a surface is prose. Chart axis labels take the precision their step requires, fixed per chart and not varying with the selected range (§9.10.2).
7. **Coordinates in Cascadia Mono**, DMS with fixed field widths, so latitude and longitude align vertically.

**Prose line length** is capped at **72 characters** (`MaxWidth` ≈ 640 px at `WzBodyTextStyle`). Applies to descriptions, confirmation dialog bodies, empty states, and error text.

**Sentence case is the default for all UI text** — buttons, labels, headers, menu items, nav items, column headers, dialog titles. Title Case is used only for proper nouns and for the app name. `PRN`, `TFOM`, `FFOM`, `GPS`, `UTC`, `SCPI`, `EFC` stay uppercase as domain terms.

### 9.6 Layout, spacing, and density

**Base unit: 4 px.** Every spacing value in the app is drawn from this scale and nothing else:

| Token key | Value | Typical use |
|---|---|---|
| `WzSpaceXxs` | 4 | Icon-to-label, inline chip padding |
| `WzSpaceXs` | 8 | Related control gap, table cell padding |
| `WzSpaceSm` | 12 | Label-to-field, list item vertical padding |
| `WzSpaceMd` | 16 | Card internal padding, control group gap |
| `WzSpaceLg` | 20 | Feature card internal padding |
| `WzSpaceXl` | 24 | Page margin, gap between cards |
| `WzSpaceXxl` | 32 | Section gap |
| `WzSpace3Xl` | 40 | Page header to first card |
| `WzSpace4Xl` | 48 | Empty-state vertical rhythm |

Page margin `WzSpaceXl` (24) at Medium and Wide, `WzSpaceMd` (16) at Compact.

**Content max-width: 1320 px.** Beyond that the content region centres in the available space rather than gaining columns. Rationale: the satellite table and the trend charts are the only things that benefit from extra width, and both have a natural maximum useful size. Stretching cards to 2400 px on an ultrawide produces label-value pairs separated by a hand's width — measurably worse for scanning. Charts *within* the 1320 grid do stretch to fill their column.

#### 9.6.1 Breakpoints

| Name | Width | NavigationView | Content grid | Behaviour |
|---|---|---|---|---|
| **Minimal** | < 640 | `Minimal` (hamburger; the pane opens as an overlay over the content) | 1 column | As Compact, except that the pane is not persistently visible and every destination is reached through the hamburger. Not reachable by dragging a window: it exists because §9.6.2's minimum is a *content* size and the work-area clamp may lower it below 640 at high display scaling. |
| **Compact** | 640 – 1023 | `LeftCompact` (48 px icon rail, flyout on expand) | 1 column | Sky plot and satellite table stack vertically; the plot caps at 360 px. Inspectors become full-width `ContentDialog`. Table columns beyond PRN / El / Az / signal collapse into an expander per row. |
| **Medium** | 1024 – 1439 | `Left` (pane 260 px, user-collapsible) | 2 columns | Default target. Sky plot beside table. |
| **Wide** | ≥ 1440 | `Left` | 2 columns within 1320 max-width, centred | A third inspector column may appear on the Satellites page for per-satellite history; it is an addition, not a redistribution. |

`NavigationView` is set to `Auto` display mode with these thresholds configured via `CompactModeThresholdWidth="640"` and `ExpandedModeThresholdWidth="1024"` — do not hand-roll breakpoint logic where the control already implements it. `Minimal` is what `Auto` selects below the compact threshold; the row above states what the app expects there rather than adding logic.

> **⚠ The Minimal row was added after the fact, and A11Y-7 is why.** §9.6.1 previously began at 640
> and §9.6.2 sets the Details minimum at 1024, so Compact was the narrowest state the document
> described and — read as a window size — the narrowest reachable. Reading the minimum as *content*
> (below) and capping it at the display work area both changed that: at 200 % scaling on a
> 1920-wide display the Details window has 1920 physical pixels to spend and about 960 effective
> pixels of content, and at 225 % about 853. A11Y-7 requires the application to be usable at 225 %
> (amended 28 Aug 2026, #27 — it was 350 %, and the arithmetic below is left at that figure because
> the clamping it justifies is unchanged), so a state below 640 is not an edge case to be excluded —
> it is the state A11Y-7 is about. The
> alternative considered and rejected was to floor the clamp at 640 effective pixels, which on a
> 1920-wide display puts the enforced minimum back above the screen at the scaling this is for.

#### 9.6.2 Minimum window sizes

**Every figure in this table is a size of _content_, in effective pixels** — the same units as
§9.6.1's breakpoints, and *not* the units `OverlappedPresenter.PreferredMinimumWidth/Height` takes.
See the amendment below the table before implementing one of them.

| Window | Minimum (content) | Behaviour at minimum |
|---|---|---|
| Main | 380 × 240 | Medallion 160 px and the two mode lines. The readout row, the figures of merit, the clock line and the footer are **collapsed** — not clipped, not scrolled: they are removed from the layout, so nothing is focusable or hit-testable off-screen (A11Y-1, A11Y-6). |
| Main, compact mode | 380 × 144 | Medallion 64 px, mode text and satellite count only; readout row and footer hidden |
| Receiver Details | **1024 × 720** | Medium breakpoint; `NavigationView` in `Left` mode; no horizontal scrolling |

> **⚠ Amends §10.2.** The Details window minimum was previously specified as 1000 × 700. That sits 24 px below the `Left`-mode threshold, so the window would open in `LeftCompact` at its own minimum size — the pane would be an icon rail at the exact width the layout was designed around. Raised to **1024 × 720** so the default state is the Medium breakpoint. Enforced via `AppWindow` `OverlappedPresenter.PreferredMinimumWidth/Height`.

> **⚠ Amends this section: the figures are content sizes, and the mechanism is not the unit.**
> Every number here was previously ambiguous between a window size and a content size, and the
> sentence naming `PreferredMinimumWidth/Height` settled it the wrong way — that property is a
> **window** size in **physical** pixels, while §9.6.1's thresholds, which the same numbers are
> chosen against, are **content** sizes in **effective** pixels. The two coincide only at 100 %
> scaling on a window with no chrome, and neither condition ever holds.
>
> Read literally the table contradicted itself. A 1024 px *window* has a 1008 px client area, so
> the Details window opened in `LeftCompact` at its own minimum — the exact failure the amendment
> above raised the number to prevent, in its own words. And at 150 % a 1024 physical window carries
> 683 effective pixels of content, at 350 % just 293, so the `Left` mode this table promises could
> not be reached at any scaling above 100 %. The main window was worse: 380 physical is 253
> effective at 150 % and 109 at 350 %, so its floor shrank with every step of the user's scaling
> setting — and at 100 % its client area was about 364 px against the 380 §10.3's wireframe is
> drawn from.
>
> **The implementation therefore converts.** A figure in this table is multiplied by the window's
> `RasterizationScale` and has the window's measured non-client chrome added, and the result is what
> `PreferredMinimumWidth/Height` receives. The conversion is recomputed whenever the scaling under
> the window changes or the window moves to another display. `PreferredMinimumWidth/Height` remains
> the mechanism; it is not the unit.
>
> **The converted floor is then capped at the work area of the display the window is on.** At 350 %
> the Details minimum needs 3600 × 2528 physical pixels, which is larger than any display it would
> be read on: the window opens wider than the screen, its resize edges are past the desktop, and no
> gesture brings it back. Capping gives up the breakpoint rather than the window, and it is why
> §9.6.1 now describes a state below 640.
>
> **Compact mode is 144, not 120.** Under the content reading its own contents no longer fit: a
> 32 px title bar inside the client area, `WzSpaceXl` (24) of page margin, §9.10.2's 64 px medallion
> and 24 more of margin come to 144. Nothing may be dropped to reach 120 — the compact layout is
> already down to a medallion and one line of text, §9.6.3 forbids reducing the margin as a density
> change, and §9.10.2 sets the medallion at 64. The figure was raised rather than the layout cut.
>
> **The main window's row was rewritten to describe what the window does.** It previously read
> "medallion 96 px, one readout row, footer wraps to two lines", none of which was ever built:
> §10.3's own wireframe draws the medallion at 160, no size-driven state existed, and the footer is
> `NoWrap`. Measured on the built layout, the footer's Connect button does not reach §9.6.3's 32 px
> pointer floor until **472** effective pixels of content height — nearly twice this table's figure
> — so at 240 the readout row and footer were simply clipped out of sight while remaining in the
> visual tree. That is an A11Y-1 and A11Y-6 defect rather than a layout: a focusable button the user
> cannot see or reach. The rows are now collapsed at that threshold and the table says so.

#### 9.6.3 Density

There is **one density**. No compact/comfortable toggle.

Justification: the app has no long homogeneous lists where density pays. The satellite table is capped at 32 rows, and the diagnostic log is the only other list. A density switch would double the layout test matrix for a benefit that does not exist here, and the ambient-legibility thesis argues against tighter text regardless.

The main window's **compact mode** (§10.3) is a different thing — a reduced *content* mode, not a reduced *density* mode. Type sizes, touch targets, and focus visuals are identical in both.

Fixed floors that no mode may reduce:

- Pointer target ≥ **32 × 32** px
- Touch target ≥ **40 × 40** px
- Focus visual stroke ≥ **2** px, never clipped by a parent

### 9.7 Navigation, structure, and commanding

**Top-level pattern: `NavigationView`, left.** Justified over the alternatives:

- `TabView` is wrong — these are destinations, not user-created documents. Nothing here is closeable or reorderable.
- `NavigationView` Top would put nine items in a horizontal strip that overflows at the Compact breakpoint, and would compete with the custom title bar for the top band.
- A custom shell buys nothing. The design differentiation is in the medallion and the readouts, not in reinventing navigation chrome — and a custom shell would forfeit the free accessibility and breakpoint behaviour.

Pane is **user-collapsible**, state persisted. Selection is rendered with the WinUI stock indicator (a 3 px accent pill on the leading edge) plus `WzBodyStrongTextStyle` on the selected label — colour plus weight, per §9.4.3.

**Hierarchy depth is capped at two.** Window → page. Anything that would be a third level is a dialog or an inline expander, never a nested page. There are therefore no breadcrumbs, and each page carries a `WzTitleTextStyle` header in the content region that names the destination. A user always knows where they are because there is only one place to be.

#### 9.7.1 Shell wireframe — Medium breakpoint

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ ⬤ WinZ3805A    [● Locked · COM3]        ⟳  ⇱  ⚙       ─  □  ✕     │  48 px custom title bar
├──────────────┬──────────────────────────────────────────────────────────────┤  ← WzStrokeSubtleBrush
│              │                                                              │
│  ☰           │   Satellites                                     ← Title      │  WzSpace3Xl (40) below
│              │                                                              │
│  ⌂ Overview  │   ┌──────────────────────┐  ┌─────────────────────────────┐  │
│  ⊕ Satellites│   │                      │  │                             │  │  L2 cards
│  ⌖ Position  │   │      sky plot        │  │      tracked table          │  │  8 px radius
│  ⏱ Timing    │   │                      │  │                             │  │  16 px padding
│  ⏸ Holdover  │   └──────────────────────┘  └─────────────────────────────┘  │
│  ◷ Time      │            ↑ WzSpaceXl (24) column gap                      │
│  ▤ Registers │   ┌────────────────────────────────────────────────────────┐ │
│  ⚕ Diagnostic│   │            elevation mask control                      │ │
│              │   └────────────────────────────────────────────────────────┘ │
│              │                                                              │
│  ⚙ Settings  │                                                              │
│              │                                                              │
└──────────────┴──────────────────────────────────────────────────────────────┘
  260 px pane      24 px page margin, content max-width 1320
```

#### 9.7.2 Shell wireframe — Compact breakpoint

```
┌───────────────────────────────────────────────────┐
│ ⬤ WinZ3805A  [● Locked]  ⟳ ⚙   ─ □ ✕     │
├────┬──────────────────────────────────────────────┤
│ ☰  │  Satellites                                  │
│    │                                              │
│ ⌂  │  ┌────────────────────────────────────────┐  │
│ ⊕  │  │           sky plot  (max 360)          │  │
│ ⌖  │  └────────────────────────────────────────┘  │
│ ⏱  │  ┌────────────────────────────────────────┐  │
│ ⏸  │  │           tracked table                │  │
│ ◷  │  │  PRN  El  Az  C/N              ▸       │  │  ← extra cols in row expander
│ ▤  │  └────────────────────────────────────────┘  │
│ ⚕  │                                              │
│ ⚙  │                                              │
└────┴──────────────────────────────────────────────┘
 48 px rail    16 px page margin
```

#### 9.7.3 Title bar

**Custom, extended into the client area.** `ExtendsContentIntoTitleBar = true`, `SetTitleBar()` on the drag host.

| Property | Main window | Details window |
|---|---|---|
| Height | 32 px | 48 px |
| Left content | App icon 16 px + app name `WzCaptionTextStyle` | Icon + name |
| Centre content | none | `ConnectionStatusPill` (§9.10) |
| Right content | none | Refresh, Export, Settings icon buttons |
| Drag regions | Full bar minus interactive elements | Same |

Rules:

- **Caption button clearance is read, never hardcoded.** A hardcoded 138 px breaks in RTL and at non-100% scaling.
- **Interactive elements inside the bar must be excluded from the drag region.**
- **The compliant path is the WinUI `TitleBar` control**, which satisfies both of the above itself: it reads `AppWindowTitleBar.RightInset`/`LeftInset` internally, and content placed in its `Content`, `LeftHeader` and `RightHeader` slots is interactive without the caller managing passthrough regions. Use it, and set no caption clearance of your own.
  > **⚠ Amended 27 Aug 2026 (#226).** This rule previously *instructed* the reader to call `AppWindowTitleBar.RightInset`/`LeftInset` and `InputNonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough, …)`. Neither is called anywhere in `src/`, deliberately, because the `TitleBar` control subsumes them — and a reviewer following the old wording literally would have "fixed" the title bar by adding the hardcoded inset arithmetic the same paragraph forbids. The requirement is unchanged; only the named technique moved.
- **Where a window ever needs a custom bar the `TitleBar` control cannot express**, the manual route is `AppWindowTitleBar.RightInset`/`LeftInset` for clearance and `InputNonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough, …)` for interactive regions. No window needs it today.
- **Inactive state:** title bar *text and icons* drop to `WzTextTertiaryBrush`; caption buttons follow the system. The `ConnectionStatusPill` **does not dim** — its whole job is to be true at a glance, and a deactivated window is exactly when someone is glancing at it from across the room.
- Nothing else is permitted in the bar. No search, no tabs, no breadcrumbs.

#### 9.7.4 Command model

| Placement | Contains |
|---|---|
| Title bar (Details) | Refresh full status (`F5`), Export current view (`Ctrl+E`), Settings (`Ctrl+,`) |
| Card header, inline | Commands scoped to that card: *Run test*, *Manage…*, *Clear*, *Apply* |
| Inline with the field it affects | All *Apply* buttons — never in a page-level bar, so the affected values are always adjacent |
| Right-click only | Copy value, copy as CSV on tables. Nothing unique lives here. |
| Menu bar | **None.** Two-level hierarchy does not need one. |

No page-level `CommandBar`. The commands are card-scoped and a page-level bar would separate them from their context.

**Destructive commands** — every tier C command from §8.3 — use `WzDestructiveButtonStyle`: `WzCriticalBrush` foreground, `WzStrokeDefaultBrush` border, transparent fill, leading `\uE7BA` Warning glyph. **Never `AccentButtonStyle`.** Accent means "the safe thing to do next."

> **⚠ Amends §8.3.** The earlier text read "destructive action as the secondary button styled `AccentButtonStyle` only where safe," which is ambiguous and half-wrong. Replaced with a single rule: in a tier C `ContentDialog`, the destructive action is the **PrimaryButton** styled `WzDestructiveButtonStyle`; **Cancel is the CloseButton and is `DefaultButton`**, so Enter and initial focus land on the safe option. For the strong variants (`:SYST:PRESet`, `:SYNC:HOLD:INITiate`, `:GPS:SAT:TRAC:IGN:ALL`, `:GPS:SAT:TRAC:INCL:NONE`) a `CheckBox` gates the PrimaryButton's `IsEnabled`. Escape always cancels.

#### 9.7.5 Keyboard accelerators

| Accelerator | Command |
|---|---|
| `Ctrl+Shift+C` | Connect / disconnect |
| `Ctrl+D` | Open Receiver details |
| `F5` | Refresh full status now |
| `Ctrl+1` … `Ctrl+9` | Jump to nav destination 1–9. There is no `Ctrl+10`: §10.2's cap is twelve destinations but only the first nine can carry an accelerator, so the pane's order decides which are one keystroke away. |
| `Ctrl+E` | Export current view |
| `Ctrl+,` | Settings |
| `Ctrl+Shift+M` | Toggle main window compact mode |
| `F1` | About |
| `Esc` | Cancel dialog, close flyout, exit compact mode |

Declared as `KeyboardAccelerator` on the command, with `KeyboardAcceleratorPlacementMode="Auto"` so the shortcut renders in the tooltip automatically. Icon-only buttons must show accelerator text in their tooltip.

### 9.8 Motion

> **Motion philosophy.** Motion explains where a thing came from. It never announces that something happened, and it never touches a measurement.

#### 9.8.1 Tokens

| Token key | Value | Use |
|---|---|---|
| `WzDurationInstant` | 0 ms | Readout value changes, medallion ring redraw, severity state changes |
| `WzDurationFast` | 150 ms | Hover, pressed, checked, focus visual |
| `WzDurationNormal` | 250 ms | Page transition, expand/collapse, `InfoBar` in/out, flyout |
| `WzDurationSlow` | 400 ms | Window first-run entrance only |

| Easing token | KeySpline | Use |
|---|---|---|
| `WzEaseStandard` | `0.8,0 0.2,1` | Two-way transitions, expand/collapse |
| `WzEaseDecelerate` | `0.1,0.9 0.2,1` | Entrances |
| `WzEaseAccelerate` | `0.7,0 1,0.5` | Exits |

#### 9.8.2 Motion spec

| Moment | Animation | Duration | Easing | Reduced-motion fallback |
|---|---|---|---|---|
| Nav page change | `SlideNavigationTransitionInfo` with `FromBottom` — **one direction, always** | `Normal` | `WzEaseStandard` | `SuppressNavigationTransitionInfo` |

> **⚠ Corrected 21 Aug 2026 (#120).** This row named two APIs the Windows App SDK does not have, failing in opposite directions. **`SlideNavigationTransitionEffect` has no `FromTop`** — it never has, in UWP or WinUI — so the "by index direction" rule was half-unbuildable, and the two effects that do exist besides `FromBottom` are horizontal, which this section’s own *Directional consistency* paragraph forbids four lines further down. So the transition is one direction, always. **`EntranceNavigationTransitionInfo` cannot be reduced to opacity**: WinUI 3 dropped the `FromHorizontalOffset` and `FromVerticalOffset` properties UWP carried, leaving nothing to zero. That one is the worse of the two — it compiles, and then animates anyway, which is the opposite of what a reduced-motion fallback is for. `SuppressNavigationTransitionInfo` is what actually suppresses motion.
| Main → Details window | `ConnectedAnimation` on the medallion → Overview medallion | `Normal` | `WzEaseDecelerate` | No connected animation; Details opens directly |
| Card enter on page load | Implicit show, opacity + 8 px translate up, 30 ms stagger, max 4 cards | `Normal` | `WzEaseDecelerate` | Opacity only, no stagger, no translate |
| `Expander` toggle | Stock expand/collapse | `Normal` | `WzEaseStandard` | Instant height change |
| `InfoBar` appear / dismiss | Height + opacity | `Normal` | `WzEaseDecelerate` / `WzEaseAccelerate` | Instant |
| `ContentDialog` | Stock | stock | stock | Stock reduced behaviour |
| Satellite row reorder | `ItemsRepeater` implicit reposition, offset only | `Fast` | `WzEaseStandard` | Instant reposition |
| Hover / pressed / focus | Brush + scale (pressed 0.98) | `Fast` | `WzEaseStandard` | Brush change only, no scale |
| Loading > 500 ms | `ProgressRing` indeterminate | — | — | `ProgressRing` continues; it is status, not decoration |
| **Readout value change** | **none** | `Instant` | — | Same — already instant |
| **Medallion ring redraw** | **none** | `Instant` | — | Same |
| **Severity state change** | **none** | `Instant` | — | Same |

**Directional consistency.** Navigation is a vertical list, so page transitions move vertically and in the direction of travel in that list. Flyouts and dialogs scale from their invoking control. Nothing slides horizontally anywhere in the app, because nothing in the navigation model is horizontal.

**Reduced motion is mandatory, not best-effort.** Read `UISettings.AnimationsEnabled` at startup and subscribe to changes. Expose it as `WzMotionService.AnimationsEnabled` and bind `Storyboard`/transition selection to it. Every fallback above is a cross-fade or an instant state change — **no fallback may produce a different layout**, only a different path to the same layout.

The three `Instant` rows are the thesis made testable: a reviewer can confirm that no `Storyboard`, `ImplicitAnimation`, or `NumberAnimation` targets a readout `Text` property, a medallion geometry, or a severity brush.

### 9.9 Iconography and imagery

**Baseline: Segoe Fluent Icons** (`\uEnnn`), present on Windows 10 1809+. Use stock glyphs wherever one exists.

**Custom icons.** Four concepts have no adequate Fluent glyph and are drawn as `PathIcon` geometry:

| Icon | Used for |
|---|---|
| Satellite | Satellites nav item, tracked-count readout |
| Sky plot | Sky plot card header, view toggle |
| Oscillator (sine in a rounded square) | EFC card, oscillator health |
| Holdover (pause inside a clock face) | Holdover nav item and state |

Construction rules:

- Designed on a **16 × 16 grid** with a 1 px safe margin, scaled by `Viewbox` to 20 and 24.
- **1.5 px stroke at 16 px**, scaling proportionally. Matches Fluent's optical weight so custom and stock icons in the same nav list do not read as two sets.
- Terminals and joins rounded, 0.75 px radius.
- **Monochrome only**, filled with `{ThemeResource}` so they recolour with theme and high contrast. No multi-colour icons anywhere.
- Optically centred, not mathematically — a circular glyph is drawn ~2% larger than a square one to match perceived size.

| Context | Size |
|---|---|
| Inline with body text, table cells | 16 |
| `NavigationView` items, buttons | 20 |
| Card headers, `InfoBar` | 24 |
| Empty state | 32 |
| Medallion centre glyph | 40 (compact) / 56 (standard) |

**An icon may appear without a visible label only when** it is in the title bar or a card header command position, has a `ToolTip` containing the label and accelerator, and has an `AutomationProperties.Name`. All three, no exceptions. Nav items always carry labels in `Left` mode and expose them via tooltip in `LeftCompact`.

**Illustration: none.** Empty and first-run states are set in type and a single 32 px icon. A house illustration style would need light, dark, and high-contrast variants, would date faster than the rest of the UI, and would sit oddly in an app whose thesis is instrument restraint. A well-set `WzTitleLargeTextStyle` headline over `WzBodyTextStyle` guidance and a primary action is better here and cheaper to maintain.

### 9.10 Component inventory

#### 9.10.1 Stock WinUI controls

| Surface | Controls |
|---|---|
| Shell | `NavigationView`, `Frame`, `AppWindowTitleBar` (custom), `MicaBackdrop` |
| Cards | `Border` with L2 treatment; `Expander` for collapsible sections |
| Settings-style rows | `SettingsCard`, `SettingsExpander` (WinUI Community Toolkit) — Timing, Holdover, Settings pages |
| Tables | `ItemsRepeater` inside `ScrollViewer` with a sticky header row. **Not `DataGrid`** — the Community Toolkit `DataGrid` is heavier than needed and its default styling is hard to bring in line with these tokens for a ≤ 32-row table. |
| Status messaging | `InfoBar`, `TeachingTip`, `ContentDialog` — selection rules in §9.11 |
| Inputs | `NumberBox` (all numeric entry, `SpinButtonPlacementMode="Compact"`, `ValidationMode="InvalidInputOverwritten"`), `ComboBox`, `ToggleSwitch`, `Slider` (elevation mask only), `CheckBox` |
| Progress | `ProgressRing` (indeterminate), `ProgressBar` (survey percentage — determinate and meaningful) |
| Commands | `Button`, `HyperlinkButton`, `DropDownButton`, `MenuFlyout` |

#### 9.10.2 Custom and templated controls

| Control | Must do |
|---|---|
| **`StatusMedallion`** | Circular. Centre: mode glyph + optional value. Ring: 60-sample radial sparkline of 1 PPS TI, redrawn on each fast poll with **no animation**, autoscaled to ±3σ of the window with a fixed floor of ±50 ns so a calm loop does not amplify into noise. Ring colour = current severity token. Sizes 64 / 96 / 160 px. Exposes `AutomationProperties.Name` as a full sentence, e.g. *"Locked to GPS, stabilising frequency, 6 satellites tracked, time interval −33.1 nanoseconds."* Ring is decorative to assistive tech (`AccessibilityView="Raw"`) since the numeric value is announced. Under high contrast: ring drawn as a 2 px `SystemColorWindowTextColor` stroke, severity conveyed by the glyph and the accompanying label. |
| **`ReadoutTile`** | Label + value + unit + optional severity. Enforces §9.5.3 rules 1–4 and 6 so no page can get numeric typesetting wrong locally. Width reserved from a `MaxDigits` property. |
| **`SeverityPill`** | Colour + `Path` shape + text. The *only* way severity is rendered anywhere in the app. Takes a `Severity` enum, never a brush. |
| **`SkyPlotControl`** | Polar plot per §10.5. North up, 0° elevation at rim, 90° at centre. Dashed elevation-mask circle. Marker area scales with signal strength; fill from the sequential ramp (§9.4.4). Keyboard: arrow keys move a focus ring between markers in PRN order, Enter selects. Exposes each marker as an automation peer with name *"PRN 19, elevation 65 degrees, azimuth 52 degrees, carrier to noise 49, tracked."* Provides a `ListView` alternate view toggle for users who cannot use the spatial form. |
| **`SatelliteStrengthBar`** | Bar + numeric. **Scale-aware**: reads `SignalStrengthKind` and maps C/N (26–55) or SS (0–255) to its own domain. Must never render the two scales against a shared axis. |
| **`ConnectionStatusPill`** | Title-bar element. Severity shape + state text + port name. Click opens the connection dialog. Does not dim on window deactivation. |
| **`TrendChart`** | Fallback if OQ-5 rejects LiveCharts. Line series, time x-axis, range selector. **Two y-axis modes:** zero-anchored with the §9.4.4 diverging fill for TI, and framed on the window's own data with a minimum span and a single categorical stroke for EFC (§10.7.1). Axis carries three labels — the two bounds and the midpoint — snapped to a round step, at a decimal precision fixed per chart (§9.11 item 6). Must support 604 800 points via decimation without dropping excursions — decimate by min/max per pixel column, never by sampling, or a 1-second glitch vanishes at the 7-day range. |

**`SkyPlotControl` is A11Y-5’s one recorded exception, and the specification names the compliant path (#117).**
A marker is 8–18 px across and **its position is the data** — it is the satellite’s actual position in the sky and
cannot be moved to make room. Growing the hit area past a point stops helping and starts hurting: on a 360 px plot,
a 32 px target covers roughly 8° of projected sky and satellites routinely sit closer than that, so the failure mode
becomes **silently selecting the wrong satellite** — worse than a small target, because a missed click is obvious and
a wrong selection is not. This is the case WCAG 2.5.8 carves out as *essential*: "a particular presentation of the
target is essential to the information being conveyed".

What is required instead:

- The visible disc is inset inside a **24 × 24 px transparent hit area** — about four times the disc’s own clickable
  area, without reaching a neighbour under roughly 6° away.
- **The keyboard model reaches every satellite regardless of size.** The plot is one tab stop, arrow keys move a ring
  through markers in PRN order, Enter selects. No pointer precision is involved.
- **The tracked/not-tracked tables are the compliant path**, and A11Y-11 is their acceptance criterion. Their rows are
  full-width and **at least 40 px**, they carry the same data, and selection is shared both ways.

> **⚠ This bullet was false when it was written, and the number is now enforced rather than asserted.**
> `WzDenseListItemStyle` set `ListViewItem.MinHeight` to 0 to escape the stock 40, and the rows
> measured **26–28 px** — under A11Y-5's 32 px *pointer* floor, on a row that is itself a selection
> target. The sky plot's exception therefore rested on an alternate that was no more compliant than
> the thing it was excusing. The floor was restored to 32 on 24 Aug 2026 (#25), which fixed A11Y-5
> for the tables but left this sentence untrue by 8 px, and to **40** with this amendment.
>
> 40 is A11Y-5's *touch* floor, and #186 records that the application has no touch-optimised mode
> for it to apply to. It is met here deliberately: these tables are the compliant alternate to a
> control that can meet neither floor, so the argument above should rest on the stronger number
> rather than on the weaker one it happens to need. `MinHeight` is a floor and not a fixed height,
> so rows still grow with scaled text (A11Y-6).

The Accessibility Insights target-size check **will flag these markers**. This paragraph is the answer to that flag,
not a claim that it is wrong to raise it. If the deviation is ever judged unacceptable, the fix is not a bigger marker
— it is promoting P1-12’s explicit list/plot toggle out of P1, so a user can turn the spatial form off entirely.

### 9.11 State matrix

| State | Surface | Copy pattern | Notes |
|---|---|---|---|
| **First run** | Full-page centred: 32 px icon, `WzTitleLargeTextStyle` headline, one line of `WzBodyTextStyle`, primary button | "Connect your receiver" / "This app talks to HP and Symmetricom GPS receivers over a serial port. Pick the port your receiver is on to begin." / **Choose a port** | No tour, no carousel, no dismissible tips |
| **Empty** (e.g. no diagnostic log entries) | In-card centred: 32 px icon, `WzBodyStrongTextStyle` line, optional action | "No log entries yet. The receiver records power-on, mode changes, and faults here." | An invitation, never a shrug |
| **Loading** | Nothing < 500 ms. 500 ms–2 s: `ProgressRing` 20 px inline in the card header. > 2 s: skeleton placeholders at final layout dimensions | — | Skeletons only where layout is known ahead of data; never a full-page spinner |
| **Partial / streaming** | Render what has arrived; unresolved fields show `—` in `WzTextTertiaryBrush`; card header carries an inline `ProgressRing` | — | Applies to `:SYST:STAT?` mid-fetch |
| **Stale** (poll overdue) | `WzCautionBrush` `SeverityPill` in the footer; values remain visible, dimmed to `WzTextSecondaryBrush` | "Last updated 47 seconds ago" | Per §10.3: amber > 15 s, critical > 60 s. **Never blank stale data** — an old reading with an honest timestamp beats an empty field |
| **Error — recoverable** | `InfoBar` `Severity="Error"`, inline at the top of the affected card, with an action button | "Couldn't set antenna delay. The receiver returned error −222, data out of range. Enter a value between 0 and 999,999 ns." / **Try again** | |
| **Error — blocking** | `ContentDialog` | Only when the user cannot continue without deciding | |
| **Disconnected** | Main window: medallion → `WzNeutralBrush`, glyph `\uE8CD`, mode text "Disconnected". Details: `InfoBar` `Severity="Informational"` pinned below the title bar, all controls disabled | "Not connected. Choose a serial port to connect." / **Choose a port** | Distinct from *error*: an intentional disconnect is not a fault |
| **Connection lost** | Both windows: `WzCriticalBrush`, `InfoBar` `Severity="Error"` with retry countdown | "Lost the connection to COM3. Retrying in 4 seconds." / **Retry now** · **Stop retrying** | Per §7.2 backoff |
| **No permission** | `ContentDialog` | "Windows wouldn't let the app open COM3. Another program may have it open. Close it, then try again." | `UnauthorizedAccessException` — usually a terminal emulator still holding the port |
| **Success — routine** | No UI. The value in the interface updates. | — | A setter that worked needs no toast |
| **Success — consequential** | `InfoBar` `Severity="Success"`, auto-dismiss 5 s | "Started the position survey. This usually takes about two hours." | Tier C commands only |

**Interruption ladder — the rule for choosing a surface:**

`InfoBar` when the user can keep working → `TeachingTip` when pointing at a specific control, first-run only, never for errors → `ContentDialog` only when the app cannot proceed without a decision, or when confirming a tier C command.

**Copy rules.**

- Errors state what happened and what to do next. No "Oops," no "Sorry," no "Something went wrong."
- Surface the SCPI error number *and* its plain-language meaning. The audience can use the number; everyone benefits from the sentence.
- A verb keeps its identity end to end: the button **Start survey** → confirmation **Start survey?** → result "Started the position survey."
- Second person for the user, no first person for the app. "Choose a port," not "Let's get you connected."
- Empty states describe what *will* appear, not what is absent.

**Validation.** `NumberBox` with min/max per the ranges in §10.6 and §10.7 — client-side, so the device is never sent an out-of-range value. Validate **on blur** for typed entry and **on change** for spinner/slider. Error text sits directly below the field in `WzCaptionTextStyle` / `WzCriticalBrush`, preceded by a 16 px `\uEA39` glyph, and the field border goes `WzCriticalBrush` — glyph plus text plus border, so the error is never carried by border colour alone. `Apply` stays disabled while any field in its card is invalid.

### 9.12 Accessibility acceptance criteria

Testable statements. Each is verified by the stated method, not by inspection alone.

| ID | Criterion | Verified by |
|---|---|---|
| A11Y-1 | Every interactive element is reachable and operable by keyboard alone; tab order follows visual reading order on every page | Manual keyboard-only pass per page, recorded in the PR |
| A11Y-2 | The focus visual is visible on every focusable element in all three themes, with ≥ 3:1 contrast against both adjacent surfaces, including on accent-filled buttons. **The focus visual is judged as the two-tone assembly Fluent draws, not stroke by stroke**: for each adjacent surface, at least one stroke must clear 3:1 (#187) | Accessibility Insights colour contrast tool at each focus stop |
| A11Y-3 | No icon-only control lacks both `AutomationProperties.Name` and a `ToolTip` | Automated XAML scan in CI: fail the build on any `Button`/`ToggleButton` whose content is only an icon and which lacks both |
| A11Y-4 | All text meets §9.4.5 contrast floors in Light, Dark, and HighContrast | **Light and Dark: `build/Test-ContrastFloor.ps1`, which gates CI** — added 21 Aug 2026 (#24). It composites each token over what sits beneath it, because almost every stock Fluent colour is semi-transparent and reading one as opaque gives a confident wrong answer. **HighContrast stays a manual Accessibility Insights pass** and cannot be otherwise: its tokens resolve to `SystemColor*`, which are the user’s own choices. The manual pass also remains the only way to measure the Mica case, where the true backdrop is a live blur of the wallpaper. |
| A11Y-5 | Pointer targets ≥ 32 × 32 px **at all times**; touch targets ≥ 40 × 40 px **in touch-optimised modes, of which the application currently has none** (#186). **`SkyPlotControl`’s markers are an exception, recorded in §9.10.2** | Accessibility Insights target-size check, which will flag the sky plot markers — §9.10.2 is the answer to that flag |
| A11Y-6 | At 200% text scaling, no text clips and no control overlaps, at every breakpoint | Manual pass at 100 / 150 / 200% text scale × three breakpoints |
| A11Y-7 | At 100–225% display scaling, layout remains usable and the title bar drag region stays correct | Manual pass at 100 / 150 / 200 / 225% |

> **⚠ Amended 28 Aug 2026 (#27).** The ceiling was 350 %, with the pass at 100 / 150 / 200 / 250 / 350 %.
> **Windows does not offer those percentages on the reference display.** Its scaling list is derived
> from the panel's size and resolution, and on the 5120 × 1440 monitor this is developed and tested on
> it stops at 225 %. Reaching 250 % or 350 % needs *Custom scaling*, which applies system-wide,
> requires a sign-out, and is not a configuration users arrive at by accident — so a criterion written
> around it was being verified nowhere.
>
> **What the pass to 225 % actually established** is the mechanism rather than four data points. §9.7.3
> requires caption-button clearance to be *read* from `AppWindowTitleBar` rather than computed, and the
> reserved clearance tracked the system through 138 → 207 → 276 px as scaling went 100 → 150 → 200 %.
> Then at 225 % **Windows stopped scaling its caption buttons**, holding them at 92 px each — verified
> on a freshly created window, so not an artefact of changing scaling live. An application computing
> `138 × scale` would by then have been reserving 310 px for buttons occupying 276. Reading the real
> inset is precisely why there is no collision at any scale, and that property does not begin to fail
> at 250 %.
>
> **The clamp is exercised too.** §9.6.2's converted floor is capped at the display work area, and on a
> 1440-tall panel that cap does real work from 200 % upward: the Details window's 720 effective pixels
> of minimum height become 1440 physical against 1392 available. Both windows were observed clamped at
> 200 % and 225 %, with nothing reported offscreen.
>
> **What is given up** is the layout half above 225 % — whether anything clips when the clamp is
> discarding most of the requested size rather than trimming it. The clamping code is unchanged and
> still protects that case; it is simply no longer claimed to be verified. If the application is ever
> run on a high-DPI laptop that offers 250 % or 350 % in the ordinary dropdown, that is the machine to
> check it on.
| A11Y-8 | High contrast is a first-class theme: no hard-coded brush survives, the medallion remains legible, severity is distinguishable | Manual pass in all four Windows HC themes |
| A11Y-9 | Mode changes, connection changes, and command results are announced as live regions | Narrator pass: force each transition, confirm announcement |
| A11Y-10 | `StatusMedallion` and `SkyPlotControl` expose complete automation peers per §9.10.2 | Accessibility Insights tree inspection |
| A11Y-11 | `SkyPlotControl` offers a non-spatial `ListView` alternate carrying the same data | Manual |
| A11Y-12 | No information is conveyed by colour alone anywhere in the app | Greyscale screenshot review of every page and state. For the §9.4.4 chart series specifically, **`build/Test-SeriesSeparation.ps1` gates CI** — added 22 Aug 2026 (#87, #177) — checking all 28 pairs in both themes under deuteranopia and protanopia as well as normal vision. It does **not** discharge this requirement: under HighContrast the series alternate between two `SystemColor*` values, so eight traces cannot be separated by colour there at all, and a second channel (dash pattern plus direct labelling) is required regardless. The greyscale review stays the check for everything that is not a chart series. |
| A11Y-13 | With animations disabled system-wide, no animation runs and no layout differs from the animated path | Manual pass with the system setting off |

A11Y-3 and A11Y-4 run in CI. The rest are a release checklist item, in [`docs/manual-qa.md`](manual-qa.md) section 4.

### 9.13 Anti-patterns

The implementation must avoid these. Each is reviewable.

1. **Default template palette.** No `SystemAccentColor` as the app's brand, no untouched `Blank App` styling, no WinUI Gallery sample colours left in place.
2. **Hard-coded colour.** No literal hex outside `Themes/Colors.xaml`. CI greps control styles and pages for `#` colour literals and fails on a hit.
3. **Opaque full-bleed panels.** No `SolidColorBrush` background spanning the content region — it defeats Mica Alt. L1 uses `LayerFillColorDefault`, which is translucent.
4. **Mixed radii and off-scale spacing.** Only 4 / 8 / circle (§9.3) and only the §9.6 spacing scale. A `Margin="13,7,13,9"` anywhere is a defect.
5. **Icon-only buttons without tooltip and automation name.** Enforced by A11Y-3.
6. **Dialogs for information.** A `ContentDialog` that has only a dismiss button and conveys no decision belongs in an `InfoBar`.
7. **Animation as decoration.** No animation without a row in §9.8.2. Nothing pulses, glows, breathes, or draws attention to itself. Specifically: the medallion does not pulse in holdover — it changes colour and shape, which is louder because everything else is still.
8. **Web idioms.** No drop shadows on cards, no gradient hero blocks, no oversized rounded cards, no full-width coloured banners as page headers, no `Opacity` fades used as elevation. These read as foreign on the desktop and immediately mark an app as a port.
9. **Counting-up numbers.** No `Storyboard` targets a readout value. Per §9.1 and §9.8.2.
10. **Colour-only severity.** Every severity render goes through `SeverityPill` (§9.10.2). A bare coloured `Ellipse` or a `Foreground`-only state cue is a defect.

### 9.14 Open design questions

| ID | Question | Assumption made | Owner | Blocking |
|---|---|---|---|---|
| **OQ-D1** | Does WinUI 3 expose `Microsoft.UI.Xaml.Documents.Typography.NumeralAlignment` as an attached property on `TextBlock`? UWP does; WinUI 3 parity needs confirming. | Assumed available. Fallback if not: Segoe UI Variable's lining figures are near-tabular, so combine fixed-width containers (§9.5.3 rule 2) with right alignment — visually equivalent for readouts, slightly worse mid-sentence. **Verify in a spike before building `ReadoutTile`.** | Engineering | Blocks `ReadoutTile` |
| **OQ-D2** | Is embedding Cascadia Mono in the MSIX acceptable to the product owner given the ~700 KB package increase? | Assumed yes; OFL 1.1 permits it and Windows 10 cannot be relied on to have it. Alternative is Consolas-only, which is less refined but zero cost. | Product | No |
| **OQ-D3** | Should the medallion ring show 1 PPS TI, or EFC? TI is the more diagnostic signal for loop behaviour; EFC is the better long-term ageing indicator but barely moves minute to minute. | Assumed **TI**, since the medallion serves the two-second glance and EFC is well served by the Overview trend chart. Worth a user check. | Product | No |
| **OQ-D4** | Does the brand teal read as "medical device" to any reviewer? It was chosen for distance from Windows blue and from all four semantic hues. | **Resolved 28 Aug 2026: keep `#0E7C86`.** The ≥ 60° separation is measured in **CIE LCH**, which §9.4.2 now states — it was unmeasurable as written. | Product | No |
| **OQ-D5** | Should the main window support Windows 11 Snap layouts and multi-instance for users with several receivers? | **Resolved 28 Aug 2026: single instance, and now *enforced* (#46).** A second launch redirects to the running one instead of opening a window that can never hold the port. §12 is untouched — this is about windows, not architecture. | Product | No |
| **OQ-D6** | Does `SkyPlotControl` need a printable/exportable form for calibration records? | **Resolved 28 Aug 2026: yes — image export ships in v1 (#47).** This *overturns* the recorded assumption. A CSV of azimuth and elevation is not a record of what the sky looked like, and an obstruction argument is made with a picture. See §10.4. | Product | No |
| **OQ-D7** | High-contrast rendering of the medallion ring loses the severity colour channel, leaving glyph and label to carry state. Is that sufficient, or should the ring change stroke *pattern* (solid / dashed / dotted) by severity in HC? | Assumed glyph plus label is sufficient, matching how the rest of the app behaves in HC. Pattern-coding is the fallback if user testing disagrees. | Design | No |

---

## 10. User Interface

### 10.1 Relationship to the design system

**§9 is normative for all appearance and interaction.** This section specifies *what each surface contains and does*; it no longer describes how anything looks. Where a wireframe below annotates a token name, that token is defined in §9 and must be used by key — the wireframes are content and hierarchy diagrams, not visual specifications.

Two behavioural principles remain here because they are functional rather than visual:

- **Progressive disclosure.** Main window → Details window → task dialog. Nothing destructive is more than one confirmation away, and nothing catastrophic is reachable at all (§8).
- **State, not events.** Every surface reflects last-known state with an explicit staleness indicator when polling has failed. Stale data is dimmed and timestamped, never blanked (§9.11).

> **⚠ Supersedes the previous §10.1.** The earlier "design principles" list (Fluent not terminal; Mica backdrop; accessibility as a Store gate; colour never alone) has been removed. Every item is now specified concretely and testably in §9: Mica Alt and layering in §9.2, the colour-plus-shape severity rule in §9.4.3, and accessibility as thirteen verifiable criteria in §9.12. The one item with no direct successor — "the details view must not reproduce the 80×24 layout" — is now carried by G2 and by the component inventory in §9.10, which contains no fixed-pitch surface other than `WzMonoTextStyle` for device-literal text.

### 10.2 Window inventory

| Window | Type | Min size | Notes |
|---|---|---|---|
| Main | `Window`, resizable | 380×240 (compact mode 380×144) | Content sizes, per §9.6.2. Status medallion. Optional always-on-top. |
| Receiver Details | `Window` | **1024×720** | `NavigationView` `Left`, per §9.6.1–9.6.2 |
| Connection | `ContentDialog` on Main | — | Port, baud, auto-detect |
| Position & Survey | Page in Details | — | |
| Satellite Tracking | Page in Details | — | |
| Timing & Antenna | Page in Details | — | |
| Holdover | Page in Details | — | |
| Time & Leap Seconds | Page in Details | — | |
| Status Registers | Page in Details | — | |
| Diagnostics | Page in Details | — | |
| Settings | Page in Details | — | |
| Advanced Console | Page in Details, hidden unless enabled | — | Allowlist-validated |
| About / Device Info | `ContentDialog` | — | |

> **⚠ Amended.** Details window minimum raised from 1000×700 to **1024×720**; rationale in §9.6.2.
>
> **Destination cap raised from eight to twelve, 19 Aug 2026.** The original cap was eight plus Settings, chosen so that `Ctrl+1`…`Ctrl+8` addressed every numbered destination. Twelve is a deliberate loosening to make room for surfaces §10.x has not yet described — #111's Time & Leap Seconds page and #137's EFC drift analysis both arrived with nowhere to live — and it is provisional: revisit once it is clear how many destinations the application actually wants.
>
> **The accelerators do not stretch with it, and cannot.** There is no `Ctrl+10`. Numbered accelerators therefore cover destinations **1 to 9** and stop; destinations 10 to 12 are reachable by pointer, by `Tab`, and by the pane's own arrow-key navigation, but have no single-keystroke jump. That is the price of the loosening and it is asymmetric — a destination past the ninth is a second-class one for a keyboard user, so **order the pane so that the most-used destinations sit in the first nine**. A11Y-1 requires every destination to be keyboard *reachable*, which pane navigation satisfies; it does not require every destination to have an accelerator.
>
> Advanced Console still appears below Settings and outside the numbered set.
>
> **The three missing sections were written on 20 Aug 2026** — §10.13 Settings, §10.14 Time & Leap
> Seconds, and §10.7's oscillator cards. Every page in the inventory above now has a section
> describing it, which had not been true since the inventory was written. See the note at the head
> of §10.13 for why they are numbered after §10.12 rather than inserted in pane order.

### 10.3 Main window

```
┌────────────────────────────────────────────────┐
│ ⬤ WinZ3805A            ─  □  ✕        │  32 px title bar · Mica Alt (L0)
├────────────────────────────────────────────────┤
│                                                │  WzSpaceXl (24) page margin
│        ╭───────────────╮                       │
│      ╱   ring = 60 s     ╲                     │  StatusMedallion, 160 px
│     │    radial TI       │   Locked to GPS     │  ← WzSubtitleTextStyle
│     │   sparkline        │   Stabilising       │  ← WzBodyTextStyle /
│     │        ✓          │   frequency         │    WzTextSecondaryBrush
│      ╲      glyph      ╱                       │  glyph 56 px, ring = severity
│        ╰───────────────╯                       │
│                                                │  WzSpaceXxl (32)
│    6            −33.1 ns                       │  ← WzReadoutLargeTextStyle
│    satellites   1 PPS TI                       │  ← WzCaptionTextStyle
│                                                │
│    ● TFOM 3     ● FFOM 0                       │  ← SeverityPill ×2
│                                                │  WzSpaceXl (24)
│    01:02:35 UTC · 27 Dec 2026 ⓘ                │  ← WzMonoTextStyle, tabular
│    COM3 · 9600-8-N-1 · updated 1 s ago         │  ← WzCaptionTextStyle /
│                                                │    WzTextTertiaryBrush
│  ┌──────────────┐              ┌────────────┐  │
│  │  Details  ▸  │              │  Connect   │  │  WzControlCornerRadius (4)
│  └──────────────┘              └────────────┘  │
└────────────────────────────────────────────────┘
```

**Compact mode — 380 × 144**

```
┌────────────────────────────────────────────────┐
│ ⬤ WinZ3805A            ─  □  ✕        │
├────────────────────────────────────────────────┤
│    ╭─────╮                                     │
│   │   ✓   │   Locked to GPS      6 satellites  │  medallion 64 px, glyph 40 px
│    ╰─────╯                                     │
└────────────────────────────────────────────────┘
```

**Behaviour**

- The medallion is `StatusMedallion` (§9.10.2). Glyph, severity token, and mode text derive from `:SYNC:STAT?`:

  | Response | Severity token | Glyph | Text |
  |---|---|---|---|
  | `LOCK` | `WzSuccessBrush` ● | `\uE73E` CheckMark | Locked to GPS |
  | `REC` | `WzCautionBrush` ▲ | `\uE72C` Refresh | Recovering |
  | `WAIT` | `WzCautionBrush` ▲ | `\uE769` Pause | Waiting to recover |
  | `HOLD` | `WzCriticalBrush` ⬢ | custom Holdover icon | Holdover |
  | `POW` | `WzNeutralBrush` ○ | `\uE823` Clock | Power-up |
  | `OFF` | `WzNeutralBrush` ○ | `\uE7E8` PowerButton | Diagnostic / off |
  | *(no connection)* | `WzNeutralBrush` ○ | `\uE8CD` DisconnectDrive | Disconnected |

  Severity is always the triple colour + shape + text (§9.4.3). The medallion changes all three at once; it never pulses or animates (§9.8.2, §9.13 item 7).

- When mode is `HOLD`, the sub-line carries the reason from `:SYNC:HOLD:WAIT?`.
- **Locked with zero satellites** renders a `WzCautionBrush` `SeverityPill` beside the count reading "coasting", with tooltip *"Locked but tracking no satellites. The receiver is coasting on a 1 PPS it can no longer verify."* This condition appears in real units with antenna or bias-tee faults and is the single most useful diagnostic the app surfaces — it is the reason the satellite count shares top billing with the mode.
- Date shows the rollover-corrected value with a trailing `\uE946` Info glyph when `WeekRolloverEpochs != 0`; the raw device date is in the tooltip (§7.4).
- Footer staleness per §9.11: `WzTextTertiaryBrush` normally, `WzCautionBrush` past 15 s, `WzCriticalBrush` past 60 s, always with the elapsed time in words.
- Always-on-top toggle; size, position, and compact state persist across launches.
- Compact mode toggles on double-click of the medallion or `Ctrl+Shift+M`. Type sizes, targets, and focus visuals are unchanged (§9.6.3).
- Opening Details runs a `ConnectedAnimation` on the medallion into the Overview page medallion (§9.8.2).

### 10.4 Receiver Details — Overview page

```
┌───────────────────────────────────────────────────────────────────────────┐
│ ☰  Receiver Details                                        ─  □  ✕        │
├──────────────────┬────────────────────────────────────────────────────────┤
│                  │                                                        │
│ ⌂ Overview       │  ┌─ Synchronization ─────────────────────────────────┐ │
│ ⊕ Satellites     │  │                                                   │ │
│ ⌖ Position       │  │   ✓  Locked to GPS                                │ │
│ ⏱ Timing         │  │      Stabilizing frequency                        │ │
│ ⏸ Holdover       │  │                                                   │ │
│ ◷ Time           │  │   Outputs Valid                        [ badge ]  │ │
│ ▤ Status Regs    │  │                                                   │ │
│ ⚕ Diagnostics    │  │   ┌──────────┬──────────┬───────────────────────┐ │ │
│ ⚙ Settings       │  │   │  TFOM 3  │  FFOM 0  │  1PPS TI  −33.1 ns    │ │ │
│                  │  │   │ 100ns–1µs│ PLL      │  relative to GPS      │ │ │
│                  │  │   │          │ stable   │                       │ │ │
│                  │  │   └──────────┴──────────┴───────────────────────┘ │ │
│                  │  └───────────────────────────────────────────────────┘ │
│                  │                                                        │
│                  │  ┌─ Holdover Uncertainty ────────────────────────────┐ │
│                  │  │  Predicted (24 h)   2.7 µs                        │ │
│                  │  │  Threshold          1.000 µs                      │ │
│                  │  │  Duration           —  (not in holdover)          │ │
│                  │  └───────────────────────────────────────────────────┘ │
│                  │                                                        │
│                  │  ┌─ Health Monitor ──────────────────────────────────┐ │
│                  │  │  ✓ Self Test   ✓ Internal Pwr   ✓ Oven Pwr        │ │
│                  │  │  ✓ OCXO        ✓ EFC            ✓ GPS Receiver    │ │
│                  │  │                                    [ Run test ]   │ │
│                  │  └───────────────────────────────────────────────────┘ │
│                  │                                                        │
│                  │  ┌─ Oscillator Control (EFC) ────────────────────────┐ │
│                  │  │      ┌──────────────────────────────────────┐     │ │
│                  │  │−16.80│              ╱‾‾‾╲___╱‾‾              │     │ │
│                  │  │−16.83│─────────────╱────────────────────────│     │ │
│                  │  │−16.86│                                      │     │ │
│                  │  │      └──────────────────────────────────────┘     │ │
│                  │  │       −6 h            −3 h             now        │ │
│                  │  │  Current: −16.83 %        [1 h][6 h][24 h][7 d]   │ │
│                  │  └───────────────────────────────────────────────────┘ │
└──────────────────┴────────────────────────────────────────────────────────┘
```

Health items map from `:STAT:OPER:HARD:COND?` where bit meanings are known, and otherwise from parsing the HEALTH MONITOR line of `:SYST:STAT?`. See Open Question OQ-1.

### 10.5 Satellites page

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Satellites                                    Tracking 6 · Visible 12   │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│   ┌────────────── Sky Plot ──────────────┐   ┌─ Tracked ──────────────┐  │
│   │                  N                   │   │ PRN  El   Az   C/N     │  │
│   │            ·  ⬤7                     │   │──────────────────────  │  │
│   │      ⬤2         ╱ ╲      ○11         │   │  2   49  243   ▓▓▓▓ 49 │  │
│   │        ╱  ┌─────────┐  ╲             │   │  7   35  186   ▓▓▓░ 46 │  │
│   │   W   │   │   90°   │   │   E        │   │ 16   24  243   ▓▓▓░ 47 │  │
│   │        ╲  └─────────┘  ╱      ⬤16    │   │ 19   65   52   ▓▓▓▓ 49 │  │
│   │      ○21        ╲ ╱        ⬤19       │   │ 27   62  327   ▓▓▓▓ 49 │  │
│   │              ⬤27     ○30             │   │ 31   34   61   ▓▓▓░ 47 │  │
│   │                  S                   │   ├─ Not tracked ──────────┤  │
│   │  ─── elevation mask 10° ───          │   │  3   10  172   acquiring│ │
│   │                                      │   │  4   61  109   ✱        │ │
│   │  ⬤ tracked   ○ predicted   ✱ trying  │   │  6    5  258   below mask│ │
│   └──────────────────────────────────────┘   │ 21   17  319   ignored  │ │
│                                              └────────────────────────┘  │
│   Elevation mask   [ 10 ]°  ────○────────    [ Apply ]                   │
│                                                                          │
│   Satellite selection                              [ Manage… ]           │
└──────────────────────────────────────────────────────────────────────────┘
```

- Sky plot: polar, north up, 0° elevation at rim, 90° at centre. Dashed circle at the elevation mask angle. Marker area scales with C/N; **fill from §9.4.4’s sequential ramp**. Tap a marker to select the matching row.

  > **⚠ Corrected 21 Aug 2026 (#116).** This line previously read "colour green ≥ 40, amber 35–39, red < 35". That is the semantic triple — `WzSuccessBrush` / `WzCautionBrush` / `WzCriticalBrush` — and §9.4.4 forbids reusing semantic tokens for charting in its opening sentence, with the reason: *a trace coloured `WzCriticalBrush` implies an alarm that is not being asserted*. A C/N of 34 is an ordinary satellite low in the sky; painting it the red the medallion uses for a lost lock asserts a fault nobody raised. §9.10.2 already specified the sequential ramp for this control, so two sections agreed against one, and both of those gave a reason. Appendix A records §10 being "reconciled" when §9 was added; this reads as a line missed in that pass.
- Data comes exclusively from `:SYST:STAT?` parsing (§11) — there is no per-satellite query.
- **Manage…** opens a dialog listing PRN 1–32 with include/ignore toggles, backed by `:GPS:SAT:TRAC:IGNore` / `:INCLude`. All writes are tier C.
- **Save image** writes the sky card — plot, heading, and the selection line — to a PNG the user names.

  > **⚠ Added 28 Aug 2026 (#47, OQ-D6 resolved).** OQ-D6 assumed no image export in v1, on the basis
  > that "export is CSV only". **That assumption is overturned**: it answered a different question
  > than the one asked. A CSV of azimuth, elevation and signal strength is a table; a calibration
  > record wants evidence of *what the sky looked like from this antenna*, and the argument #185
  > settled — a rack mean of 1.94 satellites against a backyard mean of 6.59 — is not one anybody
  > makes with a spreadsheet.
  >
  > Three properties are normative rather than incidental:
  >
  > 1. **The capture is the live card**, rendered through `RenderTargetBitmap` over the element
  >    already on screen. Not a second renderer — a separate drawing path for export is free to
  >    disagree with the one the user reviewed, and a record that differs from the screen it claims
  >    to record is worse than no record.
  > 2. **The image carries a caption the screen does not**: product name, capture time **in UTC**,
  >    tracked and predicted counts, and the elevation mask in force. The mask is not decoration —
  >    the same sky under a 10° mask and a 25° mask produces two legitimate plots with different
  >    satellites missing, so a record omitting it cannot be compared with anything.
  > 3. **No theme substitution.** The export is whatever theme the user is in, high contrast
  >    included, where the colours are the user's own choices and this application has no standing to
  >    replace them. §9.4.3's colour + shape + text encoding is what keeps a greyscale printout
  >    readable, so nothing is lost by declining to force a light theme.
  >
  > Offered only while the plot has satellites on it: an empty export is a picture of three rings,
  > which reads as a working antenna seeing nothing rather than as a receiver that is not connected.

### 10.6 Position page

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Position                                                                │
├──────────────────────────────────────────────────────────────────────────┤
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │  Mode          ● Position Hold      ○ Surveying                    │  │
│  │                                                                    │  │
│  │  Latitude      N 47° 31′ 18.822″                                   │  │
│  │  Longitude     W 122° 12′ 22.152″                                  │  │
│  │  Height        +38.00 m  (MSL)                                     │  │
│  │                                                       [ Copy ]     │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ Survey ───────────────────────────────────────────────────────────┐  │
│  │  ████████████████░░░░░░░░░░░░░░  57.3 %                            │  │
│  │  Estimated 51 min remaining · needs ≥ 4 satellites                 │  │
│  │                                                                    │  │
│  │  [ Start survey ]   [ Adopt computed position ]   [ Cancel ]       │  │
│  │                                                                    │  │
│  │  ☑ Survey on power-up                                              │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ Set position manually ────────────────────────────────────────────┐  │
│  │  Lat [N▾] [47]° [31]′ [18.822]″                                    │  │
│  │  Lon [W▾] [122]° [12]′ [22.152]″                                   │  │
│  │  Height [ 38.00 ] m         ⓘ same datum the receiver reports         │  │
│  │                                              [ Apply position ]    │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

> **⚠ Corrected 21 Aug 2026 (#114).** This field was annotated "WGS-84, GPS ellipsoid". The 58503B manual is
> not consistent with itself about the same command: its syntax line, printed four times, takes
> `<height above mean sea level, in meters>`, while its prose two paragraphs below says position is on the
> WGS-84 datum. Those differ by the geoid separation — tens of metres across most of the inhabited world,
> over 100 m in places — and the error goes straight into the position the receiver uses for every timing
> solution, with nothing in the interface to show it.
>
> **The field therefore asserts no datum of its own.** It states the one the receiver said it was reporting
> and asks for the value on the same one, which is knowable and checkable, where picking a side between the
> manual’s two halves is neither. The bench receiver reports `(MSL)`. This mirrors the read path, which
> already prints `(MSL)` or `(WGS-84 ellipsoid)` from `ReceiverStatus.HeightDatum` — only the entry field
> had been asserting a datum on the user’s behalf.

> **⚠ Amended 27 Aug 2026 (#229).** This section described **Start survey** as though it always
> starts one. On the bench Z3805A it does not: a receiver that is **already holding a position**
> refuses `:GPS:POSition:SURVey:STATe ONCE` with **−300, Device-specific error**, and no command in
> §8.2 or in any of the three family manuals releases the hold. `:GPS:POSition LAST` restores the
> last position rather than clearing it.
>
> The refusal is not about readiness, and not about spelling. It was reproduced at partial lock, and
> again at full lock with nine satellites and outputs valid; the Z3801A manual’s
> `:PTIMe:GPSYStem:POSition:SURVey ONCE` is refused identically; and a query in the same `:GPS:`
> subtree answers normally throughout, so the command is parsed and declined rather than
> unrecognised — an unknown header would be −113.
>
> **The route is survey-on-power-up**, the checkbox already in this card. Confirmed rather than
> inferred: the receiver was power-cycled at ≈22:10 on 27 Aug with nothing connected to it, and had
> a survey 3.8 % along by the time the application reached it four minutes later. That survey ran to
> completion.
>
> **So the amendment is to the copy, not to §8.2.** The command stays as specified — it is correct
> for the 58503A models the catalog also serves, and changing it would be a model-conditional change
> belonging to §6.5’s auto-profiles. What changes is that a −300 on this button must be reported
> with the reason and the route attached, rather than as a bare device error the user can do nothing
> with. The advice is attached to **−300 only**: a timeout or any other code gets the receiver’s own
> words and nothing added, because −300 is device-specific by definition and the receiver has not
> said why — offering the explanation for the wrong failure would send someone to power-cycle an
> instrument over a loose cable.

Validation before send: lat degrees 0–90, lon degrees 0–180, minutes 0–59, seconds 0–59.999 (0.001 resolution), height −1000.00 to +18000.00 m (0.01 resolution). Reject client-side rather than letting the device error.

### 10.7 Timing & Antenna page

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Timing & Antenna                                                        │
├──────────────────────────────────────────────────────────────────────────┤
│  ┌─ Antenna cable delay ──────────────────────────────────────────────┐  │
│  │  Current   77 ns                                                   │  │
│  │                                                                    │  │
│  │  ○ Enter delay directly     [  77  ] ns   (0 – 999 999)            │  │
│  │  ● Calculate from cable                                            │  │
│  │       Cable type  [ LMR-400        ▾ ]                             │  │
│  │       Length      [  20  ] m                                       │  │
│  │       → Computed delay  78.7 ns                                    │  │
│  │                                                                    │  │
│  │  ⚠ Changing this while locked may cause holdover                   │  │
│  │                                              [ Apply delay ]       │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ 1 PPS time interval ──────────────────────────────────────────────┐  │
│  │  Current −33.1 ns · σ 12.4 ns         [1 h][6 h][24 h][7 d]        │  │
│  │   +50 ns │        ╱╲                                               │  │
│  │        0 │───╲───╱──╲──────╱╲────                                  │  │
│  │   −50 ns │    ╲_╱    ╲____╱                                        │  │
│  │          └──────────────────────────────                           │  │
│  │                                                                    │  │
│  │  Oscillator control (EFC)                                          │  │
│  │  −16.80 % │      ╱╲     ╱╲                                         │  │
│  │  −16.83 % │──╲___╱  ╲___╱  ╲____╱╲___                              │  │
│  │  −16.86 % │                                                        │  │
│  │           └───────────────────────────────                         │  │
│  │                                                                    │  │
│  │  Oscillator drift                                                  │  │
│  │   ⬤ Nothing remarkable                                             │  │
│  │   Nothing in this window suggests a fault.                         │  │
│  │   Drift −8.6 ppm/day — no projection: the trend is flat            │  │
│  │   or heading away from both rails.                                 │  │
│  │   From 71,008 settled readings spanning 47.4 hours.                │  │
│  │   Unexplained scatter 32.4 ppm of range. Daily swing               │  │
│  │   about ±3.4 ppm, inferred from the reading’s own                  │  │
│  │   24-hour periodicity — no temperature is reported.                │  │
│  │   Hardware bits 6 and 7 are both clear.                            │  │
│  │                                              [ Export CSV… ]       │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

> **⚠ The wireframe above was redrawn on 26 Aug 2026** to match what §10.7.1 now specifies. It had
> shown the oscillator-control chart with a zero-anchored `+25 % / 0 % / −25 %` axis and the drift
> advisory in per cent, both of which #183 and #182 replaced. §10.4's Overview wireframe was redrawn
> in the same pass and for a stronger reason: **the chart it draws is not built yet** (P1-1, blocked
> on OQ-5), and a wireframe showing a zero-anchored EFC axis is precisely what led the Timing page's
> implementation to give EFC the 1 PPS treatment in the first place. A picture is a specification
> whether or not it is labelled as one.
>
> Figures in both are the 22–24 Aug 2026 capture, so the two wireframes and §10.7.1's amendments now
> describe the same receiver.

> **⚠ σ is over an hour of stored readings, and says how much of one it found.** §10.7's wireframe
> asks for `σ (1 h)`. When this page was built nothing kept an hour — the only history was
> `ReceiverStateStore`'s 60-sample ring — so it showed the deviation of what it had and promised the
> hour would arrive with P1 persistence. **It arrived and the sentence did not change**: the page was
> reading twelve thousand rows out of `trend.db` to draw the charts immediately below while printing
> a deviation over the last thirteen seconds.
>
> The read is always an hour whatever range is selected, because §10.7 puts σ beside *Current* as a
> property of the receiver now rather than of the window being drawn — a σ that changed when the user
> pressed **7 d** would be a different statistic wearing the same label.
>
> **The caption names what was found, not what was asked for.** The application is not always
> running, so an hour of wall clock routinely holds four minutes of readings, and the count is given
> beside the span because a deviation over 3,000 readings and one over 12 are not the same figure.
> It falls back to the 60-sample ring where no trend store exists.

Cable presets, delay per metre (from the vendor cable tables in the 58503B manual):

| Cable | ns/m |
|---|---|
| RG-213 / Belden 8267 | 5.05 |
| LMR-400 | 3.93 |
| Custom (enter velocity factor) | `3.3356 / VF` ns/m |

#### 10.7.1 The trends and the drift advisory

> **⚠ Added 20 Aug 2026** (#142). The two charts and the advisory below them were built by P1-1, P1-2
> and #137 and had no section describing them. This is the third §10.x gap of the same kind, after
> #111 and this one's own sibling #146; §10's section list was written before the feature set
> settled. The surface stays on this page rather than becoming a destination of its own: it annotates
> the 1 PPS trend it sits under, and §10.2's cap is a rule about the numbered set that a new
> destination would have had to be argued past rather than slipped through.

Both charts share one range selector — 1 h, 6 h, 24 h, 7 d — and both draw from the persisted series,
decimated per §9.10.2 (minimum and maximum per pixel column, never a sample).

**The drift fit reaches slightly further back than the charts draw.** A window of exactly *n* hours
holds a *span* of slightly under *n* hours, because its oldest sample sits just inside the leading
edge rather than on it — so the 24 h range could never satisfy the day-long separability rule below,
and the range named for a day could not reach the day-based analysis this card is built around. The
fit's window is therefore the drawn range plus a small fixed margin, and the evidence line reports
the span it actually fitted rather than the range that was selected. The charts, the CSV export and
the reading count are unaffected: they are what the user is looking at.

> **⚠ Amends this section** (#184). Before this, selecting 24 h produced *"From 67,008 settled
> reading(s) spanning 24.0 hours … A daily swing cannot be separated from the trend in under a day
> of data"* — a label and a verdict contradicting each other in one sentence, because the true span
> was about 23.98 hours and the evidence line rounded it up. The margin fixes the analysis; **spans
> are now also rounded down rather than to nearest**, since a figure sitting beside a verdict about
> a threshold must never round across it. An evidence line may understate what it had; it may not
> overstate it.

- **1 PPS time interval** is zero-anchored with a diverging fill whose neutral midpoint is exactly
  0 ns (§9.4.4). Stretches where the receiver was **not** locked are shaded.
- **Oscillator control (EFC)** is a **second chart, not a second axis.** 0 ns and 0 % are not the same
  zero, and one axis carrying both would put the colour break of one series at an arbitrary value of
  the other. Its y-axis is **framed on the window's own data**, not on zero — for EFC, 0 % is one end
  of a control range whose absolute position is arbitrary, and the diagnostic content is the
  deviation. A **minimum span** keeps a quiet oscillator from being magnified into noise, the way
  §9.10.2's medallion ring has a ±50 ns floor for the same reason. Labels are absolute percentages,
  so the chart agrees with the readout, the drift slope and the §10.7.1 rail alarms about what
  number the oscillator is sitting at.

> **⚠ Amends this section and §9.4.4** (#183). The EFC chart was built through the same
> zero-anchored bounds as TI, which the text above did not describe either way. The consequence was
> measured on the 22–24 Aug 2026 capture: a receiver holding **−16.8557 … −16.8041 %** over 47 hours
> was drawn against a ±25 % axis, so 0.05 percentage points of real structure occupied about a
> thousandth of the plot height and the trace read as dead flat. The axis extent was set by the
> offset from zero and by nothing about the signal, so no choice of floor could fix it.
>
> The minimum span is **0.01 %**, which is about 55 EFC codes on the bench receiver — its step
> measures roughly 0.00018 %, from 280 distinct values across 0.0516 %. Below that the plot would be
> drawing the converter's least significant bit rather than the oscillator.
>
> Axis labels are fixed at **2 decimal places** for this chart. One is not enough to separate −16.86
> from −16.80, and §9.11 item 6 forbids a precision that varies with the range rather than one that
> differs between quantities.

**The drift advisory reports what the fit can support and refuses what it cannot.** It states the
secular slope in **ppm of control range per day**, the sample count, the span actually fitted, the
residual, and a projection to ±100 % as both a count of days and a date — and it withholds the
projection where the window cannot support one. A slope without a sense of scatter is a number
pretending to be a measurement.

**The fit's own figures are counted in ppm of control range, the projection in per cent.** One per
cent of range is 10 000 ppm; EFC is already a percentage *of* that range, so ppm here needs no second
reference. The rails are at ±100 % and stay there — a projection is about reaching them, and saying
so in ppm would be arithmetic for its own sake.

- **Diurnal swing is reported separately from secular drift**, and only where the window is long
  enough to tell them apart. Below a day of data the fit drops to a plain line and says so, rather
  than reporting a daily amplitude of zero — which would be a measurement.

> **⚠ Amends this section and §9.11 item 6** (#182). The advisory reported its figures as
> percentages, and on a good oscillator every one of them rounded away. Measured on the 22–24 Aug
> 2026 capture: a secular drift of **−0.00086 %/day**, a diurnal amplitude of **0.00034 %** and a
> residual rms of **0.00324 %** printed as `−0.001 %/day`, `±0.00 %` and `0.00 %`. The arithmetic
> was right — it agreed with an independent fit over the same basis to five decimal places — so the
> card was committing precisely the failure the bullet above forbids, and doing it past a day of
> data rather than below one. A double-oven oscillator holding 0.05 % of range across two days is
> the *good* case, and the card could not say so.
>
> **The unit changed rather than the precision**, because §9.11 item 6's reason — a reader cannot
> compare two lines whose precision differs — does not stop being true for a card. The same three
> figures counted in ppm of range are **−8.6 ppm/day**, **±3.4 ppm** and **32.4 ppm**, all readable
> at the one decimal place item 6 already required.
- **There is no internal temperature query on this receiver.** The daily component is inferred from
  EFC's own periodicity, and the interface must say so: a user who reads "diurnal" will otherwise
  assume something measured it.
- **Samples inside the first 24 h after a power-up are excluded** and the count of them is shown. The
  loop is settling and those readings bend the fit; §10.8's power-up guard uses the same figure.
- The advisory names its evidence and **hedges its wording**. It is consistent-with, never is.
  Severity renders through `SeverityPill` — colour **and** shape **and** text (§9.4.3, A11Y-12).
- **Hardware register bits 6 and 7** — "EFC voltage near full scale" and "at full scale" — are
  surfaced here, read from the receiver rather than recomputed. They are the alarm; the slope is the
  gauge.

**Read-only.** Nothing on this card writes to the receiver. Adjusting the oscillator is not something
this application does, and nothing here may imply it could.

### 10.8 Holdover page

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Holdover                                                                │
├──────────────────────────────────────────────────────────────────────────┤
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │   Current state    ✓ Locked to GPS — not in holdover               │  │
│  │                                                                    │  │
│  │   Predicted 24 h uncertainty     2.7 µs                            │  │
│  │   Present time error             —                                 │  │
│  │   Duration                       —                                 │  │
│  │   Waiting reason                 —                                 │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ Threshold ────────────────────────────────────────────────────────┐  │
│  │  Enter holdover when 1 PPS TI exceeds  [ 1.000 ] µs                │  │
│  │  Currently exceeded:  No                        [ Apply ]          │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ Manual control ───────────────────────────────────────────────────┐  │
│  │  ⚠ Do not force holdover within 24 hours of power-up. Doing so     │  │
│  │    corrupts the SmartClock oscillator learning process.            │  │
│  │                                                                    │  │
│  │    Time since power-up:  6 d 14 h        ✓ safe                    │  │
│  │                                                                    │  │
│  │  [ Force holdover ]   [ Recover now ]   [ Ignore recovery limit ]  │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

The "time since power-up" guard is computed from app-observed uptime plus `:DIAG:LOG:READ:ALL?` power-on entries. If it cannot be determined, show "unknown" and require the extra "I understand" tick on the confirmation.

### 10.9 Diagnostics page

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Diagnostics                                                             │
├──────────────────────────────────────────────────────────────────────────┤
│  ┌─ Self test ────────────────────────────────────────────────────────┐  │
│  │  Subsystem  [ All ▾ ]                        [ Run test ]          │  │
│  │  Last result: PASS · 11 Aug 2026 09:14                             │  │
│  │  ✓ Display  ✓ Processor  ✓ RAM  ✓ EEPROM  ✓ UART                   │  │
│  │  ✓ QSPI  ✓ FPGA  ✓ Interpolator  ✓ Int Ref  ✓ GPS  ✓ Power         │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ Diagnostic log ──────────────────────  47 entries ────────────────┐  │
│  │  ⌕ [ filter…            ]        [ Refresh ]  [ Export ]  [ Clear ]│  │
│  │  ────────────────────────────────────────────────────────────────  │  │
│  │  047  11 Aug 2026 09:02:14   GPS lock started                      │  │
│  │  046  11 Aug 2026 08:58:41   Holdover started, not tracking GPS    │  │
│  │  045  09 Aug 2026 22:15:03   Position hold mode started            │  │
│  │  044  09 Aug 2026 20:11:57   Survey mode started                   │  │
│  │  043  09 Aug 2026 20:11:02   Power on                              │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ Error queue ──────────────────────────────────────────────────────┐  │
│  │  No errors.                                   [ Read errors ]      │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ Lifetime ─────────────────────────────────────────────────────────┐  │
│  │  Power-on count  1 247                                             │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

Log entries carry severity as **shape and colour together**, never colour alone: power/mode transitions neutral, holdover amber with §9.4.3's triangle, hardware failure / self-test failure red with its hexagon. The shape leads the line, in a fixed-width cell so the monospace column stays aligned.

> **⚠ Amended 27 Aug 2026 (#225).** This read *"Log entries are colour-coded by severity"* and named three colours and nothing else, which **A11Y-12 forbids** — neutral, amber and red is information by colour alone. It is also the pair §9.4.3 singles out: amber and red converge under protanopia and deuteranopia, and under high contrast `WzCautionBrush` and `WzCriticalBrush` are *both* `SystemColorWindowTextColor`, so two of the three states would render identically. The same defect was fixed in §10.3's footer staleness on the same day (#223).
>
> The colour-coding is **not implemented** — `DiagnosticsPage` renders each entry as plain `RawText` — so nothing regressed. What was wrong was that the specification instructed a reader to build the thing A11Y-12 exists to prevent, and `Test-NoColourOnlyStates.ps1` would not have caught it: that gate reads `VisualStateGroup`s, and a per-item brush binding in a `DataTemplate` passes it.
>
> The `WzShape*` geometries moved into a merged dictionary with #223, so they resolve from any page.

### 10.10 Status Registers page

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Status Registers                                    [ Refresh all ]     │
├──────────────────────────────────────────────────────────────────────────┤
│  Register  [ Operation ▾ ]                                               │
│                                                                          │
│   Bit  Cond  Event  Enab  PTr  NTr   Meaning                             │
│   ───────────────────────────────────────────────────────────────────    │
│    0    ●     ○     ☑    ☑    ☐    First satellite tracked              │
│    1    ○     ○     ☑    ☑    ☐    (see documentation)                  │
│    2    ●     ●     ☑    ☑    ☑    (see documentation)                  │
│    3    ●     ○     ☐    ☐    ☐    Position hold (0 = surveying)        │
│   …                                                                      │
│                                                                          │
│   Raw   CONDition +13   EVENt +4   ENABle +7   PTR +7   NTR +4           │
│                                                                          │
│   ⓘ Bit meanings are partially documented. Unmapped bits show raw state. │
│                                             [ Apply mask changes ]       │
└──────────────────────────────────────────────────────────────────────────┘
```

Register selector covers Operation, Operation:Hardware, Operation:Holdover, Operation:Powerup, Questionable. Where a bit meaning is unknown, show the raw state and "(see documentation)" rather than inventing a label. See OQ-1.

### 10.11 Advanced Console (hidden by default)

Enabled in Settings → Advanced. Provides a command *picker*, not a text box:

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Advanced Console                                                        │
├──────────────────────────────────────────────────────────────────────────┤
│  Command  [ :GPS:SAT:TRAC:EMANgle                              ▾ ]       │
│           ⌕ filter…                                                      │
│  Parameter  [ 10 ] degrees   (0 – 89)                                    │
│                                                                          │
│  Will send:  :GPS:SAT:TRAC:EMAN 10                    [ Send ]           │
│                                                                          │
│  ┌─ Transcript ───────────────────────────────  [Clear] [Export]──────┐  │
│  │  > :SYNC:STAT?                                                     │  │
│  │  < LOCK                                                            │  │
│  │  > :GPS:SAT:TRAC:COUN?                                             │  │
│  │  < +6                                                              │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

- The dropdown is populated **from the catalog**. Blocked commands are not in the catalog and therefore cannot be selected.
- Parameter entry is typed and range-validated per `ParameterSpec`.
- Tier-C commands selected here still raise their confirmation dialog.
- The transcript shows all traffic including polling, with a toggle to hide poll traffic.

If a future version adds free-text entry, it must run every submission through `CommandCatalog.Validate(string)` which (a) requires a catalog match on the normalised mnemonic and (b) rejects any `BlockedPatterns` match, logging the attempt. Anything not matching the catalog is rejected — allowlist semantics, not blocklist.

### 10.12 Connection dialog

```
┌──────────────────────────────────────────────┐
│  Connect to receiver                         │
├──────────────────────────────────────────────┤
│  Port    [ COM3 — USB Serial Port      ▾ ]   │
│                                  [ Refresh ] │
│                                              │
│  ○ Auto-detect settings                      │
│  ● Manual                                    │
│      Baud     [ 9600  ▾ ]                    │
│      Data     [ 8     ▾ ]                    │
│      Parity   [ None  ▾ ]                    │
│      Stop     [ 1     ▾ ]                    │
│                                              │
│  ☑ Reconnect automatically                   │
│  ☑ Connect to this device on launch          │
│                                              │
│              [ Cancel ]  [ Connect ]         │
└──────────────────────────────────────────────┘
```

Auto-detect tries, in order: 9600-8-N-1, 19200-7-E-1, 9600-7-E-1, 19200-8-N-1, 2400-8-N-1, 1200-8-N-1, 9600-7-O-1, 19200-7-O-1. Each attempt sends `*IDN?` with a 2 s timeout. Show progress and allow cancel.

### 10.13 Settings page

> **⚠ Added 20 Aug 2026** (#146). §10.2's inventory has listed a Settings page since it was written,
> §9.7.5 gives it `Ctrl+,`, and §10.11 says the Advanced Console is "Enabled in Settings → Advanced" —
> but no section described it, and the code's own destination record cited a **§10.13 that did not
> exist**. This is that section.
>
> **Why it is numbered after the Connection dialog rather than inserted in pane order.** §10.4 to
> §10.12 are cited by number throughout the source, the issue tracker and this document; inserting a
> section in the middle would renumber four of them and invalidate every one of those citations. The
> numbering therefore records the order the sections were written, not the order the user meets the
> pages — which was already true before this change, since §10.9 Diagnostics precedes §10.10 Status
> Registers while the pane draws them the other way round.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Settings                                                                │
├──────────────────────────────────────────────────────────────────────────┤
│  ┌─ Advanced ─────────────────────────────────────────────────────────┐  │
│  │  For working out what the receiver is doing, rather than for       │  │
│  │  using it.                                                         │  │
│  │                                                                    │  │
│  │  Advanced Console        [ ●━━ Shown ]                             │  │
│  │  Adds a page below Settings offering every command in the          │  │
│  │  catalog as a picker, with a transcript of everything sent         │  │
│  │  and received.                                                     │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

**Advanced Console (§10.11).** Off on a fresh install. The switch adds and removes the destination
from the pane; it does not merely hide it, so a disabled console is not an item a keyboard user can
still reach. If the console is showing when it is switched off, the pane falls back to the first
destination rather than leaving the frame on a page it no longer lists.

**Opting in changes what is reachable, never what is permitted.** The console is a picker over the
same §8.1 allowlist every other page uses, so enabling it adds no command the application could not
already send. The §8.4 exclusions are absent from the catalog and therefore absent from the console,
opted in or not. **No setting on this page may ever change that**, and none may relax a §8.3
confirmation.

**Preferences fail safe and fail silent.** A preference file that is missing, truncated or unreadable
reads as the default, and the default for anything advanced is *off* — a store that failed open would
enable an advanced surface because a disk went wrong. A write that fails is not reported: a
preference is by definition something the user can set again, and nothing load-bearing may live in
one of these files.

#### 10.13.1 Not on this page, and why

| Setting | Where it is | Status |
|---|---|---|
| Display time zone | Main window clock, and §10.14 | Built (#95) |
| Poll cadences | Nowhere — fixed by §7.3 | **See below** |
| Units | Nowhere | Not specified |
| Experimental §8.5 queries | Nowhere | P1-8 (#56) |

**Poll cadences are deliberately not offered.** §7.3 fixes them at 1 s and 10 s and §12 gives the
poller sole ownership of both. A settings page that offered to change them would contradict two
sections rather than implement one, so making them user-visible is an amendment to §7.3 and §12 and
must be argued there first.

The other three rows are unbuilt rather than refused. The page states plainly on screen that they are
not there yet; §9.11's rule against a control that looks like it works and does nothing applies to a
settings page more than to most.

### 10.14 Time & Leap Seconds page

> **⚠ Added 20 Aug 2026** (#111). §10.2's inventory, §9.7.1's pane wireframe and §15 step 8's build
> order all required this destination, and no section described it — so #110 built it from what the
> document defines for the data rather than from a specification of the page. This section settles
> the three questions #111 raised, and the answers are recorded below rather than left implicit.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Time                                                                    │
├──────────────────────────────────────────────────────────────────────────┤
│  ┌─ Receiver clock ───────────────────────────────────────────────────┐  │
│  │  12:29:09  Pacific Daylight Time · 20 Aug 2026                     │  │
│  │  Show times in  [ This computer (Pacific Daylight Time)  ▾ ]       │  │
│  │                                                                    │  │
│  │  Time scale             UTC                                        │  │
│  │  Reported by receiver   04 Jan 2007 19:29:09                       │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ Week rollover correction ─────────────────────────────────────────┐  │
│  │  ⬤ Corrected by 1 epoch of 1024 weeks                              │  │
│  │  GPS transmits the week number in ten bits, so it wraps            │  │
│  │  about every 19.6 years and a receiver of this age reports         │  │
│  │  a date that far in the past. The time of day and the 1 PPS        │  │
│  │  output are unaffected.                                            │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ Leap second ──────────────────────────────────────────────────────┐  │
│  │  ⬤ None announced                                                  │  │
│  │  GPS − UTC   +18 s accumulated                                     │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌─ Time code output ─────────────────────────────────────────────────┐  │
│  │  Format      F2 — messages begin T2                                │  │
│  │  Calendar date and time of the next 1 PPS, on the receiver's       │  │
│  │  selected time scale. 23 characters.                               │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

**The receiver's clock, in the zone the user chose.** The corrected instant in the display zone, with
the zone always named — never an unlabelled wall-clock time (§11.2, #95). The time scale the receiver
is reporting on is stated, because UTC and GPS differ by the accumulated leap seconds and a reading
that does not say which it is cannot be compared to anything.

**The receiver's own date is shown beside the corrected one, never instead of it** (§7.4). The
correction is reported and explained; it is never silently substituted, because a user who sees a
date two decades out with no explanation reasonably concludes the hardware has failed. Severity
renders through `SeverityPill`.

**The leap-second card carries what the receiver will tell it, which is less than the catalog
suggests.** The status screen announces a *pending* leap second and its direction. The `:PTIM:LEAP:`
subsystem holds four tier S queries, and they do not all answer:

| Query | On the bench receiver, 20 Aug 2026 |
|---|---|
| `:PTIM:LEAP:ACC?` | `+18` — the accumulated GPS−UTC offset |
| `:PTIM:LEAP:STAT?` | `0` — none announced |
| `:PTIM:LEAP:DATE?` | **`E-230`** |
| `:PTIM:LEAP:DUR?` | **`E-230`** |

**The date and the direction answer only while an announcement stands.** With `STAT? = 0` there is no
announced leap second to have a date or a direction, and the receiver rejects the question rather than
returning a null. So the card must read `STAT?` first and ask the other two only if it says yes — a
page that asked all four on arrival would put two errors in the error queue every time it was opened.

**The accumulated offset is the number worth showing unconditionally.** It is what anyone comparing
GPS time to UTC needs, it is always available, and it is the one figure on this page that justifies
the section title on a day when nothing is announced.

> **Built** as **#149**, closed 20 Aug 2026. The page reads `ACC?` and `STAT?` on arrival and on
> reconnect, and asks `DATE?` and `DUR?` only when `STAT?` reports an announcement. The ordering
> rule lives in `LeapSecondQueries` so it can be asserted without a receiver.

**The time code output has a format, and it is not the documented default.** `:PTIM:TCOD?` emits a
message naming the time of the next 1 PPS, in one of two notations. Which one is a receiver setting,
and `z3801.pdf` states that "T1 is the default time code format" — while the bench Z3805A answers
`F2`:

| Query | On the bench receiver, 21 Aug 2026 |
|---|---|
| `:PTIM:TCOD:FORM?` | `F2` — messages begin `T2`, 23 characters |

So the page **reads the format rather than assuming it**. Anything written against the documented
default would mis-parse every message this receiver sends. The card names both spellings, because
the command's parameter is `F1`/`F2` while the header the message carries is `T1`/`T2`, and a user
comparing the page against a raw time code has to recognise those as the same thing.

**The page shows the format and deliberately not the time code itself.** `:PTIM:TCOD?` does not
answer when asked: it answers on the receiver's own 1 Hz cadence, about **509 ms** before the 1 PPS
it names, with jitter under 2.5 ms. A request therefore lands in the next emission slot and the
transaction blocks for up to a second — a cost a read-only page has no reason to pay, and one that
would be charged again on every refresh. The format is the part that does not change and the part
without which the message cannot be read at all. **#37** records the measurement, the worked
decode and the checksum rule.

> This query reached the catalog late. It is documented in `z3801.pdf` rather than the 58503A
> programming guide the catalog was first derived from, so the §16 inventory missed it; see #154.

#### 10.14.1 The three questions #111 raised

**1. Does "& Leap Seconds" imply a history table?** **No.** There is no command that returns a
leap-second history — the receiver answers only about the accumulated offset and the one announcement
it currently holds — and a table of historical leap seconds would be a table this application had to
carry as data of its own, going stale on a schedule nobody controls. What the section title promises
and the receiver can support is the *accumulated offset* and the *pending announcement*, and that is
what the page holds.

**2. Does the page set anything?** **No. It is read-only**, the time code card included —
`:PTIM:TCOD:FORMat` has a setter, and it is not catalogued (see §16). `:PTIM:TZONe` would set the offset the
receiver itself reports in, which is a different thing from the zone this application displays in, and
it is tier C. Changing it would move every reported time including the timecode output, for a
cosmetic gain the display-zone picker already provides without touching the device.

**3. Where does the display-zone picker live?** **Both here and on the main window, deliberately.**
The main window is a glanceable surface a user leaves on a second monitor for weeks, and the zone is
part of reading the clock on it; this page is where the reasoning about time lives. One preference,
two places to set it — which is a duplicated control, not duplicated state.

---

## 11. Status Screen Parser

### 11.1 Requirements

- **Never throws.** Any unparseable field becomes `null` on the model; the UI shows "—". A firmware revision difference must degrade gracefully, not crash.
- **Header-relative column detection.** Do not hard-code character offsets. Locate the header row (`PRN  El  Az  C/N` or `PRN  El  Az  SS`) and derive column start/end from the token positions in that header. This is what makes the parser survive across the Z3801A / Z3805A / 58503A/B / 59551A variants, which differ in column labels and widths.
- **Two satellite column groups.** The not-tracking table may occupy two side-by-side PRN/El/Az groups. Detect by counting `PRN` occurrences in the header row.
- **Model-variant labels.** The signal-strength column is `C/N` on 58503B-class units (range 26–55, ≥ 35 good) and `SS` on 59551A-class units (range 0–255, 20–30 weak). Record which label was seen in `ReceiverStatus.SignalStrengthKind` and scale the UI bars accordingly. Do not assume the two scales are interchangeable.
- **Fixture-driven tests.** Store captured status screens in `tests/WinZ3805A.Tests/Fixtures/` covering: power-up (0 tracked), acquiring, locked, holdover, survey in progress, position hold, week-rollover date, and a health-monitor failure. Each fixture gets an assertion set.

### 11.2 Model

```csharp
public sealed record ReceiverStatus
{
    // SYNCHRONIZATION
    public OutputValidity Outputs { get; init; }          // Invalid | ValidReduced | Valid
    public SmartClockMode Mode { get; init; }             // Locked|Recovery|Holdover|PowerUp
    public string? ModeDetail { get; init; }              // "stabilizing frequency"
    public int? Tfom { get; init; }
    public int? Ffom { get; init; }
    public double? OnePpsTiNanoseconds { get; init; }
    public double? HoldThresholdSeconds { get; init; }
    public double? HoldoverPredictedSeconds { get; init; }
    public double? HoldoverPresentSeconds { get; init; }
    public TimeSpan? HoldoverDuration { get; init; }

    // ACQUISITION
    public bool GpsOnePpsValid { get; init; }
    public IReadOnlyList<TrackedSatellite> Tracked { get; init; }
    public IReadOnlyList<PredictedSatellite> NotTracked { get; init; }
    public int? ElevationMaskDegrees { get; init; }
    public SignalStrengthKind SignalStrengthKind { get; init; }   // CarrierToNoise | SignalStrength

    // TIME
    public TimeScale TimeScale { get; init; }             // Gps|Utc|LocalGps|Local
    public DateTimeOffset? DeviceDateTime { get; init; }
    public bool DeviceTimeIsProvisional { get; init; }    // added 28 Aug 2026, #245
    public int WeekRolloverEpochs { get; init; }
    public DateTimeOffset? CorrectedDateTime { get; init; }
    public ClockAdvisory OnePpsClockAdvisory { get; init; }    // §11.3
    public double? AntennaDelayNanoseconds { get; init; }
    public LeapSecondPending LeapPending { get; init; }   // None|Plus|Minus

    // POSITION
    public PositionMode PositionMode { get; init; }       // Hold|Survey
    public double? SurveyPercentComplete { get; init; }
    public SurveySuspendedReason SurveySuspendedReason { get; init; }  // §11.3
    public GeoPosition? Position { get; init; }
    public PositionQualifier PositionQualifier { get; init; }  // Init|Average|Held
    public HeightDatum HeightDatum { get; init; }         // GpsEllipsoid | Msl

    // HEALTH
    public bool HealthOk { get; init; }
    public IReadOnlyDictionary<string, bool> HealthItems { get; init; }

    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<string> ParseWarnings { get; init; }
}
```

> **⚠ Amended 28 Aug 2026 (#245): `DeviceTimeIsProvisional` added.** The clock row may carry a
> power-up marker — `(?)` on this unit, `[?]` in the Z3801A user guide's Figure 3-1 — between the
> time and the date:
>
> ```
>                    PRN  El  Az                GPS      05:10:04 (?) 12 Jan 2007
> ```
>
> The guide defines it as *"the default power-up setting … corrected when the first satellite is
> tracked"*. The parser previously could not match the row at all, dropped the time, and then
> reported that **no clock row existed** — sending a reader of §11.1's `ParseWarnings` to look for a
> line sitting in front of them.
>
> **The flag is why the marker is not merely tolerated.** The two known examples show how far apart a
> marked time can be from the truth:
>
> | source | printed | truth |
> |---|---|---|
> | captured from this unit | `05:10:04 (?) 12 Jan 2007` | right to the minute — the oscillator held time across the power cycle |
> | Z3801A guide, Figure 3-1 | `12:00:00[?] 01 JAN 1996` | a default, arbitrarily wrong |
>
> Nothing on the screen distinguishes them, so parsing the value while discarding the marker would
> convert a knowable caveat into a silent inaccuracy — **worse than refusing the row**, which is what
> the code did before. It is distinct from `OnePpsClockAdvisory`, which describes the 1 PPS *signal*
> and is read from the `GPS 1PPS …` line two rows below; this is a property of the time-of-day
> reading. It is also independent of `WeekRolloverEpochs`: a provisional time still gets §7.4's
> correction applied on top, and a power-up screen has **both** caveats in force at once, which is
> why §10.3 and §10.2 show them as two badges rather than one.
>
> The same pass taught the pattern the **year-first date order** the 58503A and Z3801A manuals print
> — `GPS 03:56:44 1994 DEC 01` — against the `d MMM yyyy` every screen captured from this unit uses.
> Latent on this hardware, and exactly the cross-model difference §11.1's header-relative parsing
> exists to survive.

`ParseWarnings` is surfaced in Diagnostics so field reports about odd firmware revisions are actionable.

### 11.3 Known advisory strings

The parser must recognise these `1PPS CLK` / position advisories as enum values, not free text, because the UI branches on them:

| String | Meaning |
|---|---|
| `Synchronized to UTC` / `Synchronized to GPS Time` | Locked, referenced to that scale |
| `Assessing stability` (with 0–3 trailing dots) | Hysteresis applied |
| `Questionable accuracy` | 1 PPS present but not trusted |
| `Inaccurate: not tracking` | No satellites |
| `Inaccurate: inacc position` | Surveying, no position yet |
| `Absent or freq error` | No 1 PPS, or engine idle |
| `Invalid: GPS rcvr err` | Receiver engine error |
| `Suspended: track <4 sats` / `poor geometry` / `no track data` | Survey stalled |

These decode to the `ClockAdvisory` and `SurveySuspendedReason` enums named in §11.2, and the
model carries **no string form of either**. The mapping from the device's text belongs entirely to
the parser, so no view can branch on a display string: `Assessing stability` alone arrives with
nought to three animated trailing dots, which a string comparison sees as four distinct states,
and any firmware reword would drop a `switch` silently into its default arm.

Both enums carry an `Other` member for text this table does not cover. When the parser reaches it,
the device's exact wording goes into `ReceiverStatus.ParseWarnings`, which is what makes a field
report about an unfamiliar firmware revision actionable without keeping a string on the model that
something might later branch on.

---

## 12. Architecture Notes

- `DeviceSessionService` is a singleton owning the transport, the command channel, and connection state. It exposes `IObservable`-style events (or `INotifyPropertyChanged` on an observable state object) rather than letting view models touch the port.
- `PollingService` owns the two cadences and writes into a `ReceiverStateStore`. View models bind to the store, never to the poller.
- Trend data (EFC, 1 PPS TI, TFOM) lands in a ring buffer sized for 7 days at 1 s (604 800 samples × ~16 bytes ≈ 10 MB — acceptable; downsample to 10 s beyond 24 h to cut this to ~1 MB).
- Persist trends to a SQLite file under `LocalApplicationData` so restarts do not lose history. `Microsoft.Data.Sqlite` is packaged-app safe. **The reference was removed on 15 Aug 2026** and P1-2 (#50) restores it: it was carrying 1.89 MB of native `e_sqlite3.dll` into every package for a feature no code path could reach. Note the folder — not `ApplicationData.Current.LocalFolder`, for the reason given against §6.1's logging row.
- **Multi-device readiness:** `DeviceSessionService` must be instantiable per device and resolved from a keyed DI registration, even though v1 creates exactly one. Do not use static state for connection or device identity.
- **No `DateTime.Now` / `DateTime.UtcNow` anywhere in the Device library.** Inject `TimeProvider` and call `provider.GetUtcNow()`. This is not stylistic — the week-rollover logic (§7.4), staleness display, and poll scheduling are all clock-dependent, and fixture tests must be able to pin the clock. Enforce with a Roslyn analyzer rule or a code-review checklist item.

---

## 13. Requirements by Priority

### P0 — must ship

| ID | Requirement | Acceptance criteria |
|---|---|---|
| P0-1 | Serial connection with manual and auto-detect settings | Given a Z3805A on COM3 at 9600-8-N-1, when the user selects auto-detect, then the app connects and displays the `*IDN?` string within 20 s |
| P0-2 | Echo-tolerant line protocol | Given `FDUPlex ON`, when any command is sent, then the echoed line is discarded and only the response reaches the parser |
| P0-3 | Main window showing mode + tracked count | Given the receiver transitions to holdover, when the next fast poll completes, then the main window shows red ⚠ Holdover within 2 s |
| P0-4 | `:SYST:STAT?` parser with fixture tests | All eight fixtures in §11.1 parse with zero exceptions and correct field assertions |
| P0-5 | Receiver Details window with Overview, Satellites, Position, Timing, Holdover, Time, Diagnostics pages | Every field in the source status screen is represented somewhere in the details UI |
| P0-6 | Command catalog with tier enforcement | Unit test: `CommandCatalog.All` contains zero entries matching `BlockedPatterns` |
| P0-7 | No blocked command is reachable or visible | Manual audit: search the built binary's string table for `DOWNL`, `ERAS`, `LANGuage` — only the validator regex may match |
| P0-8 | Tier-C confirmation dialogs | Given the user clicks *Force holdover*, when the dialog appears, then the confirm button is disabled until "I understand" is ticked |
| P0-9 | Sky plot | Given six tracked satellites, when the Satellites page renders, then six markers appear at correct polar positions, sized by signal strength and filled from the §9.4.4 sequential ramp, with the elevation-mask circle drawn |
| P0-10 | Week rollover detection and corrected display | Given a device reporting 27 Dec 2006 and system date 11 Aug 2026, then the UI shows the corrected date with an ⓘ badge and the raw date in tooltip |
| P0-11 | Antenna delay with cable calculator | Given LMR-400 at 20 m, then computed delay is 78.7 ns ±0.5 |
| P0-12 | Position survey start/monitor/adopt | Survey progress updates on each full poll; adopt is tier C |
| P0-13 | Diagnostic log read, filter, export, clear | Clear is tier C; export writes UTF-8 CSV |
| P0-14 | Graceful disconnect and auto-reconnect | Given the USB adapter is unplugged, then the app shows Disconnected within 10 s and reconnects within **45 s** of replug. **⚠ Amended 28 Aug 2026 (#14).** Was 30 s, which §7.2's own backoff makes unreachable: the cap *is* 30 s, so an adapter returning just after a failed attempt waits the full interval and then needs ~2.2 s to open the port and finish auto-detect — ≈32 s, measured. The two clauses could not both hold. 45 s leaves headroom over the 30 s cap without weakening the requirement to nothing, and the reconnect stays event-driven within a poll of the adapter reappearing. Reaching ~2 s regardless of backoff needs arrival detection (`WM_DEVICECHANGE` / `DBT_DEVICEARRIVAL`) rather than a timer, which is not required here. Found by the 28 Aug manual QA run, which passed every other clause. |
| ~~P0-15~~ | ~~MSIX package passing WACK~~ | **Deferred 21 Aug 2026 (#15, #39).** Store submission is not the goal for this version; the goal is that a **non-developer can install the package**, which is a different problem and shipped as #164. WACK is a submission gate rather than a quality gate, and tests little this project’s five CI gates do not already cover. The MSIX itself is unaffected — a sideloaded install uses the same package — so the single-project MSIX, the framework-dependent deployment, the lone `runFullTrust` capability, the 53 generated assets and the third-party notices all stand. `build/Invoke-Wack.ps1` still works if the Store returns. |
| P0-16 | Accessibility criteria A11Y-1 through A11Y-13 (§9.12) | Each by its stated verification method; A11Y-3 and A11Y-4 gate CI |
| P0-17 | The §9 token set is implemented in `Themes/` with Light, Dark, and HighContrast dictionaries for every token | CI greps `Views/` and `Controls/` for hex colour literals and fails on any hit (§9.13 item 2) |
| P0-18 | `StatusMedallion` renders the 60-second radial TI sparkline, updating with no animation | Given a fast poll delivers a new TI value, then the ring redraws within one frame and no `Storyboard` targets the geometry (§9.8.2) |
| P0-19 | Every severity indication in the app renders through `SeverityPill` | Greyscale screenshot of every page and state remains unambiguous (A11Y-12); no bare coloured `Ellipse` in any view |
| P0-20 | Numeric readouts use tabular figures, fixed decimals, U+2212 minus, and reserved width | Given TI steps from −33.1 to −9.8, then no glyph shifts horizontally (§9.5.3) |

### P1 — fast follow

| ID | Requirement |
|---|---|
| P1-1 | EFC and 1 PPS TI trend charts with 1 h/6 h/24 h/7 d ranges and CSV export |
| P1-2 | SQLite trend persistence across restarts |
| P1-3 | Satellite include/ignore management dialog |
| P1-4 | Status register page with mask editing |
| P1-5 | Self-test with per-subsystem selection |
| P1-6 | Compact main-window mode and always-on-top |
| P1-11 | System accent opt-in with the ΔE₀₀ collision warning (§9.4.2) |
| P1-12 | `SkyPlotControl` non-spatial `ListView` alternate view (A11Y-11) |
| P1-7 | Advanced Console (catalog picker + transcript) |
| P1-8 | Experimental read-only queries, opt-in |
| P1-9 | Windows notification on holdover entry / lock loss |
| P1-10 | System tray icon reflecting lock state |

### P2 — designed for, not built

| ID | Requirement |
|---|---|
| P2-1 | Multiple simultaneous receivers with a device switcher |
| P2-2 | Z3805A Port 2 time-of-day packet decode as a second data source (15-byte binary, 9600-8-N-1, fixed format) |
| P2-3 | Allan deviation computation from logged TI data |
| P2-4 | Model auto-profiles for 58503A/B, 59551A, Z3801A, Z3816A with per-model command masking |
| P2-5 | Widget / Windows lock-screen status |

---

## 14. Open Questions

| ID | Question | Owner | Blocking? |
|---|---|---|---|
| OQ-1 | Bit assignments for `:STAT:OPER:*` and `:STAT:QUES:*` registers are not in the fetched portion of the manual. Chapter 5 pp. 5-48 to 5-70 of 097-58503-13 contains the full status-reporting section. **Retrieve and transcribe these before implementing §10.10.** Until then, ship the register page showing raw values with unmapped bits. | Engineering | Blocks P1-4 only |
| OQ-2 | Does the Z3805A accept `:SYST:COMM:SER2:*`? The second port is TOD-broadcast-only, but the parser may still respond. Probe at connect and record the result. | Engineering | No |
| OQ-3 | Is there a documented `PROMpt` node (`:SYST:COMM:SER1:PROM OFF`) to suppress the `scpi>` prompt? The keyword appears in the firmware string table and GPSCon requires the prompt *on*, implying it is settable. If it exists and works, it simplifies the read loop — but treat it as tier C and keep prompt-tolerant parsing regardless. | Engineering | No |
| OQ-4 | ~~Exact `:PTIM:TCOD?` response format and its 20–980 ms lead relative to the 1 PPS.~~ **Answered 21 Aug 2026 (#37).** The bench receiver is in **T2**, not the documented T1 default — read `:PTIM:TCOD:FORM?` and branch, never assume. The message is 23 characters, its checksum is the sum of the 21 preceding characters mod 256 (verified on 103/103 samples), and it is emitted on the receiver’s own 1 Hz cadence **509 ms** before the 1 PPS it names, jitter ≤ 2.4 ms. It does **not** answer on demand. Worked decode in #37; the format query is now catalogued (§8.2, §10.14). | Engineering | Closed |
| OQ-5 | Confirm `LiveChartsCore.SkiaSharpView.WinUI` supports Windows App SDK 2.3.x. If not, hand-roll the trend renderer on `Canvas`. | Engineering | Blocks P1-1 |
| ~~OQ-6~~ | ~~Publisher identity and privacy policy URL from Partner Center.~~ **Closed 21 Aug 2026 (#39), deferred with P0-15.** The Partner Center identity values, the reserved name and the privacy URL are all Store-submission artefacts, and the manifest `TODO:` markers stay as placeholders that a sideloaded install never reads. `docs/privacy.md` is written and committed; **GitHub Pages is deliberately not enabled**, because the only thing that required the URL to resolve was submission. | Product | Closed |
| OQ-8 | Does `WinZ3805A` survive Store certification as a display name, or should the display name be descriptive (e.g. *"GPSDO Monitor for Z3805A"*) with `WinZ3805A` kept only as package identity and assembly name? See §6.3. Decide before first submission, not before first commit — the two are deliberately decoupled. | Product | No |
| OQ-7 | Should the app expose `:SYST:PRESet` at all? It is recoverable but wipes antenna delay and position — arguably the most annoying non-destructive command. Recommend keeping it, tier C with the "I understand" tick. | Product | No |

**Design questions are tracked separately in §9.14** as OQ-D1 through OQ-D7, to keep design and engineering decisions reviewable by their respective owners. Two carry into this table because they gate engineering work:

| ID | Question | Owner | Blocking? |
|---|---|---|---|
| OQ-D1 | `Typography.NumeralAlignment` availability in WinUI 3 (§9.5.3, §9.14) | Engineering | Blocks `ReadoutTile`, P0-20 |
| OQ-D3 | Medallion ring shows 1 PPS TI or EFC (§9.14) | Product | Blocks P0-18 |

---

## 15. Implementation Sequence

1. **Device library first.** `SerialTransport` + `LineProtocol` + echo/prompt handling, built on `PipeReader` (§6.4), with a `FakeTransport` that replays fixture files through the same pipe. Inject `TimeProvider` from the start — retrofitting it later touches every timing path. Prove the transaction loop against fixtures before touching hardware.
2. **`StatusScreenParser`** with the eight fixtures and full assertion coverage. This is the highest-risk component; do it while there is no UI to distract.
3. **`CommandCatalog`** with tier classification and the `BlockedPatterns` unit test (P0-6).
4. **`DeviceSessionService` + `PollingService`**, verified against real hardware.
5. **Design foundation before any view.** Implement `Themes/` in full — colour, typography, spacing, radius, elevation, and motion tokens with Light, Dark, and HighContrast dictionaries (§9.4–§9.8) — plus the shared controls `SeverityPill` and `ReadoutTile`. Resolve OQ-D1 with a spike first. Land the CI hex-literal check (P0-17) at the same time, so the rule is enforced from the first view rather than retrofitted. **Do not build a page before this step exists**; retrofitting tokens onto finished XAML is where design systems die.
6. **`StatusMedallion`** (P0-18) — the signature element, built and reviewed in isolation against a fixture-driven TI stream before it is placed in a window.
7. **Main window** (P0-3) — smallest useful vertical slice, proves the whole stack end to end.
8. **Details window shell**: custom title bar (§9.7.3), `NavigationView` with the §9.6.1 breakpoints, then pages in order: Overview → Satellites → Position → Timing → Holdover → Diagnostics → Time.
9. **`SkyPlotControl`** including its keyboard model and automation peers (A11Y-10, A11Y-11).
10. **Confirmation dialog infrastructure** per §9.7.4, then wire every tier-C command through it.
11. **Accessibility pass** against A11Y-1 to A11Y-13, and the §9.13 anti-pattern audit.
12. **MSIX manifest, assets, WACK**, submission dry run.
13. P1 items in listed order.

---

## 16. Reference Material

- Symmetricom, *58503B GPS Time and Frequency Reference Receiver and 59551A GPS Measurements Synchronization Module — Operating and Programming Guide*, 097-58503-13 Issue 1, March 2000. Chapter 4 (command quick reference) and Chapter 5 (command reference) are the normative source for all command syntax, ranges, and defaults in this document. Available at `leapsecond.com/museum/hp58503a/097-58503-13-iss-1.pdf`.
- Symmetricom, *Z3801A GPS Receiver User's Guide*, 097-z3801-01 Issue 1.
- Z3801A firmware string dump, `leapsecond.com/museum/z3801a/eeprom.htm` — source for the undocumented parser keywords listed in §8.4 and §8.5.

**The Z3801A guide is a source, not only a cross-reference.** `:PTIM:TCOD:FORMat` is documented in its Table 4-2 and in no part of the 58503B guide, so a catalog derived from the 58503B guide alone misses it — which is what happened, and what #154 records. When a command is absent from the 58503B guide, check the Z3801A guide before concluding the receiver does not have it: the Z3801A is the closer sibling, and the 58503B guide is a **joint** 58503A/59551A document whose `(59551A Only)` sections describe hardware the Z3805A does not have.

**Model qualifiers in the guides do not predict what the parser accepts.** Measured on the bench Z3805A on 21 Aug 2026: `:PULS:CONT:STAT?`, `:SENS:DATA:POIN?` and `:FORM:DATA?` all answer despite being marked 59551A-only, while `:PTIM:PPS:EDGE?` and `:PULS:REF:EDGE?` are rejected with `E-113`. Subsystems are split rather than present or absent as blocks, so the note below — probe at connect, disable on error — is the operative rule and the model column is not.

**`:PTIM:TCOD:FORMat` is catalogued as a query only; its setter is deliberately absent.** Changing the format changes what every consumer of the time code output sees, which makes it tier C by the same reasoning §8.3 applies to `:PTIM:TZONe`. It is not catalogued because nothing in the application reads the time code, so the setter would be a tier C write existing solely to break other equipment's decoding. It goes in when something needs it, with a §8.3 consequence line of its own.

**Note on the Z3805A specifically:** no Z3805A-specific programming manual was published. The command set is inherited from the 58503A/B SmartClock firmware family, which is why the 58503B guide is the reference. Where behaviour diverges — Port 2 being TOD-only, dual 10 MHz and dual 1 PPS outputs, 9600-8-N-1 default rather than the Z3801A's 19200-7-E-1 — this document calls it out explicitly. Any command whose Z3805A behaviour is unverified should be probed at connect and disabled if it errors, rather than assumed present.

---

## Appendix A — Change Summary (design system revision)

Added **§9 Design System** and reconciled the surrounding document. Section numbering shifted: former §9–§15 are now §10–§16. All internal cross-references were updated.

### Added

| Section | Contents | Why |
|---|---|---|
| **§9.1** Design thesis | *The instrument face* thesis, the state medallion as signature moment, and the thesis sanity check | Nothing in the document previously stated a point of view; §10 was specifying surfaces with no shared basis for judging them |
| **§9.2** Materials, layering, elevation | Mica Alt, L0–L3 hierarchy, the "shadow only if dismissible" rule, Windows 10 fallback | Former §10.1 said "Mica backdrop" with no layer model or degradation path |
| **§9.3** Corner radius | 4 / 8 / circle, circle reserved for the medallion | Was unspecified; inconsistent radii were an unguarded risk |
| **§9.4** Colour | Surface and text tokens, brand accent ramp with computed contrast, semantic colours with shape coding, three data-viz palettes, contrast floors | Former §10.3 used stock `SystemFillColor*` keys ad hoc with no ramp, no data-viz palette, and no CVD strategy |
| **§9.5** Typography | Segoe UI Variable / Cascadia Mono split, seven-step ramp, four readout styles, seven numeric typesetting rules, line length, sentence case | Entirely absent. Rules 1–7 address the specific ways data-dense instrument UIs fail |
| **§9.6** Layout, spacing, density | 4 px base scale, 1320 max-width, three breakpoints, minimum window sizes, single-density justification | Only minimum window sizes existed, and one was wrong |
| **§9.7** Navigation and commanding | `NavigationView` justification, two-level depth cap, shell wireframes at two breakpoints, custom title bar spec, command placement model, nine accelerators | Former §10.2 named `NavigationView` without display modes, and no accelerators existed |
| **§9.8** Motion | Four duration and three easing tokens, thirteen-row motion spec with reduced-motion fallbacks, the three `Instant` rows | Entirely absent |
| **§9.9** Iconography | Segoe Fluent baseline, four custom icons with construction rules, size table, the three-condition rule for label-less icons, explicit no-illustration decision | Entirely absent |
| **§9.10** Component inventory | Stock control assignments plus seven custom controls with behavioural contracts | Former §10 referenced `SkyPlotControl` and `TrendChart` without defining what they must do |
| **§9.11** State matrix | Twelve states with surface, copy pattern, and notes; the interruption ladder; copy rules; validation model | Error and empty handling was scattered across §10 pages |
| **§9.12** Accessibility | Thirteen numbered criteria with verification methods; two gate CI | Former P0-16 was a single unverifiable line |
| **§9.13** Anti-patterns | Ten reviewable prohibitions | Requested; each is checkable against a build |
| **§9.14** Open design questions | OQ-D1 to OQ-D7 with the assumption made for each | Separates design decisions from engineering ones for review |

### Edited

| Section | Change | Reason |
|---|---|---|
| **§2** Goals | G1 measure narrowed to mode and count legibility; G2 rewritten to reference §9.5.1 and §9.10; **G6 added** for visual identity | G2's old measure ("all data in Fluent cards/controls") was satisfiable by the default template, so it could not detect the failure it existed to prevent |
| **§6.2** Solution layout | Added `Themes/`, expanded `Controls/` to the seven custom controls, added `Assets/Fonts/`, added `WzMotionService` | Token dictionaries and the embedded font need a home in the tree |
| **§8.3** Tier C confirmations | Confirmation button model rewritten | The old text — "destructive action as the secondary button styled `AccentButtonStyle` only where safe" — was ambiguous and put accent styling on destructive actions. Now: destructive action is PrimaryButton in `WzDestructiveButtonStyle`, Cancel is CloseButton and `DefaultButton`, so focus and Enter land on the safe option |
| **§10.1** | "Design principles" replaced with a pointer into §9, retaining only the two principles that are functional rather than visual | Everything else is now specified concretely in §9 |
| **§10.2** Window inventory | Details window minimum **1000×700 → 1024×720**; added minimum-size column; noted the eight-destination cap | 1000 px sits below `NavigationView`'s `Left` threshold, so the window would have opened in icon-rail mode at its own minimum — a layout bug baked into the requirement |
| **§10.3** Main window | Rewritten around `StatusMedallion`; wireframe annotated with tokens; compact-mode wireframe added; severity table rewritten as colour + shape + glyph triples; "locked with zero satellites" promoted to a `SeverityPill` | Realises the signature moment and removes ad-hoc colour references |
| **§13** P0 | P0-9 and P0-16 rewritten; **P0-17 to P0-20 added** (token set with CI enforcement, medallion, `SeverityPill` universality, numeric typesetting) | Design requirements need acceptance criteria or they will not be built |
| **§13** P1 | **P1-11, P1-12 added** (system accent opt-in, sky plot list alternate) | Deferred but designed for |
| **§14** Open Questions | Added a pointer to §9.14 and surfaced OQ-D1 and OQ-D3 as engineering-blocking | Two design questions gate code |
| **§15** Implementation Sequence | **New step 5** (design foundation before any view) and **step 6** (medallion in isolation); steps renumbered to 13; accessibility pass added as step 11 | Tokens retrofitted onto finished XAML do not survive; the sequence now forces the order that works |

### Removed

| Removed | Replaced by |
|---|---|
| Former §10.1 "Design principles" bullet list | §9.2 (Mica and layering), §9.4.3 (colour-plus-shape), §9.12 (accessibility criteria), §10.1 (the two surviving functional principles) |
| Ad-hoc `SystemFillColor*` references in the §10.3 status table | `WzSuccessBrush` / `WzCautionBrush` / `WzCriticalBrush` / `WzNeutralBrush` with shapes and glyphs (§9.4.3) |
| "48px status glyph" and similar inline pixel values in §10 wireframes | Token references resolving to §9.5 and §9.6 |

### Tensions flagged rather than silently resolved

| Tension | Resolution |
|---|---|
| `NavigationView` `Left` needs ≥ 1008 px; §10.2 required 1000 px | Functional requirement (a usable Details layout) wins. Minimum raised to 1024. Noted inline in §9.6.2 and §10.2 |
| Design would prefer a single always-visible severity colour; §8 requires tier C confirmations to read as destructive | Functional requirement wins. `WzDestructiveButtonStyle` uses `WzCriticalBrush` as foreground on a transparent fill, so it reads as destructive without a saturated block of alarm colour in normal chrome |
| The instrument thesis argues for the smallest possible palette; charting needs eight separable series | Charting palette is a **separate namespace** (§9.4.4) that never reuses semantic tokens, so UI colour stays minimal while charts get what they need |
| §11 requires the parser never to throw and to mark fields unknown; a clean design wants no empty cells | Functional requirement wins. Unknown renders as `—` in `WzTextTertiaryBrush` (§9.11), which is honest and visually quiet |

---

## Appendix B — Change Summary (naming and location revision)

| Change | From | To |
|---|---|---|
| Document title and product name | SmartClock Monitor | **WinZ3805A** |
| Solution, repository, root namespace | `SmartClockMonitor` | **`WinZ3805A`** |
| Projects | `SmartClockMonitor` / `.Device` / `.Tests` | **`WinZ3805A`** / `.Device` / `.Tests` |
| Design token prefix (145 references) | `Scm*` | **`Wz*`** |
| Solution layout | flat | **`src/` and `tests/` subfolders**, with `docs/` and `CLAUDE.md` at the root |
| Working copy location | unspecified | **`C:\Users\Tony\source\WinZ3805A`** |

### Sections edited

- **§6.2** retitled *Repository location and solution layout*. Adds the absolute working-copy path, the `docs/requirements.md` specification path, an `src/`+`tests/` tree, and a naming-conventions table covering repository, assembly, token prefix, package identity, and display name.
- **§6.3** trademark bullet replaced with a fuller **trademark position**. `WinZ3805A` contains a model designation rather than a company mark, which is a weaker claim and defensible as nominative descriptive use. Two hedges added: compatibility claims live in listing body text only, and **display name must stay decoupled from package identity** — package identity is effectively permanent, display name is a one-line change, so the product name must be read from the manifest and never hard-coded in XAML.
- **§9.11** first-run copy changed from *"WinZ3805A talks to HP and Symmetricom GPS receivers…"* to *"This app talks to…"*. A model number reads poorly as the subject of a sentence.
- **§14** OQ-6 narrowed to publisher identity and privacy policy. **OQ-8 added**: whether `WinZ3805A` survives certification as a display name, or whether the display name should be descriptive. Explicitly *not* blocking first commit.

### Deliberately not renamed

**"SmartClock" as HP terminology.** The status screen field `SmartClock Mode`, and phrases such as *SmartClock family*, *SmartClock firmware*, and *SmartClock oscillator learning*, are Hewlett-Packard's and Symmetricom's own product vocabulary reproduced from 097-58503-13. They are what the device prints and what the manual calls things. Renaming them would make the parser specification wrong. Only the seven occurrences of the former product name were changed.
