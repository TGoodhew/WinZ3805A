# bench-12sep2026

Raw UCCM output, byte for byte. Written by `build/Capture-Uccm.ps1`; nothing has been decoded,
re-terminated or trimmed. The echo and any interleaved time code are evidence, not noise.

| | |
|---|---|
| Port | COM3 at 57600-8-N-1 |
| Taken | 2026-09-12 06:31:20 +10:00 |
| Identity (`*IDN?`) | `TRIMBLE,57964-80,40896646,V2.0.1.6-01` |
| Commands asked | 9 |
| Commands answered | 9 |
| Line terminators | CRLF x44 |
| Replies ending unterminated | 9 of 9 |
| Replies ending in a prompt | 9 of 9 |
| Binary `C5` packets seen | 3 |
| `0xC5` bytes not matching the packet shape | 0 |

## The three hypotheses this sitting was taken to settle

Every one of these was read out of Lady Heather's source rather than a vendor document, and the
driver treats them as hypotheses with citations until a receiver says otherwise.

| Hypothesis | Measured |
|---|---|
| The module echoes the command before answering it | **0 of 9** replies began with an echo |
| Unsolicited `C5` time codes interleave with replies | **0 of 9** replies had one mid-reply |
| `COMMAND COMPLETE` terminates a reply | **6 of 9** replies carried it |

The time-code row counts **binary** packets - byte `0xC5` through `0xCA` - found in the raw
stream. Until 10 Sep 2026 this script looked for the *characters* `C5` in text decoded as ASCII,
which turns every byte above `0x7F` into `?`, so the row could only ever have read 0.

## What was happening

A **Trimble UCCM-P**, `TRIMBLE,57964-80,40896646,V2.0.1.6-01`, on loan and wired to this machine's
COM3 through a Prolific PL2303GT at 57600-8-N-1. Roof antenna, and the unit had been powered and
tracking for about twenty minutes when this was taken — locked to GPS, `ACQUISITION`, 6 satellites
tracked of 12 visible, TFOM 2, FFOM 0, 1 PPS TI within about ±1 ns, EFC steady at 19.68 %,
`MODE Hold` against a held position. So this is a healthy, settled unit with a fix: **none of
§11.1's interesting states are in here** — no holdover, no power-up, no survey, no failing health
monitor. Those still need forcing by hand.

The capture was taken minutes after a session driving the same receiver through the application's
Advanced Console, and the app was disconnected specifically to free the port for this script.

### The three packets are all *trailing*, not interleaved

All three `C5` packets sit **after** the reply's `UCCM-P >` prompt, appended with no terminator, at
the very end of the reply — never between two payload lines. Three for three, matching the 10 Sep
sitting. That is why the hypothesis-2 row reads 0 of 9 while three packets were seen, and the
distinction is the useful finding rather than an accounting quirk.

**What that costs is visible one layer up.** A packet that trails reply *n* is still in the buffer
when reply *n+1* is read, so it prefixes the *next* answer. Driving `*IDN?` repeatedly through the
application's Advanced Console the same morning, roughly 3 sends in 34 came back with the identity
string preceded by binary, and one came back as a bare `0xC5` with the identity lost altogether.
So the practical problem is not an interleave to be parsed out of the middle of a reply — it is a
trailing broadcast that has to be drained before the next read.

### Frame anatomy, from the three packets here

44 bytes, `0xC5` … `0xCA`, as on 10 Sep. Byte for byte the three are identical except in two places:

```
off  0: C5
off  1-26: 00 80 00 00 00 00 28 1C 52 00 00 20 60 C1 91  then 00 x11
off 27-30: 57 CF 27 A4   <- varies
off 31-40: 00 12 60 04 45 80 00 00 00 00
off 41-42: 10 AA         <- varies
off   43: CA
```

Offsets 27–30 are big-endian **seconds since the GPS epoch**: `0x57CF27A4` = 1 473 193 892, which
is 2026-09-11 20:31:32 counted from 1980-01-06 — within twenty seconds of when this file was
written, out of 1.47 billion. Nothing but a correct epoch lands that close. The three packets read
`…A4`, `…A6`, `…A8`, i.e. **+2 s apart**, which is the documented broadcast cadence seen directly.

Offsets 41–42 (`10 AA`, `E6 68`, `06 64`) change with the timestamp and look like a checksum over
the frame. **Not verified** — three frames is not enough to fit one, and no algorithm was tried.

**The offset against UTC is not established here.** The decode lands 18 s after this file's own
timestamp, and 18 s is exactly GPS−UTC today, which is tempting — but the Windows time service on
this machine is *stopped*, so that timestamp is not a UTC reference, and a few seconds of the gap
is simply elapsed time between the script starting and the packet arriving. An independent
measurement the same morning put the receiver's own clock +16 s against the same untrustworthy
clock. **What is established is the encoding, not the offset.** Pinning GPS−UTC needs a synced
host or a second source.

## What to do with this

If the echo count is 0, `UccmReply.Classify`'s echo handling is answering a question the hardware
does not ask, and the driver's remarks should be corrected rather than the code kept "just in case".
If it is 9 of 9, the hypothesis is confirmed and can stop being
hedged. Anything in between is the interesting case and needs reading line by line.

The same applies to the time codes: **a count of 0 does not refute hypothesis 2**, because an
interleaved broadcast depends on timing. It means this sitting did not see one, which is a weaker
statement and should be recorded as such.
