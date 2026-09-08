# vk162-gns-no-gga.nmea

Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been
decoded, re-terminated or trimmed.

| | |
|---|---|
| Receiver | VK-162 USB GPS puck — u-blox `UBX-G70xx`, ROM CORE 1.00 (59842), PROTVER 14.00 |
| Port | COM4 at 9600-8-N-1 |
| Started | 2026-09-08 08:44:14 -07:00 |
| Ended | 2026-09-08 08:56:14 -07:00 |
| Duration | 12 min |
| Bytes | 353077 |
| Sentences, checksum good | 5767 |
| Sentences, rejected | 0 |
| Talkers seen | GP |
| Sentences seen | GLL, GNS, GSA, GSV, RMC, TXT, VTG |
| Ends mid-sentence | no |

## What was happening

**This is #429's configuration, made deliberately.** That issue says a receiver sending `GNS`
instead of `GGA` is the case that matters and that *"whether that is reachable in practice is
unknown"*. It is reachable, and trivially: two `UBX-CFG-MSG` frames on the same VK-162 already in
this corpus — enable `GNS` (class `0xF0`, id `0x0D`, rate 1), disable `GGA` (id `0x00`, rate 0).
Both ACKed. The receiver was restored to the shipped default afterwards and the return of `$GPGGA`
confirmed on the port.

**720 `GNS` sentences and not one `GGA`**, across 12 minutes and 5,767 sentences with none rejected.

A representative cycle's position sentence:

```
$GPGNS,154353.00,4731.31433,N,12212.36958,W,DN,06,1.45,41.1,-18.8,,0000*71
```

### What it settles

**#417's fear is refuted against hardware, not just against a hand-written cycle.** That review
expected a receiver emitting `GNS` in place of `GGA` to "show no fix at all while the receiver is
perfectly happy". It does not: the fix quality falls back to `RMC`'s status field and `RMC` carries
the position too. `TheRealReceiverSendingGnsAndNoGgaStillShowsAFixAndAPosition` asserts exactly that
against these bytes.

**And it confirms what the gap costs.** Field 10 of every one of the 720 sentences carries an
altitude — `41.1` above — and none of it reaches the model, because `GNS` is unparsed. `GGA` field 9
is the only altitude the driver reads, so a receiver in this configuration reports a position with
no height and `HeightDatum` reads `Unknown`. That is honest, and poorer.

**The per-constellation mode indicator is real and is `DN` in all 720.** `D` for GPS operating
differentially, `N` for GLONASS — which is disabled on this unit, and says so. This is the field
#429 notes "says *which* constellations are contributing, which nothing else in the sentence set
carries", and it is the second thing the gap costs.

### It broke a corpus assertion, which was the assertion's fault

`NmeaCaptureReplayTests.EveryCaptureIsTalkerOutput` required every capture to contain `GGA`. That
quietly encoded **the very assumption #429 exists to question** — that a receiver always sends one.
This capture is valid talker output and the test rejected it. The assertion now requires a
position-bearing sentence, `GGA` **or** `GNS`, and says why.

### What it does not settle

**Nothing about #424.** This is still one constellation; the mode string's second character is `N`
precisely because GLONASS is off. And this receiver cannot run two at once — see
`vk162-glonass-only.md`.

**The position in these sentences is genuine and was not scrubbed**, for the reason given in
`vk162-steady-state.md`: a capture edited to be safe is no longer byte-exact.
