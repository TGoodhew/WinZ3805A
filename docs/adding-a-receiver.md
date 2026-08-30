# Adding a receiver to WinZ3805A

WinZ3805A ships speaking to two families of hardware — the HP/Symmetricom
SmartClock GPS-disciplined oscillators it was written for, and any NMEA 0183
GNSS talker (proven against the simulator under `tools/`; no real talker has
been captured yet, #309 having been deferred) — because every piece of
device-specific knowledge sits behind one
interface, `IReceiverDriver`, so that supporting another receiver means
**writing a driver, not modifying the application**. This document is the
complete walkthrough: the architecture, the contract member by member, the
safety obligations, the development process in order, and — because a promise
like that is only worth what it excludes — an honest account of where the
boundary currently sits. [`tutorial-nmea-driver.md`](tutorial-nmea-driver.md)
is this process followed to the end for the second family, with the files that
resulted and every finding along the way; read the two together.

It replaces the shorter walkthrough that used to live in the README, and it is
written for a developer who has the repository building and a receiver on the
bench. If that is not you yet, start with
[Building from source](../README.md#building-from-source).

Sections of `docs/requirements.md` are cited as `§n` throughout. The
specification is the authority everywhere it speaks; where this guide and the
specification disagree, the specification wins and the disagreement is a bug in
this guide — please raise it.

---

## The architecture in one page

```
  Views / ViewModels                       (WinUI, per §9-§10)
        │
        │  DeviceContext.Driver.Find(…), driver.Commands, …
        ▼
  DeviceContext ──── Session ──── PollingService     (app services, §12)
                        │               │
                        │ Driver        │ Driver.Plan / InterpretSweep / Parse
                        ▼               ▼
              ┌───────────────────────────────┐
              │        IReceiverDriver        │   ← the seam (you implement this)
              │  SmartClockDriver │ yours     │
              └───────────────────────────────┘
                        │
                        ▼
                  ITransport                        (the wire: SerialTransport)
```

**The seam is the device, not the wire.** `ITransport` already abstracts the
serial port; `IReceiverDriver` abstracts *what is said over it and how the
answers are read*. A driver owns the command vocabulary, the safety exclusions,
the timeouts, the poll plan and cadence, the auto-detect serial preferences,
and both parsers — the fast sweep's and the full status response's. Those are
the things that used to be statics that silently meant "Z3805A".

**The common currency is `ReceiverStatus`** (plus `FastReadings` for the fast
sweep). Every driver parses *into* it and the whole UI reads *out of* it. A
field your receiver has no equivalent for is left `null`, and the UI renders
that as an em dash. This is the single most important habit in the codebase:
**absent is `null`, never zero** — a 1 PPS offset of `0 ns` reads as a perfect
lock, not a missing reading.

**One driver instance serves one connected device**, selected by the receiver's
own identity at connect time (see [How the application chooses your
driver](#how-the-application-chooses-your-driver)) and reachable everywhere as
`DeviceContext.Driver`. Nothing in the application project reaches the
SmartClock's static catalog any more; a test-project driver with a completely
fictional vocabulary runs the real connect, poll, and console paths to prove
it.

---

## What a driver owns — the contract, member by member

The interface is
[`src/WinZ3805A.Device/Drivers/IReceiverDriver.cs`](../src/WinZ3805A.Device/Drivers/IReceiverDriver.cs);
the shipped implementations are
[`SmartClockDriver.cs`](../src/WinZ3805A.Device/Drivers/SmartClockDriver.cs)
beside it and [`Nmea/NmeaDriver.cs`](../src/WinZ3805A.Device/Drivers/Nmea/NmeaDriver.cs)
under it — one that answers questions and one that talks, which between them
exercise every member — and a third, fictional and test-only, is
[`tests/WinZ3805A.Tests/Drivers/FakeReceiverDriver.cs`](../tests/WinZ3805A.Tests/Drivers/FakeReceiverDriver.cs).

| Member | What it decides | Authority |
|---|---|---|
| `Family` | A short name for logs and diagnostics — `"SmartClock"`, `"NMEA 0183"` | — |
| `Link` | Whether this family answers what it is asked (`QueryResponse`, the default) or talks unprompted and is never written to (`Broadcast`) | §12, #310 |
| `Recognises(identity)` | Whether this driver serves the receiver whose identity was read — by `*IDN?`, or by `Overhear` | §12 |
| `Overhear(lines)` | Whether the lines the receiver sent *before being asked anything* are this family's, and who it is. Defaults to "no"; a talker is recognised here and `*IDN?` is never sent to it | §12, #310 |
| `ClassifyLine(line)` | For a broadcast family, which plan key a heard line belongs to; `null` for a line that is not yours. Defaults to `null` | §12, #310 |
| `Commands` | The **allowlist** of everything this receiver may be sent | §8.1 |
| `Find(mnemonic)` | One command by name, or `null` if this receiver has none | §8.1 |
| `IsBlocked(header)` | Whether a header is one of this receiver's §8.4 exclusions — a verdict, never a list | §8.4 |
| `TimeoutFor(mnemonic)` | How long to wait, per §7.2's classes — a positive value for *every* input | §7.2 |
| `Cadence` | How often to poll: the fast sweep and the full sweep | §7.3 |
| `Plan` | *What* each sweep sends: the fast tier's queries in order, which one is refusable, and the full-status query | §7.3 |
| `AutoDetectSequence` | Serial configurations to probe, most likely first | §10.12 |
| `Parse(response)` | The full status response → `ReceiverStatus`. Never throws | §11.1 |
| `InterpretSweep(answers)` | The fast tier's answers → `FastReadings`, or a rejection with a reason. Never throws | §11.1, #209 |

Notes that do not fit in a table:

- **`Commands` is stably ordered.** The Advanced Console sorts its picker
  itself, but the §8.5 experimental card shows your experimental entries in the
  order `Commands` yields them.
- **`Plan.FastTier[0]` is the discriminator** — the query whose answer
  distinguishes your receiver's sweep from line noise (the SmartClock's is
  `:SYNC:STAT?`, whose answers form a closed six-token set). The poller reads
  it first, on its own, and keys its refusal suppression on it. Every mnemonic
  the plan names must resolve through your own `Find`; a contract test holds
  you to that, because a plan the catalog does not back polls nothing while
  looking configured.
- **On a broadcast link a plan entry is a key, not a query.** The session's
  `BroadcastListener` sorts every line your `ClassifyLine` claims into cycles
  delimited by `Plan.FastTier[0]` — so that entry must be a line the talker
  sends exactly once per cycle, *every* cycle; a cycle whose boundary never
  arrives never closes and is never answered — and answers each key from the
  last *complete* cycle, which is what keeps a paged sentence from being read
  half-arrived. Two details an `InterpretSweep` author needs: the boundary key
  itself answers with the newest boundary line, so `FastTier[0]` may be one
  cycle ahead of the rest; and an answer is *every* line of that key in the
  cycle, newline-joined, so GSV arrives as its pages.
  `PollPlan.WholeCycle` (`*`) as the full-status query hands `Parse` the cycle
  entire; it goes in your catalog like any other entry, because the session's
  allowlist check does not know it is special. A talker that has gone quiet
  for longer than `TimeoutFor` answers as a timeout, and the reconnect logic
  applies unchanged. `RefusableIndex` is `null` for a broadcast family — there
  is nothing to refuse.
- **`Plan.RefusableIndex`** marks the one query your receiver may legitimately
  refuse in some states, or `null`. §7.3.1 records why this exists: the
  SmartClock answers `:SYNC:TINT?` with an error while unlocked, and a refused
  query re-asked every second overflows the error queue and buries real faults.
  The poller stops asking it until the discriminator's answer changes. The
  index must never be `0` — the discriminator itself is read unconditionally.
- **`InterpretSweep` returns readings *and* a verdict, and the readings come
  back even when rejected.** The poller's state-change log records what was
  seen whether or not it stores it, and a rejection carries a sentence naming
  what was seen — a guard that drops readings silently is worse than no guard
  (#209). Rejection means "these answers are not my receiver's sweep"; the
  application separately bounds-checks accepted readings against the common
  currency's documented ranges, so your driver owns *"is this mine?"* and the
  app owns *"is this possible?"*.
- **`Recognises(null)` must return `false`.** The identity probe belongs to no
  driver — the session listens, then sends a bare `*IDN?` itself, and only then
  consults the drivers — so claiming `null` would be claiming every receiver
  whose identity could not be read. (This inverted when driver selection was
  built; the contract tests pin the new rule.)
- **`Overhear` claims on evidence, not silence.** It is handed whatever the
  synchronise step heard, which for a SmartClock is a banner and for a talker
  is a second or two of sentences. Claim on something that cannot be noise — a
  sentence whose checksum matches, say — and never on an empty list; a contract
  test holds every driver to `Overhear([]) == null`. The identity you return
  should round-trip through `DeviceIdentity.Parse` (four comma-separated
  fields) so the rest of the application sees a familiar shape.

---

## How the application chooses your driver

The ordering problem the old walkthrough recorded as unsolved — *auto-detect
must send `*IDN?` before anything can know what is attached* — is solved by
making the probe belong to no driver:

1. **Registration.** Drivers are registered in the composition root
   ([`App.Compose`](../src/WinZ3805A/App.xaml.cs)), one line each:

   ```csharp
   services.AddSingleton<IReceiverDriver>(
       provider => new SmartClockDriver(provider.GetRequiredService<TimeProvider>()));
   services.AddSingleton<IReceiverDriver>(
       provider => new YourDriver(provider.GetRequiredService<TimeProvider>()));
   ```

   **Registration order is priority order**, deterministically: the first
   registered driver is also the fallback for an identity nothing claims. Two
   drivers both claiming an identity is therefore not a race — the earlier
   registration wins, every time, and an over-claiming driver finds out from
   the log rather than from intermittent behaviour.

2. **Probe.** Auto-detect walks the **union** of every registered driver's
   `AutoDetectSequence`, in registration order, first appearance winning — so
   adding a driver can only append probes, never reorder the walk §10.12 fixes
   for the family already shipped. At each candidate setting the session first
   **listens** for the probe timeout (§7.2's synchronise step, which absorbs
   the SmartClock's banner) and hands whatever it heard to every driver's
   `Overhear`; a family that talks is claimed here, and nothing is sent to it.
   Only when nobody claims the lines does the session send a neutral `*IDN?`
   on its own timeout and apply IEEE 488.2's four-field shape test to the
   answer (#310).

3. **Selection.** The identity — overheard or answered — is put to each
   driver's `Recognises` in registration order (a `Recognises` or `Overhear`
   that throws is read as "does not claim", with a warning); the first claimant
   serves the connection. A `Broadcast` claimant gets a listener started over
   the transport before the command pump runs, so its first poll already has a
   cycle to read. No claimant → the
   first-registered driver serves it anyway — with a logged warning when more
   than one driver is registered; a single-driver build stays silent, since the
   fallback is the driver that would have served it regardless. Refusing to
   connect would turn every receiver with an odd identity string into a
   regression, and §8.6 already handles unknown *models* conservatively within
   a family.

4. **Re-selection.** Selection runs on *every* connect, including automatic
   reconnects, because the receiver on the port can have been swapped while
   the link was down. Services therefore read `DeviceContext.Driver` at the
   point of use, never from a cached field. (Pages currently snapshot
   driver-derived state — the console's picker, validator ranges — for one
   navigation's lifetime; that staleness across a cross-family reconnect is
   [#304](https://github.com/TGoodhew/WinZ3805A/issues/304), and it is safe
   meanwhile because the session re-checks every command against the current
   driver's allowlist at the moment it is served, refusing unsent what the
   connected receiver's driver does not offer.)

The whole flow is asserted in
[`DriverSelectionTests.cs`](../tests/WinZ3805A.Tests/Services/DriverSelectionTests.cs),
which is also the best place to see the seam exercised end to end.

---

## What works with a driver alone — and what does not yet

This is the honest boundary. **A driver gets you the monitoring core**, which
is the application's reason to exist (§1, §9.1): connection and auto-detect,
the polling loop, the primary window's readouts and medallion state text, the
trend store and charts fed by your `FastReadings`, staleness display, the lock
notifications, the tray icon and taskbar badge, the Advanced Console picker
over your allowlist, the transcript, and the §8.5 experimental card over your
experimental entries.

**The Details pages are still written in the SmartClock dialect.** They talk
through your driver — every command lookup goes via `Find`, so nothing routes
around your allowlist or exclusions — but they *require* commands and reply
shapes your receiver may not have. This is deliberate, recorded in
[#287](https://github.com/TGoodhew/WinZ3805A/issues/287)'s items 4–7 as work
best decided against a real second receiver rather than in the abstract. #287
is closed now: what the NMEA family made concrete moved to
[#304](https://github.com/TGoodhew/WinZ3805A/issues/304)'s items 1–4, and
items 5 and 7 — a binary protocol and a network transport — are on no open
issue until a family needs them:

| Surface | SmartClock assumption | Where it bites a new family |
|---|---|---|
| Tier C page actions | The mnemonics in `ReceiverDriverTests.EveryMnemonicThePagesRequireIsCatalogued` | A driver lacking one fails loudly at the click (`CommandConfirmation.Require` throws). Capability-gating the pages is [#304](https://github.com/TGoodhew/WinZ3805A/issues/304) |
| Mode-driven UI (medallion, tray, badge) | `:SYNC:STAT?`'s six tokens, mapped in `Controls/ReceiverMode.cs` | A foreign family's sync states render as *Disconnected* — your sweep is stored and trended, but the mode mapping is app-side today ([#304](https://github.com/TGoodhew/WinZ3805A/issues/304)) |
| `ReceiverStatus` itself | `SmartClockMode`, `Tfom`, `Ffom`, `WeekRolloverEpochs` are HP concepts | Your receiver's own concepts have nowhere to go; leave the first three `null` and `WeekRolloverEpochs` at `0` (it is a non-nullable `int`), and raise the §11.2 amendment for anything new |
| Status Registers page | HP's `:STAT:` register maps and bit meanings | Renders HP's registers regardless of driver |
| Time page | `:PTIM:LEAP:*`, HP time-code formats | Queries fail politely (null lookups), features show em dashes |
| Diagnostics page | `:DIAG:*` self-test keywords, HP log-entry grammar | Same |
| Timing page | Antenna-cable presets, EFC hardware-condition bit meanings | Same |
| Line protocol | Two link styles now (#310): an `scpi >` prompt grammar with `E-` error tokens for a family that answers, and a line-oriented listener for one that talks | A **binary protocol** (TSIP, UBX…) still cannot be driven — `Parse(string)` and `ClassifyLine(string)` are that assumption surfacing in the contract (item 5). A text protocol with a *different prompt* is nearer than it was: the listener shows how a second framing is served, but the prompt grammar itself is still `LineProtocol`'s |
| The connect sequence | Listens, then sends `*CLS`, then asks `*IDN?` | A talker is recognised from what it says and never asked — but the `*CLS` still goes out before the session knows what it is talking to. Harmless to every talker met so far; the end-to-end test pins that it is the *only* write |
| Control lines on open | DTR and RTS asserted, unconditionally, by the transport (§7.1) | A receiver that uses a control line as an *input* has no way to say so — the BG7TBL went silent with DTR asserted (#309). Control-line policy on open is [#304](https://github.com/TGoodhew/WinZ3805A/issues/304)'s item 4 |
| Mode vocabulary | `LOCK`, `REC`, `WAIT`, `HOLD`, `POW`, `OFF` | A broadcast family speaks the vocabulary in its own terms — the NMEA driver says `LOCK` for a fix and `POW` for none — until the mapping is driver-supplied ([#304](https://github.com/TGoodhew/WinZ3805A/issues/304)) |
| Advanced Console | *"Will send"*, then the transcript's `>` line | Over a broadcast link nothing is sent: picking a key shows the latest of what was heard, and the label is a query/response word |
| Capture harness | `Capture-Fixtures.ps1` sends `:SYST:STAT?` and strips echo and prompt; `FixtureCorpusTests` assumes every `*.txt` under `Fixtures/` is a status screen | A talker's capture is a timed listen, and belongs beside its tests rather than in the corpus folder until the corpus test can tell families apart |
| Transport | Serial only | A network transport is item 7, sketched in [`lady-heather-comparison.md`](lady-heather-comparison.md) |
| The specification | §7, §8, §11 describe the SmartClock as *the* behaviour | Raise amendments; never absorb the divergence silently — see [What to raise rather than absorb](#what-to-raise-rather-than-absorb) |

If your family is a SCPI-speaking GPSDO with a prompt, or a text talker like
an NMEA receiver, you can ship the monitoring core today. If it is not, start
by raising the items above that block you — the seam was built to make those
conversations concrete, and the tutorial shows what one of them looked like.

---

## The safety model — obligations that bind your driver

Read this section twice. Everything in it is §8, every rule in it is a safety
rule, and the wrong abstraction here is a defect rather than a missing feature.

**The catalog is an allowlist (§8.1).** Every string the application can emit
originates from a catalog entry; there is no code path that builds a command
from arbitrary user input, and the Advanced Console is a *picker* over the
allowlist, never a text box that reaches the wire. Your `Commands` inherits
that role: a command absent from it cannot be sent.

**Each entry carries the specification's safety tier** (§8.2–§8.4 — note the
letters are the specification's own, and they are not alphabetical by
severity):

| Tier | Name | Meaning |
|---|---|---|
| **S** | Safe | Read-only queries and reversible actions — execute on click |
| **C** | Confirm | Writes that disturb service — modal confirmation with consequence text, sometimes an explicit acknowledgement tick |
| **B** | Blocked | **Not a tier of entries at all**: blocked commands do not exist as catalog data |

Tier C confirmation text must say what actually happens, in the user's terms,
measured rather than assumed — the SmartClock self-test's text claimed a brief
interruption until someone ran it and watched the receiver drop lock and
re-acquire over minutes.

**The exclusions (§8.4):**

1. **Decide your own.** Never inherit another family's list — it is not a
   conservative default. A command harmless on one receiver may be destructive
   on another, and the names need not even correspond.
   `ReceiverDriverTests.EachDriverAnswersForItsOwnExclusions` demonstrates the
   SmartClock driver and the test project's fictional one giving opposite
   verdicts about the same headers.
2. **Return a verdict, never the patterns.** `IsBlocked` returns `bool` by
   design: §8.4 requires that excluded commands do not exist as data any view
   can enumerate, bind to, or log wholesale.
   `TheDriverContractCannotExposeTheExclusionsAsData` asserts this against the
   *interface* by reflection, so it binds your driver too.
3. **Keep them in one `internal` file**, mirroring
   [`Commands/BlockedCommands.cs`](../src/WinZ3805A.Device/Commands/BlockedCommands.cs),
   referenced from nowhere else. `build/Test-NoBlockedCommands.ps1` reads its
   tokens out of that file rather than restating them and scans the repository
   for leaks on every push.
4. **Never write them down** — not in issue titles, branch names, commit
   messages, TODOs, or test fixtures. The driver tests *discover* an excluded
   header by asking the validator, precisely so the test file contains no name.

**Experimental queries (§8.5)** are the one sanctioned way to carry
undocumented queries: flag the entry `IsExperimental`, query-form only, and the
Diagnostics page shows them only behind the Settings → Advanced opt-in, run on
explicit click and never on a poll timer. The set forms of undocumented nodes
stay permanently excluded.

**Commands you deliberately leave out** deserve a recorded reason, following
§16.1's discipline — its precedents (out-of-scope subsystems, absences settled
by hardware, integrity-of-the-instrument refusals, setters withheld until
needed) are worth reading before you decide your own catalog's boundaries. One
of its lessons applies directly to driver work: **probing settles whether the
parser accepts a command, not whether the hardware behind it exists.**

---

## The development process

### Step 0 — read first

- §7 (communication), §8 (safety), §11 (parsing), §12 (architecture), §16
  (catalog decisions) of [`requirements.md`](requirements.md), reading §7,
  §8 and §11 as describing the SmartClock specifically — §12's "Receiver
  readiness" bullet says exactly that.
- [`CLAUDE.md`](../CLAUDE.md) for the build, the CI gates, and the
  non-negotiables.
- The platform rules that bind the Device library: no `Microsoft.UI.*`
  references, no `DateTime.Now`/`UtcNow` (inject `TimeProvider`), nullable
  reference types with warnings as errors.

### Step 1 — capture what the receiver actually says

Before any code. Connect the receiver with a terminal and save its real output
to `tests/WinZ3805A.Tests/Fixtures/captured/`, one file per interesting state.

**Capture the awkward states, not just the good one.** The states worth having
are the ones you cannot conjure later: power-up, acquiring, holdover, a failing
self-test, a survey in progress. `locked` is the easiest to get and the least
informative. Follow
[`capture-log.md`](../tests/WinZ3805A.Tests/Fixtures/captured/capture-log.md)
for how existing captures are recorded — each says what the receiver was doing
and when, because a fixture whose provenance is unknown cannot settle an
argument later.

Save the bytes verbatim. Do not tidy whitespace: column positions carry
meaning, and trailing spaces are often significant. The fixtures directory is
marked `-text` in `.gitattributes` so line endings survive.

**A talker is captured by listening**, not by asking: open the port at the
rate you believe in and save a minute of what arrives. Keep the file beside
your driver's tests rather than under `Fixtures/` — `FixtureCorpusTests`
asserts every `*.txt` there is a status screen. With no hardware,
`dotnet run --project tools\NmeaSimulator -- --stdout` gives a capture in the
shape a real talker's will take; the tutorial's tests are written against it,
and no real talker has been captured yet — #309 was deferred when the bench
unit turned out to put no NMEA on the port the application can reach.

### Step 2 — decide what `ReceiverStatus` can hold

[`Models/ReceiverStatus.cs`](../src/WinZ3805A.Device/Models/ReceiverStatus.cs)
is the common currency between every driver and the whole UI, and
`FastReadings` (in
[`IReceiverDriver.cs`](../src/WinZ3805A.Device/Drivers/IReceiverDriver.cs)) is
its fast-sweep counterpart.

**A field your receiver has no equivalent for is left `null`.** That is not a
workaround, it is the contract: §11.1 requires it of the parser, and every
readout in the UI already renders `null` as an em dash. Do not invent a value
to fill the shape, and in particular do not use zero.

Some fields are HP's rather than general — `SmartClockMode`, `Tfom`, `Ffom`,
`WeekRolloverEpochs` — and a new driver simply leaves the first three `null`
and `WeekRolloverEpochs` at `0`, its only honest value for a receiver that
handles its own rollover. If your
receiver has a concept the record cannot express at all, add a nullable field
rather than overloading an existing one, and raise the §11.2 amendment: the
specification describes that record field by field.

### Step 3 — write the parsers

Two of them: `Parse` for the full status response, `InterpretSweep` for the
fast tier's answers. The same three rules govern both.

**Neither ever throws.** §11.1 is absolute and the reason is structural: the
poll loop calls them, once per sweep, and an exception there stops the receiver
being polled at all. An unreadable field becomes `null`; the reason goes into
`ReceiverStatus.ParseWarnings` (which Diagnostics displays) or into
`SweepInterpretation.Rejection` (which the poller logs). Wrap the whole body in
a last-resort `catch`, as
[`StatusScreenParser`](../src/WinZ3805A.Device/Parsing/StatusScreenParser.cs)
does. `InterpretSweep` must additionally tolerate an answers list of *any*
length with nulls anywhere — a dropped link mid-sweep hands it exactly that.

**Reject a sweep that is not yours, with a reason.** The discriminator's answer
is the test: the SmartClock rejects any sweep whose first answer is outside its
closed six-token set, because a misaligned link once delivered a diagnostic
dump into the sync-state slot while the same sweep's EFC read a perfectly
plausible +2 % (#209 — the write-up in `SmartClockDriver.InterpretSweep`'s
remarks is worth reading whole). Return the readings anyway; say what you saw.

**Do not hard-code column positions.** The SmartClock parser derives every
column from the header row, which is what lets it survive a firmware revision
that shifts a field by a character. Parse by field identity, never by "the byte
that was at offset 12 last time".

Write the tests against the fixtures from step 1, asserting real values from
real captures rather than values you computed with your own parser.

### Step 4 — declare the command catalog

An allowlist (§8.1), each entry a `ScpiCommand` with its tier, query flag,
display text, parameter specs and response format. Read a handful of
[`CommandCatalog.cs`](../src/WinZ3805A.Device/Commands/CommandCatalog.cs)'s
entries for the idiom — display names and confirmation texts are user-facing
sentences, and parameter specs drive the UI's field validators, so an entry
with a numeric parameter needs its documented range.

Note that the catalog's construction helpers are private to the SmartClock
catalog; your driver builds its own `ScpiCommand` instances (the fake driver
shows the minimal form).

One entry is a **contract requirement** of every query/response family:
`:SYST:ERR?`, IEEE 488.2's error query, which `CommandInvoker` drains after
every tier C command (§7.2). A broadcast family must instead have no tier C
entry at all, since the invoker never runs for it. Both are contract-tested.

### Step 5 — the exclusions

Covered in [the safety model](#the-safety-model--obligations-that-bind-your-driver)
above; implement them exactly that way, and run
`pwsh build/Test-NoBlockedCommands.ps1` before you push anything.

### Step 6 — timeouts and cadence

**These are measurements, not conventions.** Copying another receiver's figures
gives numbers that are either wastefully long or short enough to fail healthy
hardware — the SmartClock GPS self-test takes up to 24.0 s against a 30 s
class, and its full status screen takes 3521 ms of wire time at 9600 baud,
which is why `Cadence.Full` is not simply `Cadence.Fast` with more in it.

Time your receiver's slowest command and set the class from that, with
headroom. For a broadcast family there is no slow command: `TimeoutFor` is how
long silence means the talker has stopped — a small multiple of its cycle —
and `Cadence.Fast` is the talker's own rate. `TimeoutFor` must return a
positive value no longer than two minutes for **every** input including an
unknown mnemonic, and `Cadence.Full` must exceed `Cadence.Fast` — all
contract-tested. The poller follows your cadence live (an explicit override
exists for tests).

### Step 7 — the poll plan

Decide the fast tier: the discriminator first, then the cheap scalars worth a
1 Hz history — they feed the primary window and the trend store. Mark the
refusable one, if any. Name the full-status query. Everything must be in your
own catalog.

Then make `InterpretSweep` read the answers positionally into `FastReadings`.
If your receiver's dialect needs scalar helpers,
[`ScalarParsers`](../src/WinZ3805A.Device/Parsing/ScalarParsers.cs) has the
SCPI-flavoured ones the SmartClock uses.

### Step 8 — recognition

`Recognises` should claim exactly the identities your timeouts and commands
were measured against: match on the parsed `*IDN?` fields, return `false` for
`null`, and never claim an identity you are unsure of — the caller falls back
to a driver that assumes less, and claiming an unfamiliar receiver applies
every figure in your driver to hardware it was not measured on.

Extend `AutoDetectSequence` with your family's factory settings, most likely
first. Entries duplicated across drivers cost nothing (the walk deduplicates);
entries unique to you are appended after earlier-registered drivers' probes.
If your rate is not in `SerialSettings.SupportedBaudRates`, add it there too
and amend §7.1 — the connection dialog offers only that list.

**A family that talks is recognised by `Overhear`**, not `Recognises`. The
synchronise step already listens before the probe; implement `Overhear` to
claim the receiver on evidence in those lines — one sentence whose checksum
matches is enough for a talker, since a wrong baud rate never produces one —
and return the identity you want the application to show. Set
`Link => LinkStyle.Broadcast`, and `*IDN?` is never sent to your receiver.
`Recognises` should then claim the identity `Overhear` reported and nothing
else, so a reconnect re-selects you the same way.

### Step 9 — register it

One line in `App.Compose`, after the SmartClock's registration:

```csharp
services.AddSingleton<IReceiverDriver>(
    provider => new YourDriver(provider.GetRequiredService<TimeProvider>()));
```

That is the only application-project edit a new family needs, and it is the
line this document's title promises. Everything else — session, poller,
console, pages, DI — picks your driver up through the selection flow.

### Step 10 — test it

- **Join the contract theory.** Add your driver to
  `ReceiverDriverTests.AllDrivers`; every family-agnostic contract test then
  runs against it unmodified. This is deliberately cheap — it is one line —
  and deliberately hard to skip, because the contract is only real if every
  implementation meets it.
- **Fixture-test your parsers** against step 1's captures, pinning the clock
  with `FakeTimeProvider` wherever dates matter.
- **Exercise the seam end to end** the way
  [`DriverSelectionTests`](../tests/WinZ3805A.Tests/Services/DriverSelectionTests.cs)
  does: a `ControllableTransport` scripted with your identity and your
  answers, through the real `DeviceSessionService` and `PollingService`. Wind
  the fake clock; never sleep (the test files' comments record the flake
  family that rule comes from).
- **Run the gates** before pushing — all of them, listed in
  [`CLAUDE.md`](../CLAUDE.md), but `Test-NoBlockedCommands.ps1` is the one
  that matters most here.

```powershell
dotnet test tests\WinZ3805A.Tests\WinZ3805A.Tests.csproj
pwsh build/Test-NoBlockedCommands.ps1
```

### Step 11 — what to raise rather than absorb

§7, §8 and §11 of the specification are written in terms of one receiver
family, and §12's "Receiver readiness" bullet records that as a known gap.
Adding a family means amending them so the document and the code do not drift
apart — **raise the amendment rather than absorbing the divergence in code**.
The same applies to anything on the [boundary
table](#what-works-with-a-driver-alone--and-what-does-not-yet): capability-gated
pages, mode mapping and control-line policy are
[#304](https://github.com/TGoodhew/WinZ3805A/issues/304); binary protocols and
network transports were
[#287](https://github.com/TGoodhew/WinZ3805A/issues/287)'s items 5 and 7, and
with #287 closed they are on no open issue — file one when a family needs them.

---

## The checklist

Before opening the pull request:

- [ ] Fixtures captured from real hardware, awkward states included, bytes
      verbatim, provenance recorded in `capture-log.md` (a talker's capture
      goes beside its tests, per step 1)
- [ ] `Parse` and `InterpretSweep` never throw, tested against garbage, empty,
      null and torn inputs
- [ ] Absent fields are `null`, never zero or a guess
- [ ] Catalog is an allowlist; tiers per §8.2/§8.3; confirmation texts state
      measured consequences; `:SYST:ERR?` present (query/response) or no tier
      C entries (broadcast)
- [ ] Exclusions: your own list, one `internal` file, verdict-only `IsBlocked`,
      no excluded name written anywhere — `Test-NoBlockedCommands.ps1` green
- [ ] Timeouts and cadence measured on your hardware, not copied
- [ ] Plan resolves entirely through your own catalog; refusable index off the
      discriminator
- [ ] `Recognises(null)` is `false`; claimed identities are exactly the
      measured ones
- [ ] Driver added to `ReceiverDriverTests.AllDrivers` and the whole suite
      green
- [ ] One registration line in `App.Compose`, after the SmartClock
- [ ] Specification amendments raised for anything §7/§8/§11 now under-describe
- [ ] All CI gates green locally

---

*This guide is part of the repository and versioned with the code it
describes; if you find it disagreeing with the source, the source is newer —
please raise the discrepancy on
[the issue tracker](https://github.com/TGoodhew/WinZ3805A/issues).*
