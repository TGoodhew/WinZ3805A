# NmeaSimulator

A GPS receiver made of sentences: one NMEA 0183 cycle a second — RMC, GGA, GSA, the GSV pages,
ZDA — from power-up through a 2D fix to a 3D one, with checksums, satellites that drift across
the sky and time that advances. It is the tutorial's receiver on the bench (#310,
[`docs/tutorial-nmea-driver.md`](../../docs/tutorial-nmea-driver.md)), so that every step of
[`docs/adding-a-receiver.md`](../../docs/adding-a-receiver.md) can be followed with nothing on
the desk.

It is not a particular product. There are no proprietary sentences, no lock or holdover state —
NMEA has none — and no serial quirks. A real talker was captured on 7 Sep 2026 — a VK-162, under
[`tests/WinZ3805A.Tests/Nmea/Captures/`](../../tests/WinZ3805A.Tests/Nmea/Captures/) (#420) — and
what it does that this does not is recorded there and in
[`docs/tutorial-nmea-driver.md`](../../docs/tutorial-nmea-driver.md).

## In-process

The test project references this project and drives `NmeaTalkerSimulator` with a
`FakeTimeProvider`, feeding `NextCycleText()` into a `FakeTransport` with `Silent = true`,
`EchoCommands = false`, `EmitPrompt = false` and `WaitForReaderToConsume = true` — the last is
what keeps the emits from outpacing the listener (the tutorial's finding 8). That runs the real
session, the real listener and the real driver with no port at all — `NmeaSessionTests.Bench`
under `tests/WinZ3805A.Tests/Nmea/` is the bench to copy.

## Over a serial-port pair

The packaged application connects to a port, so the simulator needs one to talk into. Two ways:

- **A virtual pair.** [com0com](https://com0com.sourceforge.net/) creates a linked pair such as
  `COM7`⇄`COM8`; the simulator talks into one and the application connects to the other. Install
  it once (it is a signed kernel driver and asks for elevation), then in its setup command:
  `install PortName=COM7 PortName=COM8`.
- **Two USB-serial adapters** with their TX and RX crossed, and a common ground. Slower to set up,
  but real wire.

Then:

```powershell
dotnet run --project tools\NmeaSimulator -- --port COM7 --baud 4800
```

and in the application choose the other port with **Auto-detect settings**, or **Manual** at
4800-8-N-1. The application listens for the talker, recognises it by its sentences, and never
sends it a command — the connect sequence's one `*CLS` write, which a talker ignores, goes out
before recognition. One of `--port` or `--stdout` is required (the program prints its usage and
exits otherwise); in port mode a per-second phase / tracked / used line goes to standard error,
which is useful for comparing with what the application shows. Options:

| Option | Default | Meaning |
|---|---|---|
| `--port COMn` | — | The port to talk into |
| `--baud n` | `4800` | The standard's rate; most modules actually ship at `9600` |
| `--talker GP` | `GP` | The talker identifier — `GN` for a multi-constellation receiver |
| `--fix-after n` | `20` | Seconds after start until the first (2D) fix |
| `--3d-after n` | `40` | Seconds after start until the fix is 3D |
| `--stdout` | — | Write the sentences to standard output instead of a port, to see them or to capture a file |
| `--outage-after n --outage-for n` | — | Take the fix away `n` seconds after start, for `n` seconds |
| `--extra-talker XX` | — | A second constellation running its own GSV cycle — `GL`, `GA`, `GB`, `GQ` |
| `--extra-first-prn n` | `65` | That constellation's first satellite number |
| `--sentence-spacing-ms n` | `0` | Time between one sentence of a cycle and the next |

## The three cases added for #420 stage 1

These are the ordinary things a generator *can* anticipate, and they were all missing.

**A fix being taken away.** The phases only ever moved forward — power-up, 2D, 3D — so a fix had
never been *lost* in any test. A receiver that loses its fix and regains it is ordinary behaviour:
an antenna knocked, a van parked alongside, a building passed. Reacquisition is modelled as a hot
start — the fix returns as 2D for a few seconds, then 3D — because a receiver that has just lost
its fix still has almanac and ephemeris, and snapping straight back to 3D would let a driver pass
without ever seeing a downgrade.

```powershell
dotnet run --project tools\NmeaSimulator -- --stdout --fix-after 5 --3d-after 10 --outage-after 30 --outage-for 20
```

**Two constellations at once.** A multi-constellation receiver runs a *separate GSV cycle per
constellation*, each with its own page numbering, interleaved in the same second. Every talker this
project had tested against was one of two, so the interleaving had never been produced — and it
turned out to break the parser's page accounting (#417).

```powershell
dotnet run --project tools\NmeaSimulator -- --stdout --talker GN --extra-talker GL
```

Setting `--extra-first-prn` into another constellation's range reproduces #424's collision on
purpose.

**A cycle straddling midnight.** A real talker takes tens of milliseconds to put a cycle on the
wire, so a cycle beginning just before midnight ends after it and the sentences carrying the date
disagree with the sentences carrying the time. `--sentence-spacing-ms` is what makes that
reachable; it found a genuine 24-hour error in the parser (#420).

```powershell
dotnet run --project tools\NmeaSimulator -- --stdout --sentence-spacing-ms 200
```

Note that spacing only shows up across a second boundary: the seconds field is emitted with a
hard-coded `.00` fraction, so sub-second timing is not otherwise visible.

`--stdout` is also the capture: `dotnet run --project tools\NmeaSimulator -- --stdout > cycles.txt`
for a minute gives a file in the shape a real talker's capture will take.
