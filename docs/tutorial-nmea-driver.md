# Adding a receiver, worked: an NMEA 0183 driver

[`adding-a-receiver.md`](adding-a-receiver.md) says how to add a receiver family. This is that
process followed to the end for a real one — **any NMEA 0183 GNSS talker**: a u-blox module, a
marine receiver — with the files that resulted and the things that
had to change along the way. Read the guide first; this document assumes it and follows its
step numbers.

Two things to know before starting.

**You do not need hardware.** The tutorial's receiver is a simulator,
[`tools/NmeaSimulator`](../tools/NmeaSimulator/README.md), which speaks what a real module speaks
— one cycle a second, checksums and all, from power-up through a 2D fix to a 3D one. The tests
drive it in-process; the packaged application can be pointed at it over a serial-port pair. A real
talker replaces it without changing the driver, which is the point: the driver never depends on
the simulator, only on the sentence codec they share.

**The family was chosen because it is the opposite shape to the SmartClock.** A talker speaks
unprompted and is never written to; it has no status screen, no error queue and no commands.
Every assumption the seam had made about a receiver that answers questions surfaced, and each
one is recorded below as a *finding* — what was assumed, what changed, and where. That is what the
issue that asked for this ([#310](https://github.com/TGoodhew/WinZ3805A/issues/310)) wanted from
it: not a second driver, but the seam made honest by one.

The whole driver is one folder,
[`src/WinZ3805A.Device/Drivers/Nmea/`](../src/WinZ3805A.Device/Drivers/Nmea/): the sentence codec,
the cycle parser, and the driver. Copy it to start your own.

---

## Step 0 — read first

The guide's list, plus the standard itself. NMEA 0183 is a paid document, but the summary that
circulates — *The NMEA 0183 Protocol*, Klaus Betke's compilation — is enough for a GNSS talker
and is attached to #310 (a copy lives in the manual library as `NMEA0183.pdf`). Its section 3,
*General Sentence Format*, is the codec; its GGA, GSA, GSV, RMC and ZDA entries are the parser.

The one thing to take from §7 of the specification is that it describes the SmartClock's line
protocol and *nothing else*: §7.2's prompt, echo and connect sequence are that receiver's. A
talker has none of them, and reading §7.2 as "how receivers work" is the first thing to unlearn.

## Step 1 — capture what the receiver actually says

With no hardware, the capture is the simulator's:

```powershell
dotnet run --project tools\NmeaSimulator -- --stdout
```

for a minute, into a file, gives sixty cycles in the shape a real talker's capture will take. The
tests do the same thing in-process: every expectation in
[`NmeaDriverTests`](../tests/WinZ3805A.Tests/Nmea/NmeaDriverTests.cs) is asserted against a
simulated cycle, and the file says so in its remarks, because a value asserted against your own
simulator proves consistency and not truth. **No real talker has been captured yet** — #309, the
BG7TBL, was deferred when the bench unit turned out to put no NMEA on its RS-232 port — and when
one is, its capture is what these get compared against, with whatever disagrees folded back into
both.

> **Finding 1 — the capture harness sends.** `build/Capture-Fixtures.ps1` asks for the status
> screen, strips the echoed command and the prompt, and waits for a state it has not seen. A
> talker needs none of that and cannot be asked: its capture is a timed raw listen. Nothing was
> built for it here — `--stdout` covers the simulator — and a real capture, when there is a talker
> to take one from, is a timed raw listen with the port's control lines noted.
>
> **Finding 2 — `FixtureCorpusTests` assumes every `*.txt` under `Fixtures/` is a status
> screen.** It globs the folder and asserts each file has a `SmartClock Mode` line. An NMEA capture
> put there fails the corpus. Talker captures belong beside their tests
> (`tests/WinZ3805A.Tests/Nmea/`), or the corpus test learns to tell families apart when a real
> capture arrives.

## Step 2 — decide what `ReceiverStatus` can hold

The record is SmartClock-shaped, as #287 recorded, and a talker fills less than half of it. What
maps:

| NMEA | `ReceiverStatus` | Note |
|---|---|---|
| GGA quality, GSA mode | `ModeDetail` | *"no fix"*, *"GPS fix (2D)"*, *"GPS fix (3D)"*, *"differential GPS fix"* |
| GGA quality > 0 | `GpsOnePpsValid` | A judgement: a GPS timing receiver's fix is what makes its 1 PPS valid |
| GSV | `Tracked`, `NotTracked` | An entry with a signal-to-noise ratio is tracked; one without is in view |
| GSV SNR | `SignalStrength` with `SignalStrengthKind.CarrierToNoise` | dB-Hz, on the scale §11.1 calls C/N |
| RMC time and date, ZDA, GGA time | `DeviceDateTime`, `CorrectedDateTime`, `TimeScale.Utc` | ZDA's full year wins; RMC's two-digit year is read as this century; `WeekRolloverEpochs` is 0 — a module's rollover handling is its firmware's |
| GGA quality = 0 | `DeviceTimeIsProvisional` | A judgement: before a fix a module's clock is whatever it last had |
| GGA position and altitude | `Position`, `HeightDatum.Msl` | Only with a fix; south and west negative |

What does not map is left exactly as the contract says — null, or the enum's `Unknown`: TFOM,
FFOM, the 1 PPS time interval, holdover, EFC, the antenna delay, the health monitor, position
mode and survey, the SmartClock mode. The Overview page shows *"No health data"*, the Timing page
shows dashes, and that is correct. **No new field was needed**, which was not obvious going in.

The two judgements are the only places the parser says something the sentences do not say
outright, and both are stated in
[`NmeaStatusParser`](../src/WinZ3805A.Device/Drivers/Nmea/NmeaStatusParser.cs)'s remarks so
they can be argued with.

## Step 3 — write the parsers

Three files, in the order they were written.

**[`NmeaSentence`](../src/WinZ3805A.Device/Drivers/Nmea/NmeaSentence.cs)** takes a line apart —
talker, identifier, fields, checksum — and puts one together. It is the one thing the driver and
the simulator share, and that sharing is a trap: a codec that computes and checks the same wrong
checksum agrees with itself perfectly. So the tests check it against two sentences published
outside this repository, the GLL and GGA examples every reference quotes.

> **Finding 3 — do not type checksums by hand.** Two of this tutorial's first test expectations
> carried checksums computed in the author's head, and both were wrong. Every test line is now
> built by the codec (`NmeaSentence.Format`), and the only hand-typed literals are published ones
> — the GLL and GGA examples, u-blox's `$PUBX` example and a `$GPTXT`. (#316's audit then found
> one more, an RMC line in the contract tests with a wrong checksum that passes only because a
> query/response driver never claims it — the same finding, one file over.)

**[`NmeaStatusParser`](../src/WinZ3805A.Device/Drivers/Nmea/NmeaStatusParser.cs)** reads a whole
cycle into the record per the table above. It follows the guide's rules exactly: never throws (a
last-resort catch turns anything unexpected into a warning), an unreadable field is null with a
reason in `ParseWarnings`, and it parses by field identity — a GSV page's satellites are read as
groups of four fields, never as "the value at offset 12".

