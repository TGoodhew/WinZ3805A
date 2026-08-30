using System.Buffers;
using System.IO.Pipelines;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WinZ3805A.Device.Drivers;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// The read side of a <see cref="LinkStyle.Broadcast"/> link: hears everything the talker sends,
/// sorts it by the driver's keys into cycles, and answers a plan entry with the latest of what it
/// heard (#310).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cycle, not a cache.</b> A talker's status is spread across sentences that arrive in a
/// burst once a second, and one kind of line — GSV, for satellites — is paged across several. The
/// latest line of a key is therefore not the latest <i>answer</i>: two of four GSV pages is half a
/// satellite table. So lines are grouped by cycle, delimited by the driver's discriminator (the
/// first entry of its fast tier, which a talker sends once per cycle), and a key is answered from
/// the last <i>complete</i> cycle. The answer is at most one cycle old, which at 1 Hz is inside the
/// poll interval.
/// </para>
/// <para>
/// <b>Silence is a timeout.</b> A query/response link times out when a reply does not come; a
/// broadcast link has no replies to wait for, so the equivalent is a talker that has stopped. An
/// answer asked for later than the driver's timeout after the last line heard is reported as
/// timed out, and the session's reconnect logic — three in a row — does the rest, exactly as it
/// would for a receiver that stopped answering.
/// </para>
/// <para>
/// One reader, for the same reason <see cref="LineProtocol"/> has one: the transport's
/// <see cref="PipeReader"/> has a single consumer. The session starts this after the connect
/// probe has finished with the pipe and stops it before the transport closes.
/// </para>
/// </remarks>
public sealed class BroadcastListener : IAsyncDisposable
{
    private const byte Lf = (byte)'\n';

    private readonly ITransport _transport;
    private readonly IReceiverDriver _driver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly string _discriminator;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();

    private List<(string Key, string Line)> _current = [];
    private List<(string Key, string Line)>? _last;
    private long? _lastHeardAt;
    private Task? _loop;
    private bool _ended;

    /// <summary>Creates a listener over an open transport for a broadcast driver.</summary>
    public BroadcastListener(ITransport transport, IReceiverDriver driver, TimeProvider timeProvider, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (driver.Plan.FastTier.Count == 0)
        {
            throw new ArgumentException("A broadcast driver's plan needs a first fast-tier entry: it is the cycle boundary.", nameof(driver));
        }

        _transport = transport;
        _driver = driver;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger.Instance;
        _discriminator = driver.Plan.FastTier[0];
    }

    /// <summary>How many complete cycles have been heard.</summary>
    public int CyclesHeard { get; private set; }

    /// <summary>Lines the driver did not claim — noise, another talker, a wrong baud rate.</summary>
    public int LinesDiscarded { get; private set; }

    /// <summary>Whether the transport closed under the listener.</summary>
    public bool Ended => _ended;

    /// <summary>
    /// Replays lines heard before the listener existed — what the connect probe absorbed while it
    /// was waiting for a prompt that never came.
    /// </summary>
    /// <remarks>
    /// The synchronise step consumes the talker's first seconds of sentences from the pipe, so a
    /// listener started afterwards would begin empty, and the first sweep would find nothing to
    /// answer with for up to a cycle. Those lines are real data the receiver sent; seeding them
    /// gives the first poll a complete cycle and starts the silence clock at connect rather than
    /// at the next sentence. Found by the end-to-end test failing one run in three: three empty
    /// answers in a row read as a talker that had stopped, and the session reconnected onto a
    /// link that was fine.
    /// </remarks>
    public void Seed(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        foreach (string line in lines)
        {
            Take(line);
        }
    }

