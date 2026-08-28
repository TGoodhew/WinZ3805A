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
