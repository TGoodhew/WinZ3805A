# form8n-gps-beidou-outdoors.nmea

Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been
decoded, re-terminated or trimmed.

| | |
|---|---|
| Receiver | RCmall forM8N — u-blox `HW UBX-M8130`, ROM CORE 3.01 (107888), PROTVER 18.00, `GNSS OTP=GPS;BDS` |
| Port | COM6 at 9600-8-N-1 |
| Started | 2026-09-09 16:52:21 -07:00 |
| Ended | 2026-09-09 17:22:21 -07:00 |
| Duration | 30 min |
| Bytes | 1124137 |
| Sentences, checksum good | 19136 |
| Sentences, rejected | 0 |
| Talkers seen | GB, GN, GP |
| Sentences seen | GGA, GLL, GSA, GSV, RMC, TXT, VTG |
| Ends mid-sentence | yes, 1 byte |

## What was happening

**The antenna question from `form8n-gps-beidou-first-light`, answered.** Same module, same day,
about ninety minutes later, with the one variable that capture identified changed: the module's own
wideband patch antenna, outdoors, in place of Tony's fixed GPS timing antenna.

That note predicted exactly this:

> That is the antenna rather than the receiver — a GPS timing antenna is narrowband about L1 at
> 1575.42 MHz and BeiDou B1 is at 1561.098 MHz — so #424 needs the module's own wideband patch
> antenna outdoors, not different silicon.

**It was right.** BeiDou tracks immediately. Where first light has `$GBGSV,1,1,00` and a `GSA` row
carrying nothing but its system id, this capture has BeiDou satellites 6, 20, 23, 24 and 25, C/N up
to 51 dB-Hz, in the `GSA` row that computes the fix. Same silicon, same firmware, different antenna.

Nothing was done to the receiver during the thirty minutes. It was not moved, covered or unplugged,
and no `UBX` frame was sent — this is the module in its shipped configuration.

### Why this file exists: #424, no longer latent

This is **the only capture in the corpus in which two talkers report the same satellite number**.
Every other file — all seven of them — has zero such cycles. This one has 1,217.

- **The receiver numbers per constellation.** BeiDou arrives as 6, 20, 23, 24 and 25, all inside the
  GPS 1–32 range, with only the `GB` and `GP` talkers to separate them. NMEA 4.10 gives each
  constellation its own range so a conforming receiver never collides; this one does not do that.
  Note the contrast with `vk162-glonass-only`, whose GLONASS PRNs are 67–85 and therefore *cannot*
  collide — the corpus now holds one receiver of each kind.
- **GPS 4 and BeiDou 4 are in view together in 1,217 of the 1,800 cycles.** `NmeaStatusParser`
  dedupes with a plain `HashSet<int>` of PRNs across every GSV page in the cycle, so in each of those
  cycles one of the two is silently dropped. That is **68% of the sitting**, under an open sky, with
  nothing unusual done. #424 called the defect "latent, not active"; on this hardware it is the
  majority case.

### Two things it holds that were not the reason for taking it

- **It crosses UTC midnight** — 23:52:10 on 090926 through 00:22:08 on 100926. **No other capture
  in the corpus spans two dates.** The parser's own comment warns about a time from one sentence
  paired with a date from the next, and `EveryCycleParsesWithinItsInvariants` asserts the clock never
  goes backwards; until now nothing exercised either against a real rollover.
- **It is an autonomous fix with no SBAS anywhere** — `GGA` quality `1` and `RMC` mode `A` in all
  1,800 cycles — where `vk162-steady-state` is differential throughout. The two long steady-state
  captures now cover both cases rather than duplicating one.

### The rest of the inventory

- **Exactly 1,800 each of RMC, VTG, GGA and GLL, and exactly 3,600 GSA.** One cycle per second for
  1,800 seconds with none missing, and **two `GSA` per cycle** because two constellations are in the
  fix — every earlier capture has one. Anything assuming one `GSA` per cycle is wrong here.
- **8,324 GSV pages**, 5,306 `GP` and 3,018 `GB`, each talker running its own page sequence and the
  two interleaved within the cycle. That is per-constellation page accounting (#417) at a scale no
  single-constellation capture can reach.
- **C/N from 7 to 51 dB-Hz** across 23,379 readings; **HDOP 0.73 to 1.46**.
- **Twelve `$GNTXT` sentences** at the head of the file and nowhere else — the u-blox boot banner,
  emitted when the port is opened, including `ANTSTATUS=OK`. The driver does not claim `TXT`, so
  these are lines the listener must discard without complaint.
- **It ends one byte into a sentence**, on a lone `$`, because the capture stops on a clock rather
  than a sentence boundary.

**The position in these sentences is genuine and was not scrubbed**, as with every file here: a
capture edited to be safe is no longer byte-exact.
