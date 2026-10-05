using System.Globalization;
using System.IO.Pipes;
using System.IO.Ports;
using System.Text;

using WinZ3805A.Simulation;

// The tutorial's receiver on the bench (#310). One NMEA 0183 cycle a second, to a serial port or
// to standard output, from power-up through a 2D fix to a 3D one. See README.md beside this file
// for the serial-port pair that lets the packaged application connect to it.

string? port = null;
string? pipeName = null;
int baud = 4800;
string talker = "GP";
int fixAfter = 20;
int threeDAfter = 40;
bool toStdout = false;
int outageAfter = 0;
int outageFor = 0;
string? extraTalker = null;
int extraFirstPrn = 65;
int spacingMs = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length:
            port = args[++i];
            break;
        case "--pipe-client" when i + 1 < args.Length:
            pipeName = args[++i];
            break;
        case "--baud" when i + 1 < args.Length:
            baud = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--talker" when i + 1 < args.Length:
            talker = args[++i];
            break;
        case "--fix-after" when i + 1 < args.Length:
            fixAfter = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--3d-after" when i + 1 < args.Length:
            threeDAfter = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--outage-after" when i + 1 < args.Length:
            outageAfter = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--outage-for" when i + 1 < args.Length:
            outageFor = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--extra-talker" when i + 1 < args.Length:
            extraTalker = args[++i];
            break;
        case "--extra-first-prn" when i + 1 < args.Length:
            extraFirstPrn = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--sentence-spacing-ms" when i + 1 < args.Length:
            spacingMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--stdout":
            toStdout = true;
            break;
        default:
            Console.Error.WriteLine(
                "usage: NmeaSimulator (--port COMn [--baud 4800] | --pipe-client <name> | --stdout) [--talker GP] " +
                "[--fix-after 20] [--3d-after 40] [--outage-after N --outage-for N] " +
                "[--extra-talker GL [--extra-first-prn 65]] [--sentence-spacing-ms 0]");
            return 2;
    }
}

if (port is null && pipeName is null && !toStdout)
{
    Console.Error.WriteLine(
        "usage: NmeaSimulator (--port COMn [--baud 4800] | --pipe-client <name> | --stdout) [--talker GP] " +
        "[--fix-after 20] [--3d-after 40] [--outage-after N --outage-for N] " +
        "[--extra-talker GL [--extra-first-prn 65]] [--sentence-spacing-ms 0]");
    return 2;
}

NmeaTalkerSimulator simulator = new(
    TimeProvider.System,
    talker,
    fixAfter: TimeSpan.FromSeconds(fixAfter),
    threeDimensionalAfter: TimeSpan.FromSeconds(threeDAfter),
    outages: outageFor > 0
        ? [new NmeaOutage(TimeSpan.FromSeconds(outageAfter), TimeSpan.FromSeconds(outageFor))]
        : null,
    extraConstellations: extraTalker is null
        ? null
        : [new NmeaConstellation(extraTalker, extraFirstPrn, Count: 6)],
    sentenceSpacing: TimeSpan.FromMilliseconds(spacingMs));

using CancellationTokenSource stopping = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

SerialPort? serial = null;
if (port is not null)
{
    serial = new SerialPort(port, baud, Parity.None, 8, StopBits.One)
    {
        Handshake = Handshake.None,
        NewLine = "\r\n",
        WriteTimeout = 2000,
    };
    serial.Open();
    Console.Error.WriteLine($"Talking on {port} at {baud}-8-N-1 as {talker}; fix after {fixAfter} s, 3D after {threeDAfter} s. Ctrl+C stops.");
}

// A named pipe something else serves - a VMware VM's serial port (#633) - as SmartClockSimulator's
// --pipe-client does: the talker writes into it, and reconnects if the VM goes away, as a cable
// plugged back in would. A VM's pipe carries no baud rate, so the guest may open the port at any.
NamedPipeClientStream? pipe = null;
if (pipeName is not null)
{
    Console.Error.WriteLine($@"Talking into \\.\pipe\{pipeName} as {talker}; fix after {fixAfter} s, 3D after {threeDAfter} s. Ctrl+C stops.");
}

try
{
    using PeriodicTimer tick = new(TimeSpan.FromSeconds(1));
    do
    {
        string cycle = simulator.NextCycleText();
        if (pipeName is not null)
        {
            try
            {
                if (pipe is null || !pipe.IsConnected)
                {
                    pipe?.Dispose();
                    pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(TimeSpan.FromSeconds(1), stopping.Token);
                    Console.Error.WriteLine("* connected");
                }

                byte[] bytes = Encoding.ASCII.GetBytes(cycle);
                await pipe.WriteAsync(bytes, stopping.Token);
                await pipe.FlushAsync(stopping.Token);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                // Not there yet, or gone: the next cycle tries again.
                pipe?.Dispose();
                pipe = null;
            }
        }
        else if (serial is not null)
        {
            serial.Write(cycle);
        }
        else
        {
            Console.Out.Write(cycle);
            Console.Out.Flush();
        }

        if (serial is not null || pipe is not null)
        {
            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss}  {simulator.Phase}, {simulator.SatellitesTracked} tracked, {simulator.SatellitesUsed} used");
        }
    }
    while (await tick.WaitForNextTickAsync(stopping.Token));
}
catch (OperationCanceledException)
{
    // Ctrl+C.
}
finally
{
    serial?.Dispose();
    pipe?.Dispose();
}

return 0;
