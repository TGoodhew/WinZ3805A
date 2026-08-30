using System.Globalization;
using System.IO.Ports;

using WinZ3805A.Simulation;

// The tutorial's receiver on the bench (#310). One NMEA 0183 cycle a second, to a serial port or
// to standard output, from power-up through a 2D fix to a 3D one. See README.md beside this file
// for the serial-port pair that lets the packaged application connect to it.

string? port = null;
int baud = 4800;
string talker = "GP";
int fixAfter = 20;
int threeDAfter = 40;
bool toStdout = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length:
            port = args[++i];
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
        case "--stdout":
            toStdout = true;
            break;
        default:
            Console.Error.WriteLine("usage: NmeaSimulator (--port COMn [--baud 4800] | --stdout) [--talker GP] [--fix-after 20] [--3d-after 40]");
            return 2;
    }
}

if (port is null && !toStdout)
{
    Console.Error.WriteLine("usage: NmeaSimulator (--port COMn [--baud 4800] | --stdout) [--talker GP] [--fix-after 20] [--3d-after 40]");
    return 2;
}

NmeaTalkerSimulator simulator = new(
    TimeProvider.System,
    talker,
    fixAfter: TimeSpan.FromSeconds(fixAfter),
    threeDimensionalAfter: TimeSpan.FromSeconds(threeDAfter));

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

try
{
    using PeriodicTimer tick = new(TimeSpan.FromSeconds(1));
    do
    {
        string cycle = simulator.NextCycleText();
        if (serial is not null)
        {
            serial.Write(cycle);
        }
        else
        {
            Console.Out.Write(cycle);
            Console.Out.Flush();
        }

        if (serial is not null)
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
}

return 0;
