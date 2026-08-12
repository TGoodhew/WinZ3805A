# WinZ3805A — agent conventions

WinZ3805A is a WinUI 3 desktop application for monitoring and controlling
HP/Symmetricom SmartClock GPS-disciplined oscillators — the Z3805A and its
siblings (Z3801A, 58503A/B, 59551A, Z3816A) — over RS-232. It replaces
Windows-9x-era tooling with a modern, Store-distributable app built around two
ideas: a glanceable primary window that a lab user can leave on a second monitor
for weeks, and a command model that makes destructive receiver commands
*unreachable* rather than merely warned about. The receiver's full status is
presented through a native Fluent surface, never as a reproduction of the
device's 80×24 terminal screen.

---

## The specification

**The specification is `docs/requirements.md`. Read it before implementing
anything. §-numbers in issues refer to it.**

It is the authority. Where this file, a prompt, a skill, or a plausible
convention disagrees with it, the document wins — and the conflict gets
surfaced, not silently resolved.

---

## Naming

Repository, solution, root namespace, assembly, design-token prefix (`Wz`),
package identity, and display name are all specified in **§6.2**. Follow that
table exactly. Nothing there needs inventing or mapping.

Two rules from **§6.3** that are easy to get wrong:

1. **Never hard-code the product name in XAML, resources, or tests.** Read the
   display name from `Package.appxmanifest` at runtime
   (`Package.Current.DisplayName`). Package identity is effectively permanent —
   changing it means a new app, a new listing, and no upgrade path — whereas the
   display name is a one-line change. §6.3 decouples them deliberately, and
   coupling them in code destroys that option. This applies to the title-bar
   text in the §10.3 and §9.7.1 wireframes. `Views/MainWindow.xaml.cs` already
   does this; keep it that way.

2. **"SmartClock" is HP's terminology, not a leftover.** `SmartClock Mode` is a
   field the receiver prints, and the specification uses *SmartClock family*,
   *SmartClock firmware*, and *SmartClock oscillator learning* throughout §7,
   §10, and §11. Do **not** "fix" these to `WinZ3805A`. Appendix B says so
   explicitly — renaming them would make the parser specification wrong.

---

## Platform

**.NET 10, not .NET Framework.** If a prompt says ".NET Framework," it means
modern .NET — see §6.1. WinUI 3 cannot run on .NET Framework 4.x, so this is
forced, not a preference. Do not scaffold WinForms or WPF on 4.x.

Use the **LTS** release. Do not adopt .NET 11 (Nov 2026, STS) on release.

§6.4 lists the platform features this project uses deliberately
(`System.IO.Pipelines`, `SearchValues<byte>`, `Channels`, `PeriodicTimer`,
`TimeProvider`, `FrozenDictionary`, records with `required` members) and the
ones it must not (**Native AOT** and **trimming** — both break WinUI 3's
reflection-driven XAML type resolution; `SerialPort.DataReceived`;
`Task.Run` around serial reads). Read it before reaching for an idiom.

---

## Safety — non-negotiable

**Commands listed in §8.4 are never implemented, displayed, logged, or
referenced. Do not add them to any catalog, list, comment, or test fixture.**

This extends to issue titles, branch names, commit messages, and TODOs. The
command catalog is an **allowlist** (§8.1): blocked commands are not entries
with a flag, they do not exist as data. The only place their patterns may appear
is `CommandCatalog.BlockedPatterns`, used solely by the Advanced Console
validator to reject a typed string and log the attempt. That collection must not
be enumerable through any public API a view can bind to.

---

## Architecture boundaries

- **`WinZ3805A.Device` never references `Microsoft.UI.*`.** All parsing, command
  classification, and transport lives there, and it is unit-tested headlessly
  against captured status screens in `tests/WinZ3805A.Tests/Fixtures/`. The
  library currently references only `System.Runtime`, `System.IO.Ports`, and
  `Microsoft.Extensions.Logging.Abstractions`. Keep it that way.
- **No `DateTime.Now` / `DateTime.UtcNow` anywhere in the Device library.**
  Inject `TimeProvider` and call `provider.GetUtcNow()`. This is not stylistic:
  the GPS week-rollover logic (§7.4), staleness display, and poll scheduling are
  all clock-dependent, and fixture tests must be able to pin the clock. Tests use
  `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`.
- **The parser never throws** (§11.1). Unparseable fields become `null` on the
  model and render as `—`. Nullable reference types with warnings-as-errors is
  what makes the compiler enforce that every consumer handles it.
- `DeviceSessionService` must be instantiable per device and resolvable from a
  keyed DI registration even though v1 creates exactly one (§12). No static state
  for connection or device identity.

---

## Design system

**No hard-coded hex colours outside `Themes/Colors.xaml`** (§9.13 item 2). This
is enforced in CI, not by review (P0-17). Every brush is referenced by key with
`{ThemeResource}` — never `{StaticResource}`, which would not re-resolve on
theme change.

Related §9.13 prohibitions worth keeping in view: only the 4 / 8 / circle corner
radii and the §9.6 spacing scale; severity always renders through `SeverityPill`
as colour **+ shape + text**; no animation without a row in §9.8.2; readouts
never animate.

