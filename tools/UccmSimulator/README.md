# UccmSimulator

A Symmetricom or Trimble UCCM telecom GPSDO module that is not a UCCM module.

## Why it exists

The UCCM driver (#416) was written by reading [Lady Heather](https://www.leapsecond.com/heather/)'s
source, because no UCCM hardware was available. Without something to run it against, the only
testing possible is "these parsers turn strings into values correctly" — which leaves the parts most
likely to be wrong completely unexercised, because they are not about a single string at all:

- the receiver **echoes each command** before answering it
- unsolicited **`C5` time codes interleave** with replies, including inside a status
- the two vendors answer `DIAG:LOOP?` in **entirely different shapes**
- a plain UCCM answers a **UCCM-P query with an error**, which is how the variant is told apart

This produces all four, so the driver can be driven as a sequence rather than fed strings.

## What it is not

**It reproduces a belief, not a receiver.** Every behaviour here is something read out of a third
party's source rather than seen on a wire. The driver and this simulator were written from the same
source, so **a shared misreading passes every test silently.** Green here means the two agree with
each other; it does not mean either is right.

That is still worth having. When a real module arrives, every disagreement between it and this is a
specific, located finding rather than a vague "the driver doesn't work" — and the fix belongs in
*both* files, because if this was wrong the tests that passed were testing the wrong thing.

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

## The baud rate is a guess

9600-8-N-1 is assumed and **not verified**. Heather sets no baud rate of its own for these modules.
If the real unit turns out to be something else, this default and the driver's auto-detect sequence
are both wrong and both want correcting.

## When hardware arrives

The single most useful thing to bring back is a **raw byte log of a whole session** — connect,
steady state, and an antenna disconnect if that is safe to do. It settles the echo and interleaving
behaviour, the vendor discrimination, the timeouts and the field meanings at once, and it turns this
simulator's guesses into a fixture under `tests/WinZ3805A.Tests/Fixtures/` that runs in CI forever.
That is the pattern every part of this project that turned out right has followed (§11.1).
