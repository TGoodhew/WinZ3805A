using System.Globalization;
using System.IO.Ports;

using WinZ3805A.Device.Drivers.Uccm;

namespace WinZ3805A.Simulation;

/// <summary>
/// Runs <see cref="UccmModuleSimulator"/> against a serial port, or to stdout for a look.
/// </summary>
/// <remarks>
/// See the README beside this file. Point it at one end of a com0com pair and the packaged
/// application at the other, exactly as the NMEA simulator is used.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? port = Option(args, "--port");
        UccmVendor vendor = Enum.TryParse(Option(args, "--vendor"), ignoreCase: true, out UccmVendor v)
            ? v
            : UccmVendor.Symmetricom;
        UccmVariant variant = Enum.TryParse(Option(args, "--variant"), ignoreCase: true, out UccmVariant t)
            ? t
            : UccmVariant.Uccm;
        UccmSimulatedState state =
            Enum.TryParse(Option(args, "--state"), ignoreCase: true, out UccmSimulatedState s)
                ? s
                : UccmSimulatedState.Locked;

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(
                """
                UccmSimulator - a UCCM module that is not a UCCM module.

                  --port <name>       serial port to answer on (omit to write to stdout)
                  --baud <rate>       default 9600
                  --vendor <name>     Symmetricom | Trimble        (default Symmetricom)
                  --variant <name>    Uccm | UccmP                 (default Uccm)
                  --state <name>      PowerUp | Settling | Locked | Holdover  (default Locked)
                  --interleave        put an unsolicited time code inside every reply

                IT REPRODUCES A BELIEF, NOT A RECEIVER. Every behaviour is read out of Lady
                Heather's source rather than seen on a wire, so a disagreement between this and
                real hardware is a finding about BOTH.
                """);
            return 0;
        }

        UccmModuleSimulator module = new(vendor, variant, TimeProvider.System)
        {
            State = state,
            InterleaveTimeCode = args.Contains("--interleave"),
        };

        return port is null ? Demonstrate(module) : Serve(module, port, Baud(args));
    }

    /// <summary>Writes one of everything to stdout, so the shapes can be eyeballed.</summary>
    private static int Demonstrate(UccmModuleSimulator module)
    {
        Console.WriteLine($"# {module.Vendor} {module.Variant}, {module.State}");
        Console.WriteLine();
        Console.WriteLine("# unsolicited time code");
        Console.WriteLine(module.TimeCodeLine());

        foreach (string command in new[]
        {
            "*IDN?", UccmCommands.LockLed, UccmCommands.TimeInterval, UccmCommands.EfcRelative,
            UccmCommands.Loop, UccmCommands.HoldoverDuration, UccmCommands.Status,
        })
        {
            Console.WriteLine();
            Console.WriteLine($"# {command}");
            Console.Write(module.Respond(command));
        }

        return 0;
    }

    /// <summary>
    /// Answers on a serial port until stopped, emitting a time code once a second in between.
    /// </summary>
    private static int Serve(UccmModuleSimulator module, string port, int baud)
    {
        using SerialPort serial = new(port, baud, Parity.None, 8, StopBits.One)
        {
            NewLine = "\r\n",
            ReadTimeout = 200,
        };

        try
        {
            serial.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"Could not open {port}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"{module.Vendor} {module.Variant} on {port} at {baud}. Ctrl+C to stop.");

        DateTimeOffset nextTick = DateTimeOffset.UtcNow;
        while (true)
        {
            try
            {
                string line = serial.ReadLine().Trim();
                if (line.Length > 0)
                {
                    serial.Write(module.Respond(line));
                }
            }
            catch (TimeoutException)
            {
                // Nothing asked. That is the normal case, and the module still talks.
            }

            if (DateTimeOffset.UtcNow >= nextTick)
            {
                serial.Write(module.TimeCodeLine() + "\r\n");
                nextTick = DateTimeOffset.UtcNow.AddSeconds(1);
            }
        }
    }

    private static int Baud(string[] args) =>
        int.TryParse(Option(args, "--baud"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int baud)
            ? baud
            : 9600;

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
