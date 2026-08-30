# WinZ3805A

A WinUI 3 desktop application for monitoring and controlling HP/Symmetricom
SmartClock GPS-disciplined oscillators over RS-232.

| | |
|---|---|
| **Receivers** | the HP/Symmetricom **Z3805A** and its SmartClock siblings — Z3801A, 58503A/B, 59551A, Z3816A; and, for monitoring only, any NMEA 0183 GNSS talker |
| **Platform** | Windows 10 (1809 or later) and Windows 11, x64 — see [Supported platforms](#supported-platforms) |
| **Stack** | WinUI 3 (Windows App SDK) on .NET 10, packaged as MSIX |
| **Extensible** | every receiver-specific fact sits behind one interface, `IReceiverDriver`; another GPS-disciplined oscillator is a driver plus one registration line, not a fork — see [Adding a receiver](#adding-a-receiver) |

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

Feature-complete against the specification's P0 set and in daily use against a
bench Z3805A; **there is no Store release yet**. The transport, parser, command
model, design system and every view are implemented, with the test suite and
eleven CI gates green. Progress is tracked in the
[issue backlog](https://github.com/TGoodhew/WinZ3805A/issues), whose `§`
references resolve against the specification. Where it stands against Lady
Heather, the tool most people run on these receivers today, is in
[docs/lady-heather-comparison.md](docs/lady-heather-comparison.md).

## Supported hardware

| Receiver | Serial default |
|---|---|
| HP/Symmetricom **Z3805A** (reference device) | 9600-8-N-1 |
| Symmetricom Z3801A | 19200-7-O-1 from the factory; some units in the field 19200-7-E-1 |
| HP/Symmetricom 58503A/B | — |
| Symmetricom 59551A | — |
| Symmetricom Z3816A | — |
| **Any NMEA 0183 GNSS talker** — a u-blox module, a marine receiver; proven against the simulator under `tools/`, not yet against hardware | 4800-8-N-1 (the standard), commonly 9600 |

The SmartClock units share the 58503A/B command set. The NMEA family is the
second driver ([docs/tutorial-nmea-driver.md](docs/tutorial-nmea-driver.md)): it
gets the monitoring core — fix state, satellites, position and time — and is
never written to once recognised; a talker has no disciplined oscillator, so the
timing pages show dashes. Because the defaults
differ between siblings, every serial parameter is user-settable — baud, data
bits, parity, and stop bits — and the connection dialog offers an auto-detect
that walks every registered driver's likely combinations, listening first for a
receiver that talks unprompted and then sending `*IDN?`, until one is recognised.
Handshaking is always off; DTR and RTS are asserted on open. §7.1 of the
specification gives the full parameter ranges.

## Supported platforms

The application is a Windows App SDK (WinUI 3) desktop app, so what it runs on is
what the Windows App SDK runs on.

| | |
|---|---|
| **Minimum** | Windows 10, version 1809 (build 10.0.17763) |
| **Also supported** | Windows 10 21H2 / 22H2 / 23H2, Windows 11 21H2 through 25H2 |
| **Windows Server** | Server 2019 (17763) and Server 2022 (20348) |
| **Architecture** | **x64 only** — see §6.1. Windows on ARM is not a supported configuration |
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
1809 support is needed. **This project keeps
`Microsoft.WindowsAppSDK.ML`**, which arrives transitively with the 2.3.1 meta-package
and is the path that retains the 1809 floor: the csproj references the refactored
`Microsoft.Windows.AI.MachineLearning` directly only to exclude its runtime and native
assets, and its comment explains why `.ML` itself cannot be excluded — a
framework-dependent build refuses to restore without it.

Microsoft's published support matrix currently documents releases up to 1.8 and
does not yet list 2.x, so the 1809 floor for **2.3.1 specifically** rests on the
component guidance above plus the SDK packages themselves: the one package in the
restored 2.3.1 tree that carries a check above 17763 — `Microsoft.Windows.AI.MachineLearning`,
at 18362 — has that check switched off by `Microsoft.WindowsAppSDK.ML`'s props, nothing
else enforces a higher floor, and `Microsoft.WindowsAppSDK.Base` still special-cases
`10.0.17763.0` in its self-contained targets. §6.1 asks for exactly this check and it has now been
made to that depth. **It has not been confirmed by running the application on
Windows 10** — there is no such machine on this project.

Sources: [Windows App SDK and supported Windows releases](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/support),
[Windows App SDK 2.0 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0).

## Installing

The application is installed by sideloading. No package is published yet, so
the install is built from source:
[`build/New-SideloadPackage.ps1`](build/New-SideloadPackage.ps1) builds the
MSIX, signs it, and wraps it — with the x64 Windows App Runtime, an
`Install.cmd` and a plain-language [README](build/sideload/README.txt) — into
`dist\WinZ3805A-<version>-x64.zip`, which a machine with neither Visual Studio
nor an internet connection can install from. Deployment of the Windows App SDK
is framework-dependent rather than self-contained (§6.3), which is why the
runtime travels in the zip.

The package is signed with a self-signed certificate (`build/devcert.pfx`,
generated from the manifest on first use so its subject cannot drift from the
declared publisher), so the person installing trusts it once: `Install.cmd`
asks for administrator permission for exactly that step — adding the
certificate to *Trusted People* and nothing wider — and the README in the zip
explains what that does and does not grant before asking. Uninstall from
*Settings › Apps*, or with
[`build/Uninstall-Sideload.ps1`](build/Uninstall-Sideload.ps1), which also
removes the certificate and is what puts a test machine back to clean.

## Building from source

### Prerequisites

- **.NET 10 SDK (LTS)** — the exact version is pinned in
  [global.json](global.json); `rollForward: latestFeature` accepts a newer patch or
  feature band.
- **Visual Studio 2026** with the *.NET desktop development* workload and the
  Windows App SDK extension. Windows App SDK 2.3.1 itself is restored from NuGet
  rather than installed separately.
- A machine meeting the floors in [Supported platforms](#supported-platforms) above.
  For building specifically, the app project needs the **10.0.26100** Windows SDK.

### Build

This section owns the build, run and test commands; the copy in `CLAUDE.md` is
for agents and follows this one. MSBuild is not on `PATH` by default. Resolve it
with `vswhere`, or use the full path:

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
configurations against the `x64` platform, which is the only one: there is no
`AnyCPU` (the Windows App SDK is native), no `x86` and no `ARM64`. See §6.1.

`TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are both on, so a clean
build produces zero warnings and a code-style violation is a build error rather
than an editor suggestion.

[`build/Invoke-Wack.ps1`](build/Invoke-Wack.ps1) builds the Release MSIX and
runs the Windows App Certification Kit over it. It must run elevated, and it
takes over the desktop for ten to twenty minutes while the kit installs,
drives, and uninstalls the package.

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
records their provenance, which tests assert against each, and which receiver
state is still missing; captures from
[`build/Capture-Fixtures.ps1`](build/Capture-Fixtures.ps1) land in
[Fixtures/captured/](tests/WinZ3805A.Tests/Fixtures/captured/README.md), where
the tests read them in place.

What neither the tests nor the gates can reach — anything that needs a person, a
receiver, or a machine setting — is in [docs/manual-qa.md](docs/manual-qa.md),
the release checklist.

### The CI gates

Eleven acceptance criteria — design-system, accessibility and safety — are
enforced by script rather than by review. All are dependency-free and answer in
seconds, which makes them the fastest local check available;
[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs every one in its own
dependency-free job alongside the build, so a regression fails in seconds instead of
after a full build (a twelfth script there checks the fixture-capture harness rather
than the source). The list, with what each guards and why it exists, is in
[CLAUDE.md](CLAUDE.md); the two below are the ones to know first:

```powershell
pwsh build/Test-NoHexLiterals.ps1       # no hex colour literals outside Themes/Colors.xaml
pwsh build/Test-NoBlockedCommands.ps1   # §8.4's exclusions appear nowhere but their one file
```

Run them all before pushing.

## Repository layout

```
docs/requirements.md          the specification
docs/                         the other project documents — listed under Documentation below
src/WinZ3805A/                WinUI 3 app, single-project MSIX
src/WinZ3805A.Device/         class library — no UI references
tests/WinZ3805A.Tests/        xUnit, with Fixtures/ for captured status screens
tools/NmeaSimulator/          the NMEA 0183 talker the tests and the tutorial run against
build/                        the CI gate scripts and their inputs (palette/, fluent-stock-colours.txt), the fixture-capture harness, the asset generator, and the sideload and WACK packaging scripts
.github/workflows/ci.yml      the gates in their own jobs, alongside the Debug and Release x64 builds and the tests
```

The `Device` library has zero dependency on `Microsoft.UI.*`. All parsing,
command classification, and transport lives there, which is what makes the
highest-risk logic testable without a UI.

## Adding a receiver

WinZ3805A talks to the HP/Symmetricom SmartClock family, but every piece of
device-specific knowledge sits behind one interface — `IReceiverDriver` — so
supporting another GPS-disciplined oscillator means writing a driver, not
modifying the application. What the receiver says — its `*IDN?` answer, or for a
talker the sentences it sends unprompted — chooses the driver at every connect,
the poller sweeps whatever the driver's plan says to sweep,
and a registered driver is one line in the composition root.

**The complete walkthrough is
[docs/adding-a-receiver.md](docs/adding-a-receiver.md)**: the architecture, the
contract member by member, the §8 safety obligations that bind third-party
drivers, the development process in order, the testing story (a fictional
second family in the test project runs the real connect and poll paths), and an
honest map of what a driver gets you today versus what is still written in the
SmartClock dialect. [docs/tutorial-nmea-driver.md](docs/tutorial-nmea-driver.md)
is the walkthrough followed to the end for the second family that ships, an
NMEA 0183 talker — the opposite shape to the SmartClock, which is why it was
chosen — with a simulator under `tools/` so it can be followed with nothing on
the desk.

## Documentation

- **[docs/requirements.md](docs/requirements.md) is the specification.** It is
  the authority on behaviour, and the `§` references in issues, commit messages,
  and code comments resolve against it. Where anything else disagrees with it,
  the document wins.
- **[CLAUDE.md](CLAUDE.md)** carries the working conventions for the repository,
  including every CI gate with what it guards and why it exists.

**One fact, one owner.** The table says which document owns which kind of fact.
Every other document links to the owner, or — where a reader will not follow a
link, as in the user's guide or the sideload README — restates it in words
without the number. Where this README restates something (the build commands,
the platform floor, the x64 decision), the owner is named beside it.

| Kind of fact | Owner |
|---|---|
| What the application does and every figure in it — timeouts, cadences, baud rates, sizes, thresholds, the status model, the safety tiers | [docs/requirements.md](docs/requirements.md), by `§` |
| Platform, repository layout, naming and packaging decisions | §6 of the specification |
| Design tokens, components, motion and the accessibility criteria | §9 of the specification |
| How to build, run and test on this machine | this README, [Building from source](#building-from-source) |
| The CI gates — what each guards and why it exists — and the agent conventions | [CLAUDE.md](CLAUDE.md) |
| The receiver contract and how to add a driver | [docs/adding-a-receiver.md](docs/adding-a-receiver.md); the tutorial is that guide followed once, and links rather than restates |
| What the user sees and presses | [docs/how-to-use.md](docs/how-to-use.md), in the user's words; the behaviour behind it is the specification's |
| Which checks need a person, a receiver or a machine setting | [docs/manual-qa.md](docs/manual-qa.md) |
| Which fixtures exist and which test reads each | [tests/WinZ3805A.Tests/Fixtures/README.md](tests/WinZ3805A.Tests/Fixtures/README.md); their provenance is `captured/capture-log.md` |
| What ships in the package, and under which licence | [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), regenerated from the project files |
| The Store decisions and the privacy position | [docs/store-listing.md](docs/store-listing.md), [docs/privacy.md](docs/privacy.md) |
| Where this stands against the incumbent tool | [docs/lady-heather-comparison.md](docs/lady-heather-comparison.md) |

The rest of `docs/`, and the other documents worth knowing about:

- [docs/how-to-use.md](docs/how-to-use.md) — the user's guide: every window and
  control, every keyboard shortcut, and how to get the window back from the
  notification area, with screenshots from the running application.
- [docs/adding-a-receiver.md](docs/adding-a-receiver.md) — the driver author's
  guide, summarised in [Adding a receiver](#adding-a-receiver) above.
- [docs/tutorial-nmea-driver.md](docs/tutorial-nmea-driver.md) — that guide
  followed to the end for a real family, an NMEA 0183 talker, with the
  simulator that stands in for one and every finding along the way.
- [docs/manual-qa.md](docs/manual-qa.md) — the manual QA checklist: the checks
  that need a person, a receiver, or a machine setting.
- [docs/lady-heather-comparison.md](docs/lady-heather-comparison.md) — where the
  application stands against Lady Heather, the incumbent tool for this family.
- [docs/privacy.md](docs/privacy.md) — the privacy policy: the application
  collects nothing and transmits nothing.
- [docs/store-listing.md](docs/store-listing.md) — everything the Microsoft
  Store submission asks for that is a decision rather than a file.
- [docs/index.md](docs/index.md) — the front page of the GitHub Pages site,
  which exists to give the Store listing a privacy-policy URL and is not enabled
  until submission needs it; it publishes the policy and the user's guide, and
  nothing else.
- [tests/WinZ3805A.Tests/Fixtures/README.md](tests/WinZ3805A.Tests/Fixtures/README.md)
  — provenance of the captured status screens the parser is tested against.
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) — the third-party components
  the application ships with, and the trademark position.

## Naming

`WinZ3805A` is the repository, solution, root namespace, and MSIX package
identity name. The Store *display* name is deliberately a separate thing that can
change at any time, so it is read from the package manifest at runtime and never
hard-coded (§6.3).

The name contains a model designation rather than a company mark. This project is
not affiliated with, endorsed by, or sponsored by HP, Hewlett-Packard, Agilent,
Keysight, or Symmetricom; those names appear here only to describe the hardware
the application talks to. §6.3 owns that position and its two hedges;
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) is the notice that ships.

## Licence

[MIT](LICENSE). Third-party components and trademarks are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
