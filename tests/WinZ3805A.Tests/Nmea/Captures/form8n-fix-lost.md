# form8n-fix-lost.nmea

Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been
decoded, re-terminated or trimmed.

| | |
|---|---|
| Port | COM7 at 9600-8-N-1 |
| Started | 2026-09-11 18:59:53 -07:00 |
| Ended | 2026-09-11 19:11:53 -07:00 |
| Duration | 12 min |
| Bytes | 413365 |
| Sentences, checksum good | 7290 |
| Sentences, rejected | 0 |
| Talkers seen | GB, GN, GP |
| Sentences seen | GGA, GLL, GSA, GSV, RMC, TXT, VTG |
| Ends mid-sentence | no |
| **Cold start sent** | 2026-09-11 19:01:53 -07:00, at byte 87164 |

## What was happening

**The fix was taken away deliberately, and this is the capture #420's stage 2 was still missing.**

The RCmall forM8N — u-blox `HW UBX-M8130`, PROTVER 18.00 — indoors on its own patch antenna, on COM7.
Two minutes of an ordinary 3D fix, then a **`UBX-CFG-RST` cold start** at 19:01:53, at byte 87164.
Nothing else was done to it: it was not moved, covered or unplugged, and the USB link never dropped.

| | |
|---|---|
| Cycles | 721 |
| Fix before the reset | 120 cycles |
| **No fix** | **65 cycles** |
| Fix after | 536 cycles |

Exactly three runs — `A`×120, `V`×65, `A`×536 — so the transition is unambiguous in both directions.

### Why a cold start rather than an enclosure

The README's list of what the corpus lacked said this case "needs a real enclosure", because every
attempt on 7 September failed: an inverted metal cover managed about 5 dB and a microwave oven with
its door shut about 10 dB, and neither dented an 11-satellite fix.

**Attenuating the signal was the wrong approach.** A cold start throws the ephemeris away instead, so
the receiver has nothing to compute a fix from and has to reacquire from cold — which is the state
change the driver had never been shown. `navBbrMask` is `0xFFFF` and `resetMode` is **`0x02`**, a
controlled reset of the GNSS subsystem only; `0x00` would re-enumerate the USB device and take the
port with it, ending the capture instead of continuing it.

It was proven on the VK-162 first, which gave 87 void cycles and a reacquisition through 2D to 3D to
differential. This module went **straight from no fix to 3D** with no 2D rung — worth knowing before
anyone writes a test expecting the ladder, and the reason the synthetic test in
`NmeaOutageAndBoundaryTests` keeps its own 2D step rather than being replaced by this file.

### What else is in here, unplanned

**433 of the 721 cycles collide** — two talkers reporting the same satellite number in one cycle.
BeiDou was tracking indoors during this sitting, which it was not earlier the same evening
(`$GBGSV,1,1,00`), so this is a second collision capture and the first one taken indoors. It is
incidental to what the file was recorded for, and it means `form8n-gps-beidou-outdoors` is no longer
the corpus's only colliding sitting.

**Thirteen cycles carry a time and no date**, all immediately after the reset. A receiver that has
thrown its almanac away emits a time before it can date it, and §11.1's "the cycle carried a time but
no date" path had never been reached by anything but a hand-written test until now.
