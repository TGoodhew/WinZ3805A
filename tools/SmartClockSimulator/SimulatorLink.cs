using System.Text;

namespace WinZ3805A.Simulation.SmartClock;

/// <summary>Splits the bytes a client sends into command lines.</summary>
/// <remarks>
/// CR, LF and CRLF all end a command on the bench unit, and none of them leaves anything behind
/// (§7.2), so either byte ends a line and an empty line is dropped rather than answered.
/// </remarks>
public sealed class LineAssembler
{
    private readonly StringBuilder _partial = new();

    /// <summary>Takes some bytes and returns every line they complete.</summary>
    public IReadOnlyList<string> Feed(ReadOnlySpan<byte> bytes)
    {
        List<string> lines = [];
        foreach (byte b in bytes)
        {
            if (b is (byte)'\r' or (byte)'\n')
            {
                if (_partial.Length > 0)
                {
                    lines.Add(_partial.ToString());
                    _partial.Clear();
                }

                continue;
            }

            _partial.Append((char)b);
        }

        return lines;
    }

    /// <summary>Forgets a half-received line, as a reconnect does.</summary>
    public void Reset() => _partial.Clear();
}

/// <summary>What the link does wrong, on purpose. Changed while running from the control channel.</summary>
public sealed class LinkFaults
{
    /// <summary>The receiver hears everything and answers nothing, as a dead TX line looks.</summary>
    public bool Silent { get; set; }

    /// <summary>Every reply is replaced by noise, as a mismatched baud rate looks.</summary>
    public bool Garbage { get; set; }

    /// <summary>The next reply stops halfway, with no prompt.</summary>
    public bool TruncateNext { get; set; }

    /// <summary>Added to every reply's latency.</summary>
    public TimeSpan ExtraLatency { get; set; }

    /// <summary>Set to drop the connection: the host closes the stream, as a pulled cable does.</summary>
    public bool DropRequested { get; set; }

    /// <summary>Back to a healthy link.</summary>
    public void Clear()
    {
        Silent = false;
        Garbage = false;
        TruncateNext = false;
        ExtraLatency = TimeSpan.Zero;
        DropRequested = false;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        List<string> on = [];
        if (Silent)
        {
            on.Add("silent");
        }

        if (Garbage)
        {
            on.Add("garbage");
        }

        if (TruncateNext)
        {
            on.Add("truncate-next");
        }

        if (ExtraLatency > TimeSpan.Zero)
        {
            on.Add($"latency +{ExtraLatency.TotalMilliseconds:0} ms");
        }

        return on.Count == 0 ? "none" : string.Join(", ", on);
    }
}

/// <summary>
/// Runs a <see cref="ScpiEngine"/> over a byte stream: a serial port, a named pipe, anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire time is reproduced, not just latency.</b> At 9600 baud a character takes about a
/// millisecond, so the bench unit's 1.9 kB status screen takes two seconds to arrive, and its whole
/// transaction measured 3521 ms (§7.3). A serial-port pair in software moves bytes instantly, so
/// without pacing every timeout in the application would go untested. <see cref="BaudRate"/> sets the
/// pace; zero turns it off.
/// </para>
/// <para>
/// One lock guards the engine and the receiver, because the control channel changes them from
/// another thread while a reply is being built.
/// </para>
/// </remarks>
public sealed class SimulatorLink(ScpiEngine engine, TimeProvider clock)
{
    private readonly LineAssembler _lines = new();
    private readonly Random _noise = new(9600);

    /// <summary>The lock the control channel takes before touching the engine or the receiver.</summary>
    public object Gate { get; } = new();

    /// <summary>The faults in force.</summary>
    public LinkFaults Faults { get; } = new();

    /// <summary>The line rate to pace output at, in bits per second. Zero sends at once.</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>Whether a new connection gets the banner and the framing glitch (§7.2).</summary>
    /// <remarks>
    /// Off when nothing can see the client open the port — a VM's serial port behind a named pipe
    /// carries no DTR — because a banner sent then is lost and a glitch armed then fires on the wrong
    /// command.
    /// </remarks>
    public bool Announce { get; set; } = true;

    /// <summary>Written for each command and reply, for a console to show.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Serves one connection until the client goes, the token fires, or a drop is requested.</summary>
    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _lines.Reset();

        if (Announce)
        {
            Reply banner;
            lock (Gate)
            {
                banner = engine.Connected();
            }

            await SendAsync(stream, banner, cancellationToken).ConfigureAwait(false);
        }

        byte[] buffer = new byte[256];
        using CancellationTokenSource dropped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task watchdog = WatchForDropAsync(dropped);

        try
        {
            while (!dropped.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, dropped.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                foreach (string line in _lines.Feed(buffer.AsSpan(0, read)))
                {
                    Trace?.Invoke("> " + line);
                    Reply? reply;
                    lock (Gate)
                    {
                        reply = engine.Receive(line);
                    }

                    if (reply is not null)
                    {
                        await SendAsync(stream, reply, dropped.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (dropped.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Trace?.Invoke("* connection dropped");
        }
        catch (IOException)
        {
            Trace?.Invoke("* client went away");
        }
        finally
        {
            await dropped.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
        }
    }

    private async Task WatchForDropAsync(CancellationTokenSource dropped)
    {
        try
        {
            while (!dropped.IsCancellationRequested)
            {
                if (Faults.DropRequested)
                {
                    Faults.DropRequested = false;
                    await dropped.CancelAsync().ConfigureAwait(false);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, dropped.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The connection ended some other way.
        }
    }

    private async Task SendAsync(Stream stream, Reply reply, CancellationToken cancellationToken)
    {
        if (Faults.Silent)
        {
            Trace?.Invoke("< (silent)");
            return;
        }

        TimeSpan delay = reply.Delay + Faults.ExtraLatency;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
        }

        byte[] bytes = Encoding.Latin1.GetBytes(reply.Text);
        if (Faults.Garbage)
        {
            _noise.NextBytes(bytes);
        }

        if (Faults.TruncateNext)
        {
            Faults.TruncateNext = false;
            bytes = bytes[..(bytes.Length / 2)];
        }

        Trace?.Invoke("< " + Summarise(reply.Text));
        await WritePacedAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task WritePacedAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        if (BaudRate <= 0)
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Ten bits a character (start, eight data, stop), sent in small bursts so a reader sees the
        // reply arrive over time rather than all at once at the end.
        const int Burst = 96;
        TimeSpan perBurst = TimeSpan.FromSeconds(Burst * 10.0 / BaudRate);
        for (int offset = 0; offset < bytes.Length; offset += Burst)
        {
            int count = Math.Min(Burst, bytes.Length - offset);
            await stream.WriteAsync(bytes.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(perBurst * count / Burst, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Summarise(string text)
    {
        string oneLine = text.Replace("\r\n", "⏎", StringComparison.Ordinal);
        return oneLine.Length <= 100 ? oneLine : oneLine[..100] + $"… ({text.Length} chars)";
    }
}
