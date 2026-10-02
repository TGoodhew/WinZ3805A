using System.Globalization;
using System.IO.Pipes;
using System.IO.Ports;
using System.Text;

namespace WinZ3805A.Simulation.SmartClock;

/// <summary>
/// Runs the simulated Z3805A on a serial port, a named pipe, or to standard output for a look.
/// </summary>
/// <remarks>See the README beside this file.</remarks>
internal static class Program
{
    private const string Usage =
        """
        SmartClockSimulator - a Z3805A that is not a Z3805A (#639).

        Where it answers (one of):
          --port <COMn>          a serial port, e.g. one end of a com0com pair
          --pipe <name>          a named pipe it serves (\\.\pipe\<name>)
          --pipe-client <name>   a named pipe something else serves, e.g. a VMware VM's serial port
          --stdout               print one answer to every catalogued query, then exit
          --compare <COMn>       ask a REAL receiver and the simulator the same read-only
                                 queries; write the report to --report (default compare.md)
          --watch <COMn>         ask a REAL receiver the same read-only queries over and over
                                 while a person changes its state; keeps every reply and each new
                                 screen in --out (default watch), for --minutes (default 120)

        How it behaves:
          --start powerup|locked how it starts (default powerup)
          --speed <factor>       run the timeline faster; the reported clock stays real (default 1)
          --baud <rate>          the pace replies are sent at; 0 for instant (default 9600)
          --echo                 echo commands, as FDUPLEX ON does (the bench unit does not)
          --leading-space        a space before each value, as §7.2 records (the bench unit has none)
          --announce             the banner and framing glitch §7.2 records on connecting
          --control <name>       also take control commands on \\.\pipe\<name>
          --seed <n>             the noise seed (default 3625)

        Control commands are read from standard input (and the control pipe). Type help.

        IT REPRODUCES THE BENCH UNIT WHERE THE BENCH UNIT HAS BEEN SEEN, AND THE MANUAL OR A
        LABELLED GUESS WHERE IT HAS NOT. The README lists which is which.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        Console.OutputEncoding = Encoding.UTF8;
        TimeProvider clock = TimeProvider.System;
        SimulatedReceiver receiver = new(clock, Integer(args, "--seed") ?? 3625)
        {
            Speed = Option(args, "--speed") is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed) && speed > 0 ? speed : 1,
        };
        ScpiEngine engine = new(receiver, clock)
        {
            Echo = args.Contains("--echo"),
            LeadingSpace = args.Contains("--leading-space"),
        };
        SeedLog(engine, receiver);

        if (Option(args, "--start") is "locked")
        {
            receiver.StartLocked();
        }

        SimulatorLink link = new(engine, clock)
        {
            BaudRate = Integer(args, "--baud") ?? 9600,
            Announce = args.Contains("--announce"),
            Trace = line => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}")),
        };

        if (args.Contains("--stdout"))
        {
            return Demonstrate(engine);
        }

        if (Option(args, "--watch") is string watched)
        {
            TimeSpan duration = TimeSpan.FromMinutes(Integer(args, "--minutes") ?? 120);
            return Comparison.Watch(watched, Integer(args, "--baud") ?? 9600, Option(args, "--out") ?? "watch", duration);
        }

        if (Option(args, "--compare") is string real)
        {
            return Comparison.Run(real, Integer(args, "--baud") ?? 9600, Option(args, "--report") ?? "compare.md");
        }

        using CancellationTokenSource stop = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        _ = Task.Run(() => ReadConsole(receiver, engine, link, stop));
        if (Option(args, "--control") is string control)
        {
            _ = Task.Run(() => ServeControlAsync(control, receiver, engine, link, stop.Token));
        }

        try
        {
            if (Option(args, "--port") is string port)
            {
                return await ServePortAsync(link, port, Integer(args, "--baud") ?? 9600, stop.Token);
            }

            if (Option(args, "--pipe") is string pipe)
            {
                return await ServePipeAsync(link, pipe, stop.Token);
            }

            if (Option(args, "--pipe-client") is string target)
            {
                return await ConnectPipeAsync(link, target, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }

        Console.Error.WriteLine("Say where to answer: --port, --pipe, --pipe-client or --stdout.");
        return 1;
    }

    /// <summary>A full log, as the bench unit's is, so the Diagnostics page reads what it reads on hardware.</summary>
    private static void SeedLog(ScpiEngine engine, SimulatedReceiver receiver) =>
        engine.Log.FillLikeTheBenchUnit(receiver.ReportedUtc.AddHours(-1));

    /// <summary>Prints every catalogued query's answer, so the shapes can be eyeballed.</summary>
    private static int Demonstrate(ScpiEngine engine)
    {
        foreach (string command in new[]
        {
            "*CLS", "*IDN?", ":SYNC:STAT?", ":SYNC:TFOM?", ":SYNC:FFOM?", ":SYNC:TINT?", ":DIAG:ROSC:EFC:REL?",
            ":GPS:SAT:TRAC:COUN?", ":PTIM:TIME?", ":SYNC:HOLD:DUR?", ":GPS:REF:ADEL?", ":SYST:DATE?", ":SYST:TIME?",
            ":PTIM:DATE?", ":PTIM:TCOD:FORM?", ":PTIM:LEAP:ACC?", ":PTIM:LEAP:STAT?", ":PTIM:LEAP:DATE?",
            ":GPS:SAT:TRAC:IGN?", ":GPS:SAT:TRAC:INCL?", ":DIAG:IDEN:GPS?", ":DIAG:LOG:READ:ALL?", ":SYST:ERR?",
            ":SYST:ERR?", ":SYST:STAT?",
        })
        {
            Console.WriteLine($"> {command}");
            Console.WriteLine(engine.Receive(command)?.Text.Replace("\r\n", "\n", StringComparison.Ordinal));
            Console.WriteLine();
        }

        return 0;
    }

    private static async Task ReadConsole(SimulatedReceiver receiver, ScpiEngine engine, SimulatorLink link, CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            string? line = await Console.In.ReadLineAsync(stop.Token);
            if (line is null)
            {
                // Standard input closed: run on, controlled only by the pipe if there is one.
                return;
            }

            if (line.Trim() is "quit" or "exit")
            {
                await stop.CancelAsync();
                return;
            }

            string answer = ControlCommands.Apply(line, receiver, engine, link);
            if (answer.Length > 0)
            {
                Console.WriteLine(answer);
            }
        }
    }

    /// <summary>One control client at a time, a line in and a line out.</summary>
    private static async Task ServeControlAsync(string name, SimulatedReceiver receiver, ScpiEngine engine, SimulatorLink link, CancellationToken stop)
    {
        // Everything for one client sits inside the try, disposal included. The first version caught
        // only the reading loop, so a client that hung up after its answer made the writer's flush on
        // disposal throw outside it, the task faulted with nobody watching, and the control pipe
        // stopped accepting after its second command (the QA pass, 2 Oct 2026). Any number of
        // instances is allowed so the next one can be created before the last is fully released.
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream pipe = new(
                    name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(stop);
                using StreamReader reader = new(pipe, Encoding.ASCII, leaveOpen: true);
                StreamWriter writer = new(pipe, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                while (await reader.ReadLineAsync(stop) is string line)
                {
                    string answer = ControlCommands.Apply(line, receiver, engine, link);
                    Console.WriteLine($"[control] {line} -> {answer}");
                    await writer.WriteLineAsync(answer.ReplaceLineEndings(" | "));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The client went mid-conversation. Say so, and wait for the next one.
                Console.WriteLine($"[control] client went: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Serves a serial port. The banner goes out when the client asserts DTR, which a com0com pair
    /// shows this end as DSR.
    /// </summary>
    private static async Task<int> ServePortAsync(SimulatorLink link, string port, int baud, CancellationToken stop)
    {
        using SerialPort serial = new(port, baud <= 0 ? 9600 : baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
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

        Console.WriteLine($"Z3805A on {port}. Waiting for the other end to open its port. Ctrl+C to stop.");
        while (!stop.IsCancellationRequested)
        {
            // Wait for the far end to open (DSR up), serve until it closes (DSR down) or a drop.
            while (!serial.DsrHolding)
            {
                await Task.Delay(100, stop);
            }

            Console.WriteLine("* the other end opened its port");
            using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Task watch = Task.Run(async () =>
            {
                while (serial.DsrHolding && !session.IsCancellationRequested)
                {
                    await Task.Delay(100, CancellationToken.None);
                }

                await session.CancelAsync();
            }, CancellationToken.None);

            try
            {
                await link.RunAsync(serial.BaseStream, session.Token);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                Console.WriteLine("* the other end closed its port");
            }

            await session.CancelAsync();
            await watch;

            // After a requested drop the far end may still hold DTR; wait for it to let go, as it
            // will once it notices the receiver has stopped answering and reopens.
            while (serial.DsrHolding && !stop.IsCancellationRequested)
            {
                await Task.Delay(100, stop);
            }
        }

        return 0;
    }

    private static async Task<int> ServePipeAsync(SimulatorLink link, string name, CancellationToken stop)
    {
        Console.WriteLine($@"Z3805A on \\.\pipe\{name}. Ctrl+C to stop.");
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream pipe = new(
                    name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(stop);
                Console.WriteLine("* client connected");
                await link.RunAsync(pipe, stop);
            }
            catch (IOException ex)
            {
                // A client leaving can make the pipe's disposal throw; serve the next one regardless.
                Console.WriteLine($"* client went: {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Connects to a pipe a VM serves. A VM's serial port carries no DTR through the pipe, so the
    /// guest opening its port is invisible here; the banner and the glitch are off unless asked for.
    /// </summary>
    private static async Task<int> ConnectPipeAsync(SimulatorLink link, string name, CancellationToken stop)
    {
        Console.WriteLine($@"Z3805A connecting to \\.\pipe\{name}. Ctrl+C to stop.");
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(TimeSpan.FromSeconds(5), stop);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                Console.WriteLine("* connected");
                await link.RunAsync(pipe, stop);
            }
            catch (IOException ex)
            {
                // The VM went, or its pipe was busy; try again, as a cable plugged back in would.
                Console.WriteLine($"* pipe went: {ex.Message}");
            }

            await Task.Delay(1000, stop);
        }

        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static int? Integer(string[] args, string name) =>
        int.TryParse(Option(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}
