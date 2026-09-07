# Captured talker output

Raw bytes from a real NMEA 0183 talker, written by `build/Capture-Talker.ps1`. Nothing here has
been decoded, re-terminated or trimmed.

**Empty so far.** The NMEA driver has never met a real talker (#310, #420) — everything it has been
tested against came from `tools/NmeaSimulator`. The first capture goes here.

## Why not `Fixtures/`

`FixtureCorpusTests` globs every `*.txt` under `tests/WinZ3805A.Tests/Fixtures/` and asserts that
each one **is a SmartClock status screen**. That check exists because an arbitrary text file parses
to nulls and then satisfies every assertion vacuously — a corpus of junk passes and reports itself
as covered. It has caught this before: `Capture-Fixtures.ps1` wrote its own log into that folder and
the corpus collected it as a screen (#221).

A talker log is not a status screen, so it belongs beside the driver's own tests rather than in a
corpus that would either reject it or, worse, accept it.

## The bytes are the point

`.gitattributes` marks this folder `-text`, so git performs no end-of-line conversion in either
direction on any platform — the same rule the SmartClock fixtures live under, for the same reason.

A talker emits things the parser has to survive: a sentence split across reads, a bare LF where CRLF
was expected, a truncated line when a cable is pulled, noise at the wrong baud rate. All of that is
only in the capture if the capture did not tidy it away.

## Every capture has a `.md` beside it

`Capture-Talker.ps1` writes the port, rate, duration, byte count, sentence and talker inventory, and
a **"What was happening"** section left deliberately blank.

**Fill that in on the day.** Where the antenna was, what was done to the receiver and when, anything
seen on screen that the bytes alone will not explain. Only the person who was there can write it,
and a capture nobody can attribute is a file rather than evidence.

The extension is `.md` on purpose: `.txt` gets collected by the fixture corpus, and `.log` is
gitignored, so provenance written to either never reaches the repository. Both have happened (#221).

## What a capture is for

Three open issues want one:

- **#420** — the driver against actual receiver output, which is the whole point of stage 2
- **#424** — whether two constellations ever number their satellites the same way, which needs a
  multi-constellation receiver emitting `$GPGSV` and `$GLGSV` in one cycle
- **#429** — whether `GNS` ever arrives without `GGA`

A single log covering cold start, acquisition, steady state and a mid-session cable pull serves all
three, and can be replayed indoors for ever afterwards.