**`NmeaDriver.InterpretSweep`** reads the fast tier: RMC for the cycle and its status, GGA for the
fix quality, GSA for the mode, GSV for the satellites being tracked. Its rejection rule is the
SmartClock's transposed — a sweep whose first answer is not an RMC sentence is not a reading from
a talker this driver understands, and is rejected with what was seen.

## Step 4 — declare the command catalog

A talker cannot be sent anything, so the allowlist is reads only: one entry per sentence the
driver understands (`$--RMC`, `$--GGA`, …, written the way the standard writes a sentence's
format, talker-agnostic) and one for the whole cycle. The Advanced Console offers these as reads;
picking one shows the latest of what was heard.

> **Finding 4 — the error-queue query was a SmartClock habit wearing a contract's clothes.** The
> contract test required `:SYST:ERR?` of every driver, because `CommandInvoker` drains it after
> every tier C command. A talker has no tier C commands and the invoker never runs for it. The
> test now requires the entry of query/response families and requires a broadcast family to have
> no tier C command at all — exempt by construction, not by exception.

## Step 5 — the exclusions

There are none, and `IsBlocked` returns false for everything. That is not a shortcut around
§8.4; it is §8.4 applied to a family with no setters. A u-blox has proprietary configuration
sentences (`$PUBX`), a MediaTek has `$PMTK`, and a talker connected to this application is
protected from both by their *absence* from a reads-only catalog — the console cannot type, and
nothing outside the catalog can be sent. `pwsh build/Test-NoBlockedCommands.ps1` passes over the
new files because there is nothing in them to name.

## Step 6 — timeouts and cadence

A talker has no slow command to time. What it has is a rate — one cycle a second — and a
timeout is what silence means: three missed cycles is a talker that has stopped, so `TimeoutFor`
answers three seconds for every key. The cadence is the talker's own second for the readings and
five seconds for the whole cycle, a multiple of it.

## Step 7 — the poll plan

Here the seam moved. A plan entry is *what to ask* on a query/response link; on a broadcast link
it has to be *what to listen for*. The plan keeps its shape — the same record, the same rule that
every entry resolves through the catalog — and gains one word:

- The fast tier is `$--RMC`, `$--GGA`, `$--GSA`, `$--GSV`. **RMC first, because the first entry is
  the cycle boundary**: the session's listener starts a new cycle every time it arrives, and it is
  the one sentence every talker sends exactly once per cycle.
