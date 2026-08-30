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

Every receiver-specific fact sits behind a driver (§12, `src/WinZ3805A.Device/Drivers/`).
The SmartClock family is the first driver; a generic NMEA 0183 talker (#310) is the second,
proven against the simulator under `tools/` rather than against hardware, and it shows only
what NMEA carries.

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
(`System.IO.Pipelines`, `SequenceReader<byte>`, `Channels`, `PeriodicTimer`,
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
with a flag, they do not exist as data. The only place their patterns exist is
`src/WinZ3805A.Device/Commands/BlockedCommands.cs`, private to the Device assembly;
the only way out is the `IsBlocked` predicate on `CommandCatalog` and
`IReceiverDriver`, which answers one bool about one candidate and cannot be
enumerated or bound to (§8.4, corrected 21 Aug 2026 by #85). No production path
feeds it typed text: the Advanced Console shipped as a picker over the allowlist
with no free-text box (#55), so the log-the-attempt half was never built.

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
- **Every receiver-specific fact sits behind `IReceiverDriver`** (`Drivers/`). The
  app never reaches `SmartClockDriver` or `NmeaDriver` directly; it asks the driver
  the session selected. Adding a receiver is `docs/adding-a-receiver.md`, and
  `docs/tutorial-nmea-driver.md` is that guide followed to the end.

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

Restore is per-platform because the RID differs. The only valid platform is
**x64** — no AnyCPU, no x86, and no ARM64 (§6.1, amended 15 Aug 2026: WACK
cannot cross-test and there is no ARM64 hardware to certify on; Windows on ARM
runs the x64 package under emulation).

```powershell
# Tests (UI-independent, so the plain SDK is fine here)
dotnet test tests\WinZ3805A.Tests\WinZ3805A.Tests.csproj

# Run the packaged app: build first, then point winapp at the output folder
winapp run src\WinZ3805A\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64 --detach
```

`TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are both on, so **the build
must be clean with zero warnings** — including code-style rules. File-scoped
namespaces are a build error (`IDE0161`), not a suggestion.

Three things the tree does not make obvious. `tools/NmeaSimulator` is referenced by the
test project, so changing the simulator changes the tests; its README says how to run it
against a port or to stdout. `docs/how-to-use.md` and its images ship inside the package
as linked `Content` items, and `HelpDocumentTests` parses the real document and checks
every image it names, so editing the guide can fail the tests. And `Themes/Colors.xaml`
is also an `EmbeddedResource` that `ThemePalette` reads at run time, so colours are never
restated in C#.

### CI gates — run them before you push

Several §9.12 and §9.13 criteria are enforced by CI rather than by review; what no gate
can reach — a person, a receiver, or a machine setting — is `docs/manual-qa.md`. All are
plain scripts, so run them locally and get the answer in a second:

```powershell
pwsh build/Test-NoHexLiterals.ps1        # P0-17 / §9.13 item 2
pwsh build/Test-IconOnlyButtons.ps1      # A11Y-3 / §9.9
pwsh build/Test-ThemeDictionaryParity.ps1  # §9.4 / A11Y-8
pwsh build/Test-NoBlockedCommands.ps1    # P0-7 / §8.4
pwsh build/Test-ContrastFloor.ps1        # A11Y-4 / §9.4.5
pwsh build/Test-SeriesSeparation.ps1     # A11Y-12 / §9.4.4
pwsh build/Test-SpacingScale.ps1         # §9.13 item 4 / §9.6
pwsh build/Test-HighContrastLegibility.ps1 # A11Y-8 / §9.2
pwsh build/Test-NoColourOnlyStates.ps1   # A11Y-12 / §9.4.3
pwsh build/Test-FocusVisualCoverage.ps1  # A11Y-2 / §9.12
pwsh build/Test-PointerTargets.ps1       # A11Y-5 / §9.6.3
```

One more script runs in CI and is **not** a gate on the source — it checks a tool rather
than a rule:

```powershell
pwsh build/Capture-Fixtures.ps1 -SelfTest # #4 / #185 — the harness, not the app
```

`.github/workflows/ci.yml` runs all twelve in their own dependency-free jobs, alongside the
build rather than ahead of it — they need no restore, so a token, accessibility, or safety
regression fails in seconds rather than after a full build. A separate matrix job builds both
Configuration × Platform combinations — Debug and Release against x64,
since §6.1 dropped ARM64 on 15 Aug 2026 — and runs the tests.

**Why a build script is in CI at all.** `Capture-Fixtures.ps1` collects §11.1's
fixtures, and the states it exists to catch — power-up, acquiring, holdover, a failing health
monitor; the first three were captured on 27–28 Aug 2026 and only the last is still missing —
happen only while the receiver is being moved. It is used perhaps once a season, and
a parsing bug found afterwards cannot be retried without moving the hardware again. So the
half that needs no serial port is checked on every push rather than on the day. The serial
half still cannot be, and the self-test says so when it passes.

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

The pointer-target gate was added 28 Aug 2026, and it exists because **A11Y-5 was signed
off as passing while two breaches sat in the primary window**. Tony found the first by trying to use
it: the §7.4 rollover badge was a bare `TextBlock` in a symbol font, and **a `TextBlock` is
hit-testable only where its glyph is**, so the target was about 12 × 15 px against a 32 × 32 floor —
hittable only by landing on the glyph exactly, and the tooltip dismissed the moment the pointer
slipped off. `Test-IconOnlyButtons.ps1` already checks that floor but **only on `Button`-like
controls**, so a `Border`, a `TextBlock` or a custom control never reached it. Measuring the
neighbours rather than trusting the first fix then found `TfomPill` and `FfomPill` at **73 × 28** — a
20 px line inside XXS padding lands four short — so the floor now lives on the `SeverityPill` style
rather than on the two call sites that happen to need it today. Two things worth not rediscovering: a
hit target's `Background` must be **`Transparent` and not unset**, because an unset background is
`null` and not hit-testable at all, so padding enlarges the box on screen and changes nothing; and
the gate reads tooltips **set in code** as well as in XAML, since `ToolTipService.SetToolTip` is how
the badge that prompted it got its tooltip — a gate reading only XAML would have missed the defect it
exists to catch. It requires the floor to be **declared** rather than inferred, on the same reasoning
the icon gate gives: a floor that holds only while a stock style happens to supply it is a
coincidence, not a floor.

The focus-visual coverage gate was added 27 Aug 2026 alongside the A11Y-2 pass that
closed #22. **The surface behind a focus ring is not knowable from source**: the accent-filled
button uses stock `AccentButtonStyle` and this application does not remap `AccentFillColorDefault`,
so the fill is the **end user's Windows accent colour** — every measured ratio (3.06:1 on 24 Aug,
3.10:1 on 27 Aug) is specific to the machine it was taken on and says nothing about a user who has
chosen yellow. So the gate checks the property that makes the measurement unnecessary: one stroke
near black and one near white **cover the whole luminance range**, because a colour cannot be within
3:1 of black and within 3:1 of white at once. Worst cases are Light 4.14:1 at L=0.200 and Dark
4.43:1 at L=0.187. It exists to catch a future "softer" or brand-coloured focus ring, whose failure
would otherwise appear only for users whose accent happens to land in the gap.

The colour-only-states gate was added 27 Aug 2026 for #32. §10.3's footer staleness had
three states whose only difference was `FooterText.Foreground` — the age in words was in the text
either way, but **the judgement about that age was hue and nothing else**. §9.4.3 already says
caution and critical converge under protanopia and deuteranopia; under high contrast it is not a
convergence but an **identity**, both being `SystemColorWindowTextColor`, so two of the three states
rendered the same pixels. The rule is structural: within a `VisualStateGroup`, the properties its
states set must include at least one that is not a brush. It cannot judge whether a second channel
is a *good* one — a group setting `Opacity` passes and may still be weak — but it makes the specific
thing review kept missing impossible. Pointer and focus feedback are exempt with reasons, because
those groups convey no information to read.

The high-contrast legibility gate was added 26 Aug 2026 for #218 — the first defect
found by actually switching the desktop into high contrast rather than reasoning about it. The
parity gate above **passed** it: `WzSequential1Brush` and `WzSequential2Brush` existed, in all
three themes, with the right type — and were defined as `SystemColorWindowColor`, the surface
they are painted on. A tracked satellite is filled with a ramp step chosen by signal strength,
those two steps span C/N 26–34, and §11.1 calls 35 and above good — so **every satellite that was
not already good was drawn in the page background**, while the legend swatch, hard-wired to step
5, went on showing a filled dot. **Key parity is not legibility.** The check is structural rather
than photometric, which is what lets it work at all where `Test-ContrastFloor` cannot: it does not
need to know what colour the user's window is to know that a foreground must not *be* it. Its
allowlist holds the four genuine surfaces, each with a reason asserted non-empty — a row there is
a claim that the token names something drawn *under* other content, not an exemption.

The contrast gate was added 21 Aug 2026 for #24 — which had carried a `ci-gate`
label since the backlog was written while nothing in the repository computed a contrast ratio.
It needed `build/fluent-stock-colours.txt`, because §9.4.1 maps the text and surface tokens onto
**stock Fluent colours** that are not readable from source: the SDK ships no XAML, so they were
measured from the running app and recorded with provenance, the way a fixture is. Two traps are
worth not rediscovering — almost every stock token is **semi-transparent**, so a check that reads
them as opaque produces confident nonsense, and **HighContrast cannot be checked at all**, its
tokens being the user's own `SystemColor*` choices. It found tertiary text at 3.28:1 against a
4.5:1 floor in Light in 117 places (#176), and two chart series under 3:1 (#177). Both were
then fixed — #176 in PR #180, #177 with #87 in PR #181 — and the gate's baseline table has been
empty since, which is the point: **a baseline row is a debt with a number on it, not an
exemption**.

The spacing gate was added 15 Aug 2026, after the §15 step 11 anti-pattern audit found
**nine** off-scale values that had each passed review — `Padding="0,3"`,
`Margin="0,0,0,6"`, `Margin="28,0,0,0"`. None of them is visible one at a time, which
is exactly how a spacing scale stops being one. It strips XML comments before
scanning, because `Spacing.xaml`'s own header quotes §9.13's `Margin="13,7,13,9"`
example. `BorderThickness` is deliberately **not** checked: a stroke width is §9.2's
business rather than the spacing scale's, and `SkyPlotControl`'s 1 px and 1.5 px
marker outlines are correct.

The series-separation gate was added 22 Aug 2026 for #87 and #177. §9.4.4 claimed the
categorical palette was derived from **Okabe–Ito**, which separates every pair under the common
dichromacies — but three of its eight entries had been substituted for values that read better as
thin lines, and the substitution silently gave up precisely that property. Series 1 and 7 measured
**4.5 ΔE₀₀ apart under deuteranopia**, which is one colour, and it survived three months of review
because nobody eyeballs a dichromat simulation correctly. The gate checks all 28 pairs in both
themes under three vision models, plus two rules the derivation learned by getting them wrong: a
**minimum hue gap**, because two browns separated by lightness satisfy the arithmetic and fail a
person asked which trace is which; and **clearance from the §9.4.3 severity colours**, because "a
separate namespace" is a perceptual claim as well as a naming one. Two things worth not
rediscovering — the **neutral series must be exempt** from that second rule (series 8 is grey and
`WzNeutralBrush` is grey, and requiring them apart makes it unsatisfiable), and **HighContrast
cannot be checked at all**, its series alternating between two `SystemColor*` values, so eight
traces are not separable by colour there under any ramp. `build/palette/` holds the derivation;
`validate.py` there checks the colour maths against #87's published figures before anything trusts
it. Note also that **PowerShell's comma binds tighter than binary minus** — `@($a - 1, $b)` parses
as a subtraction of an array, which is how the gate's Lab conversion failed on first run.

The blocked-command gate reads its tokens out of
`src/WinZ3805A.Device/Commands/BlockedCommands.cs` rather than restating them, so
that file stays the single place in the repository where those names occur. It
applies §8.4's two rules separately: the named exclusions may not appear at all,
while an undocumented parser node may appear only in query form, because §8.5
enables exactly that as an opt-in. Only `docs/requirements.md` is exempt — the specification is where §8.4 is written down; the rest of `docs/` and the root-level documents are scanned, because `docs/adding-a-receiver.md` instructs driver authors about the exclusions and a leak written there would otherwise pass CI forever (#287 narrowed the old whole-directory exemption).

Note CI builds with `dotnet build` while the guidance above prefers MSBuild
locally. That is deliberate: the hosted runner's Visual Studio MSBuild is too old
to load `net10.0` projects, so CI uses the SDK's own. The consequence is that a
XAML failure in CI may not name the file — reproduce it locally with MSBuild.

`PublishReadyToRun` lives in `Properties/PublishProfiles/*.pubxml`, not in the
csproj: setting it for all non-Debug configurations makes an ordinary Release
build fail with NETSDK1094 unless the crossgen pack was restored with the flag
already set. Publishing is framework-dependent, never self-contained (§6.3).

---

## Branches and merges

Never work on `main`, not even for a one-line fix. Branch off an up-to-date `main`, named for
the work — `feat/310-nmea-tutorial`, `fix/307-compact-medallion`, `docs/316-documentation-audit`
— commit in separately revertable pieces where the work has separable parts, and open a pull
request so CI runs. Merge when it is green (rebase merge, so `main` stays linear), then leave the
local repository matching the remote: delete the branch on both sides
(`gh pr merge --rebase --delete-branch` does both), fast-forward local `main`
(`git fetch origin main:main` from another branch), and `git remote prune origin`.

---

## Repository layout

§6.2 owns the tree; this copy is for orientation.

```
docs/requirements.md          the specification
src/WinZ3805A/                WinUI 3 app, single-project MSIX
  Views/ ViewModels/ Controls/ Themes/ Services/ Assets/Fonts/
src/WinZ3805A.Device/         class library, no UI references
  Transport/ Commands/ Parsing/ Models/ Drivers/ (SmartClock, and Nmea/)
tests/WinZ3805A.Tests/        xUnit, with Fixtures/ for captured status screens
tools/NmeaSimulator/          the NMEA 0183 talker the tests and the tutorial run against
build/                        the gate scripts, the sideload packager, the palette derivation
.github/workflows/ci.yml      the gates in their own jobs, then the Debug and Release builds and the tests
```

**§15 is an ordering constraint, not a suggestion.** In particular: the `Themes/`
token layer (step 5) exists before any page is built. Retrofitting tokens onto
finished XAML is where design systems die.
