# form8n-gps-beidou-first-light.nmea

Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been
decoded, re-terminated or trimmed.

| | |
|---|---|
| Port | COM6 at 9600-8-N-1 |
| Started | 2026-09-09 15:09:18 -07:00 |
| Ended | 2026-09-09 15:16:02 -07:00 |
| Duration | 6.7 min |
| Bytes | 207238 |
| Sentences, checksum good | 3824 |
| Sentences, rejected | 0 |
| Talkers seen | GB, GN, GP |
| Sentences seen | GGA, GLL, GSA, GSV, RMC, TXT, VTG |
| Ends mid-sentence | no |

> **This note was written by hand, not by the script.** The capture was stopped by killing the
> process rather than by Ctrl+C, which is the one path that skips the provenance writer, so the
> figures above were recomputed from the file afterwards. Recorded here because a sidecar that
> looks generated but is not would be worse than none.

## What was happening

**Receiver.** RCmall forM8N, USB, on Tony's bench. It identifies itself in its own startup `TXT`
banner rather than needing UBX:

```
$GNTXT,01,01,02,HW UBX-M8130 00080000*61
$GNTXT,01,01,02,ROM CORE 3.01 (107888)*2B
$GNTXT,01,01,02,FWVER=SPG 3.01*46
$GNTXT,01,01,02,PROTVER=18.00*11
$GNTXT,01,01,02,GPS;GLO;BDS*06
$GNTXT,01,01,02,QZSS*58
$GNTXT,01,01,02,GNSS OTP=GPS;BDS*26
```

Note the hardware is **UBX-M8130**, not the M8030 that was assumed when the module was ordered, and
its one-time-programmable default constellation set is `GPS;BDS`. Nothing was configured before this
capture — this is the module exactly as it arrived.

**Antenna.** Tony's fixed GPS *timing* antenna, not the small active patch supplied with the module.
This matters, and is the finding of the sitting.

**BeiDou is enabled, reports every cycle, and tracks nothing.** Every cycle carries
`$GBGSV,1,1,00,0*77` — zero satellites in view — and a second `GSA` row that is empty but for its
system id:

```
$GNGSA,A,3,03,04,06,07,09,26,16,,,,,,2.40,1.17,2.10,1*0F   <- systemId 1, GPS, seven satellites
$GNGSA,A,3,,,,,,,,,,,,,2.40,1.17,2.10,4*06                 <- systemId 4, BeiDou, none
```

The receiver is doing what it was asked; the **antenna is the limit**. A GPS timing antenna is
typically narrowband about L1 at 1575.42 MHz, and BeiDou B1 is at 1561.098 MHz — outside that
filter. GLONASS at roughly 1602 MHz would be excluded on the same grounds. **So a second
constellation is not reachable on this antenna, whatever the receiver supports**, and #424 needs the
module's own wideband patch antenna rather than different silicon.

**What this capture is therefore good for.** Not #424 — but it is the corpus's first sample of a
talker that *advertises a constellation it cannot see*, which is its own §11.1 case: a `GSV` with a
count of `00` and no satellite fields at all, and a `GSA` whose satellite slots are all empty while
its system id and DOP fields are populated. Both must parse to nothing rather than throw or invent a
satellite. It is also the first capture carrying the NMEA 4.x trailing **signal id** field on `GSV`
(the `,0` before the checksum), which a parser striding four fields at a time will read as an eighth
satellite with PRN 0 — a mistake made and caught while analysing this very file.
