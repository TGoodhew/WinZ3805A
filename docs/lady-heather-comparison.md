# WinZ3805A against Lady Heather

*Written 29 Aug 2026 for [#121](https://github.com/TGoodhew/WinZ3805A/issues/121). A comparison, not
a fix list: the point is to know where this application stands before deciding what, if anything, to
adopt.*

Lady Heather (`heather.exe`, Mark S. Sims) is the incumbent. Most people running a
Z3801A/Z3805A/58503A today are running it, and it has had two decades to accumulate features against
exactly this family. Anything it does that is genuinely useful and that §13 has no row for is a gap
in the **specification**, not merely in the code.

## Where this document's facts come from

Provenance matters here more than usual, because a comparison written from impression rather than
evidence would produce spec amendments nobody could defend.

| Source | Used for |
|---|---|
| **`heather.cfg` on this machine** — a real Lady Heather configuration file, with its own explanatory comments | Auto-detect order, display options, scripting and hex-send mechanisms, platform notes |
| Project documentation and the published source tree | Receiver list, deviation statistics, log-file options, temperature control |

**What could not be verified, and is therefore not claimed below:** the precise handling of
destructive receiver commands. That is the most interesting half of the comparison for this project
and the part where a wrong statement would be worst, so it is left as an open question rather than
guessed at.

## Feature comparison

| Area | Lady Heather | WinZ3805A today | Specified, unbuilt |
|---|---|---|---|
| **Receiver coverage** | ~20 families: Trimble Thunderbolt/-E and TSIP, UCCM, Datum STARLOC II, NEC/STAR-4, Jupiter-T, Lucent KS24361, Motorola binary, NMEA, SiRF, u-blox UBX, Venus, Nortel SCPI, **Z3801A and compatible SCPI**, HP 5xxxx SCPI, NVS, Oscilloquartz, GPSD | SmartClock family only, behind `IReceiverDriver` since #122 | The driver seam exists and is documented; no second driver |
| **Auto-detect** | Tries 9600:8:N:1, 115200:8:N:1, 57600:8:N:1, 19200:7:E:1; uses **19200:7:O:1** for the Z3801A specifically | Eight combinations, documented default first (§7.1) | — |
| **Deviation statistics** | **ADEV, HDEV, MDEV and TDEV**, adapted from Tom Van Baak's `adev1.c` | **Overlapping ADEV only** (#63, shipped 28 Aug) with gap-aware segmentation and a pair count per τ | MDEV/TDEV/HDEV have **no §13 row** |
| **Plotting** | Multi-trace on shared axes, keyboard-driven scaling and annotation | Single-series `TrendChart`, EFC and 1 PPS TI on separate axes, min/max decimation that preserves excursions | Multi-series requires §9.4.4's second channel first |
| **Logging** | Configurable interval (default 1 s), optional comments, signal-level comments, optional timestamp header, tab **or comma** separator, reads its own older formats | CSV export (§9.7.5), plus a durable SQLite `trend.db` with retention and compaction | No interop with any external log format |
| **Satellite display** | Sky map, signal levels, constellation views | `SkyPlotControl` with polar plot, mask circle, signal-scaled markers, **plus a non-spatial list alternate carrying the same data** (A11Y-11) | — |
| **Temperature / environment** | Temperature display to configurable precision, and **PID parameters for temperature control** | **Nothing** | **No §13 row at all** |
| **Platform** | Windows, Linux, macOS; can reach a receiver over **TCP/IP**, local or internet | Windows 11 only, local serial only | Network transport has no row |
| **Extensibility** | Keyboard scripting (`@` lines), **raw hex sent to the receiver** (`$` lines) | Advanced Console is a catalogue **picker**, never a text box (§8.1); six opt-in experimental queries, query-only, off by default | Deliberate divergence — see below |
| **Interface** | Console-derived, keyboard-driven, very large single-file implementation | Native Fluent, mouse and keyboard, tokenised design system, eleven CI gates | — |
| **Accessibility** | Not a stated goal | Thirteen A11Y criteria, high contrast as a first-class theme, colour never the sole channel | — |

## Where this project is deliberately different

**The control surface is the real divergence, and it is a decision rather than a gap.** Lady Heather
will send arbitrary hex to the receiver from a config file. §8.1 makes the command set an
**allowlist**, §8.2 tiers it, and §8.4 excludes a set of commands so completely that they do not
exist as data anywhere a view could reach.

That is a worse tool for someone reverse-engineering a receiver and a better one for someone whose
reference is in service. §4's audience is the second. **Nothing in this comparison suggests
changing it.**

**Accessibility is the other.** It is not a feature Lady Heather is missing so much as a different
premise about who is using the program — and it is the premise that produced this project's most
expensive defects and its eleven gates.

## Candidate specification amendments

Each becomes its own issue if wanted. None is filed by this document.

1. **Temperature monitoring — the clearest gap.** Lady Heather displays it and can run a PID loop
   against it; §13 has no row. Oscillator temperature is the dominant environmental influence on a
   double-oven OCXO's drift, and #137's EFC drift analysis is trying to characterise exactly that
   without it. **Whether the Z3805A even reports a temperature over SCPI is unverified** and should
   be probed before anything is specified.
2. **MDEV and TDEV alongside ADEV.** #63 delivers overlapping ADEV. MDEV separates white from
   flicker phase noise, which ADEV alone cannot, and TDEV is the natural statistic for a timing
   receiver. The estimator already segments on gaps, so the additional work is arithmetic rather
   than architecture.
3. **A network transport.** Lady Heather reaches a receiver over TCP/IP. `ITransport` already
   abstracts the wire and `SerialTransport` is one implementation, so this is a driver-adjacent
   addition rather than a redesign — and it is what would let the receiver live in a rack and the
   display on a desk.
4. **Log interoperability.** Reading a Lady Heather log would let someone bring years of history to
   this application; writing one would let them take it away. The format is tab or comma separated
   with optional headers, which is not difficult. **Worth doing only if someone wants it** — it is
   the kind of feature that is easy to justify and rarely used.

## What this comparison does not recommend

- **Raw hex send, or a free-text command box.** See above; it is the thing §8.1 exists to prevent.
- **Higher baud rates.** Lady Heather auto-detects 115200 and 57600 for other receiver families;
  the SmartClock family tops out at 19200, so §7.1's list is complete for the hardware this
  application supports.
- **Reproducing the console layout.** §1's premise is that a native surface beats a reproduction of
  a terminal screen. Having looked at what the terminal screen actually offers, that premise holds —
  the density is real, but it is achieved with a keyboard vocabulary that has to be learned, and
  §9.1's user is glancing rather than operating.

## One thing the comparison settled

`heather.cfg`'s auto-detect list contains `19200:7:E:1`, and Lady Heather uses `19200:7:O:1` for the
Z3801A specifically. §7.1 described the Z3801A as *"commonly 19200-7-E-1"* until 28 Aug 2026, when
the Z3801A user guide was found to give **odd** parity twice and the order was corrected under #64.

The incumbent agrees, from a completely independent direction. It also shows where the even-parity
folklore probably came from: it is in the **generic** auto-detect list, which serves the other SCPI
families, not the Z3801A entry.
