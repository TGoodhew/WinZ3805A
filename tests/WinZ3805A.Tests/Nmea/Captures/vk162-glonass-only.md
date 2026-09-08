# vk162-glonass-only.nmea

Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been
decoded, re-terminated or trimmed.

| | |
|---|---|
| Receiver | VK-162 USB GPS puck — u-blox `UBX-G70xx`, ROM CORE 1.00 (59842), PROTVER 14.00 |
| Port | COM4 at 9600-8-N-1 |
| Started | 2026-09-08 07:58:32 -07:00 |
| Ended | 2026-09-08 08:12:33 -07:00 |
| Duration | 14 min |
| Bytes | 352467 |
| Sentences, checksum good | 6563 |
| Sentences, rejected | 0 |
| Talkers seen | GL |
| Sentences seen | GGA, GLL, GSA, GSV, RMC, TXT, VTG |
| Ends mid-sentence | yes, 1 byte(s) |

## What was happening

**This is the only capture in the folder taken with the receiver reconfigured**, and that is the
whole point of it. The other three are the puck as it ships. This one was put into **GLONASS-only**
mode over UBX so the driver would meet a talker that is not `GP`, which until now only the simulator
had ever produced.

The reconfiguration was `UBX-CFG-GNSS` **to RAM only** — no `CFG-CFG` save — so unplugging the
receiver reverts it. It was also explicitly set back afterwards and the return to `$GP` with a valid
fix was confirmed on the port.

### What the receiver refused, which is half the finding

Three combinations were tried and the receiver answered each one:

| Asked for | Answer |
|---|---|
| GPS + GLONASS concurrently | **NAK** |
| GLONASS + SBAS + QZSS, GPS off | **NAK** |
| GLONASS alone, all augmentations off | **ACK** |

**u-blox 7 cannot run two constellations at once.** SBAS and QZSS are GPS augmentations and are
refused alongside GLONASS, which is why the second attempt was NAKed even with GPS already off. So
this puck can give a second constellation but never a *concurrent* one — see the note on #424 below.

### What it holds that no other capture does

- **A `GL` talker, from hardware.** And note the receiver switches the talker ID on **every**
  sentence, not just `GSV`: `$GLGGA`, `$GLRMC`, `$GLGSA`, `$GLVTG`, `$GLGLL`, `$GLTXT`. A driver
  that keys anything off the literal `GP` would read none of this, and `NmeaSentence.Key` strips the
  talker precisely so it does not.

- **152 consecutive no-fix cycles**, then acquisition. `RMC` status `V` → `A`, `GGA` quality `0` →
  `1`, `GSA` mode `1` → `3`, all at the same crossing. This matters because `vk162-cold-start`
  yielded **one** no-fix cycle — a hot GPS start reacquires in a second — whereas GLONASS from cold
  with no almanac took two and a half minutes. **It is the longest genuine no-fix stretch in the
  corpus**, and it was free: no shielding, no enclosure, just a constellation with nothing cached.

- **The fix-quality ladder without SBAS.** Quality goes `0` → `1` and stops. Every GPS capture here
  reaches `2` (SBAS differential), because SBAS is on by default; with it disabled there is no `2`
  and no `D` mode indicator — only `N` then `A`. So this is the *standalone* branch of
  `NmeaStatusParser.ModeDetail`, which the other three never take.

### The GLONASS satellite numbering, which answers a question #424 asked

Nine distinct satellites appear across the sitting:

```
67 68 69 75 76 82 83 84 85
```

**Every one is in 65–96, the range NMEA 4.10 assigns to GLONASS.** This receiver therefore uses the
standard per-constellation *offset* ranges and does **not** number GLONASS satellites from 1.

That narrows #424 without closing it. The collision that issue is about needs a receiver that
numbers per constellation *and* reports two of them in one cycle. This puck does neither: it
conforms to 4.10, and it cannot run two constellations at all. So #424 stays open and still needs
different hardware — but "a u-blox 7 might expose it" is now ruled out by measurement rather than
left as a maybe.

### Ends mid-sentence

Yes, on a lone `$`, and from the capture clock expiring rather than a cable pull — the same way
`vk162-steady-state` ends. **The truncation-from-a-cable-pull case is still unobserved.**

**The position in these sentences is genuine and was not scrubbed**, for the reason given in
`vk162-steady-state.md`: a capture edited to be safe is no longer byte-exact.
