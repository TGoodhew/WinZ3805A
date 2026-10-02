# SmartClockSimulator

A Symmetricom Z3805A that is not a Z3805A (#639).

## Why it exists

The SmartClock family is this project's first receiver, and until this existed the only way to see
the application handle it was the one bench unit. That was a problem for three things:

- **The automated QA pass (#633)** runs in virtual machines, which have no receiver. Sections 21 and
  23 of `docs/manual-qa.md` need one connected.
- **The faults worth testing** can otherwise only be caused by doing them to the bench unit: pulling
  the antenna for holdover, waiting out a power-up, or dropping the cable mid-session.
- **Two states have never been seen at all:** a health-monitor failure, and a forced (manual) holdover.

This produces every state the bench unit was captured in, plus those two, and the faults on demand.

## It is a second reading, not a copy

Unlike the NMEA and UCCM simulators, **this one references nothing from the application**, not
even the Device library. It is checked against the bench unit in two ways:

1. **Its screens are the bench unit's, byte for byte.** `StatusScreenWriterTests` types out the values
   each capture under [`tests/WinZ3805A.Tests/Fixtures/`](../../tests/WinZ3805A.Tests/Fixtures/README.md)
   shows. It then requires the simulator to print that capture exactly, every space, underscore and
   CRLF. All eleven captures pass, and a new capture without a matching test fails `EveryCaptureHasASnapshot`.
2. **Its wire behaviour is §7.2's**, which was itself corrected against the bench unit (#78).
   `ScpiEngineTests` checks the rules that once cost a client its connection:
   - the error prompt names the error queue, not the last command;
   - the framing glitch on the first command;
   - the reading that has no answer while unlocked.

Only after that is the application run against it (`SmartClockSimulatedSessionTests`). Because the
two share no code, agreement there is two independent readings agreeing, not one reading agreeing
with itself.

## Running it

```powershell
# One answer to each query, printed, for a look:
dotnet run --project tools\SmartClockSimulator -- --stdout --start locked

# On one end of a com0com pair, with the application on the other:
dotnet run --project tools\SmartClockSimulator -- --port COM7 --start locked

# On a named pipe a VMware VM's serial port is connected to (the VM serves the pipe):
dotnet run --project tools\SmartClockSimulator -- --pipe-client winz-qa --start locked --control winz-qa-ctl
```

| Option | Default | |
|---|---|---|
| `--port COMn` / `--pipe <name>` / `--pipe-client <name>` / `--stdout` | | Where to answer. One is required. |
| `--start powerup` or `locked` | `powerup` | `locked` starts in a settled lock, as a unit that has run for a day would be. |
| `--speed <factor>` | `1` | Runs the timeline faster. The clock the receiver reports stays real. |
| `--baud <rate>` | `9600` | Paces replies at the line rate, so a status screen takes the ~2 s of wire time it takes on hardware. Use `0` for instant. |
| `--echo` | off | Echo each command, as `FDUPLEX ON` does. The bench unit does not echo. |
| `--leading-space` | off | A space before each value, as §7.2 records. The bench unit sends none. |
| `--announce` | off | The banner and framing glitch §7.2 records on connecting. The bench unit showed neither on 2 Oct 2026. |
| `--control <name>` | | Also take control commands on `\\.\pipe\<name>`. |
| `--compare COMn` | | Compare against a real receiver. See *Comparing it with the bench unit*. |

**Connecting a VM to it.** Each QA VM has a COM2 that the VM serves on `\\.\pipe\winz-qa-<name>`
(`Add-QaSimulatorPort`, `build/qa/README.md`), and the QA pass's `receiver` scenario runs the app in
the guest against this simulator over it. It connects, locks, follows a pulled antenna into holdover
and back, and survives a 30-second power cycle, all driven through `--control`. A VM's serial port
carries no DTR through the pipe, so the simulator cannot see the guest open its port; leave
`--announce` off there, or the banner goes out to nobody and the glitch fires on whatever the guest
sends first.

### Control commands

Type these on standard input, or write them a line at a time to the control pipe. A QA scenario
pulls the antenna this way, which takes the application down the same path a person pulling the
real one would.

```
antenna off | on          pull or reconnect the antenna (holdover, then recovery)
power-cycle               start again from power-up
start locked              jump straight to a settled lock
holdover | recover        force holdover, or start recovery, as the commands would
health <item> fail | ok   item: selftest intpwr ovenpwr ocxo efc gpsrcv
fault silent | garbage | truncate | latency <ms> | drop | none
echo on | off             the receiver's FDUPLEX setting
speed <factor>            run the timeline faster
status                    one line describing the receiver and the link
```

The faults behave as follows:
- `silent` hears and never answers, as a dead TX line looks.
- `garbage` replaces every reply with noise, as a wrong baud rate does.
- `truncate` cuts the next reply off before its prompt.
- `drop` closes the connection.

## The timeline

These are the states the bench unit passed through, in the order it passed through them:

```
Power-up: GPS acquisition -> Power-up: fine freq adj -> Locked to GPS: stabilizing frequency -> Locked to GPS
  antenna off: about a minute still LOCK with nothing tracked -> Holdover: GPS 1PPS invalid (WAIT)
  antenna on:  holdover with the signal back (WAIT) -> Recovery: fine freq adj (REC) -> Locked (stabilizing)
  holdover:    Holdover: manually initiated (HOLD), until recover
  power-cycle: a lost first command, slow first screens, then power-up; cold, with no position
```

**The order and what each state answers come from the bench unit.** How long each state lasts mostly
does not, because that depends on the oscillator, the sky and how long the unit was off. The defaults
are close to what was seen on 2 Oct 2026 where it was measured: 40 s to acquire warm and 6 minutes
cold, 54 s before holdover and again before recovery. The rest are chosen to keep a run watchable,
and `--speed` shortens them further.

## What is measured and what is not

Read this before trusting a green test that runs through the simulator.

**Compared with the bench unit on 2 Oct 2026.** `--compare` asked both the same 71 read-only
queries. 65 answers first differed in shape. After the fixes below, 4 differ, and all 4 only in
their values: a checksum digit, which log entry comes first, and the readings on the screen and
in the log. The screens match line for line in width and structure. The error queue the run left
behind read back identically, four entries deep. The last run's report is
[`comparisons/bench-2026-10-02.md`](comparisons/bench-2026-10-02.md).

**Taken through its states the same day.** `--watch` recorded every reply through a forced holdover, a
pulled antenna, recovery and two power cycles. What each state answered, and what changed here as a
result, is in [`comparisons/states-2026-10-02.md`](comparisons/states-2026-10-02.md), beside the raw
records.

| Behaviour | Source |
|---|---|
| The status screen's layout: every column, width, label, underscore and trailing space, in all fifteen captured states, among them an unknown satellite (`-- ---   --`), a starred single-digit PRN (`* 4`), the factory initial position (`INIT LAT`) and a suspended survey | **Bench unit**, byte for byte |
| Readings scaled to the unit that keeps them at one or more: `+300 ps`, `800 ns` | **Bench unit**, 2 Oct 2026 (`locked-to-gps-sub-unit-readings.txt`) |
| `ANT DLY 0 ns` and `ELEV MASK 0 deg` during GPS acquisition | **Bench unit**, once (`power-up-gps-acquisition.txt`) |
| The `scpi > ` and `E-nnn> ` prompts: the newest error shown, the oldest read, the clean prompt on the read that empties the queue | **Bench unit** (§7.2), and replayed identically by the comparison |
| No echo; CR, LF and CRLF all end a command | **Bench unit** (§7.2) |
| **No space before a value**: `+3`, `LOCK` | **Bench unit**, 2 Oct 2026: none in 70 replies. §7.2 records one; `--leading-space` reproduces that |
| **No banner and no `-362`** on connecting | **Bench unit**, 2 Oct 2026, in two openings, one with DTR asserted at open and one raising it later, and in two power cycles. §7.2 records both on 21 Aug 2026; `--announce` reproduces that, with the banner as the identity, CRLF and a prompt |
| Power-up: the first command after power returns answered with a bare prompt; the first screen over 15 s late and the second 7.3 s; the time, date, leap and prediction queries refused until lock, the position until there is one, and the engine identity, predictions and count for the first half minute | **Bench unit**, two power cycles on 2 Oct 2026 |
| Holdover: `HOLD` when forced, with the time interval still answered; **`WAIT`** when GPS is lost, with `:SYNC:HOLD:WAIT?` `GPS`; about 54 s of `LOCK` with nothing tracked before it, the time interval answering for the first 18 | **Bench unit**, 2 Oct 2026 |
| The lamps, `:GPS:REF:VAL?` and the three condition registers that move with the state, in every state above | **Bench unit**, 2 Oct 2026 |
| A cold power-up: six minutes acquiring, the factory initial position, `inacc position`, the survey suspended | **Bench unit**, once. What made it cold is not known |
| `:SYNC:TINT?` unlocked, and `:PTIM:LEAP:DATE?`/`DUR?` with nothing announced: no data, `E-230` | **Bench unit** (§7.3.1, §10.14) |
| `:SYNC:HOLD:TUNC:PRES?` outside holdover and `:GPS:POS:SURV:PROG?` with no survey: no data, `E-221` | **Bench unit**, 2 Oct 2026. The 58503A guide gives `-230` for the first |
| The §8.5 undocumented queries answer `-113` | **Bench unit** (§8.5) |
| Every other query's form: the integers signed, the reals five decimals and a three-digit exponent, the threshold an integer (`+86400`), the uncertainties `+0.8E-006`, booleans `0` and `1`, `:SYST:COMM?` `SER1`, `:DIAG:QUER:RESP?` repeating the previous answer, the position as nine parts, the tracking lists on their first line, the survey state `ONCE` and its progress `+1.8`, `*SRE?` `+136` | **Bench unit**, 2 Oct 2026, for all 71 queries in `Comparison.cs` |
| `:SYST:ERR?` with nothing queued: `+0,"No error"` | **Bench unit**, 2 Oct 2026 |
| The full log: a status line (`Log status: 222 entries (overwriting)`), a blank line, unquoted entries with two spaces after the time, two blank lines. A single entry: quoted, one space | **Bench unit**, 2 Oct 2026 |
| `:PTIM:TCOD?`, format T2 with its checksum, sent on the receiver's tick about 509 ms before the second | **Bench unit** (#37) |
| A survey refused with `-300` while a position is held; survey at power-up on | **Bench unit** (#229; 2 Oct 2026) |
| Timings: 15 to 50 ms for a query, 3.2 to 3.5 s for a screen, 14 s for the whole log, 0.9 s for a lamp write, 9.67 s for a position setter, 12.4 s and 11.6 s for the ALL and GPS tests | **Bench unit** (2 Oct 2026, §7.3, #440, #256, #53) |
| `*TST?` and the subsystem tests taking the receiver back to power-up | **Bench unit** for the subsystem tests (#53). `*TST?` was never run |
| The health failure: `[ Error ]` and `Err` | **Manual** (p. 3-18), never seen |
| `Holdover: manually initiated` | **Bench unit**, 2 Oct 2026, as the manual (p. 3-13) says |
| A time interval of a microsecond or more printed in `us` | **Manual** sample screen; never seen |
| `Holdover started, temporary` in the log | **Bench unit** wrote it; what causes it is not known, so the simulator never does |
| What the log writes for a forced holdover | **Guess**: `Holdover started, manually initiated` |
| The error queue's capacity, 30 | **Guess**. Five were read back as five; overflow to `-350` was seen but not counted |
| A holdover past 99 minutes; a day of the month below ten; satellites beyond twelve rows | **Guess** |
| How long each state lasts; the sky; the noise on the time interval and the control voltage | **Made up**, to be plausible |

## Comparing it with the bench unit

`--compare` sends the same read-only queries to a real receiver and to the simulator, then writes the
answers side by side:

```powershell
# Disconnect the application from the receiver first: only one program can hold the port.
dotnet run --project tools\SmartClockSimulator -- --compare COM3 --report compare.md
```

- **It only reads.** Its list is fixed in code and every entry is a query. It leaves out `*TST?` and
  `:DIAG:TEST?`, the two queries that act on the receiver, and a guard checks every line before it
  is sent. `ComparisonTests` pins both.
- **It compares shapes, not values.** The receiver and the simulator are in different states under
  different skies. Each answer is reduced to its form:
  - signs become `±`;
  - a run of integer digits becomes `#`;
  - decimal places and exponent digits are kept one `#` each.

  A difference in shape is then either a wrong guess here, or a keyword that differs with the state
  (`LOCK` against `HOLD`). The report shows both answers so a person can tell which.
- **It saves both status screens** beside the report. A new screen from the bench unit is also a
  candidate fixture.

Where the bench unit and the simulator disagree, **the bench unit wins**: fix the simulator, then
move that row of the table above.
