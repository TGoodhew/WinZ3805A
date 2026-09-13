# transitions-13sep2026

**The first capture of this family in any state other than locked and settled.** Every earlier
sitting caught a module that had been up for hours with a good antenna, so forty of the forty-four
time-code bytes never moved and the corpus could say nothing about what any of them meant.

| | |
|---|---|
| Module | `TRIMBLE,57964-80,40896646,V2.0.1.6-01`, a **UCCM-P** |
| Port | COM3 at 57600-8-N-1, Prolific PL2303GT |
| Site | Mt Warrigal; surveyed position S 34:32:39.019, E 150:50:25.107, +49.72 m MSL |
| Taken | 13 Sep 2026, 10:02 to 10:30 +10:00 |
| Harness | `Watch-UccmTransitions.ps1` — listens continuously rather than taking one catalogue pass |

Three files, written as they arrived rather than at the end:

- `.frames.txt` — every 44-byte `C5`..`CA` time code, one per line, timestamped
- `.events.txt` — each watched byte the moment it changed, and each gap in the data
- `.replies.txt` — a probe of six catalogued queries once a minute, verbatim

## What was done to the receiver, and when

Tony drove the hardware; the times are from the capture, not from memory.

| time | action | what it produced |
|---|---|---|
| before 10:02 | powered, antenna already disconnected | cold, has never locked |
| 10:03:03 | **power cycled**, antenna still off | 18.2 s of silence; first frame at boot + 2 s |
| ~10:06:15 | **antenna reconnected** | acquisition from cold |
| 10:11:08 | — | **locked**, 7 min 55 s after power-on |
| 10:12:40 | **antenna disconnected** | true holdover, from a locked module with a valid fix |
| 10:28:18 | **antenna reconnected** | warm reacquisition |

The 18.2 s gap at 10:03 is the power cycle: the USB adapter stays enumerated while the module is
down, so the reads simply return nothing and the gap length is the down time.

## Eight state-byte combinations, against the one the old corpus had

Offsets 32 to 36 — leap, PPS state, antenna, lock, date validity:

```
111  00 41 08 4F 90   cold, antenna never present since power-up
 64  00 41 00 4F 90   antenna just reconnected, not yet validated
462  12 60 0C 4F 90   TRUE HOLDOVER
 50  12 41 04 4F 80   acquiring, antenna good, date valid, PPS still settling
 59  12 60 04 45 80   locked   <- the only combination in every previous capture
 25  12 41 00 4F 80   transitional
  9  12 60 04 4F 90   the first ten seconds of holdover, before the antenna byte caught up
  5  00 41 00 4F 80   transitional
```

## What this settles

**Offset 32 is the leap-second offset.** It read `00` with no fix and became `12` — decimal 18,
the current GPS-UTC offset — at 10:08:38, about two minutes after the antenna went back on. That
also confirms the existing remark on `Reports`, which says the leap offset is real on this family
and arrives in the time code rather than from any query.

**Heather's Trimble transitions are confirmed on hardware**, four of five exactly as her comments
in `heathgps.cpp` record them: `[33]` `41`→`60`, `[35]` `4F`→`45` on power-up and `45`→`4F` on
antenna disconnect, `[36]` `90`→`80` and back.

**`0x41` is not a UCCM-P value.** Her comments distinguish `41 -> 4F -> 45` on a plain UCCM from
`4F -> 45` on a UCCM-P, and this module's very first frame after boot — two seconds after power, the
earliest a frame can exist — already read `4F`. So the power-up lock state our driver carries as
Trimble's is a plain-UCCM value that this variant never shows. Note `0x41` *is* in every cold frame,
at offset **33**, where Heather glosses it as phase settling; that is the likely origin of the number.

**The antenna byte has a value nobody had recorded.** Heather lists `00` at power-up, `04` normal,
`0C` open or shorted, `06` normal(?). Powered up with no antenna attached, this module reads
**`0x08`**, which is in neither her list nor ours. The two reconnects took different paths and the
difference is the point: from cold it went `08 -> 00 -> 04` over about three minutes, while from
holdover it went `0C -> 04` at once. Her table describes the warm path correctly; the cold path is
what is undocumented.

**Holdover is invisible to the text and plain in the bytes.** Fourteen minutes with no antenna and
the module never degraded TFOM or FFOM past 2 and never moved off `UCCM A Status[ACTIVE]`;
`LED:GPSL?` answered `1` throughout. But the frame says it clearly — leap present and PPS stable,
so it *had* a fix, with the date gone invalid, so it has lost it. That combination cannot occur on a
module that has never locked, whose leap is absent and whose PPS is still settling.

**`:ROSC:HOLD:DUR?` is refused in every state.** `Command error` when cold, when locked, and through
fourteen minutes of genuine holdover. The two survey queries are refused differently — `Undefined
header` — so the module distinguishes a mnemonic it does not know from one it will not answer, and
all three are catalogued as the queries only a UCCM-P answers.

**`FREQ_DIFF` leaves the sanity band only when undisciplined.** `-3.59E-07` cold, against the
±2.00E-7 the driver accepts; `+7.18E-10` once locked. So the band is right for a disciplined
oscillator and the open question is what to do with a reading from an oscillator that is not.

## What it does not settle

Offsets 1 to 26 and 37 to 40 were constant in all 785 frames, as they have been in every capture
ever taken here, so they remain unknown. The trailing pair 41-42 is uniform over the full 16-bit
range with a mean near zero and a mean consecutive delta of 21,861 against the 21,845 predicted for
independent uniform draws — the signature of a checksum output, which rules out the sawtooth
correction it might otherwise have been, but thirty standard constructions fail against all 285
frames tested and prior work showed the function is not affine over GF(2), which excludes every CRC.

A plain UCCM has still never been seen. Everything here is one module of one variant.

## One caveat about the `.events.txt` annotations

The harness prints a human-readable GPS time beside the first frame of each run, and **those
annotations are 11 hours late**. `[datetime]'1980-01-06T00:00:00Z'` in PowerShell converts to
*local* time, and 6 January falls in Australian daylight saving, so the epoch was taken as UTC+11.
The script has been corrected; the files here were written before that.

**No raw data is affected.** The frames, the byte values and the seconds counts are exactly what
came off the wire — only the convenience annotation was wrong, and only in `.events.txt`. Decoded
properly, the frames agree with their own capture timestamps to the second:

| frame | `[27..30]` | GPS | UTC | capture timestamp |
|---|---|---|---|---|
| holdover | `57 D0 B0 62` | 2026-09-13 00:27:14Z | 00:26:56Z | 10:26:56 +10:00 |
| relocked | `57 D0 B0 D0` | 2026-09-13 00:29:04Z | 00:28:46Z | 10:28:46 +10:00 |

That agreement is worth more than the annotation was: it confirms offsets 27-30, the byte order and
the epoch a second time, independently of the 12 Sep sitting that first established them, and it
confirms the retained leap offset of 18 is being applied in the right direction.

With no fix at all the counter reads from a base of **1999-08-22 00:00:00 UTC** exactly, plus
seconds since power-up — the first frame after the 10:03 power cycle read `24 EA 00 02`, two
seconds past that base.
