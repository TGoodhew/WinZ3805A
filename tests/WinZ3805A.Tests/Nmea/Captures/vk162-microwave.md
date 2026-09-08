# vk162-microwave.nmea

Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been
decoded, re-terminated or trimmed.

| | |
|---|---|
| Receiver | VK-162 USB GPS puck — u-blox `UBX-G70xx`, ROM CORE 1.00 (59842), PROTVER 14.00 |
| Port | COM4 at 9600-8-N-1 |
| Started | 2026-09-07 18:01:17 -07:00 |
| Ended | 2026-09-07 18:07:44 -07:00 |
| Duration | 6.5 min |
| Bytes | 210429 |
| Sentences, checksum good | 3490 |
| Sentences, rejected | 0 |
| Talkers seen | GP |
| Sentences seen | GGA, GLL, GSA, GSV, RMC, TXT, VTG |
| Ends mid-sentence | no |

## What was happening

**The receiver was inside a microwave oven with the door shut** — not switched on — for the whole
of this capture, on 7 September 2026. It was plugged in already inside, so the file starts at
power-on. Part-way through, the door was opened and an inverted saucepan was placed over the puck
*inside* the oven, and the door shut again; that made no measurable difference.

**This was a failed attempt to take the fix away, and it is kept because the failure is the
finding.** The intent was a sustained outage — a receiver that has a fix and loses it — which is
the one thing #420's stage 2 asked for that the day did not produce. Neither an inverted metal
cover (about 5 dB, see `vk162-cold-start.md`) nor this oven stopped it:

| | mean C/N | median | min | max |
|---|---|---|---|---|
| `vk162-steady-state`, outdoors | 36.6 | 38 | 4 | 49 |
| `vk162-cold-start`, under a metal cover | 31.1 | 31 | 8 | 44 |
| **this capture, in the microwave** | **27.4** | **27** | **6** | **45** |

About 10 dB of attenuation against open sky, and it still held a fix in **every one of 387
cycles** — `GGA` quality `1` and `RMC` status `A` throughout, on 7 to 11 satellites. A GPS receiver
is simply more sensitive than household metalwork. **Anyone planning an outage sitting should skip
straight to a proper enclosure** rather than repeating this.

What the file is genuinely good for is the opposite of what it was made for: **a receiver working
at the edge of its sensitivity.**

- **17% of `GSV` entries have a blank SNR** — in view, not tracked — against 6.6% in the outdoor
  capture. This is the corpus's densest supply of `PredictedSatellite`, the branch
  `NmeaStatusParser.Satellites` takes when a group's signal field is empty.
- **The satellite count moves between 7 and 11** within six minutes, so satellites cross the
  tracked/not-tracked boundary repeatedly. In the outdoor capture the count barely moves.
- **Signal strengths cluster where the §9.4.4 sequential ramp is hardest to read**: a median of 27
  sits inside the C/N 26–34 span that #218 found being drawn in the page background under high
  contrast. This is the realistic input for that ramp, rather than the strong signals a receiver
  with a clear view produces.

**A note on the figures above.** This is the first capture written after the summariser fix in
`Capture-Talker.ps1`, and its stated count of 3,490 good sentences matches an independent recount of
the bytes exactly. The two captures before it were written by the defective version — see the note
in `vk162-steady-state.md`, whose figures had to be recomputed by hand.

**The position in these sentences is genuine and was not scrubbed**, for the reason given in
`vk162-steady-state.md`.
