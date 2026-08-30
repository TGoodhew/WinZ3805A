# NmeaSimulator

A GPS receiver made of sentences: one NMEA 0183 cycle a second — RMC, GGA, GSA, the GSV pages,
ZDA — from power-up through a 2D fix to a 3D one, with checksums, satellites that drift across
the sky and time that advances. It is the tutorial's receiver on the bench (#310,
[`docs/tutorial-nmea-driver.md`](../../docs/tutorial-nmea-driver.md)), so that every step of
[`docs/adding-a-receiver.md`](../../docs/adding-a-receiver.md) can be followed with nothing on
the desk.

It is not a particular product. There are no proprietary sentences, no lock or holdover state —
NMEA has none — and no serial quirks. When a real talker is captured, its behaviour is compared
against this; none has been yet (#309, the BG7TBL, was deferred because that unit puts no NMEA on
the port the application can reach).

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

`--stdout` is also the capture: `dotnet run --project tools\NmeaSimulator -- --stdout > cycles.txt`
for a minute gives a file in the shape a real talker's capture will take.
