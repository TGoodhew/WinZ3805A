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
| Emits `C5` time codes as text, interleaved mid-reply | Emits **44-byte binary packets**, `0xC5`–`0xCA`, every 2 s, with **no line terminator** — and 0 of 25 landed mid-reply in a sitting where chance predicted ~9 | **Wrong shape**; the module defers a broadcast to the end of a reply |
| Terminates replies with `COMMAND COMPLETE` | Spelled `Command complete`, in 6 of 9 | Confirmed |

**Neither refutation covers the family.** Heather's interleaving claim names *Symmetricom* units and
no Symmetricom module has ever been seen here, so it stands untested rather than disproved; a plain
UCCM has never been seen either. That is why this simulator's behaviours are left in place and the
driver's tolerance for them is left in place: the cost of both is nothing, and the evidence against
them is one module of one variant.

**What it means for anyone reading a green test.** A pass involving the echo or a mid-reply time
code proves the driver survives something no measured module does. That is a robustness test, not a
fidelity test, and it must not be read as agreement with hardware — for that, the captures are the
authority and `UccmStatusParser`'s tests replay them.

## What it is not

**It reproduces a belief, not a receiver.** Every behaviour here was read out of a third party's
source rather than seen on a wire. The driver and this simulator were written from the same source,
so **a shared misreading passes every test silently.** Green here means the two agree with each
other; it does not mean either is right — and for two behaviours above, both are now known to
disagree with the one module that has been measured.

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
