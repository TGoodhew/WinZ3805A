# The NMEA driver against Lady Heather's

*Written 6 Sep 2026 for [#417](https://github.com/TGoodhew/WinZ3805A/issues/417). Sentence by
sentence. A review, not a rewrite — this driver is smaller on purpose, and the restraint is a design
decision rather than an oversight.*

The asymmetry that makes this worth doing: Lady Heather has been exercised against a great many real
receivers over many years, and `NmeaDriver` (#310) **has never met a real talker**. Heather has seen
wild inputs we have not.

What the review actually found was not what was expected. **Two of the differences are defects in
Heather**, one is a defect of ours that is now fixed, one is a real limitation of ours that needs
hardware, and the gap the issue feared most turned out not to exist.

For the feature-level comparison of the two applications, see
[lady-heather-comparison.md](lady-heather-comparison.md).

## Provenance

`heathgps.cpp` in the default Lady Heather install (`C:\Program Files (x86)\Heather`), MIT licensed,
© 2008–2016 Mark S. Sims. Line numbers are from that file. Nothing below was verified against a
receiver by either party in the last week — Heather's behaviour is read from its source, ours from
its tests.

## Sentence coverage

| Sentence | Heather | Us | Note |
|---|---|---|---|
| `GGA` | yes | yes | |
| `RMC` | yes | yes | our cycle boundary |
| `GSA` | yes | yes | we read the fix mode only |
| `GSV` | yes | yes | see below |
| `ZDA` | yes | yes | |
| `GLL` | no | yes | |
| `VTG` | no | yes | |
| `GNS` | yes | **no** | see below |

## 1. Talker handling — our design is the more robust, measurably

**Heather enumerates every talker and identifier pair by hand.** `decode_nmea_msg` is a ladder of
`strcmp` against `"GPGSV"`, `"BDGSV"`, `"GBGSV"`, `"GLGSV"`, `"GAGSV"`, `"GNGSV"`, then the same six
for `GSA`, `GGA`, `RMC`, `ZDA`. Roughly thirty string comparisons.

**We strip the talker instead.** `NmeaSentence.Key` is `"$--" + identifier`, so one case arm handles
every constellation, and a talker nobody anticipated is handled by construction.

That difference is not cosmetic, and the evidence is in Heather itself:

- **`BDRCM` (heathgps.cpp:9580) is a typo for `BDRMC`.** It occurs exactly once and the correct
  spelling occurs nowhere, so **Lady Heather silently ignores BeiDou RMC sentences.** A ladder of
  thirty hand-written literals is a place for exactly this bug, and it has been there for years
  without anyone noticing — which is the point, because the failure is silent.
- **There is no `GQ` arm at all**, so QZSS sentences are ignored there. Ours are read; a test covers
  `GL`, `GA`, `GB` and `GQ`.

**Nothing to adopt here.** Recorded because a review that only looks for our gaps is not a review,
and because this is a concrete argument for keeping `Key` talker-agnostic the next time someone
proposes making it specific.

## 2. Per-constellation GSV paging — a real defect of ours, now fixed

A multi-constellation receiver runs **a separate GSV cycle per constellation**, each with its own
page numbering, interleaved in the same second: `$GPGSV,3,1,…` through `3,3` alongside
`$GLGSV,2,1,…` and `2,2`.

Because our key strips the talker, all five pages reached the parser as one run. It read the page
total from the **first** page — three, GPS's — and compared it against the count of **all** pages,
five, producing:

```
the cycle carried 5 GSV page(s) of 3
```

on **every cycle, for ever, from a receiver working perfectly.** Parse warnings surface on the
Diagnostics page, so this is the parser's own version of a gate that cries wolf: a permanent stream
of spurious warnings teaches a reader to ignore the one that matters.

Fixed by grouping the pages by talker and checking each constellation's own count, naming the talker
in the warning when one genuinely is short. `NmeaMixedConstellationTests` covers both halves — the
false alarm is gone and the real alarm still fires — because fixing a false positive by deleting the
check is the easy wrong answer.

**The satellite list itself was always right**, and deliberately: a user wants every satellite in
view regardless of which constellation it belongs to.

## 3. Satellite numbering collisions — a real limitation, not fixed

NMEA 4.10 gives each constellation its own satellite-number range (GPS 1–32, SBAS 33–64, GLONASS
65–96) and a conforming receiver never collides. **Receivers exist that number per constellation and
rely on the talker to disambiguate.** Against one of those our dedupe is a plain `HashSet<int>` of
PRNs, so the second constellation's satellite 1 is silently dropped: the sky plot shows fewer
satellites than are being tracked, which reads as poor reception rather than as a parsing choice.

Heather does not have this problem, because it carries the constellation as a parameter through
`parse_gpgsv(int system)` and keeps the systems apart.

**Deliberately not fixed here.** The remedy is either duplicate PRNs in the model or a wider
satellite identity, and both are model changes that want a real receiver in front of them — a fix
aimed at a receiver nobody has seen is a guess with tests attached. The current behaviour is pinned
by a test that names the cause, and the work is tracked separately.

## 4. `GNS` — the issue's premise was wrong

#417 feared that a receiver emitting `GNS` instead of `GGA` would "show no fix at all while the
receiver is perfectly happy". **It does not.** The fix quality falls back to RMC's status field:

```csharp
int quality = ParseInt(gga?.Field(5)) ?? (rmc?.Field(1) == "A" ? 1 : 0);
```

and RMC also carries the position. A cycle of RMC + GNS + GSV yields a valid fix, a position, and a
non-provisional time. A test asserts it.

What the gap actually costs is the **altitude** — GGA's, which GNS also carries and we do not read —
and GNS's per-constellation mode-indicator string. That is an enhancement, not a correctness bug,
and the issue is downgraded accordingly rather than left overstated.

Note also that RMC is our **cycle boundary**, so a talker that omitted it would not merely lose the
fix, it would never complete a cycle at all. Any receiver we work with sends RMC by construction.

## 5. Proprietary sentences — a decision, recorded

Heather both sends and understands `PUBX` (u-blox) and references `PSTI` (Skytraq).

**We should read neither and send neither**, and this is the decision rather than an omission.
Sending is settled by §8.4 and §10.11 — the catalog is an allowlist and the NMEA driver's catalog
holds reads only, so a proprietary configuration sentence is protected by its absence. Reading is a
judgement: a proprietary sentence carries vendor-specific state that this application has nowhere
honest to display, and §7's design is to show what NMEA carries and no more. Adding one would mean a
surface for one vendor's receiver, which is the shape of decision §13 exists to make deliberately.

## 6. Malformed input — the habits worth having, and the one we already had

This was expected to be the richest section, on the reasoning that a decade of real-world
malformation is knowledge unobtainable any other way. It is thinner than expected, and the reason is
structural: **Heather's parsers read fields positionally with `atof` and `sscanf`**, which cannot
fail loudly — a missing field yields 0.0 and a truncated line yields whatever was in the buffer.
Its robustness comes from never throwing in the first place, not from validation.

Ours refuses instead: `NmeaSentence.TryParse` returns null for anything malformed, the checksum is
verified before a sentence is used, and a failure becomes a parse warning naming the identifier.
That is the stronger position and it is already in place (§11.1), tested against bad checksums,
foreign sentences, noise, split lines and a transport closing mid-stream.

**One habit worth noting rather than adopting:** Heather bounds its copy out of the receive buffer
(`if(i >= sizeof(nmea_msg)-12) break;`) against a sentence longer than its buffer. We are not
exposed to that class — `string` has no fixed buffer — but a talker emitting an unbounded line is a
real thing, and the transport rather than the parser is where that belongs.

## What this review changed

| Finding | Outcome |
|---|---|
| GSV page accounting conflated constellations | **Fixed**, with tests for both the false alarm and the real one |
| Satellite numbering collisions across constellations | **Pinned by test, tracked separately** — needs hardware |
| `GNS` causes no fix | **Premise corrected**; downgraded to an enhancement worth only the altitude |
| Talkers beyond `GP`/`GN` | **Verified working**, test added for `GL`, `GA`, `GB`, `GQ` |
| Proprietary sentences | **Decision recorded**: read none, send none |
| Malformed input | **Nothing to adopt**; our position is already the stronger one |
| Heather's `BDRCM` typo and missing `GQ` | **Nothing to do**, recorded as the argument for our design |

## What it did not change

No sentence was added, no field widened, and the driver is the same size it was. The one code change
is a page count that now groups by talker. That is the expected shape of a review whose subject was
built deliberately narrow.