- The full-status query is `PollPlan.WholeCycle` (`*`): every line of the last complete cycle,
  which is what the parser reads. It is in the catalog like any other entry, because the
  session's point-of-send allowlist check does not know it is special.

> **Finding 5 — the plan's queries are keys, and a key needs a classifier.** Two members were
> added to `IReceiverDriver`, both with defaults so the SmartClock is untouched:
> `LinkStyle Link` says which kind of link this is, and `string? ClassifyLine(string line)` says
> which key a heard line belongs to. A new type,
> [`BroadcastListener`](../src/WinZ3805A.Device/Transport/BroadcastListener.cs), reads the
> transport, sorts lines by key into cycles, and answers a key from the last *complete* cycle — so
> GSV, which is paged, is never half a satellite table — and reports a talker that has gone quiet
> as a timeout, which the session's reconnect logic already understands. The session serves a
> broadcast driver's commands from the listener and writes nothing.

## Step 8 — recognition

The guide says to match on the parsed `*IDN?`. A talker never answers `*IDN?`; it announces
itself by talking.

> **Finding 6 — recognition by hearing.** The connect sequence already listened before it probed
> (§7.2's synchronise step absorbs the SmartClock's banner). It now hands every driver what it
> heard through a third new member, `DeviceIdentity? Overhear(IReadOnlyList<string> lines)`, and
> the first to claim the lines is selected without `*IDN?` being sent. `NmeaDriver.Overhear`
> claims a receiver on one valid-checksum sentence from a GNSS talker — a checksum that matches is
> not noise, and a wrong baud rate never produces one. The identity it reports is
> `NMEA 0183,GP talker,,`, which round-trips through `DeviceIdentity.Parse` so the rest of the
> application sees a familiar shape; `Recognises` claims that manufacturer and nothing else.
>
> **Finding 7 — the standard's baud rate was not offered.** NMEA 0183 specifies 4800, and the
> connection dialog offered 1200, 2400, 9600 and 19200 — the SmartClock family's four. 4800 and
> the high-speed 38400 are now in `SerialSettings.SupportedBaudRates` (§7.1 amended), and the
> driver's auto-detect sequence starts at 4800, then the 9600 most modules ship at, then 38400.
> The union walk puts them after the SmartClock's eight, so a SmartClock is found exactly as
> before.

## Step 9 — register it

```csharp
services.AddSingleton<IReceiverDriver>(
    provider => new NmeaDriver(provider.GetRequiredService<TimeProvider>()));
```

One line in `App.Compose`, after the SmartClock's, exactly as promised. Nothing else in the
application project changed for the driver; what changed in the session was for the *link
style*, and serves any broadcast family.

## Step 10 — test it

Five files under [`tests/WinZ3805A.Tests/Nmea/`](../tests/WinZ3805A.Tests/Nmea/):

- `NmeaSentenceTests` — the codec, against the published examples.
- `NmeaTalkerSimulatorTests` — the receiver on the bench: checksums, sentence order, GSV paging,
  the fix schedule, determinism.
- `NmeaDriverTests` — recognition by hearing, classification, the fast sweep at each phase, the
  full parse, the never-throw rule, the reads-only catalog.
- `BroadcastListenerTests` — cycles, what each key answers with, a partial first cycle, silence, a
  closed transport.
- `NmeaSessionTests` — the seam end to end: the real session hears the simulator, selects the
  driver, writes nothing but the synchronise step's `*CLS`, and the real poller reads the store
  from power-up through the 3D fix; a talker that falls silent faults the session; a SmartClock
  is still the SmartClock's with the talker's driver registered.

The contract tests in `ReceiverDriverTests` run against the NMEA driver too, and gained four for
the new members.

> **Finding 8 — force the ordering.** The end-to-end test failed only in the full run: ticks
> advanced the fake clock whether or not the listener had consumed the emitted cycle, so under a
> parallel run the listener could fall three fake seconds behind, the poller read that as silence,
> and the session reconnected onto a fake transport it had disposed. `FakeTransport`'s
> `WaitForReaderToConsume` makes each emit complete only once the reader has it — except for the
> one test that emits *half* a line, which the listener rightly holds back, so that a waiting
> writer would wait forever; that test uses a plain transport and a bounded settle. This is the
> shape every flake in this repository has had; the fix is never a retry.
>
> **Finding 9 — the first sweep asked before the listener had heard anything.** With the
> ordering forced, the test still failed one run in three, and a per-tick trace of every
> transaction showed why: the connect probe *consumes* the talker's first seconds of sentences
> while it waits for a prompt that never comes, so the listener started empty, and the poller's
> first asks — `*` for the full status, then `$--RMC` — came back as timeouts until the next cycle
> landed. Three in a row is "the talker has stopped", and the session reconnected a healthy link.
> Two changes, both in the listener: `Seed` replays what the probe heard, so the first poll has a
> complete cycle, and `Start` gives a fresh listener its own timeout as a grace period, so
> "nothing yet" answers empty rather than stale. A real port would have shown this as an
> occasional reconnect straight after connecting — the kind of fault nobody can reproduce on
> demand, which is what the trace was for.

