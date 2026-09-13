# form8n-gst-gbs.nmea

Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been
decoded, re-terminated or trimmed.

| | |
|---|---|
| Port | COM7 at 9600-8-N-1 |
| Started | 2026-09-12 17:52:01 -07:00 |
| Ended | 2026-09-12 17:57:02 -07:00 |
| Duration | 5 min |
| Bytes | 247133 |
| Sentences, checksum good | 4200 |
| Sentences, rejected | 0 |
| Talkers seen | GB, GN, GP |
| Sentences seen | GBS, GGA, GLL, GSA, GST, GSV, RMC, VTG |
| Ends mid-sentence | yes, 1 byte(s) |
| **Sentences switched on** | GST, GBS, by UBX-CFG-MSG to RAM before the capture |

> **This receiver does not send these by default.** They were enabled for the sitting
> with `UBX-CFG-MSG` and the configuration was written to RAM, so a power cycle undoes
> it. Read the capture as what the hardware is *capable* of, not as what arrives from
> it out of the box.

## What was happening

The RCmall forM8N on its own patch antenna, **indoors**, on the desk beside the machine — the same
place and the same antenna as `form8n-gns-without-gga`, not the outdoor sitting. A 12-satellite
3D fix from the first cycle to the last, GPS and BeiDou, never lost and never degraded. Nothing was
done to the receiver during the five minutes: the two `UBX-CFG-MSG` frames went out before the
first byte was read and nothing was sent afterwards.

**This capture exists to answer #516, and the question was whether the sentences could be had at
all.** #435 filed `GST` and `GBS` as waiting for a receiver that emits them. Neither unit here does,
and the issue's own guess was that `UBX-CFG-MSG` would make one — stated as a hypothesis, with a
second doubt beside it: that RAIM needs redundancy, so an indoor fix might yield a `GBS` with
nothing in it. Both resolved the favourable way, within a second of the frames going out.

Three things about the bytes worth knowing before reading them.

**The error ellipse is empty in every `GST`.** Fields 3, 4 and 5 — semi-major, semi-minor and
orientation — are blank in all 300 of them, while the RMS and the three per-axis deviations are
filled. That is u-blox declining to publish an ellipse, not a truncated sentence and not a parser
problem, and it is why the model makes every field independently nullable.

**The `GBS` fault fields are empty in every cycle too, and that is the good news rather than an
absence.** No satellite was ever flagged. The distinction matters more than it looks: a receiver
never asked for `GBS` and a receiver reporting perfect health both leave the satellite id blank, so
nothing in this file exercises the faulted branch. The synthetic case in `NmeaFixIntegrityTests`
covers it and says plainly that it is synthetic.

**The `GST` range RMS from this receiver is unusable, and it is the one field here that cannot be
believed.** Across the 300 sentences it runs from 17 to **3,179,277** — six orders of magnitude,
between one second and the next, from a receiver sitting still on one unbroken fix — and 34 of the
300 are in the millions. Every one of them passes its checksum, so this is what the module sends.
The three deviations in those very same sentences stay at 1.8 / 1.6 / 3.7 m throughout, which is
what says the field offsets are right and the receiver is at fault rather than the parser. Nothing
in the application displays the RMS; the readout is built from the deviations.

**Dilution and uncertainty disagree here, which is the point of carrying both.** The `GSA` rows
report an HDOP between 0.65 and 0.82 — excellent figures — while `GST` puts the horizontal error at
roughly 3.2 m throughout. Geometry was good and the ranging was not, and no dilution figure can say
so.

The receiver was put back to its as-found sentence set immediately afterwards, with `GST` and `GBS`
set to rate 0 and the absence read back over eight seconds rather than assumed from the write.

**One process at a time on the port.** Three earlier attempts at this sitting were cut short at
78 s, 30 s and 4 s by a second process opening COM7 while the capture held it — a second opener
aborts the first's pending read, and the failure names the read rather than the port, so it reads
like a device fault. The receiver was present and talking throughout all three.