    /// <summary>Starts hearing. Idempotent. A fresh listener is not stale until the silence timeout has passed.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _lastHeardAt ??= _timeProvider.GetTimestamp();
        }

        _loop ??= Task.Run(ReadLoopAsync, CancellationToken.None);
    }

    /// <summary>
    /// Answers a plan key from what has been heard, as the <see cref="Transaction"/> the poller
    /// expects from a query.
    /// </summary>
    /// <param name="key">A plan entry, or <see cref="PollPlan.WholeCycle"/>.</param>
    /// <param name="staleAfter">How long after the last line heard the talker counts as gone.</param>
    public Transaction Answer(string key, TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            bool gone = _ended
                || _lastHeardAt is not long heard
                || _timeProvider.GetElapsedTime(heard) > staleAfter;

            if (gone)
            {
                return new Transaction
                {
                    Command = key,
                    Outcome = _ended ? TransactionOutcome.Faulted : TransactionOutcome.TimedOut,
                    Lines = [],
                    EchoDiscarded = false,
                    Elapsed = TimeSpan.Zero,
                    Fault = _ended ? TransportFault.DeviceRemoved : TransportFault.None,
                    FaultMessage = _ended ? $"{_transport.Description} closed." : null,
                };
            }

            List<string> lines = [];
            if (key == PollPlan.WholeCycle)
            {
                foreach ((_, string line) in _last ?? _current)
                {
                    lines.Add(line);
                }
            }
            else if (key == _discriminator)
            {
                // The boundary line is complete the moment it arrives, and the newest one is the
                // freshest thing the talker has said.
                if (_current.Count > 0)
                {
                    lines.Add(_current[0].Line);
                }
                else if (_last is { Count: > 0 } last)
                {
                    lines.Add(last[0].Line);
                }
            }
            else
            {
                foreach ((string lineKey, string line) in _last ?? _current)
                {
                    if (lineKey == key)
                    {
                        lines.Add(line);
                    }
                }
            }

            return new Transaction
            {
                Command = key,
                Outcome = TransactionOutcome.Completed,
                Lines = lines,
                EchoDiscarded = false,
                Elapsed = TimeSpan.Zero,
            };
        }
    }

    /// <summary>Stops hearing and waits for the loop to end. The transport is not closed here.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            _transport.Input.CancelPendingRead();
        }
        catch (TransportException)
        {
            // Already closed; the loop has ended or will end on its own.
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
            {
                _logger.LogDebug(exception, "The broadcast listener ended faulted.");
            }
        }

        _stop.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        PipeReader reader;
        try
        {
            reader = _transport.Input;
        }
        catch (TransportException)
        {
            MarkEnded();
            return;
        }

        CancellationToken token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(token).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition consumed = buffer.Start;

                if (!result.IsCanceled)
                {
                    consumed = Hear(buffer);
                }

                reader.AdvanceTo(consumed, buffer.End);

                if (result.IsCanceled)
                {
                    break;
                }

                if (result.IsCompleted)
                {
                    MarkEnded();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by DisposeAsync.
        }
        catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
        {
            _logger.LogDebug(exception, "{Transport} failed under the broadcast listener.", _transport.Description);
            MarkEnded();
        }
    }

    private SequencePosition Hear(in ReadOnlySequence<byte> buffer)
    {
        SequenceReader<byte> reader = new(buffer);
        while (reader.TryReadTo(out ReadOnlySequence<byte> raw, Lf, advancePastDelimiter: true))
        {
            Take(Decode(raw).TrimEnd('\r'));
        }

        return reader.Position;
    }

    /// <summary>Files one heard line under its key, starting a new cycle at the boundary.</summary>
    private void Take(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        string? key;
        try
        {
            key = _driver.ClassifyLine(line);
        }
        catch (Exception exception)
        {
            // A classifier that throws is a driver bug; treating the line as unclaimed keeps the
            // talker heard rather than the session torn down, which is the same call the session
            // makes for a Recognises that throws.
            _logger.LogWarning(exception, "The {Family} driver's ClassifyLine threw; the line was discarded.", _driver.Family);
            key = null;
        }

        lock (_gate)
        {
            _lastHeardAt = _timeProvider.GetTimestamp();
            if (key is null)
            {
                LinesDiscarded++;
                return;
            }

            if (key == _discriminator)
            {
                if (_current.Count > 0)
                {
                    _last = _current;
                    CyclesHeard++;
                }

                _current = [(key, line)];
            }
            else
            {
                _current.Add((key, line));
            }
        }
    }

    private void MarkEnded()
    {
        lock (_gate)
        {
            _ended = true;
        }
    }

    private static string Decode(in ReadOnlySequence<byte> sequence) =>
        sequence.IsSingleSegment
            ? Encoding.Latin1.GetString(sequence.FirstSpan)
            : Encoding.Latin1.GetString(sequence.ToArray());
}
