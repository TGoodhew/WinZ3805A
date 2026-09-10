# trimble-uccm-p-2026-09-10

Raw UCCM output, byte for byte. Written by `build/Capture-Uccm.ps1`; nothing has been decoded,
re-terminated or trimmed. The echo and any interleaved time code are evidence, not noise.

| | |
|---|---|
| Port | COM3 at 57600-8-N-1 |
| Taken | 2026-09-10 12:10:35 +10:00 |
| Identity (`*IDN?`) | `TRIMBLE,57964-80,40896646,V2.0.1.6-01` |
| Commands asked | 9 |
| Commands answered | 9 |
| Line terminators | CRLF x44, bare LF x1 |
| Replies ending unterminated | 9 of 9 |
| Replies ending in a prompt | 9 of 9 |
| Binary `C5` packets seen | 1 |
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

**Nobody was there.** This sitting was taken remotely by an agent over a Claude Code session on the
bench laptop, which is in Australia while its owner was in the United States. That is a real
limitation and it is recorded here rather than glossed: everything below is either what the machine
can attest or what the receiver said about itself, and the physical questions this section normally
answers are **open**.

What the machine can attest:

- The module is on **COM3**, through a **Prolific PL2303GT** USB-serial adapter
  (`USB\VID_067B&PID_23A3`), driver healthy.
- It answers at **57600-8-N-1**, not the 9600 the driver's `AutoDetectSequence` tries first. It is
  silent at 9600, 19200 and 4800 - a passive sweep at those rates returns only noise whose volume
  tracks the baud rate, which is what an idle line looks like.
- It identifies as `TRIMBLE,57964-80,40896646,V2.0.1.6-01` and prompts `UCCM-P >`, so it is a
  **UCCM-P** rather than a plain UCCM. `UccmVariant`'s expectation that a UCCM-P answers where a
  plain UCCM returns an undefined-header error is consistent with the three rejections below, but
  this sitting cannot separate variant from firmware.

What the receiver said about its own state, from `SYST:STAT?`:

- `MODE Hold` - in holdover. `ACQUISITION ... [GPS 1PPS Valid]`, `Tracking: 7`, `Not Tracking: 3`,
  `TFOM 2`, `FFOM 0`, `>> GPS: [phase:-1.4E-08]`, `UCCM A Status[ACTIVE]`.
- All four `Ref 8KHz` inputs read `[LOS]`, which is expected for a module on a bench rather than in
  the telecom shelf it was built for.
- It reports a position of S 34:32:39.019, E 150:50:25.107 and GPS time `01:30:33 10 Sep 2026`, so
  it has a fix and an antenna that works. **How long it had been running is not known**, which
  matters: holdover and `TFOM 2` may be a settling module or a disciplining problem, and one
  sitting cannot tell those apart.

What is **not** known and needs somebody at the bench: how the antenna is mounted and where, what
the module is being fed for power, whether the serial lead is straight-through or has been made up,
and whether the holdover state is normal for this unit or a symptom.

Three of the nine catalogued commands are rejected outright - `SYNC:HOLD:DUR?` with `Command error`,
and both `GPS:POS:SURV:STAT?` and `GPS:POS:SURV:PROG?` with `Undefined header`. That is a
**command-catalog finding, not a receiver fault**, and it is the next thing worth chasing.

## What to do with this

If the echo count is 0, `UccmReply.Classify`'s echo handling is answering a question the hardware
does not ask, and the driver's remarks should be corrected rather than the code kept "just in case".
If it is 9 of 9, the hypothesis is confirmed and can stop being
hedged. Anything in between is the interesting case and needs reading line by line.

The same applies to the time codes: **a count of 0 does not refute hypothesis 2**, because an
interleaved broadcast depends on timing. It means this sitting did not see one, which is a weaker
statement and should be recorded as such.