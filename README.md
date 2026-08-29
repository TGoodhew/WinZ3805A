# WinZ3805A

A WinUI 3 desktop application for monitoring and controlling HP/Symmetricom
SmartClock GPS-disciplined oscillators over RS-232.

The SmartClock family — the Z3805A and its siblings — is widely used in home and
small labs as a 10 MHz frequency and 1 PPS time reference. The receivers expose a
rich SCPI command set over a serial port, but the tools built to drive them are
Windows-9x-era applications that need serial-port shims, look badly out of place
on modern Windows, and in several cases put destructive firmware commands
directly next to harmless queries. The alternative is screen-scraping
`:SYST:STAT?` in a terminal emulator.

Two ideas shape this replacement:

- **A glanceable primary window.** The receiver's state should be readable at a
  glance on a second monitor that has been left running for weeks — not a
  reproduction of the device's 80×24 terminal screen, but a native Fluent
  surface.
- **Destructive commands are unreachable, not merely warned about.** The command
  catalog is an allowlist. Commands that can damage a receiver's calibration or
  firmware are absent from it entirely — they are not entries carrying a warning
  flag, and there is no dialog that leads to them.

## Status

Early development; **there is no release yet**, and the application does not yet
do anything useful when run. The serial transport and line protocol are
implemented and tested against a real receiver; the status-screen parser, command
catalog, design-token layer, and every view are still to come. Progress is
tracked in the [issue backlog](https://github.com/TGoodhew/WinZ3805A/issues),
whose `§` references resolve against the specification.

## Supported hardware

| Receiver | Serial default |
|---|---|
| HP/Symmetricom **Z3805A** (reference device) | 9600-8-N-1 |
| Symmetricom Z3801A | commonly 19200-7-E-1 |
| HP/Symmetricom 58503A/B | — |
| Symmetricom 59551A | — |
| Symmetricom Z3816A | — |

These units share the 58503A/B SmartClock command set. Because the defaults
differ between siblings, every serial parameter is user-settable — baud, data
bits, parity, and stop bits — and the connection dialog offers an auto-detect
that walks the most likely combinations sending `*IDN?` until a valid identity
returns. Handshaking is always off; DTR and RTS are asserted on open. §7.1 of the
specification gives the full parameter ranges.

> **On ARM64 machines:** the application is built for x64 only and runs on
> Windows on ARM under emulation. The thing that actually bites there is the
> driver, not the emulation: third-party USB-serial drivers frequently lack
> ARM64 builds — Prolific PL2303 and CH340 clones especially, while FTDI is
> generally fine. A serial-port driver is kernel-mode, so emulating the
> application does not help. If no ports enumerate on an ARM64 machine, the
> adapter's driver is the likely cause rather than the application.

## Supported platforms

The application is a Windows App SDK (WinUI 3) desktop app, so what it runs on is
what the Windows App SDK runs on.

| | |
|---|---|
| **Minimum** | Windows 10, version 1809 (build 10.0.17763) |
| **Also supported** | Windows 10 21H2 / 22H2 / 23H2, Windows 11 21H2 through 25H2 |
| **Windows Server** | Server 2019 (17763) and Server 2022 (20348) |
| **Architecture** | **x64 only.** ARM64 runs under emulation — see the note under *Supported hardware* |
| **Runtime** | .NET 10 (LTS) and the Windows App SDK runtime, both resolved at install time |

The floor is set in [`WinZ3805A.csproj`](src/WinZ3805A/WinZ3805A.csproj) as
`TargetPlatformMinVersion` `10.0.17763.0`, while the project *builds* against the
10.0.26100 SDK (`TargetFramework` `net10.0-windows10.0.26100.0`). Those two are
different jobs: the first is the oldest Windows the app will install and run on,
the second is the API surface it compiles against.

> **Windows 10 1809 is supported by the SDK but is no longer a healthy target.**
> Mainstream servicing for 1809 has ended on both Home/Pro and Enterprise. Only
> the LTSC Extended channel is still serviced, until 9 January 2029. Treating
> 1809 as the floor is a compatibility statement, not a recommendation — anyone
> choosing a machine for this application should be on Windows 11.

**A caveat specific to Windows App SDK 2.x.** From 2.0 the minimum is no longer
one number for the whole SDK; it varies by component. The refactored
`Microsoft.Windows.AI.MachineLearning` package supports Windows 10 v1903 and
later, and Microsoft's guidance is to keep using `Microsoft.WindowsAppSDK.ML` if
1809 support is needed. **This project references
`Microsoft.WindowsAppSDK.ML`**, which is the path that retains the 1809 floor —
see the comment in the csproj, which explains that the reference is there because
a framework-dependent build refuses to restore without it.

Microsoft's published support matrix currently documents releases up to 1.8 and
does not yet list 2.x, so the 1809 floor for **2.3.1 specifically** rests on the
component guidance above plus the SDK packages themselves: nothing in the
restored 2.3.1 package tree enforces a `TargetPlatformMinVersion` above 17763,
and `Microsoft.WindowsAppSDK.Base` still special-cases `10.0.17763.0` in its
self-contained targets. §6.1 asks for exactly this check and it has now been
made to that depth. **It has not been confirmed by running the application on
Windows 10** — there is no such machine on this project.

Sources: [Windows App SDK and supported Windows releases](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/support),
[Windows App SDK 2.0 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0).

## Installing

There is no release yet. When there is, the application will be distributed
through the Microsoft Store, with a signed MSIX available for sideloading.
Deployment of the Windows App SDK is framework-dependent rather than
self-contained, so the framework package dependency is resolved at install time
(§6.3).

## Building from source

### Prerequisites

- **.NET 10 SDK (LTS)** — the exact version is pinned in
  [global.json](global.json); `rollForward: latestFeature` accepts a newer patch.
- **Visual Studio 2026** with the *.NET desktop development* workload and the
  Windows App SDK extension. Windows App SDK 2.3.1 itself is restored from NuGet
  rather than installed separately.
- A machine meeting the floors in [Supported platforms](#supported-platforms) above.
  For building specifically, the app project needs the **10.0.26100** Windows SDK.

### Build

MSBuild is not on `PATH` by default. Resolve it with `vswhere`, or use the full
path:

```powershell
$msb = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe'

& $msb WinZ3805A.sln -t:Restore -p:Configuration=Debug -p:Platform=x64
& $msb WinZ3805A.sln -t:Build   -p:Configuration=Debug -p:Platform=x64
```

**Prefer MSBuild over `dotnet build` locally.** Both work, but `dotnet build`
does not surface XAML compiler diagnostics: a malformed `.xaml` fails with no
indication of which file or what is wrong, while MSBuild reports it correctly.
This repository is XAML-heavy by design, so the difference matters in practice.
(CI uses `dotnet build` regardless — the hosted runner's Visual Studio MSBuild is
too old to load `net10.0` projects. A XAML failure in CI may therefore not name
the file; reproduce it locally with MSBuild.)

Restore is per-platform, because the runtime identifier — and so the assets file
— differs between them. The valid combinations are `Debug` and `Release`
configurations against the `x64` platform. There is no `AnyCPU` (the Windows App
SDK is native), no `x86`, and — since 15 August 2026 — no `ARM64`: the
certification kit installs and runs what it certifies and so cannot cross-test,
and there is no ARM64 hardware to certify a submission on. See §6.1.

`TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are both on, so a clean
build produces zero warnings and a code-style violation is a build error rather
than an editor suggestion.

### Run

The app is a single-project MSIX and needs package identity to launch, so run it
from the build output rather than by starting the `.exe` directly:

```powershell
winapp run src\WinZ3805A\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64 --detach
```

### Tests

The transport, parser, and command catalog live in a library with no UI
references, so the tests run headlessly and the plain SDK is enough here:

```powershell
dotnet test tests\WinZ3805A.Tests\WinZ3805A.Tests.csproj
```

Fixtures are status screens captured from real hardware.
[tests/WinZ3805A.Tests/Fixtures/README.md](tests/WinZ3805A.Tests/Fixtures/README.md)
records their provenance and which receiver states are still missing.

### The CI gates

Two of the design-system acceptance criteria are enforced by script rather than
by review. Both are dependency-free and answer in about a second, which makes
them the fastest local check available — CI runs them before any restore, so a
regression fails in seconds instead of after four builds:

```powershell
pwsh build/Test-NoHexLiterals.ps1     # no hex colour literals outside Themes/Colors.xaml
pwsh build/Test-IconOnlyButtons.ps1   # icon-only controls carry an automation name and a tooltip
```

Run them before pushing.

## Repository layout

```
docs/requirements.md          the specification
src/WinZ3805A/                WinUI 3 app, single-project MSIX
src/WinZ3805A.Device/         class library — no UI references
tests/WinZ3805A.Tests/        xUnit, with Fixtures/ for captured status screens
build/                        the two CI gate scripts
```

The `Device` library has zero dependency on `Microsoft.UI.*`. All parsing,
command classification, and transport lives there, which is what makes the
highest-risk logic testable without a UI.

## Adding a receiver

WinZ3805A talks to the HP/Symmetricom SmartClock family, but the device-specific
knowledge sits behind one interface so another GPS-disciplined oscillator can be
added without touching the UI. This section is the walkthrough: what to
implement, in what order, and where each decision is made.

**The seam is the device, not the wire.** `ITransport` already abstracts the
serial port. `IReceiverDriver` abstracts what is *said* over it and how the
answers are read.

### What a driver owns

`src/WinZ3805A.Device/Drivers/IReceiverDriver.cs`, and nothing else:

| Member | What it decides |
|---|---|
| `Family` | A short name for logs and diagnostics — `"SmartClock"` |
| `Recognises(identity)` | Whether this driver handles the receiver that answered `*IDN?` |
| `Commands` | The **allowlist** of everything this receiver may be sent |
| `Find(mnemonic)` | One command by name, or `null` if this receiver has none |
| `IsBlocked(header)` | Whether a typed header is one of this receiver's §8.4 exclusions |
| `TimeoutFor(mnemonic)` | How long to wait, per §7.2's classes |
| `Cadence` | How often to poll — fast sweep and full sweep |
| `AutoDetectSequence` | Serial configurations to try, most likely first |
| `Parse(response)` | Turn a status response into a `ReceiverStatus` |

The worked example is
[`SmartClockDriver.cs`](src/WinZ3805A.Device/Drivers/SmartClockDriver.cs). Read
it alongside this section — it is deliberately thin, because each piece it
returns already existed and the driver is only where "which receiver" stopped
being implied.

### Step 1 — capture what the receiver actually says

Before any code. Connect the receiver with a terminal and save its real output
to `tests/WinZ3805A.Tests/Fixtures/captured/`, one file per interesting state.

**Capture the awkward states, not just the good one.** The states worth having
are the ones you cannot conjure later: power-up, acquiring, holdover, a failing
self-test, a survey in progress. `locked` is the easiest to get and the least
informative. Follow
[`Fixtures/captured/capture-log.md`](tests/WinZ3805A.Tests/Fixtures/captured/capture-log.md)
for how existing captures are recorded — each says what the receiver was doing
and when, because a fixture whose provenance is unknown cannot settle an
argument later.

Save the bytes verbatim. Do not tidy whitespace: column positions carry meaning,
and trailing spaces are often significant.

### Step 2 — decide what `ReceiverStatus` can hold

[`Models/ReceiverStatus.cs`](src/WinZ3805A.Device/Models/ReceiverStatus.cs) is
the common currency between every driver and the whole UI.

**A field your receiver has no equivalent for is left `null`.** That is not a
workaround, it is the contract: §11.1 requires it of the parser, and every
readout in the UI already renders `null` as an em dash. Do not invent a value to
fill the shape, and in particular do not use zero — a 1 PPS offset of `0 ns`
reads as a *perfect* lock, not as a missing one.

Some fields are HP's rather than general, and a new driver will simply leave
them empty: `SmartClockMode`, `Tfom`, `Ffom`, `WeekRolloverEpochs`. If your
receiver has a concept the record cannot express at all, add a nullable field
rather than overloading an existing one, and raise the §11.2 amendment — the
specification describes that record field by field.

### Step 3 — write the parser

Your `Parse` implementation, called once per sweep.

**It must never throw.** §11.1 is absolute about this and the reason is
structural: the poll loop calls it, and an exception there stops the receiver
being polled at all. An unreadable field becomes `null` and the reason goes into
`ReceiverStatus.ParseWarnings`, which Diagnostics displays. An unrecognisable
response yields a status whose fields are all absent and whose warnings say so.
Wrap the whole body in a last-resort `catch`, as
[`StatusScreenParser`](src/WinZ3805A.Device/Parsing/StatusScreenParser.cs) does.

**Do not hard-code column positions.** The SmartClock parser derives every
column from the header row, which is what lets it survive a firmware revision
that shifts a field by a character. If your receiver speaks a binary protocol
this does not apply — but the equivalent discipline does: parse by field
identity, never by "the byte that was at offset 12 last time".

Write the tests against the fixtures from step 1, asserting real values from real
captures rather than values you computed with your own parser.

### Step 4 — declare the command catalog

An **allowlist** (§8.1). A command that is not in it cannot be sent, and that is
the property everything else depends on.

Each entry carries its safety tier (§8.2):

| Tier | Meaning | UI treatment |
|---|---|---|
| **A** | Read-only queries | Sent freely |
| **B** | Writes with a bounded, reversible effect | Sent with feedback |
| **C** | Writes that disturb service | Confirmation dialog with consequence text, and sometimes an explicit acknowledgement |

**Tier C confirmation text must say what actually happens**, in the user's terms
rather than the protocol's. Get this from measurement, not assumption: the
SmartClock self-test text said it "may briefly interrupt normal operation" until
someone ran it and found the receiver drops out of lock and re-acquires over
several minutes.

### Step 5 — the exclusions, and read this one twice

`IsBlocked` implements §8.4 — commands that must never be sent, offered,
displayed, logged or referenced.

**Every rule here is a safety rule, and the wrong abstraction is a defect rather
than a missing feature.**

1. **Decide your own.** Do not inherit another family's list. It is not a
   conservative default: a command harmless on one receiver may be destructive
   on another, and the names need not even correspond.
2. **Return a verdict, never the patterns.** `IsBlocked` returns `bool` by
   design. §8.4 requires that excluded commands do not exist as data any view can
   enumerate, bind to, or log wholesale. A driver exposing a list would
   reintroduce exactly what the rule forbids — and
   `ReceiverDriverTests.TheDriverContractCannotExposeTheExclusionsAsData`
   asserts against the *interface* by reflection, so it binds you and not merely
   the existing driver.
3. **Keep them in one file.** The SmartClock patterns live only in
   [`Commands/BlockedCommands.cs`](src/WinZ3805A.Device/Commands/BlockedCommands.cs),
   which is `internal`. Put yours in one equivalent file and reference it from
   nowhere else. `build/Test-NoBlockedCommands.ps1` reads its tokens out of that
   file rather than restating them, so it keeps working if you follow the same
   shape — and it runs in CI on every push.
4. **Not in issue titles, branch names, commit messages, TODOs or test
   fixtures.** The rule covers writing them down anywhere, which is why the
   driver tests discover an excluded header by asking the validator rather than
   containing one.

### Step 6 — timeouts and cadence

**These are measurements, not conventions.** Copying another receiver's figures
gives numbers that are either wastefully long or short enough to fail healthy
hardware — the SmartClock GPS self-test takes up to 24.0 s against a 30 s class,
and its full status screen takes 3521 ms of wire time, which is why the full
sweep is not simply the fast sweep with more in it.

Time your receiver's slowest command and set the class from that, with headroom.
`TimeoutFor` must return a positive value for **every** input including an
unknown mnemonic: returning `TimeSpan.Zero` fails every transaction instantly
rather than waiting for one.

### Step 7 — wire it up

`DeviceSessionService` and `PollingService` each take an `IReceiverDriver?` as
their last constructor parameter, defaulting to `SmartClockDriver`. Pass yours
from
[`DeviceRegistration.AddDevice`](src/WinZ3805A/Services/DeviceRegistration.cs).

Devices are registered **keyed**, so a second receiver is a second `AddDevice`
call and nothing else (§12).

#### Choosing a driver before the identity is known

There is a genuine ordering problem here, and it is not solved yet. Auto-detect
must send `*IDN?` *before* anything can know what is attached — so the driver
used for that first exchange is chosen before there is an identity to choose it
by. `SmartClockDriver.Recognises(null)` returns `true` for exactly that reason,
which is correct while it is the only driver and wrong the moment there are two.

**If you are adding the second driver, this is the piece you will have to
design.** The likely shape is a probe phase belonging to no driver, sending a
neutral `*IDN?` at each candidate serial configuration, then selecting the driver
whose `Recognises` claims the answer. Raise it rather than quietly making your
driver claim `null` as well — two drivers both claiming an unknown identity is a
race, not a default.

### Step 8 — run the gates

```powershell
dotnet test tests\WinZ3805A.Tests\WinZ3805A.Tests.csproj
pwsh build/Test-NoBlockedCommands.ps1     # §8.4 — the one that matters most here
```

and the rest of the gates listed in [CLAUDE.md](CLAUDE.md). CI runs all of them
before it builds anything.

### What the specification will need

§7, §8 and §11 are written throughout in terms of one receiver family and name
SmartClock behaviour as *the* behaviour. Adding a driver means amending them so
the document and the code do not drift apart — **raise that rather than
absorbing the divergence in code.** Where `docs/requirements.md` and anything
else disagree, the document wins.

## Where the authority lives

- **[docs/requirements.md](docs/requirements.md) is the specification.** It is
  the authority on behaviour, and the `§` references in issues, commit messages,
  and code comments resolve against it. Where anything else disagrees with it,
  the document wins.
- **[CLAUDE.md](CLAUDE.md)** carries the working conventions for the repository.

Both are linked rather than restated here, so there is one authority per fact.

## Naming

`WinZ3805A` is the repository, solution, root namespace, and MSIX package
identity name. The Store *display* name is deliberately a separate thing that can
change at any time, so it is read from the package manifest at runtime and never
hard-coded (§6.3).

The name contains a model designation rather than a company mark. This project is
not affiliated with, endorsed by, or sponsored by HP, Hewlett-Packard, Agilent,
Keysight, or Symmetricom; those names appear here only to describe the hardware
the application talks to.

## Licence

[MIT](LICENSE).
