# Captured status screens

Device output, reproduced verbatim. These are the assertion corpus for `StatusScreenParser`
(§11.1, P0-4, issue #4), and their exact bytes are the point: the parser derives satellite
columns from the position of the tokens in the header row, so a stray trimmed trailing space
changes what is being tested. `.gitattributes` marks this folder `-text` for that reason —
no end-of-line conversion in either direction, on any platform.

Each file holds the response to `:SYST:STAT?` with the framing removed: no echoed command
at the front, no prompt at the back, everything in between untouched, CRLF endings intact.

## Provenance

Every capture so far is from one unit:

| | |
|---|---|
| Identity | `SYMMETRICOM,Z3805A,3625A02931,1.01.03-A` |
| Line settings | 9600-8-N-1 |
| Captured | 12 August 2026 |

## What is here

| File | State | Also covers |
|---|---|---|
| `locked-stabilizing.txt` | `>> Locked to GPS: stabilizing frequency`, TFOM 3, FFOM 1, 1 satellite tracked and 9 not tracked, health monitor all OK | **Position hold** (`MODE Hold` with LAT/LON/HGT) and the **week rollover** — this unit reports 27 Dec 2006, which is the exact case P0-10 names |

Useful properties of that one file, beyond the state it captures: the header row is
`PRN  El  Az  C/N`, the 58503B-class spelling of the signal-strength column (§11.1), and the
satellite table uses **two side-by-side column groups**, so the two-group detection has
something real to run against.

Scalar queries taken in the same session, for cross-checking parsed values:

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

§11.1 asks for eight states. These five need the receiver put into them, which is a person
at the bench rather than a query:

| State | How to reach it |
|---|---|
| Power-up (0 tracked) | Capture immediately after power is applied |
| Acquiring | The first minute or two after power-up, before lock |
| Holdover | Disconnect the antenna and wait for the mode to change |
| Survey in progress | Requires starting a survey — tier C (§8.3), so it is a deliberate act, not something a capture tool should do on its own |
| Health-monitor failure | Opportunistic: capture whenever the health line is not `[ OK ]` |

Add each as its own file named after the state, and add a row to the table above.
