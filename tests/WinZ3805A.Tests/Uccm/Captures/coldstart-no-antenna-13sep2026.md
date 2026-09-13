# coldstart-no-antenna-13sep2026

Raw UCCM output, byte for byte. Written by `build/Capture-Uccm.ps1`; nothing has been decoded,
re-terminated or trimmed. The echo and any interleaved time code are evidence, not noise.

| | |
|---|---|
| Port | COM3 at 57600-8-N-1 |
| Taken | 2026-09-13 09:57:45 +10:00 |
| Identity (`*IDN?`) | `TRIMBLE,57964-80,40896646,V2.0.1.6-01` |
| Commands asked | 17 |
| Commands answered | 17 |
| Line terminators | CRLF x60 |
| Replies ending unterminated | 17 of 17 |
| Replies ending in a prompt | 17 of 17 |
| Binary `C5` packets seen | 5 |
| `0xC5` bytes not matching the packet shape | 0 |

## The three hypotheses this sitting was taken to settle

Every one of these was read out of Lady Heather's source rather than a vendor document, and the
driver treats them as hypotheses with citations until a receiver says otherwise.

| Hypothesis | Measured |
|---|---|
| The module echoes the command before answering it | **0 of 17** replies began with an echo |
| Unsolicited `C5` time codes interleave with replies | **0 of 17** replies had one mid-reply |
| `COMMAND COMPLETE` terminates a reply | **14 of 17** replies carried it |

The time-code row counts **binary** packets - byte `0xC5` through `0xCA` - found in the raw
stream. Until 10 Sep 2026 this script looked for the *characters* `C5` in text decoded as ASCII,
which turns every byte above `0x7F` into `?`, so the row could only ever have read 0.

### Hypothesis 2, with the exposure it was tested against

A count of zero refutes nothing on its own, so here is what the count was measured against. A code
can only land *inside* a reply that is still arriving, so the exposure is reply wire time over the
sitting; a scalar query answers in milliseconds and cannot test this however long anyone sits there.

| | |
|---|---|
| Sitting length | 9.7 s |
| Reply wire time | 0.54 s, 6% of the sitting |
| Codes seen / due at one per 2 s | 5 / ~4.8 |
| Mid-reply expected by chance | ~0.28 |
| **Verdict** | **INCONCLUSIVE: 5 code(s) seen, but only ~0.28 would have landed mid-reply by chance. Ask a longer reply, or ask more often.** |

## What was happening

**This is not holdover, and the file was named `holdover-antenna-off` for the first half hour of
its life.** It is renamed because the distinction is the whole point of the sitting.

The module is the bench **Trimble UCCM-P**, `TRIMBLE,57964-80,40896646,V2.0.1.6-01`, on COM3
through a Prolific PL2303GT at 57600-8-N-1. It sits at Mt Warrigal; the position the screen still
reports, S 34:32:39.019 / E 150:50:25.107 / +49.72 m MSL, is the surveyed one held over from before.

**The antenna was disconnected, and the unit also lost power at 09:29:20 +10:00** - visible in the
application's own log as twelve seconds in which the serial link stopped answering, after which the
session reconnected and the reported state changed from 1 to 0. So at capture time the unit had
been powered for about 28 minutes and had **never had a GPS reference since boot**. That is a
different §11.1 state from holdover, which means *was locked, and then lost the reference*, and the
two must not be conflated: this one has no valid time, no valid position fix and nothing to hold
over from.

What the status screen says, and it is worth reading before the bytes:

- `UCCM A Status[OCXO WARMUP]`, `ACQUISITION`, `[GPS 1PPS Invalid]`
- all four `Ref 8KHz N: [LOS]`, and `GPS: [No Ref]`
- Tracking 0, Not Tracking 12 - every satellite listed with `--` elevation and azimuth
- TFOM 2, FFOM 3
- `GPS  00:28:03 (?) 22 Aug 1999` - the elapsed time since power-up, against the module's epoch
  base date rather than a real one, flagged `(?)`. **22 Aug 1999 is the GPS week 1024 rollover
  date**, which is what an unfixed receiver of this generation counts from, and §7.4 is the
  specification section that cares.

### What the bytes gave that four previous sittings could not

