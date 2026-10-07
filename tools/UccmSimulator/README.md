# UccmSimulator

A Symmetricom or Trimble UCCM telecom GPSDO module that is not a UCCM module.

## Why it exists

The UCCM driver (#416) was written by reading [Lady Heather](https://www.leapsecond.com/heather/)'s
source, because at the time no UCCM hardware was available. Without something to run it against, the
only testing possible is "these parsers turn strings into values correctly" — which leaves the parts
most likely to be wrong completely unexercised, because they are not about a single string at all:

- the receiver **echoes each command** before answering it
- unsolicited **`C5` time codes interleave** with replies, including inside a status
- the two vendors answer `DIAG:LOOP?` in **entirely different shapes**
- a plain UCCM answers a **UCCM-P query with an error**, which is how the variant is told apart

This produces all four, so the driver can be driven as a sequence rather than fed strings.

## Hardware arrived, and two of those four were wrong

**A Trimble UCCM-P has been on the bench since 10 September 2026**, across seven sittings recorded
under [`tests/WinZ3805A.Tests/Uccm/Captures/`](../../tests/WinZ3805A.Tests/Uccm/Captures/README.md).
The captures settled three protocol hypotheses, and this simulator still reproduces the pre-capture
belief for two of them:

| What this simulator does | What the module did | Status |
|---|---|---|
| Echoes each command before answering | Did not echo, in 9 of 9 replies across two sittings | **Refuted** for this module |
| Puts `C5` time codes mid-reply — inside every status, and after the echo with `--interleave` — and ticks one every second. *Since 7 Oct 2026 the `--port` and `--pipe-client` modes write each as the measured 44-byte binary frame; until then they were a line of hex text* | Emits **44-byte binary packets**, `0xC5`–`0xCA`, every 2 s, with **no line terminator** — and 0 of 25 landed mid-reply in a sitting where chance predicted ~9 | **Shape corrected** (#738); the placement still diverges — the module defers a broadcast to the end of a reply |
| Terminates replies with `COMMAND COMPLETE` | Spelled `Command complete`, in 6 of 9 | Confirmed |

**The time code's shape was the one divergence that broke the application rather than flattering
it.** The driver lifts a binary frame out of the byte stream before it splits lines, so the hex text
went straight past that and arrived as a line of its own; polling `LED:GPSL?`, the application
sometimes took that line for the answer, and a connected, locked module read *Disconnected* on the
primary window (found judging 1.4.0's `receiver-families` QA step). The frame now copies the
captures byte for byte wherever they never moved — offsets 1 to 26, 31 and 37 to 40, constant across
all 935 frames of `frames-13sep2026` and `transitions-13sep2026` — carries the GPS second counter at
27 to 30 and this simulator's state bytes at 32 to 36 as before, and **leaves 41 and 42 zero**,
because they look like a checksum whose function nobody has identified (`frames-13sep2026.md`). The
driver checks only the length and the two marker bytes. Writing to stdout still shows the hex text,
for reading, and so does `Respond()`, which the tests hand to a parser as a string; what reaches a
port or a pipe is `RespondOnWire()` and `TimeCodeFrame()`, and `UccmSimulatorWireTests` pushes those
through the driver's real framing. *(Added 7 Oct 2026, #738.)*

**Neither refutation covers the family.** Heather's interleaving claim names *Symmetricom* units and
no Symmetricom module has ever been seen here, so it stands untested rather than disproved; a plain
UCCM has never been seen either. That is why this simulator's behaviours are left in place and the
driver's tolerance for them is left in place: the cost of both is nothing, and the evidence against
them is one module of one variant.

**The transitions sitting found five more, the earlier sittings a sixth, and none of them is one of
the original four.** Every sitting before 13 Sep 2026 caught the module locked and settled, so the
simulator's other states had never been checked against anything. `transitions-13sep2026` took the
module through a cold power-up, acquisition and fourteen minutes of true holdover, and the simulator
disagrees with it here too; the prompt, in the last row, was in every sitting from the first. None
of them has been changed in the simulator, except the prompt in its port and pipe modes (see that
row). *(Added 29 Sep 2026, #556; the exception 6 Oct 2026.)*

| What this simulator does | What the module did | Where |
|---|---|---|
| `--vendor Trimble --state Holdover` emits state bytes `12 41 04 4F 80` — offsets 32 to 36, leap, PPS, antenna, lock, date — with the antenna connected, which is the only way the command line runs it; `12 41 0C 4F 90` with `AntennaConnected` false | Real holdover read **`12 60 0C 4F 90`**, in 462 frames. The simulator's pattern is what the module read while **acquiring**, with the PPS still settling | `transitions-13sep2026.md`, L62–63 |
| Answers `LED:GPSL?` with `0`, and the status with `NO REF` and TFOM 9 FFOM 3, in any state but `Locked` | `LED:GPSL?` answered **`1` throughout** the holdover, and TFOM and FFOM never went past 2 | `transitions-13sep2026.md`, L94–96; the probes from 10:13:27 to 10:27:28 in `transitions-13sep2026.replies.txt` |
| Emits lock byte `0x41` for `--vendor Trimble --state PowerUp`, whichever `--variant` | The very first frame after boot already read `4F`. **`0x41` is a plain-UCCM value this variant never shows** at offset 35 | `transitions-13sep2026.md`, L81–85 |
| Emits leap byte `0x12` — 18, the current GPS-UTC offset — at offset 32 in every state, `PowerUp` included | A cold module read **`00`** until it had a fix, and became `12` at 10:08:38, about two minutes after the antenna went back on | `transitions-13sep2026.md`, L60–61 and L72–73; `transitions-13sep2026.events.txt`, L24 |
| With `--variant UccmP`, answers `:ROSC:HOLD:DUR?` (`412,1` in `Holdover`, `0,0` otherwise), `:GPS:POS:SURV:STAT?` (`0`) and `:GPS:POS:SURV:PROG?` (`100`) | **Refused all three in every state**, in all 27 probes: `Command error` for the holdover duration, when cold, when locked and through fourteen minutes of holdover; `Undefined header` for both survey queries | `transitions-13sep2026.md`, L100–103; `transitions-13sep2026.replies.txt` |
| `Respond()` ends each reply at `COMMAND COMPLETE`, with **no prompt**. *Since 6 Oct 2026 the `--port` and `--pipe-client` modes write `UCCM-P >` after it with `--variant UccmP`, and `UCCM >` otherwise*: without one the application could not connect at all, because the driver has waited for the prompt since #470 — found by the QA pass's `receiver-families`, the first thing ever to try | Every reply is followed by `UCCM-P >`, no trailing space — 9 of 9 in the first sitting, 50 of 50 in `hypothesis2-12sep2026`. A plain UCCM's `UCCM >` is unmeasured | `trimble-uccm-p-2026-09-10.md`, L49; `trimble-uccm-p-2026-09-11.md`, L87; `hypothesis2-12sep2026.md`, L15 |

**What it means for anyone reading a green test.** A pass involving the echo, a mid-reply time code
or the absent prompt proves the driver survives something no measured module does; a pass driven
through the simulator's `Holdover` or `PowerUp` state proves it reads bytes the measured module does
not emit in that state; and a pass that reads an answer to one of the three UCCM-P-only queries
proves it handles a reply the measured module has never given. Both are robustness tests, not fidelity tests, and must not be read as
agreement with hardware — for that, the captures are the authority, and `UccmStatusParser`'s tests and
`UccmTransitionStateTests` replay them. *(Widened 29 Sep 2026, #556, from the echo and the
time code alone.)*

## What it is not

**It reproduces a belief, not a receiver.** Every behaviour here was read out of a third party's
source rather than seen on a wire. The driver and this simulator were written from the same source,
so **a shared misreading passes every test silently.** Green here means the two agree with each
other; it does not mean either is right — and for eight behaviours above, all eight are now known to
disagree with the one module that has been measured. *(Corrected 29 Sep 2026, #556: this said two,
before the five from the transitions sitting and the prompt were listed.)*

That was still worth having. Every disagreement between the real module and this one was a specific,
located finding rather than a vague "the driver doesn't work", which is exactly what #470 and #481
turned out to be.

## Running it

Written to stdout, to see the shapes:

```powershell
dotnet run --project tools\UccmSimulator -- --vendor Trimble --variant UccmP --state Locked
```

On a serial port, for the packaged application to connect to. Use a
[com0com](https://sourceforge.net/projects/com0com/) pair exactly as with `NmeaSimulator`: point
this at one end and the app at the other.

```powershell
dotnet run --project tools\UccmSimulator -- --port COM11 --baud 9600 --vendor Symmetricom
```

| Option | Values | Default |
|---|---|---|
| `--port` | a serial port name; omit to write to stdout | stdout |
| `--baud` | | 9600 |
| `--vendor` | `Symmetricom`, `Trimble` | `Symmetricom` |
| `--variant` | `Uccm`, `UccmP` | `Uccm` |
| `--state` | `PowerUp`, `Settling`, `Locked`, `Holdover` | `Locked` |
| `--interleave` | put an unsolicited time code inside **every** reply | off |

`--interleave` is harsher than a real module is believed to be, deliberately: if the driver survives
a time code in every single reply, one arriving occasionally will not surprise it.

## The baud rate was a guess, and it was wrong

This program's `--baud` still defaults to **9600**, which is what Heather implies and what the
driver's auto-detect sequence tried first. The bench Trimble UCCM-P answers at **57600-8-N-1**
(#470), which the driver's sequence now also carries and §7.1 records. The default here is left
alone deliberately: it is a knob on a program that reproduces a belief, and the person pointing it
at a port passes whatever rate they want. **It is not evidence about any module.**

## When more hardware arrives

The pattern that worked is [`build/Capture-Uccm.ps1`](../../build/Capture-Uccm.ps1): it sends the
catalogue and keeps the bytes verbatim — the echo, the prompt and anything unsolicited — and reports
each protocol belief as a **count** rather than assuming it, because a harness built on a hypothesis
confirms it by construction. Its `-SelfTest` runs in CI and needs no module.

The two things still worth bringing back are a **Symmetricom** module of any kind, and a **plain
UCCM** rather than a UCCM-P: the first is the only way to test Heather's interleaving claim, which
names that vendor, and the second is what the variant discrimination above was written for and has
never met. A raw byte log of a whole session — connect, steady state, and losing the antenna if that
is safe — settles most of the rest at once, and the captures it produces are permanent where the
opportunity is not (§11.1).
