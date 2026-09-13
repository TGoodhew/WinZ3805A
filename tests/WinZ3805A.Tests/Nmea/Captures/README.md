# Captured talker output

Raw bytes from a real NMEA 0183 talker, written by `build/Capture-Talker.ps1`. Nothing here has
been decoded, re-terminated or trimmed.

**The driver met its first real receiver on 7 September 2026** (#420) — a **VK-162** USB GPS puck,
u-blox `UBX-G70xx`, ROM CORE 1.00 (59842), PROTVER 14.00, at 9600-8-N-1 on COM4. Before that day
everything the NMEA family had ever been tested against came from `tools/NmeaSimulator` (#310).

**A second receiver arrived on 9 September 2026** — an **RCmall forM8N**, u-blox `HW UBX-M8130`,
ROM CORE 3.01 (107888), PROTVER 18.00, whose one-time-programmable constellation set is `GPS;BDS`.
It is what made #424 answerable, though not on the first attempt: see the table's `first-light` and
`outdoors` rows, which are the same module on two different antennas.

| Capture | Duration | Bytes | What it is for |
|---|---|---|---|
| `vk162-steady-state` | 30 min | 927 KB | The boring case at length. A differential 3D fix in all 1,800 cycles, and **exactly 1,800 of each per-second sentence** — so a gap in any other capture is the receiver's doing, not the harness's. |
| `vk162-cold-start` | 5.6 min | 186 KB | Power-on. The **only no-fix cycle** anywhere in the corpus, then all three GGA qualities and all three RMC mode indicators in order. |
| `vk162-microwave` | — | — | The same receiver at the edge of its sensitivity, ~10 dB down, with a fifth of its satellites in view but untracked. |
| `vk162-glonass-only` | 14 min | 352 KB | The only non-`GP` talker. **152 consecutive no-fix cycles** — the corpus's longest — then acquisition, and the fix ladder without SBAS. |
| `vk162-gns-no-gga` | 12 min | 353 KB | **720 `GNS` and not one `GGA`** — #429's configuration, made with `UBX-CFG-MSG`. Proves the fix survives `GGA`'s absence, and that the altitude and the per-constellation mode string are what its absence costs. |
| `form8n-gps-beidou-first-light` | 6.7 min | 202 KB | The forM8N as it arrived, on a GPS **timing** antenna. BeiDou is enabled, reports every cycle and **tracks nothing** — `$GBGSV,1,1,00` and a `GSA` row with only its system id. A talker advertising a constellation it cannot see. |
| `form8n-gns-without-gga` | 2.2 min | 77 KB | The same module's `GNS` configuration, where the mode indicator is **four** characters (`ANNN`) against the VK-162's two — the field is one character per constellation, not a fixed code. |
| `form8n-gps-beidou-outdoors` | 30 min | 1.1 MB | The same module on its **own patch antenna, outdoors**, where BeiDou does track. 1,217 of its 1,800 cycles have two talkers reporting the same satellite number, and it is the corpus's **only** crossing of UTC midnight. |
| `form8n-gst-gbs` | 5 min | 247 KB | **The only capture carrying `GST` or `GBS`** — 300 of each, enabled with `UBX-CFG-MSG` because no receiver here sends them (#516). Position error in metres, and RAIM integrity. Also the corpus's clearest case of **geometry and error disagreeing**: HDOP 0.65–0.82 with a horizontal error near 3.2 m. Its `GST` range RMS spans 17 to 3,179,277 and is **not usable**; the deviations beside it are steady. |
| `form8n-fix-lost` | 12 min | 413 KB | **The only capture in which a fix is taken away and comes back** — 120 cycles of 3D, a `UBX-CFG-RST` cold start at byte 87164, **65 cycles with no fix**, then 536 with it back. Also the only one holding the "time but no date" state, and a second colliding sitting at 433 of 721 cycles. |

Each has a `.md` beside it saying what was happening; read those rather than this table.

`NmeaCaptureReplayTests` replays every one of them through the real `BroadcastListener` and
`NmeaStatusParser`, cycle by cycle, and holds each cycle to what must be true of any of them.

## What the corpus still does not contain

Being explicit about this matters more than the table above, because a gap nobody wrote down is a
gap somebody later assumes is covered.

- ~~**A fix lost while powered.** Every attempt on 7 September failed: an inverted metal cover
  managed about 5 dB of attenuation and a microwave oven with the door shut about 10 dB, and
  neither stopped an 11-satellite fix. It needs a real enclosure.~~ **Answered 11 September 2026,
  and it did not need one.** Attenuating the signal was the wrong approach: a **cold start** throws
  the ephemeris away, so the receiver has nothing to compute a fix from and must reacquire from
  cold, with the link never going down. `form8n-fix-lost` is twelve minutes of the result — 120
  cycles of fix, 65 with none, 536 with it back.

  Two details are what make it work. `resetMode` is **`0x02`**, a controlled reset of the GNSS
  subsystem only; `0x00` re-enumerates the USB device and takes the port with it, ending the capture
  rather than continuing it. And the frame's checksum failure mode is **silence** — a receiver
  ignores a bad one without complaining, so the sitting would run its full length, keep its fix, and
  look like an ordinary capture. `build/Capture-Talker.ps1 -ColdStartAfterMinutes` sends it and its
  self-test pins the bytes.
- **Two constellations *at once*.** Corrected 8 Sep 2026: this receiver is **not** GPS-only, and the
  earlier claim here was a label believed rather than a receiver asked. Its ROM advertises
  `GPS;SBAS;GLO;QZSS` and `UBX-CFG-GNSS` carries a GLONASS block, which is why
  `vk162-glonass-only` exists. What it will not do is run two together — GPS + GLONASS is **NAKed**,
  and so is GLONASS beside SBAS or QZSS, those being GPS augmentations. #424 needs two
  constellations *in one cycle* and cannot be answered here. It also needs a receiver that numbers
  satellites per constellation, and this one does not: its GLONASS PRNs are **67–85**, inside NMEA
  4.10's 65–96 range.

  **Answered 9 September 2026, on the other receiver.** `form8n-gps-beidou-outdoors` runs GPS and
  BeiDou together, numbers BeiDou per constellation — 6, 20, 23, 24, 25, all inside the GPS range —
  and carries GPS 4 and BeiDou 4 in the same cycle 1,217 times out of 1,800. So the corpus now holds
  one receiver of each kind: the VK-162, which cannot collide because its GLONASS PRNs are 67–85, and
  the forM8N, which collides in 68% of its cycles. What remains open on #424 is the model change,
  not the evidence for it.
- ~~**`GNS`.** Never emitted, so #429 is likewise unanswerable here.~~ **Answered 8 Sep 2026.** It is
  not emitted *by default*, but two `UBX-CFG-MSG` frames turn `GNS` on and `GGA` off, and
  `vk162-gns-no-gga` is twelve minutes of the result. #429 said the reachability of that
  configuration "is unknown"; it is two frames. What remains open on that issue is the decision of
  whether to read `GNS`, not the evidence for it.
- ~~**Pseudorange error statistics and RAIM integrity.** `GST` and `GBS` are emitted by nothing on
  the bench, so #435 filed both as waiting for hardware that sends them.~~ **Answered 12 September
  2026, and it did not need hardware.** It needed two `UBX-CFG-MSG` frames, the same technique
  `vk162-gns-no-gga` and `form8n-fix-lost` already rest on, and
  `build/Capture-Talker.ps1 -EnableSentences GST,GBS` sends them. The second doubt in that issue —
  that RAIM needs redundancy so an indoor fix might yield an empty `GBS` — did not survive contact
  either: a 12-satellite indoor fix filled the error estimates in all 300 cycles.

  **What the capture does not contain is a fault.** No satellite was ever flagged in it, so the
  populated half of `GBS` — a satellite id, a bias, a missed-detection probability — remains
  unobserved, and the only test of it is explicitly synthetic. A healthy receiver and a receiver
  never asked both leave those fields blank, which is why the model distinguishes an absent report
  from a clean one rather than treating a blank id as good news.
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
