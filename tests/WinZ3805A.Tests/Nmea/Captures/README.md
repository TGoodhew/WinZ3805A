# Captured talker output

Raw bytes from a real NMEA 0183 talker, written by `build/Capture-Talker.ps1`. Nothing here has
been decoded, re-terminated or trimmed.

**The driver met its first real receiver on 7 September 2026** (#420) — a **VK-162** USB GPS puck,
u-blox `UBX-G70xx`, ROM CORE 1.00 (59842), PROTVER 14.00, at 9600-8-N-1 on COM4. Before that day
everything the NMEA family had ever been tested against came from `tools/NmeaSimulator` (#310).

| Capture | Duration | Bytes | What it is for |
|---|---|---|---|
| `vk162-steady-state` | 30 min | 927 KB | The boring case at length. A differential 3D fix in all 1,800 cycles, and **exactly 1,800 of each per-second sentence** — so a gap in any other capture is the receiver's doing, not the harness's. |
| `vk162-cold-start` | 5.6 min | 186 KB | Power-on. The **only no-fix cycle** anywhere in the corpus, then all three GGA qualities and all three RMC mode indicators in order. |
| `vk162-microwave` | — | — | The same receiver at the edge of its sensitivity, ~10 dB down, with a fifth of its satellites in view but untracked. |
| `vk162-glonass-only` | 14 min | 352 KB | The only capture taken with the receiver **reconfigured**, and the only non-`GP` talker. **152 consecutive no-fix cycles** — the corpus's longest — then acquisition, and the fix ladder without SBAS. |

Each has a `.md` beside it saying what was happening; read those rather than this table.

`NmeaCaptureReplayTests` replays every one of them through the real `BroadcastListener` and
`NmeaStatusParser`, cycle by cycle, and holds each cycle to what must be true of any of them.

## What the corpus still does not contain

Being explicit about this matters more than the table above, because a gap nobody wrote down is a
gap somebody later assumes is covered.

- **A fix lost while powered.** Every attempt on 7 September failed: an inverted metal cover
  managed about 5 dB of attenuation and a microwave oven with the door shut about 10 dB, and
  neither stopped an 11-satellite fix. It needs a real enclosure. This is the one item of #420's
  stage 2 still outstanding, and the reason nothing here exercises a receiver going *from* a fix
  *to* none.
- **Two constellations *at once*.** Corrected 8 Sep 2026: this receiver is **not** GPS-only, and the
  earlier claim here was a label believed rather than a receiver asked. Its ROM advertises
  `GPS;SBAS;GLO;QZSS` and `UBX-CFG-GNSS` carries a GLONASS block, which is why
  `vk162-glonass-only` exists. What it will not do is run two together — GPS + GLONASS is **NAKed**,
  and so is GLONASS beside SBAS or QZSS, those being GPS augmentations. #424 needs two
  constellations *in one cycle* and cannot be answered here. It also needs a receiver that numbers
  satellites per constellation, and this one does not: its GLONASS PRNs are **67–85**, inside NMEA
  4.10's 65–96 range.
- **`GNS`.** Never emitted, so #429 is likewise unanswerable here.
- **A dynamic model that changes anything the driver sees.** `CFG-NAV5` was set to **stationary**
  and a 12-minute sitting taken on 8 Sep 2026. The sentence set was identical and the latitude
  spread was **12.04 m against 12.98 m** for the portable `vk162-steady-state` — indistinguishable.
  **Deliberately not committed:** it would be the only row in the table above with nothing to put in
  the last column, and a near-duplicate costs replay time in CI for no coverage. The measurement is
  the result; the bytes add nothing and can be re-taken in twelve minutes.
- **A truncated sentence from a cable pull.** `vk162-steady-state` ends mid-sentence, but only
  because the capture's clock ran out — it stops one byte into the next sentence, on a lone `$`.
  `vk162-cold-start` *was* ended by pulling the lead and still ends on a complete sentence, the
  lead having come out during the idle gap between cycles. Truncation by cable pull is therefore
  still unobserved, and it is luck rather than design that separates the two.

## Why not `Fixtures/`

`FixtureCorpusTests` globs every `*.txt` under `tests/WinZ3805A.Tests/Fixtures/` and asserts that
each one **is a SmartClock status screen**. That check exists because an arbitrary text file parses
to nulls and then satisfies every assertion vacuously — a corpus of junk passes and reports itself
as covered. It has caught this before: `Capture-Fixtures.ps1` wrote its own log into that folder and
the corpus collected it as a screen (#221).

A talker log is not a status screen, so it belongs beside the driver's own tests rather than in a
corpus that would either reject it or, worse, accept it.

## The bytes are the point

`.gitattributes` marks `*.nmea` here `-text`, so git performs no end-of-line conversion in either
direction on any platform — the same rule the SmartClock fixtures live under, for the same reason.

A talker emits things the parser has to survive: a sentence split across reads, a bare LF where CRLF
was expected, a truncated line when a cable is pulled, noise at the wrong baud rate. All of that is
only in the capture if the capture did not tidy it away.

## Every capture has a `.md` beside it

`Capture-Talker.ps1` writes the port, rate, duration, byte count, sentence and talker inventory, and
a **"What was happening"** section left deliberately blank.

**Fill that in on the day.** Where the antenna was, what was done to the receiver and when, anything
seen on screen that the bytes alone will not explain. Only the person who was there can write it,
and a capture nobody can attribute is a file rather than evidence. `NmeaCaptureReplayTests` fails a
capture whose note still carries the placeholder, so this is enforced rather than requested.

The extension is `.md` on purpose: `.txt` gets collected by the fixture corpus, and `.log` is
gitignored, so provenance written to either never reaches the repository. Both have happened (#221).

## The positions in these files are real

They were not scrubbed, and that was a decision rather than an oversight: a capture edited to be
safe is no longer byte-exact, and byte-exact is the only property that makes it worth keeping.
Anyone adding a capture should know that is what they are committing.
