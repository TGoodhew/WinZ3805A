# vk162-cold-start.nmea

Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been
decoded, re-terminated or trimmed.

| | |
|---|---|
| Receiver | VK-162 USB GPS puck — u-blox `UBX-G70xx`, ROM CORE 1.00 (59842), PROTVER 14.00 |
| Port | COM4 at 9600-8-N-1 |
| Started | 2026-09-07 17:50:20 -07:00 |
| Ended | 2026-09-07 17:55:58 -07:00 |
| Duration | 5.6 min |
| Bytes | 186014 |
| Sentences, checksum good | 3058 |
| Sentences, rejected | 0 |
| Talkers seen | GP |
| Sentences seen | GGA, GLL, GSA, GSV, RMC, TXT, VTG |
| Ends mid-sentence | no |

## What was happening

The receiver was **unplugged for 9.4 minutes** and then plugged back in under an inverted metal
cover, on 7 September 2026. The capture starts at the instant the port appeared, so the file begins
with the receiver's **first sentence after power-on** — that is what it exists for.

**The cover was meant to block the sky and did not.** It attenuated by roughly 5 dB — mean C/N
31.1 here against 36.6 in `vk162-steady-state` — which is real but nowhere near enough to stop a
GPS receiver. The intent was a sustained no-fix period; what was actually obtained is the single
cycle below. A later attempt in a microwave oven managed about 10 dB and also failed
(`vk162-microwave`), so **an outage capture is still outstanding** and needs a proper enclosure.

The sitting ended when the USB was pulled, which is why it is 5.6 minutes rather than the 45 it was
started for. Note that it ends on a **complete** sentence: the lead came out during the idle gap
between two cycles, so this is a cable pull that did *not* truncate a line.

What it holds that no other capture does:

- **The one no-fix cycle**, at 00:50:10 UTC, and it is worth reading in full:

  ```
  $GPRMC,005010.00,V,,,,,,,080926,,,N*7C
  $GPGGA,005010.00,,,,,0,03,2.52,,,,,,*54
  $GPGSA,A,1,09,21,14,,,,,,,,,,2.71,2.52,1.00*0F
  $GPGLL,,,,,005010.00,V,N*4E
  ```

  `RMC` status `V`, `GGA` quality `0` with **every position field empty**, `GSA` mode `1`, and a
  `GLL` that is nothing but a time. The parser must produce a null position and `"no fix"` from
  this, and until this file existed nothing but the simulator had ever asked it to.

  Note `GGA` reports **three satellites used** while the quality is `0`. A receiver can be using
  satellites and still have no fix, so "satellites used" is not a fix indicator and must not be
  read as one.

- **All three GGA fix qualities and all three RMC mode indicators**, in order, in five minutes:
  quality `0` once, then `1` for 166 cycles, then `2` for 172; mode `N`, then `A`, then `D`. That
  is the whole `NmeaStatusParser.ModeDetail` ladder walked by a real receiver — no fix, standalone
  GPS, then SBAS-corrected differential. `vk162-steady-state` is quality `2` from first byte to
  last and shows none of it.

- **The `$GPTXT` power-on banner**, seven sentences the driver does not claim, including
  `ANTSTATUS=OK`. They appear only at power-on, so only a capture that starts with one has them.

**The position in these sentences is genuine and was not scrubbed**, for the reason given in
`vk162-steady-state.md`: a capture edited to be safe is no longer byte-exact.