Every earlier capture was taken locked and settled, so offsets 1-26 and 31-40 had been byte-identical
throughout and the frame carried no information about what any of them meant. Against the locked
corpus in `frames-13sep2026.txt`, this sitting moves five of them:

| offset | locked | here | reading |
|---|---|---|---|
| 27-30 | `57 D0 5B DE` | `24 EA 06 94` | GPS seconds. `24EA0694` is 1999-08-22, matching the screen |
| 32 | `12` | `00` | unknown; had been assumed static |
| 33 | `60` | `41` | `PpsState` |
| **35** | **`45`** | **`4F`** | **`LockState`: locked -> settling** |
| 36 | `80` | `90` | `DateValidityState`; the `(?)` on screen is presumably this |

**Offset 35 is the one that matters.** `0x45` locked and `0x4F` settling are Lady Heather's
*Trimble* values, and until this sitting the project had only ever seen `0x45`. Both halves of the
Trimble row of `ModeFromLockState` are now observed on hardware rather than read out of somebody
else's source. `0x41`, power-up, is still unseen and is what the power cycle is for.

Note also that the vendor discrimination in `SuggestedVendor` still resolves correctly here:
`0x4F & 0xF0` is `0x40` and `0x90 & 0xF0` is `0x90`, both of which say Trimble, and they agree. That
path had never been exercised outside the locked state.

### Two answers that are not the same answer

`:ROSC:HOLD:DUR?` was refused with **`Command error`**. The two survey queries,
`:GPS:POS:SURV:STAT?` and `:GPS:POS:SURV:PROG?`, were refused with **`Undefined header`**. All three
are catalogued as the queries only a UCCM-P answers, and this *is* a UCCM-P - it says `UCCM-P >` at
every prompt.

Two different refusals are two different facts. `Undefined header` is the module saying it does not
know the mnemonic at all; `Command error` is the module knowing it and declining it here. So the
survey pair looks genuinely unsupported on this firmware, while holdover duration looks
**state-dependent** - which is exactly the discrimination #483 has been unable to make, and the
reason the sitting was continued into real holdover rather than settling for this one.

### One reading that is out of band

`DIAG:LOOP?` answered, three payload lines as always plus the `LINK0:` line Heather does not
document, with:

```
DAC_LINK    DAC_AVG    DAC_GPS   FREQ_CORR  LINK_OFF   FREQ_DIFF  FREQ_FRAC
+5.91E-08  -3.00E-07  +5.91E-08  +0.00E+00  -3.59E-07  -3.59E-07  +0.00E+00
```

`FREQ_DIFF` is **-3.59E-07**, outside the ±2.00E-7 band `UccmLoopReading` accepts, so the driver
discards it and renders `—`. That band is #418 section 3's open question and it had never been
tested against an unlocked module. Whether -3.59E-07 is the bogus value the band exists to reject,
or a perfectly real offset from an oscillator that has not been disciplined yet and the band is
simply too narrow for the unlocked states, is not answerable from one reading - but it is now
answerable at all, which it was not this morning.

## What to do with this

If the echo count is 0, `UccmReply.Classify`'s echo handling is answering a question the hardware
does not ask, and the driver's remarks should be corrected rather than the code kept "just in case".
If it is 17 of 17, the hypothesis is confirmed and can stop being
hedged. Anything in between is the interesting case and needs reading line by line.

The same applies to the time codes: **a count of 0 does not refute hypothesis 2**, because an
interleaved broadcast depends on timing. It means this sitting did not see one, which is a weaker
statement and should be recorded as such.

## Postscript, from the holdover run an hour later

The hypothesis above — that `Command error` marks a state-dependent refusal — **did not survive.**
`:ROSC:HOLD:DUR?` was asked again through fourteen minutes of genuine holdover, on a module that had
locked with a valid fix and then had its antenna pulled, and it answered `Command error` every time.
It is refused when cold, when locked, and in holdover alike.

So the distinction between the two refusals is real and still worth recording, but it does **not**
mean what this note guessed it meant. The practical conclusion for the driver is simpler and firmer
than the guess: this firmware never answers that query in any state we can produce, so it belongs
with the measured absences rather than being waited on.

See `transitions-13sep2026.md` for that sitting.