## Step 11 — what to raise rather than absorb

Made here, because the issue said to make the contract changes here or file them:

| Change | Where | Why |
|---|---|---|
| `LinkStyle Link`, `Overhear`, `ClassifyLine` on `IReceiverDriver`, all defaulted | `Drivers/IReceiverDriver.cs`, `Drivers/LinkStyle.cs` | A broadcast family cannot be described without them; a query/response family need not know they exist |
| `PollPlan.WholeCycle` | `Drivers/IReceiverDriver.cs` | A talker's status is spread across sentences and `Parse` takes one response |
| `BroadcastListener` | `Transport/BroadcastListener.cs` | The read side of a link that is never written to |
| The connect sequence overhears before it asks; a broadcast driver is served from its listener | `Services/DeviceSessionService.cs` | Recognition by hearing; nothing on the wire |
| 4800 and 38400 baud | `Transport/SerialSettings.cs`, §7.1 | The standard's rate was not offered |
| The error-queue contract test binds query/response families | `ReceiverDriverTests` | Finding 4 |

Left open when this was written. The first two were
[#304](https://github.com/TGoodhew/WinZ3805A/issues/304)'s items 3 and 1, which this family made
concrete, and **both shipped on 30 Aug 2026**; the other three are recorded only here:

- ~~**The mode mapping is still app-side.**~~ **Closed by #304.** `IReceiverDriver.InterpretSyncState`
  is the mapping now, and this driver supplies its own. It still says `LOCK` for a fix and `POW` for
  none, because a receiver with a GPS fix is locked to GPS in the only sense it has and one without
  is where a receiver is at power-up — but it says so itself rather than borrowing the SmartClock's
  table, and the token is what `trend.db` stores, so changing the words later would split the
  history. What the seam bought is the family that *cannot* fit those words: it names a
  `ReceiverMode` directly instead of rendering as *Disconnected*.
- ~~**The pages assume their commands exist.**~~ **Closed by #304.** Position, Timing, Holdover and
  the rest still find nothing and show dashes — that part is honest — but their tier C controls are
  now disabled with a sentence naming the family, rather than resolving to a command the catalog
  does not have.
- **The console says *"Will send"* over a link that sends nothing.** Picking `$--GGA` shows the
  latest GGA, which is right; the label is a query/response word.
- **The synchronise step writes `*CLS`** before it knows what it is talking to. A talker ignores
  it, and the end-to-end test pins that it is the *only* thing written — but a link that is never
  written to is written to once.
- **Auto-detect is not exercised over a fake transport.** The session reuses one transport per
  candidate and the fakes are single-use; the walk's *order* is tested, its *outcome* for a talker
  was checked by reasoning: at the wrong rate a talker produces no valid checksum, so nothing is
  overheard, `*IDN?` times out, and the walk moves on.

---

## Running it against the application

Over a serial-port pair (see the simulator's
[README](../tools/NmeaSimulator/README.md) for com0com), with the simulator on one end:

```powershell
dotnet run --project tools\NmeaSimulator -- --port COM7 --baud 4800
```

and the application connected to the other with *Auto-detect settings*. What to expect:

- The log: *"The NMEA 0183 driver overheard GP talker on COM8 in N line(s); the identity probe is
  skipped"*, then *"Session COM8 is now Connected. NMEA 0183,GP talker,,"*.
- The main window: **Power-up** with the satellite count climbing, then **Locked to GPS** at the
  first fix. TFOM, FFOM and 1 PPS TI read as dashes.
- Satellites: the sky plot fills, with the two below the mask never tracked.
- Position: latitude, longitude and, from the 3D fix, height. No survey.
- Time: the receiver's UTC, marked provisional until the fix. No leap second, no time code.
- Overview: *"No health data"*. Holdover: dashes. Diagnostics: no receiver log, no error queue.
- Advanced Console: `$--RMC` and its siblings as reads; nothing to send.

**No real talker is on the bench.** [#309](https://github.com/TGoodhew/WinZ3805A/issues/309) set
out to capture a BG7TBL GPSDO and found its DB9 carries a ~10 kHz square wave gated by DTR and no
NMEA at all, so it was closed as deferred with the bench evidence recorded on it. Resuming needs a
receiver that emits NMEA 0183 at RS-232 levels — or the right pins and a TTL adapter — and a listen
with DTR released first, then asserted: the application asserts DTR and RTS on open (§7.1), which
that unit reacted to, and control-line policy on open is now #304's item 4. Then the capture,
compared with the simulator, with every difference folded back into this tutorial and the guide.
