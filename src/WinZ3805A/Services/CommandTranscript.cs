using System.Collections.Concurrent;

using WinZ3805A.Device.Transport;

namespace WinZ3805A.Services;

/// <summary>What asked for a transaction, which is the only thing the transcript filters on.</summary>
public enum CommandOrigin
{
    /// <summary>A user action — a page's button, or the §10.11 console.</summary>
    User = 0,

    /// <summary>§7.3's poll timers. The traffic §10.11's toggle hides.</summary>
    Poll,

    /// <summary>
    /// The connect sequence: the <c>*IDN?</c> read on a query/response link, or what the
    /// synchronise step overheard on a broadcast one (#310). The synchronising write itself is not
    /// recorded.
    /// </summary>
    Session,
}

/// <summary>One transaction, as the §10.11 transcript shows it.</summary>
/// <param name="Ticks">When it completed, in UTC ticks from the session's <c>TimeProvider</c>.</param>
/// <param name="Origin">Who asked.</param>
/// <param name="Sent">The exact bytes' worth of text written, without its terminator.</param>
/// <param name="Received">The response lines, echo removed, exactly as the device sent them.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Elapsed">Wall time from the write to the prompt, the timeout or the fault.</param>
/// <param name="PromptStatus">The token the receiver put in the prompt, such as <c>E-230</c>.</param>
public sealed record TranscriptEntry(
    long Ticks,
    CommandOrigin Origin,
    string Sent,
    IReadOnlyList<string> Received,
    TransactionOutcome Outcome,
    TimeSpan Elapsed,
    string? PromptStatus);

/// <summary>
/// A bounded record of everything the session has put on the wire (§10.11).
/// </summary>
/// <remarks>
/// <para>
/// <b>It records, it does not send.</b> Nothing here can originate a command; it subscribes to a
/// session and keeps what already happened. That matters for §8.1 — a transcript is the one place
/// in the application where command text is handled as text rather than as an
/// <see cref="Device.Commands.ScpiCommand"/>, and it is write-only with respect to the wire.
/// </para>
/// <para>
/// <b>Always recording, whether or not the console is open.</b> §10.11 says the transcript "shows
/// all traffic including polling", and a buffer that only started when the page was opened would
/// show a user nothing about the transaction they came to investigate. The cost is bounded by
/// <see cref="Capacity"/> rather than by how long the application has been running.
/// </para>
/// <para>
/// <b>Written from the session's pump thread and read from the UI thread</b>, so the queue is a
/// concurrent one and <see cref="Snapshot"/> copies. A page redrawing a list while the poller
/// appends to it is not a rare interleaving here — it is once a second.
/// </para>
/// </remarks>
public sealed class CommandTranscript
{
    /// <summary>
    /// How many transactions are kept.
    /// </summary>
    /// <remarks>
    /// At §7.3's cadences the fast tier alone is six transactions a second, some 21 600 an hour,
    /// so this is roughly the last 80 to 100 seconds of polling, or far longer once poll traffic
    /// is filtered out of the view. Chosen against the full status screen, the largest entry at
    /// some 1 900 bytes: 500 of those is under a megabyte, and the fast tier's replies are a few
    /// bytes each.
    /// </remarks>
    public const int Capacity = 500;

    private readonly ConcurrentQueue<TranscriptEntry> _entries = new();

    /// <summary>Raised on every append and on <see cref="Clear"/>, on the caller's thread.</summary>
    public event EventHandler? Changed;

    /// <summary>How many transactions are held.</summary>
    public int Count => _entries.Count;

    /// <summary>Appends one transaction, discarding the oldest if the buffer is full.</summary>
    public void Add(TranscriptEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries.Enqueue(entry);

        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
            // Trimming to Capacity. The loop rather than a single dequeue because two threads may
            // both have just enqueued.
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Everything held, oldest first.</summary>
    /// <param name="includePolls">
    /// False to leave out <see cref="CommandOrigin.Poll"/> traffic, which is §10.11's toggle. The
    /// connect sequence stays either way: it is a handful of lines and it is what a user
    /// investigating a connection problem opened this page for.
    /// </param>
    public IReadOnlyList<TranscriptEntry> Snapshot(bool includePolls = true) =>
        includePolls
            ? [.. _entries]
            : [.. _entries.Where(entry => entry.Origin != CommandOrigin.Poll)];

    /// <summary>Discards everything held.</summary>
    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
            // Nothing per item.
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
