using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace WinZ3805A.Simulation.SmartClock;

/// <summary>
/// Asks a real receiver and the simulator the same read-only questions and writes the answers side
/// by side (#639).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only reads, and only these.</b> The list below is fixed in code, every entry is a query, and the
/// two queries that act on the receiver (<c>*TST?</c> and <c>:DIAG:TEST?</c>, both tier C) are not on
/// it. <see cref="IsReadOnly"/> checks every line before it is sent, so an edit to the list that
/// added a setter would stop the run rather than send it. The connect sequence's <c>*CLS</c> is the
/// only other thing written, as the application writes it (§7.2).
/// </para>
/// <para>
/// <b>Shapes, not values.</b> The real receiver and the simulator are not in the same state and do
/// not have the same sky, so their numbers differ and should. What must agree is the form: the sign,
/// the decimals, the exponent's width, the leading space, the line structure, the prompt. Each answer
/// is reduced to a shape — every run of digits becomes <c>#</c>, every sign <c>±</c> — and the report
/// marks where the shapes part. A differing shape is either the simulator's guess being wrong or a
/// state difference (a keyword like <c>LOCK</c> against <c>HOLD</c>); the report shows both answers so
/// a person can tell which.
/// </para>
/// </remarks>
public static class Comparison
{
    /// <summary>Every read-only query the simulator answers, in an order that disturbs nothing.</summary>
    private static readonly string[] Queries =
    [
        "*IDN?", "*ESE?", "*SRE?", "*STB?",
        ":SYST:STAT:LENG?", ":SYST:DATE?", ":SYST:TIME?", ":SYST:COMM?",
        ":SYNC:STAT?", ":SYNC:TFOM?", ":SYNC:FFOM?", ":SYNC:TINT?",
        ":SYNC:HOLD:DUR?", ":SYNC:HOLD:DUR:THR?", ":SYNC:HOLD:DUR:THR:EXC?",
        ":SYNC:HOLD:TUNC:PRED?", ":SYNC:HOLD:TUNC:PRES?", ":SYNC:HOLD:WAIT?",
        ":GPS:REF:VAL?", ":GPS:REF:ADEL?",
        ":GPS:POS?", ":GPS:POS:ACT?", ":GPS:POS:HOLD:LAST?", ":GPS:POS:HOLD:STAT?",
        ":GPS:POS:SURV:PROG?", ":GPS:POS:SURV:STAT?", ":GPS:POS:SURV:STAT:POW?",
        ":GPS:SAT:TRAC?", ":GPS:SAT:TRAC:COUN?", ":GPS:SAT:TRAC:EMAN?",
        ":GPS:SAT:TRAC:IGN?", ":GPS:SAT:TRAC:IGN:COUN?", ":GPS:SAT:TRAC:IGN:STAT? 5",
        ":GPS:SAT:TRAC:INCL?", ":GPS:SAT:TRAC:INCL:COUN?", ":GPS:SAT:TRAC:INCL:STAT? 5",
        ":GPS:SAT:VIS:PRED?", ":GPS:SAT:VIS:PRED:COUN?",
        ":PTIM:TCOD?", ":PTIM:TCOD:FORM?", ":PTIM:DATE?", ":PTIM:TIME?", ":PTIM:TIME:STR?", ":PTIM:TZON?",
        ":PTIM:LEAP:ACC?", ":PTIM:LEAP:STAT?", ":PTIM:LEAP:DATE?", ":PTIM:LEAP:DUR?",
        ":LED:ALAR?", ":LED:GPSL?", ":LED:HOLD?", ":LED:ACT?", ":LED:ENAB?",
        ":DIAG:ROSC:EFC:REL?", ":DIAG:LIF:COUN?", ":DIAG:IDEN:GPS?", ":DIAG:QUER:RESP?",
        ":DIAG:LOG:COUN?", ":DIAG:LOG:READ? 1", ":DIAG:TEST:RES?",
        ":STAT:OPER:COND?", ":STAT:OPER:HARD:COND?", ":STAT:OPER:HOLD:COND?", ":STAT:OPER:POW:COND?", ":STAT:QUES:COND?",
        ":SYST:STAT?", ":DIAG:LOG:READ:ALL?",
        ":SYST:ERR?", ":SYST:ERR?", ":SYST:ERR?", ":SYST:ERR?",
    ];

    /// <summary>The queries a comparison sends, in order.</summary>
    public static IReadOnlyList<string> QueryList => Queries;

