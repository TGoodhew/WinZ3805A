# frames-13sep2026

A **Trimble UCCM-P**, `TRIMBLE,57964-80,40896646,V2.0.1.6-01`, on COM3 at 57600-8-N-1 through a
Prolific PL2303GT, roof antenna, **locked and settled** — the same unit and state as every other
sitting here.

| | |
|---|---|
| Taken | 2026-09-13T04:1x+10:00 |
| Listening time | 300 s |
| Raw bytes | 6,600 |
| Frames recovered | **150** |
| Frame interval | 2 s exactly — 149 consecutive steps of 2 in the GPS counter, no gaps |

Not written by `Capture-Uccm.ps1`: that script sends the catalog and records replies, and this
sitting asked nothing at all. It listened, because the question is about the unsolicited broadcast.

## Why this sitting exists

#481's remaining half, minus the power cycle. Offsets **41–42** move with the timestamp and look
like a checksum, and the issue records that as **unverified** — *"three frames cannot fit and
twenty-five have not been tried against."* This is 150.

## What varies, and why that is the whole problem

Across all 150 frames, **four byte positions move and forty do not**:

| Position | Behaviour |
|---|---|
| 29, 30 | the low half of the GPS second counter |
| 41, 42 | the candidate checksum — 149 distinct values each |
| everything else | **constant**, because the unit was locked and settled throughout |

So although 150 frames sounds like a broad sample, the *input* varies in two bytes. Nothing here can
distinguish a checksum computed over the whole frame from one computed over the counter alone, and
that limit is not fixed by listening for longer — only by making other bytes move, which means the
power cycle or the antenna disconnect #481 already needs.

## What was ruled out

The checksum **is** a deterministic function of the frame: 150 distinct counter values, 150 distinct
checksums, and no case of the same input giving two different outputs.

It is **not a CRC.** Every CRC is affine over GF(2), so the XOR of two inputs must determine the XOR
of their outputs. Across 11,175 pairs and 404 distinct input XORs there are **613 violations** — and
the violating pairs differ by values like `0x0030` and `0x1000`, which are *carries*. That is the
signature of arithmetic, not of a polynomial, and it rules out the entire CRC family at once rather
than one polynomial at a time.

Batteries run and failed, each against all 150 frames:

| Family | Variants | Ranges |
|---|---|---|
| CRC-16 | CCITT-FALSE, XMODEM, GENIBUS, AUG-CCITT, CMS, BUYPASS, ARC, MODBUS, KERMIT, X-25, MCRF4XX, USB | 6, both byte orders |
| Arithmetic | Fletcher-16 (mod 255 and 256), Adler-16, one's-complement 16-bit sum (±complement, ±word swap), plain word sum, byte sum | 5 |

## What would settle it

Frames in which **more than two bytes differ**. The state bytes at 33–36 move on a power cycle and
on an antenna disconnect, and offsets 1–26 and 31–40 have never been seen to move at all. A sitting
across a power cycle gives both the state-byte ladder #481 wants *and* an input space wide enough to
identify what the checksum actually covers.

Until then the honest position is that 41–42 are a deterministic, high-entropy, non-CRC function of
the frame, and that this driver validates a frame by its length and its `C5`/`CA` markers only.
