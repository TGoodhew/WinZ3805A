# trimble-uccm-p-2026-09-11

Raw UCCM output, byte for byte. Written by `build/Capture-Uccm.ps1`; nothing has been decoded,
re-terminated or trimmed. The echo and any interleaved time code are evidence, not noise.

| | |
|---|---|
| Port | COM3 at 57600-8-N-1 |
| Taken | 2026-09-11 11:40:27 +10:00 |
| Identity (`*IDN?`) | `TRIMBLE,57964-80,40896646,V2.0.1.6-01` |
| Commands asked | 9 |
| Commands answered | 9 |
| Line terminators | CRLF x44 |
| Replies ending unterminated | 9 of 9 |

## The three hypotheses this sitting was taken to settle

Every one of these was read out of Lady Heather's source rather than a vendor document, and the
driver treats them as hypotheses with citations until a receiver says otherwise.

| Hypothesis | Measured |
|---|---|
| The module echoes the command before answering it | **0 of 9** replies began with an echo |
| Unsolicited `C5` time codes interleave with replies | **0 of 9** replies had one mid-reply |
| `COMMAND COMPLETE` terminates a reply | **6 of 9** replies carried it |

## What was happening

A **Trimble UCCM-P**, `TRIMBLE,57964-80,40896646,V2.0.1.6-01`, on Tony's bench, reached over a
Prolific PL2303GT USB serial adapter enumerating as COM3. It was **tracking and locked** when this
was taken: the `SYST:STAT?` screen in this file reports `ACQUISITION …[GPS 1PPS Valid]`, seven
satellites tracked and three not, `TFOM 2` / `FFOM 0`, position `MODE Hold`, and a position in
Australia at about 49 m MSL. So the antenna was connected, outdoors enough to see sky, and the
module had been up long enough to have surveyed and settled — this is a working receiver in its
ordinary state, not a cold start.

**Written from the evidence rather than from the room.** The sitting was taken by an agent driving
the port, so the lines above are what the bytes and the adapter's own identification support, and
nothing here is a claim about hardware nobody photographed: which antenna, what cable run, and how
long it had been powered are **not known** and are deliberately not guessed at. Section 14 of
`docs/manual-qa.md` is where a human-attested sitting belongs.

Three things about this module that the bytes will not explain on their own:

- **Screen lines end `0D 0A 00`** — CR, LF, then a NUL. Every line of the `SYST:STAT?` screen after
  the first therefore *begins* with an unprintable byte once the terminator is read as CRLF. A
  parser that splits on CRLF and trims nothing sees a leading NUL on every row.
- **Three of the nine catalogued queries errored**: `SYNC:HOLD:DUR?`, `GPS:POS:SURV:STAT?` and
  `GPS:POS:SURV:PROG?`. Whether they are absent from this firmware, spelled differently, or refused
  in Hold mode is not established by this sitting.
- **The prompt is `UCCM-P >`** with no trailing space, which is what #470 was opened and fixed for.

## What to do with this

If the echo count is 0, `UccmReply.Classify`'s echo handling is answering a question the hardware
does not ask, and the driver's remarks should be corrected rather than the code kept "just in case".
If it is 9 of 9, the hypothesis is confirmed and can stop being
hedged. Anything in between is the interesting case and needs reading line by line.

The same applies to the time codes: **a count of 0 does not refute hypothesis 2**, because an
interleaved broadcast depends on timing. It means this sitting did not see one, which is a weaker
statement and should be recorded as such.