    /// <summary>Runs the comparison against a real receiver on <paramref name="portName"/>.</summary>
    public static int Run(string portName, int baud, string reportPath)
    {
        string? refused = Queries.FirstOrDefault(q => !IsReadOnly(q));
        if (refused is not null)
        {
            Console.Error.WriteLine($"Refusing to run: '{refused}' is not a read-only query.");
            return 2;
        }

        SimulatedReceiver simulated = new(TimeProvider.System);
        simulated.StartLocked();
        ScpiEngine engine = new(simulated, TimeProvider.System);
        engine.Receive("*CLS");

        using SerialPort port = new(portName, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 200,
            Encoding = Encoding.Latin1,
        };

        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"Could not open {portName}: {ex.Message}. Is the application still connected to it?");
            return 1;
        }

        // §7.2's connect sequence: absorb the banner, then spend the framing glitch on *CLS, twice.
        string banner = ReadUntilPrompt(port, TimeSpan.FromSeconds(3), out _);
        Exchange(port, "*CLS", TimeSpan.FromSeconds(2), out _);
        Exchange(port, "*CLS", TimeSpan.FromSeconds(2), out _);

        StringBuilder report = new();
        report.AppendLine("# Simulator against the bench receiver");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Taken {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz} on {portName} at {baud} baud by `SmartClockSimulator --compare`.");
        report.AppendLine("Values differ and should; the shapes are what is compared. `#` is a run of digits, `±` a sign, `⏎` a CRLF.");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Banner on opening the port: `{Escape(banner)}`");
        report.AppendLine();
        report.AppendLine("| Query | Same shape | Bench unit | Simulator | Bench ms |");
        report.AppendLine("|---|---|---|---|---|");

        int differ = 0;
        foreach (string query in Queries)
        {
            TimeSpan timeout = query is ":SYST:STAT?" ? TimeSpan.FromSeconds(15) : query.StartsWith(":DIAG:LOG:READ", StringComparison.Ordinal) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(3);
            string real = Exchange(port, query, timeout, out TimeSpan took);
            string sim = engine.Receive(query)?.Text ?? string.Empty;
            bool same = Shape(real) == Shape(sim);
            differ += same ? 0 : 1;
            Console.WriteLine($"{(same ? "same  " : "DIFFER")} {query}");

            if (query is ":SYST:STAT?" or ":DIAG:LOG:READ:ALL?")
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"| `{query}` | {(same ? "yes" : "**no**")} | {Lines(real)} | {Lines(sim)} | {took.TotalMilliseconds:0} |");
            }
            else
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"| `{query}` | {(same ? "yes" : "**no**")} | `{Escape(real)}` | `{Escape(sim)}` | {took.TotalMilliseconds:0} |");
            }

            if (query is ":SYST:STAT?")
            {
                File.WriteAllText(Path.ChangeExtension(reportPath, ".bench-screen.txt"), real, Encoding.Latin1);
                File.WriteAllText(Path.ChangeExtension(reportPath, ".simulator-screen.txt"), sim, Encoding.Latin1);
            }
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"{Queries.Length} queries, {differ} with differing shapes. The two screens are saved beside this report.");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"{differ} of {Queries.Length} differ. Report: {reportPath}");
        return 0;
    }

    /// <summary>A query, and not one of the two that make the receiver do something.</summary>
    public static bool IsReadOnly(string command)
    {
        string header = command.Split(' ')[0];
        return header.EndsWith('?')
            && !header.Equals("*TST?", StringComparison.OrdinalIgnoreCase)
            && !ScpiEngine.Matches(":DIAGnostic:TEST?", header);
    }

    /// <summary>
    /// An answer reduced to its form. Signs become <c>±</c>; a run of integer digits becomes one
    /// <c>#</c>, because a count of nine and a count of ten are the same form; but digits after a
    /// decimal point or in an exponent are kept one <c>#</c> each, because <c>E+002</c> against
    /// <c>E+02</c> is exactly the kind of difference worth finding.
    /// </summary>
    public static string Shape(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        StringBuilder shape = new(reply.Length);
        bool inDigits = false;
        bool counted = false;
        for (int i = 0; i < reply.Length; i++)
        {
            char c = reply[i];
            if (char.IsAsciiDigit(c))
            {
                if (!inDigits)
                {
                    // Counted when the run follows a decimal point, or an exponent's sign.
                    counted = i > 0 && (reply[i - 1] == '.' ||
                        (i > 1 && reply[i - 1] is '+' or '-' && reply[i - 2] is 'E' or 'e'));
                }

                if (counted || !inDigits)
                {
                    shape.Append('#');
                }

                inDigits = true;
                continue;
            }

            inDigits = false;
            shape.Append(c is '+' or '-' ? '±' : c);
        }

        return shape.ToString();
    }

    private static string Exchange(SerialPort port, string command, TimeSpan timeout, out TimeSpan took)
    {
        if (!IsReadOnly(command) && command != "*CLS")
        {
            throw new InvalidOperationException($"'{command}' is not a read-only query.");
        }

        port.DiscardInBuffer();
        long start = Environment.TickCount64;
        port.Write(command + "\r\n");
        string reply = ReadUntilPrompt(port, timeout, out _);
        took = TimeSpan.FromMilliseconds(Environment.TickCount64 - start);
        return reply;
    }

    /// <summary>Reads until a prompt ends the reply (§7.2's grammar) or the timeout passes.</summary>
    private static string ReadUntilPrompt(SerialPort port, TimeSpan timeout, out bool prompted)
    {
        StringBuilder text = new();
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        prompted = false;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                text.Append((char)port.ReadByte());
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (EndsWithPrompt(text))
            {
                prompted = true;
                break;
            }
        }

        return text.ToString();
    }

    private static bool EndsWithPrompt(StringBuilder text)
    {
        string tail = text.Length > 12 ? text.ToString(text.Length - 12, 12) : text.ToString();
        int line = Math.Max(tail.LastIndexOf('\n'), -1);
        string last = tail[(line + 1)..];
        return last.EndsWith("> ", StringComparison.Ordinal) &&
            (last.TrimStart().StartsWith("scpi", StringComparison.Ordinal) || last.TrimStart().StartsWith("E-", StringComparison.Ordinal));
    }

    private static string Escape(string text) =>
        text.Replace("\r\n", "⏎", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal);

    private static string Lines(string text)
    {
        string[] lines = text.Split("\r\n");
        return string.Create(CultureInfo.InvariantCulture, $"{lines.Length - 1} lines, widths {string.Join(" ", lines[..^1].Select(l => l.Length))}, then `{Escape(lines[^1])}`");
    }
}
