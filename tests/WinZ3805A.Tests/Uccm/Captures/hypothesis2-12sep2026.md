# hypothesis2-12sep2026

Raw UCCM output, byte for byte. Written by `build/Capture-Uccm.ps1`; nothing has been decoded,
re-terminated or trimmed. The echo and any interleaved time code are evidence, not noise.

| | |
|---|---|
| Port | COM3 at 57600-8-N-1 |
| Taken | 2026-09-12 11:48:37 +10:00 |
| Identity (`*IDN?`) | `(none)` |
| Commands asked | 1 |
| Commands answered | 50 |
| Line terminators | CRLF x1450, bare LF x1 |
| Replies ending unterminated | 50 of 50 |
| Replies ending in a prompt | 50 of 50 |
| Binary `C5` packets seen | 25 |
| `0xC5` bytes not matching the packet shape | 0 |

## The three hypotheses this sitting was taken to settle

Every one of these was read out of Lady Heather's source rather than a vendor document, and the
driver treats them as hypotheses with citations until a receiver says otherwise.

| Hypothesis | Measured |
|---|---|
| The module echoes the command before answering it | **0 of 50** replies began with an echo |
| Unsolicited `C5` time codes interleave with replies | **0 of 50** replies had one mid-reply |
| `COMMAND COMPLETE` terminates a reply | **50 of 50** replies carried it |

The time-code row counts **binary** packets - byte `0xC5` through `0xCA` - found in the raw
stream. Until 10 Sep 2026 this script looked for the *characters* `C5` in text decoded as ASCII,
which turns every byte above `0x7F` into `?`, so the row could only ever have read 0.

### Hypothesis 2, with the exposure it was tested against

A count of zero refutes nothing on its own, so here is what the count was measured against. A code
can only land *inside* a reply that is still arriving, so the exposure is reply wire time over the
sitting; a scalar query answers in milliseconds and cannot test this however long anyone sits there.

| | |
|---|---|
| Sitting length | 50 s |
| Reply wire time | 18.96 s, 38% of the sitting |
| Codes seen / due at one per 2 s | 25 / ~25 |
| Mid-reply expected by chance | ~9.48 |
| **Verdict** | **REFUTED for this module: 0 mid-reply against ~9.48 expected, with 25 code(s) seen where ~25 were due.** |

## What was happening

A **Trimble UCCM-P**, `TRIMBLE,57964-80,40896646,V2.0.1.6-01`, on COM3 at 57600-8-N-1 through a
Prolific PL2303GT, roof antenna, locked to GPS and settled — the same unit and the same state as
`bench-12sep2026`. The identity row above reads `(none)` because `-Only` excluded `*IDN?`; this
sitting asked one query fifty times and nothing else.

**This sitting was designed to answer one question and is not a general capture.** Hypothesis 2 —
that unsolicited `C5` time codes arrive *in the middle* of another message's response — had been
carried by the driver since it was written, and three previous sittings had said nothing about it:
seven frames, every one trailing the prompt, which is exactly what a module that never interleaves
and a module that happened not to interleave both look like.

### Why fifty status reads

A code can only land inside a reply that is **still arriving**, so the chance of catching one is the
reply's wire time over the broadcast interval. A scalar query answers in a few milliseconds against
a ~2 s interval and can never test this, however long anyone sits there. `SYST:STAT?` is ~2176 bytes,
about 378 ms on the wire, so asking it repeatedly is the only version of this experiment that has
any power at all. Fifty reads put the exposure at **38% of the sitting**.

### The result, and why it is a refutation rather than a quiet afternoon

**Zero mid-reply, against ~9.4 expected by chance, with every broadcast accounted for.** Twenty-five
codes arrived where ~25 were due at one per two seconds — so the codes were certainly being sent
throughout, and 38% of that time a reply was in flight. If the module simply broadcast on its own
clock and let the bytes fall where they may, roughly nine should have landed inside a reply. None
did. Reproduced twice, minutes apart, with the same counts.

**The codes are not being dropped, which is the finding underneath the finding.** A module that
suppressed a broadcast colliding with a reply would show a *deficit* — that is the third outcome the
script reports, and it is not what happened. Every code arrived; none arrived mid-reply. So this
firmware **defers a broadcast to the end of the reply rather than dropping it or interleaving it**,
which is a more specific fact than "does not interleave" and explains why all seven frames in the
earlier sittings trailed the `UCCM-P >` prompt.

### What this does *not* settle

**Heather's claim is about Symmetricom units, and this is a Trimble.** Her comment says "the
Symmetricom units send a time code packet in the middle of another message's response", so no
sitting against this receiver can confirm or refute it as written. What is established is what this
Trimble firmware does.

**So the driver's tolerance of an interleaved code stays.** It costs nothing — the transport lifts
frames out of the byte stream wherever they sit — and removing it on the strength of a different
vendor's behaviour would be the same reasoning that put the hypothesis in the driver unexamined in
the first place, running backwards.

## What to do with this

If the echo count is 0, `UccmReply.Classify`'s echo handling is answering a question the hardware
does not ask, and the driver's remarks should be corrected rather than the code kept "just in case".
If it is 50 of 50, the hypothesis is confirmed and can stop being
hedged. Anything in between is the interesting case and needs reading line by line.

The same applies to the time codes: **a count of 0 does not refute hypothesis 2**, because an
interleaved broadcast depends on timing. It means this sitting did not see one, which is a weaker
statement and should be recorded as such.
