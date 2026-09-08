# vk162-steady-state.nmea

Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been
decoded, re-terminated or trimmed.

| | |
|---|---|
| Receiver | VK-162 USB GPS puck — u-blox `UBX-G70xx`, ROM CORE 1.00 (59842), PROTVER 14.00 |
| Port | COM4 at 9600-8-N-1 |
| Started | 2026-09-07 17:08:31 -07:00 |
| Ended | 2026-09-07 17:38:31 -07:00 |
| Duration | 30 min |
| Bytes | 926979 |
| Sentences, checksum good | 14868 |
| Sentences, rejected | 0 |
| Talkers seen | GP |
| Sentences seen | GGA, GLL, GSA, GSV, RMC, TXT, VTG |
| Ends mid-sentence | yes, 1 byte |

> **The two sentence counts were recomputed from the bytes, not taken from the script's live
> tally.** The run reported 14,717 good, because the summariser cleared its buffer at every
> five-second report and so lost the sentence straddling each boundary. That is fixed, and
> `-SelfTest` now holds a piecewise summary to the same answer as a whole one; this capture is
> simply older than the fix. The figures above are what the file contains.

## What was happening

**Outdoors, clear view of the sky**, on the end of its USB lead, on 7 September 2026. The receiver
had been powered and locked for some time before the capture began, so this file is **steady state
from its first byte** — there is no acquisition here and no cold start. `vk162-cold-start-and-outage`
is the sitting that has those.

Nothing was done to the receiver during the thirty minutes. It was not moved, covered or unplugged,
and the sky did not change. That is the point of the file: it is the boring case, at length, and it
is what makes it possible to say that anything the *other* capture shows is a real event rather than
ordinary jitter.

What it holds, and what each part is evidence for:

- **Exactly 1,800 each of RMC, VTG, GGA, GSA and GLL** — one cycle per second for 1,800 seconds,
  with not one missing. The talker never stuttered and the capture never dropped a byte, so a gap
  anywhere in a future capture is the receiver's doing rather than the harness's.
- **5,861 GSV pages**, which is more than three per cycle: the page count rises to four when a
  thirteenth satellite comes into view, and that is what #417's per-constellation page accounting
  has to survive on a single-constellation talker.
- **A differential 3D fix in every cycle** — `GGA` quality `2` and `RMC` status `A`, 1,800 times.
  The `D` mode indicator in RMC is SBAS-corrected, which is what the two SBAS satellites below are
  doing here.
- **PRNs 4, 5, 7, 8, 9, 14, 16, 21, 22, 27 and 30, plus 46 and 48.** The last two are **SBAS**
  (WAAS) and are outside the 1–32 GPS range, and both appear in the `GSA` list of satellites used
  in the fix. Anything that assumes a PRN fits in 1–32 breaks here.
- **C/N from 4 to 49 dB-Hz.** The weak end matters: a satellite at 4 is tracked, so it is rendered
  with the lowest step of the §9.4.4 sequential ramp — the case #218 found rendered in the page
  background.
- **Seven `$GPTXT` sentences**, emitted once at power-on and never again — the u-blox boot banner,
  including `ANTSTATUS=OK`. The driver does not claim `TXT`, so these are the capture's supply of
  lines the listener must discard without complaint.
- **It ends one byte into a sentence** — a lone `$` after the last line ending, because the capture
  stops on a clock rather than on a sentence boundary. The listener holds a partial line back until
  its ending arrives, and this is the real-world instance of that.

**The position in these sentences is genuine and was not scrubbed**, which was a deliberate choice
rather than an oversight: a capture edited to be safe is no longer byte-exact, and byte-exact is the
only property that makes it worth keeping.
