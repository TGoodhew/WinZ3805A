# Captured status screens

Device output, reproduced verbatim. These are the assertion corpus for `StatusScreenParser`
(§11.1, P0-4, issue #4), and their exact bytes are the point: the parser derives satellite
columns from the position of the tokens in the header row, so a stray trimmed trailing space
changes what is being tested. `.gitattributes` marks this folder `-text` for that reason —
no end-of-line conversion in either direction, on any platform.

Each file holds the response to `:SYST:STAT?` with the framing removed: no echoed command
at the front, no prompt at the back, everything in between untouched, CRLF endings intact.

## Provenance

Every capture is from one unit — `SYMMETRICOM,Z3805A,3625A02931,1.01.03-A`, at 9600-8-N-1 —
in two sittings:

| Sitting | When | How | Files |
|---|---|---|---|
| The first capture | 12 August 2026 | By hand, from a terminal session, with the scalar queries below taken alongside it | `locked-stabilizing.txt` |
| The backyard sitting | 27–28 August 2026 | `build/Capture-Fixtures.ps1` left watching the screen through a hardware move, a power cycle and an antenna pull, writing one file per state it had not seen — plus two screens taken by hand where the harness could not (#242, #247) | everything under `captured/` |

## What is here

One corpus in two folders. `captured/` was meant, on 21 August, as a staging area — a capture
would be promoted by moving it up a level and renaming it. What happened instead is that the
tests were pointed at the captures where they lie, so the harness's own names and
`captured/capture-log.md` stay attached to the bytes they describe. **Promoting a capture now
means adding a row to the table below and pointing a test at it; the file does not move.**

| File | State | Also covers | Asserted by (`StatusScreenParserTests`) |
|---|---|---|---|
| `locked-stabilizing.txt` | `Locked to GPS: stabilizing frequency`, TFOM 3, FFOM 1, 1 satellite tracked and 9 not, health all OK | **Position hold** (`MODE Hold` with LAT/LON/HGT); the **week rollover** — this unit reports 27 Dec 2006, the exact case P0-10 names; the `PRN  El  Az  C/N` header (the 58503B-class spelling, §11.1); the satellite table in **two side-by-side column groups** | the nine `TheCapturedScreen…` tests; the truncation, clock-injection and panel-edge tests; and the `AnAveragedPositionIsDistinguishedFromAHeldOne` and `OnlyAMarkedRowIsProvisional` theories (also read outside this class by `FixtureReplayTests` and `PollingServiceTests`) |
| `captured/power-up-gps-acquisition.txt` | `Power-up: GPS acquisition`, outputs invalid, 1 PPS invalid, **0 tracked** — §11.1's *power-up (0 tracked)*. **Its 28th line is a stray `*IDN?` answer and prompt**: the harness's reconnect path ran during the power cycle and the framing strip, which cuts after the last CRLF, could not remove it. Kept as captured; the parser ignores it (#316 audit) | satellites the receiver is *attempting* to track, kept rather than dropped; the provisional power-up time and its `?` marker in the GPS time scale (#245) | `SatellitesTheReceiverIsAttemptingToTrackAreNotDropped`, `AProvisionalPowerUpTimeIsReadAndFlagged` |
| `captured/power-up-fine-freq-adj.txt` | `Power-up: fine freq adj`, outputs invalid, 1 PPS valid, 8 tracked — §11.1's *acquiring* | the provisional power-up time and its `?` marker (#245); absent readings as distinct from provisional ones; a mode detail stopping before its bracketed figure | `AProvisionalPowerUpTimeIsReadAndFlagged`, `ThePowerUpScreenSeparatesAbsentFromProvisionalReadings`, `AnAveragedPositionIsDistinguishedFromAHeldOne`, `OnlyAMarkedRowIsProvisional`, `AModeDetailStopsBeforeItsBracketedFigure` |
| `captured/locked-to-gps-stabilizing-frequency.txt` | `Locked to GPS: stabilizing frequency`, outputs valid / reduced accuracy, 8 tracked | | `TheStabilizingScreenParses`, `AnAveragedPositionIsDistinguishedFromAHeldOne` |
| `captured/locked-to-gps.txt` | `Locked to GPS`, outputs valid, 9 tracked — the fully locked state, §11.1's *locked* | an averaged position as distinct from a held one | `TheFullyLockedScreenParses`, `AnAveragedPositionIsDistinguishedFromAHeldOne` |
| `captured/surveying-locked-to-gps-stabilizing-frequency.txt` | a **survey in progress** while locked and stabilizing — §11.1's *survey in progress*. Taken by hand: until #242 the harness could not see a survey and reported the screen as one already seen, so its log entry is reconstructed | the rolled-over date corrected on a surveying screen | `TheSurveyingScreenParses`, `TheSurveyingScreensRolledOverDateIsCorrected`, `AnAveragedPositionIsDistinguishedFromAHeldOne` |
| `captured/holdover-gps-1pps-invalid.txt` | `Holdover: GPS 1PPS invalid`, 0 tracked, three seconds in — the antenna pulled. §11.1's *holdover* | both holdover uncertainties; how long the receiver has been degraded; the mode row not mistaken for the advisory; the surveyed position held through holdover | `TheHoldoverScreenParses`, `TheHoldoverScreenReportsBothUncertainties`, `TheHoldoverScreenReportsHowLongItHasBeenDegraded`, `ThePresentUncertaintyIsTheSameOnBothHoldoverScreens`, `TheModeRowIsNotMistakenForTheAdvisory`, `TheHoldoverScreenHoldsTheSurveyedPosition` |
| `captured/holdover-gps-1pps-invalid-deep.txt` | the same holdover **11 m 34 s in** — taken by hand into a scratch directory with an empty seen-set, because the harness never takes a second sample of a signature it has seen | the only evidence that the minutes field is unpadded rather than fixed-width | `TheHoldoverScreenReportsHowLongItHasBeenDegraded`, `ThePresentUncertaintyIsTheSameOnBothHoldoverScreens` |
| `captured/holdover-gps-1pps-invalid-3.txt` | `Holdover: GPS 1PPS invalid` with the 1 PPS **valid again** and 6 tracked — the antenna back, the mode not yet changed | holdover is still holdover with the signal back; the duration keeps counting into recovery | `HoldoverWithTheSignalBackIsStillHoldover`, `TheDurationKeepsCountingFromHoldoverIntoRecovery` |
| `captured/recovery-fine-freq-adj.txt` | `Recovery: fine freq adj`, outputs valid / reduced accuracy, 7 tracked | the duration carried from holdover into recovery; a mode detail stopping before its bracketed figure | `TheRecoveryScreenParses`, `TheDurationKeepsCountingFromHoldoverIntoRecovery`, `AModeDetailStopsBeforeItsBracketedFigure` |

`FixtureCorpusTests` also runs its invariants over **every** `*.txt` at any depth under
`Fixtures/` — it looks like a status screen (at least 200 bytes and a `SmartClock Mode` line),
it keeps its carriage returns, the parser never throws on it or on any prefix of it, every PRN is
a GPS slot, every angle is on the sky, no satellite is both tracked and not or listed twice, the
tables match the counts the screen states, and the health items agree with the health line. A
new capture is in the corpus the moment it lands — which is why a capture from another family
(an NMEA talker's timed listen) lives beside its driver's tests, as `docs/adding-a-receiver.md`
step 1 says, and never here.

Scalar queries taken in the first sitting, for cross-checking parsed values:

```
:SYNC:STAT?           LOCK
:SYNC:TFOM?           +3
:SYNC:FFOM?           +1
:SYNC:TINT?           -5.4E-009
:SYNC:HOLD:DUR?       +6.00000E+002,0
:DIAG:ROSC:EFC:REL?   -1.68528E+001
:GPS:SAT:TRAC:COUN?   +1
:GPS:REF:ADEL?        +7.70000E-008
:SYST:DATE?           +2006,+12,+27
:SYST:TIME?           +14,+45,+1
:SYST:STAT:LENG?      +23
```

Note that response values arrive with a **leading space** — `_+3`, not `+3`. Trim before
parsing rather than treating the space as part of the field.

## What is still missing

§11.1 asks for eight states. Seven are captured — power-up with 0 tracked, acquiring, locked,
holdover, survey in progress, position hold and the week-rollover date. One is not:

| State | How to reach it |
|---|---|
| Health-monitor failure | Opportunistic: capture whenever the health line is not `[ OK ]`. Leave the harness running during any hardware move (the procedure is `docs/manual-qa.md` §5); it writes one file per state it has not seen and is designed to reconnect when the power goes and the adapter re-enumerates — a path that has not been deliberately exercised. |

Two modes the application distinguishes have no capture either, because neither happened during
the sitting and §11.1 does not ask for them: *Waiting to recover* — a holdover screen carrying a
wait reason — and *Diagnostic / off*. The harness will take either the first time it sees one.