### `winui-design` skill override

> The `winui-design` skill steers toward stock Fluent defaults. **§9 of
> `docs/requirements.md` overrides it** on colour, typography, corner radius,
> spacing, elevation, and motion. This project uses a custom token system (`Wz*`
> resource keys), a brand accent that is deliberately not the system accent,
> shape-coded severity, and a custom circular signature control. Treat
> `winui-design` as advisory for control selection, XAML correctness, and
> data-binding review only. If the skill's guidance and §9 disagree, §9 wins —
> surface the conflict, do not resolve it silently.

---

## Build

**Prefer MSBuild over `dotnet build` when available, because `dotnet build` does
not surface XAML compiler diagnostics.** A malformed `.xaml` file under
`dotnet build` fails with no indication of which file or what is wrong; MSBuild
reports it correctly. This project is XAML-heavy by design (§9), so this matters
in practice rather than theoretically.

MSBuild is not on `PATH`. Resolve it with `vswhere`, or use the full path:

```powershell
$msb = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe'

& $msb WinZ3805A.sln -t:Restore -p:Configuration=Debug -p:Platform=x64
& $msb WinZ3805A.sln -t:Build   -p:Configuration=Debug -p:Platform=x64
```

Restore is per-platform because the RID differs. Valid platforms are **x64** and
**ARM64** only — no AnyCPU, no x86 (§6.1).

```powershell
# Tests (UI-independent, so the plain SDK is fine here)
dotnet test tests\WinZ3805A.Tests\WinZ3805A.Tests.csproj

# Run the packaged app: build first, then point winapp at the output folder
winapp run src\WinZ3805A\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64 --detach
```

`TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are both on, so **the build
must be clean with zero warnings** — including code-style rules. File-scoped
namespaces are a build error (`IDE0161`), not a suggestion.

### CI gates — run them before you push

Two of the §9.12 criteria are enforced by CI rather than by review. Both are
plain scripts, so run them locally and get the answer in a second:

```powershell
pwsh build/Test-NoHexLiterals.ps1        # P0-17 / §9.13 item 2
pwsh build/Test-IconOnlyButtons.ps1      # A11Y-3 / §9.9
pwsh build/Test-ThemeDictionaryParity.ps1  # §9.4 / A11Y-8
pwsh build/Test-NoBlockedCommands.ps1    # P0-7 / §8.4
```

`.github/workflows/ci.yml` runs all four first, before any restore, so a token,
accessibility, or safety regression fails in seconds rather than after a full build. It then
builds all four Configuration × Platform combinations and runs the tests.

The hex gate implements the broader §9.13 wording rather than P0-17's minimum: it
scans every `*.xaml` under `src/` except `Themes/Colors.xaml`, plus `*.cs` under
any `Views/` or `Controls/` folder. The icon gate parses XAML as XML rather than
grepping, and fails a `Button`-like control that is icon-only and missing
*either* an `AutomationProperties.Name` or a `ToolTipService.ToolTip` — §9.9
requires both.

The theme-parity gate exists because Light and Dark are exercised every time anyone
runs the app and HighContrast is not — testing it means switching the whole
desktop over. A token defined in one theme and not another compiles, passes
review, and then fails at run time for precisely the user who needs that theme.

The blocked-command gate reads its tokens out of
`src/WinZ3805A.Device/Commands/BlockedCommands.cs` rather than restating them, so
that file stays the single place in the repository where those names occur. It
applies §8.4's two rules separately: the named exclusions may not appear at all,
while an undocumented parser node may appear only in query form, because §8.5
enables exactly that as an opt-in. `docs/` is not scanned — the specification is
where §8.4 is written down.

Note CI builds with `dotnet build` while the guidance above prefers MSBuild
locally. That is deliberate: the hosted runner's Visual Studio MSBuild is too old
to load `net10.0` projects, so CI uses the SDK's own. The consequence is that a
XAML failure in CI may not name the file — reproduce it locally with MSBuild.

`PublishReadyToRun` lives in `Properties/PublishProfiles/*.pubxml`, not in the
csproj: setting it for all non-Debug configurations makes an ordinary Release
build fail with NETSDK1094 unless the crossgen pack was restored with the flag
already set. Publishing is framework-dependent, never self-contained (§6.3).

---

## Repository layout

Mirrors §6.2. Empty folders carry a `.gitkeep` and are placeholders for work that
has not started — creating a file in one is expected, not a licence to skip the
implementation sequence in §15.

```
docs/requirements.md          the specification
src/WinZ3805A/                WinUI 3 app, single-project MSIX
  Views/ ViewModels/ Controls/ Themes/ Services/ Assets/Fonts/
src/WinZ3805A.Device/         class library, no UI references
  Transport/ Commands/ Parsing/ Models/
tests/WinZ3805A.Tests/        xUnit, with Fixtures/ for captured status screens
```

**§15 is an ordering constraint, not a suggestion.** In particular: the `Themes/`
token layer (step 5) exists before any page is built. Retrofitting tokens onto
finished XAML is where design systems die.
