# catalog-spellings-12sep2026

Raw UCCM output, byte for byte. Written by `build/Capture-Uccm.ps1`; nothing has been decoded,
re-terminated or trimmed. The echo and any interleaved time code are evidence, not noise.

| | |
|---|---|
| Port | COM3 at 57600-8-N-1 |
| Taken | 2026-09-12 12:02:05 +10:00 |
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

### Hypothesis 2, with the exposure it was tested against

A count of zero refutes nothing on its own, so here is what the count was measured against. A code
can only land *inside* a reply that is still arriving, so the exposure is reply wire time over the
sitting; a scalar query answers in milliseconds and cannot test this however long anyone sits there.

| | |
|---|---|
| Sitting length | 5.1 s |
| Reply wire time | 0.48 s, 9% of the sitting |
| Codes seen / due at one per 2 s | 3 / ~2.6 |
| Mid-reply expected by chance | ~0.28 |
| **Verdict** | **INCONCLUSIVE: 3 code(s) seen, but only ~0.28 would have landed mid-reply by chance. Ask a longer reply, or ask more often.** |

## What was happening

A **Trimble UCCM-P**, `TRIMBLE,57964-80,40896646,V2.0.1.6-01`, on COM3 at 57600-8-N-1 through a
Prolific PL2303GT, roof antenna, locked to GPS and settled — the same unit and state as the other
12 Sep 2026 sittings.

**This sitting exists to close #482, and its whole point is the three mnemonics at the end.** The
script used to hand-copy the catalog and the copy had drifted in exactly those three, so every
previous full sweep asked `SYNC:HOLD:DUR?` and two survey queries without their leading colons —
spellings the driver does not use. #416 recorded their failures as a finding about the firmware when
they might equally have been a finding about the script.

This is the first sweep taken with the list read out of `UccmCommands.cs`, so it is the first one
that can say anything about the driver's catalog:

| Asked, as the driver spells it | Reply |
|---|---|
| `:ROSC:HOLD:DUR?` | `Command error` |
| `:GPS:POS:SURV:STAT?` | `Undefined header` |
| `:GPS:POS:SURV:PROG?` | `Undefined header` |

**The finding survives the correction, and the distinction between the two errors is the substance.**
The module separates `Undefined header` — it has no such node — from `Command error`, which it gives
for a node it recognises. So the survey queries are genuinely absent on this firmware, while
`:ROSC:HOLD:DUR?` reaches a real subsystem and fails for some other reason. State is the obvious
candidate and is untested: the unit has been locked throughout every sitting, so the query has never
been put to it *in holdover*, which is the test that separates "unsupported" from "not valid now".
That is #416's, and #483 turns on the same question.

This matches the ladder run by hand through the Advanced Console earlier the same day, which also
tried both spellings and found the leading colon made no difference — so the drift was never what
caused these errors. Now it is recorded from the catalog rather than from an argument.

### On the hypothesis 2 line above

Read it as the guard working rather than as a result. Nine short queries give 9% exposure and ~0.28
mid-reply codes expected, so `INCONCLUSIVE` is the correct answer and the verdict says so instead of
reporting a refutation it has not earned. `hypothesis2-12sep2026` is the sitting designed to answer
that question, at 38% exposure over fifty status reads.

## What to do with this

If the echo count is 0, `UccmReply.Classify`'s echo handling is answering a question the hardware
does not ask, and the driver's remarks should be corrected rather than the code kept "just in case".
If it is 9 of 9, the hypothesis is confirmed and can stop being
hedged. Anything in between is the interesting case and needs reading line by line.

The same applies to the time codes: **a count of 0 does not refute hypothesis 2**, because an
interleaved broadcast depends on timing. It means this sitting did not see one, which is a weaker
statement and should be recorded as such.
