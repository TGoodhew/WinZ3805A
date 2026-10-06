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

    /// <summary>
    /// Set to put the link mid-reply (#707): a status screen's lines trickle out with no prompt
    /// until the next command arrives, and that reply then ends - one more line and its prompt -
    /// before the command is answered.
    /// </summary>
    /// <remarks>
    /// What an application restarted while the receiver was partway through a long reply sees: the
    /// port opens into the tail, its listen hears lines and no prompt, and the first command's read
    /// gets the rest of the tail and the tail's prompt before its own. The trickle stands in for a
    /// tail that outlasts the listen, which on the wire depends on when the port happened to open;
    /// one that ends within the listen is the ordinary case and needs no fault. One-shot.
    /// </remarks>
    public bool MidReplyRequested { get; set; }

    /// <summary>Back to a healthy link.</summary>
    public void Clear()
    {
        Silent = false;
        Garbage = false;
        TruncateNext = false;
        ExtraLatency = TimeSpan.Zero;
        DropRequested = false;
        MidReplyRequested = false;
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

        if (MidReplyRequested)
        {
            on.Add("mid-reply");
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

    // The mid-reply fault's state: the reply being trickled, the next line of it, and the trickle.
    // Started by the fault watcher and ended by the read loop, so guarded by its own lock.
    private readonly Lock _staleGate = new();
    private string[] _staleLines = [];
    private int _staleNext;
    private Task? _stale;
    private CancellationTokenSource? _staleStop;

    /// <summary>How long the mid-reply fault waits between the lines it trickles.</summary>
    /// <remarks>
    /// Several lines inside the application's two-second listen, so it hears a reply under way and
    /// no prompt.
    /// </remarks>
    public TimeSpan StaleLineInterval { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The lock the control channel takes before touching the engine or the receiver.</summary>
    public object Gate { get; } = new();

    /// <summary>The faults in force.</summary>
    public LinkFaults Faults { get; } = new();

    /// <summary>The line rate to pace output at, in bits per second. Zero sends at once.</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>Whether a new connection gets the banner and the framing glitch (§7.2).</summary>
    /// <remarks>
    /// <para>
    /// Off by default. §7.2 records both on the bench unit on 21 Aug 2026, but the comparison on
    /// 2 Oct 2026 opened the port with DTR asserted, and then raised DTR on an open port, and saw
    /// neither: no banner in five seconds, and the first <c>*CLS</c> answered cleanly. Turn it on to
    /// show a client the behaviour §7.2 describes, which the application is built to survive.
    /// </para>
    /// <para>
    /// Leave it off where nothing can see the client open the port, too: a VM's serial port behind a
    /// named pipe carries no DTR, so a banner sent on connecting is lost and a glitch armed then
    /// fires on the wrong command.
    /// </para>
    /// </remarks>
    public bool Announce { get; set; }

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
        Task watchdog = WatchFaultsAsync(stream, dropped);

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

                    // A command while a reply is still going out is answered after it, as the
                    // receiver works through its input in order.
                    await EndStaleReplyAsync(stream, dropped.Token).ConfigureAwait(false);
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
            await StopStaleReplyAsync().ConfigureAwait(false);
        }
    }

    private async Task WatchFaultsAsync(Stream stream, CancellationTokenSource dropped)
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

                if (Faults.MidReplyRequested)
                {
                    Faults.MidReplyRequested = false;
                    StartStaleReply(stream, dropped.Token);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, dropped.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The connection ended some other way.
        }
    }

    private void StartStaleReply(Stream stream, CancellationToken connection)
    {
        string screen;
        lock (Gate)
        {
            screen = engine.StatusScreen();
        }

        lock (_staleGate)
        {
            if (_stale is not null)
            {
                return;
            }

            _staleLines = screen.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            _staleNext = 0;
            _staleStop = CancellationTokenSource.CreateLinkedTokenSource(connection);
            _stale = TrickleStaleReplyAsync(stream, _staleStop.Token, connection);
        }

        Trace?.Invoke("* mid-reply: a status screen trickles out with no prompt until the next command");
    }

    // Line by line, round the screen, never the prompt. Each line is written whole - the stop is
    // only heard between lines - so the reply's end picks up at a line boundary.
    private async Task TrickleStaleReplyAsync(Stream stream, CancellationToken stop, CancellationToken connection)
    {
        try
        {
            while (true)
            {
                string line = _staleLines[_staleNext % _staleLines.Length];
                await WritePacedAsync(stream, Encoding.Latin1.GetBytes(line + "\r\n"), connection).ConfigureAwait(false);
                _staleNext++;
                await Task.Delay(StaleLineInterval, clock, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Ended: by a command, or by the connection going.
        }
    }

    // The reply the port opened into, ended: its next line and its prompt. Nothing if none is going.
    private async Task EndStaleReplyAsync(Stream stream, CancellationToken connection)
    {
        if (!await StopStaleReplyAsync().ConfigureAwait(false))
        {
            return;
        }

        string prompt;
        lock (Gate)
        {
            prompt = engine.Prompt;
        }

        string end = _staleLines[_staleNext % _staleLines.Length] + "\r\n" + prompt;
        Trace?.Invoke("< (the reply under way ends) " + Summarise(end));
        await WritePacedAsync(stream, Encoding.Latin1.GetBytes(end), connection).ConfigureAwait(false);
    }

    private async Task<bool> StopStaleReplyAsync()
    {
        Task? stale;
        CancellationTokenSource? stop;
        lock (_staleGate)
        {
            (stale, stop) = (_stale, _staleStop);
            (_stale, _staleStop) = (null, null);
        }

        if (stale is null || stop is null)
        {
            return false;
        }

        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await stale.ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The client went while a line was going out; the read loop hears that itself.
        }
        finally
        {
            stop.Dispose();
        }

        return true;
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
