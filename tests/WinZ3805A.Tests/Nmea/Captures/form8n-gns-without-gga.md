# form8n-gns-without-gga.nmea

Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been
decoded, re-terminated or trimmed.

| | |
|---|---|
| Port | COM6 at 9600-8-N-1 |
| Started | 2026-09-09 15:23:19 -07:00 |
| Ended | 2026-09-09 15:25:30 -07:00 |
| Duration | 2.2 min |
| Bytes | 78530 |
| Sentences, checksum good | 1442 |
| Sentences, rejected | 0 |
| Talkers seen | GB, GN, GP |
| Sentences seen | GLL, GNS, GSA, GSV, RMC, TXT, VTG |
| Ends mid-sentence | no |

> Provenance written by hand: the capture was stopped by killing the process rather than by Ctrl+C,
> which is the one path that skips the script's own provenance writer, so these figures were
> recomputed from the file afterwards.

## Why this capture exists — a SECOND receiver, and it disagrees about GNS's shape

**It does not settle #429; `vk162-gns-no-gga.nmea` did that on 8 Sep.** Reachability was answered
there, and this file was very nearly redundant. What justifies keeping it is that the two receivers
do not emit the same sentence:

```
vk162   $GPGNS,154416.00,4731.31480,N,12212.36773,W,DN,06,1.44,36.0,-18.8,,0000*..
forM8N  $GNGNS,222320.00,4731.30936,N,12212.37027,W,ANNN,09,0.95,24.8,-18.8,,,V*..
```

Three differences, all of which a parser has to survive:

| | VK-162, u-blox 7 | forM8N, UBX-M8130 |
|---|---|---|
| Talker | `GP` | `GN` |
| Mode indicator | `DN` — **two** characters | `ANNN` — **four** |
| Tail | `,,0000` | `,,,V` |

**The mode indicator is variable length**, one character per constellation the receiver knows about,
so its length is a property of the *receiver* and not of the sentence. And the M8 carries an extra
field: NMEA 4.10 adds a navigational-status indicator (`V` here) after the reference-station id. So
**a reader that indexes GNS fields from the end, or assumes a fixed field count, gets a different
answer on these two files** — which is exactly the kind of thing a single-receiver corpus cannot
show.

That is the argument for this file. If #429 is ever built, these two captures together are the test
case; either alone is a receiver-specific parser waiting to happen.

## How it was produced

Two `UBX-CFG-MSG` writes, **to RAM only** — no `CFG-CFG` save was sent, so unplugging the receiver
reverts both. Each was acknowledged rather than assumed:

```
UBX-CFG-MSG  class 0xF0 id 0x0D rate 1   -> UBX-ACK-ACK    enable GNS
UBX-CFG-MSG  class 0xF0 id 0x00 rate 0   -> UBX-ACK-ACK    disable GGA
```

This is a **deliberately configured** receiver, like the VK-162 one. #429's remaining argument
against building GNS support — *"no receiver has been observed in this configuration by accident"* —
is untouched by this capture and still stands.

## What was happening

**Receiver.** RCmall forM8N, `HW UBX-M8130`, ROM CORE 3.01, `PROTVER=18.00`, on Tony's bench,
indoors, on his fixed GPS **timing** antenna. Thirteen GPS satellites in view, good fix throughout.

**BeiDou is enabled and tracking nothing** (`GB=0` in view) — the timing antenna is narrowband about
L1 at 1575.42 MHz while BeiDou B1 is at 1561.098 MHz. Worked out in
`form8n-gps-beidou-first-light.md`; #424 needs the module's own wideband patch antenna outdoors.

**So `ANNN` is honest rather than a defect.** Only GPS contributes to the fix, so only the first
character is `A`. A capture with a second constellation actually tracking should show a second
non-`N` character, and that is worth checking when one exists.
