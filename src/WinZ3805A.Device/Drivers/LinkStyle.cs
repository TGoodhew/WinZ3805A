namespace WinZ3805A.Device.Drivers;

/// <summary>
/// How a receiver family's link carries answers (#310).
/// </summary>
/// <remarks>
/// <para>
/// The seam was built around a receiver that speaks only when spoken to (§7.2): a command goes
/// out, a reply comes back behind a prompt, and the transport discards anything unasked-for. An
/// NMEA 0183 talker inverts all of that — it speaks unprompted, at its own cadence, and is never
/// written to. The two are not variations of one protocol; they are two ways of obtaining an
/// answer, and the session has to know which one it is holding before it serves the first poll.
/// </para>
/// <para>
/// A driver states its style once. Everything else in the contract — the plan, the catalog, the
/// interpreters — keeps its meaning under both: a plan entry is <i>what to ask</i> on a
/// query/response link and <i>what to listen for</i> on a broadcast one.
/// </para>
/// </remarks>
public enum LinkStyle
{
    /// <summary>
    /// The receiver answers what it is asked, behind a prompt, and says nothing otherwise. The
    /// SmartClock family; the default for a driver that does not say.
    /// </summary>
    QueryResponse = 0,

    /// <summary>
    /// The receiver talks unprompted and is never written to. A plan entry names a kind of line to
    /// listen for, and an answer is the latest of what was heard — see
    /// <see cref="IReceiverDriver.ClassifyLine"/> and <see cref="IReceiverDriver.Overhear"/>.
    /// </summary>
    Broadcast,
}
