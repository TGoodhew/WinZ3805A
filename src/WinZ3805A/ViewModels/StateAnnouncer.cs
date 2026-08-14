using WinZ3805A.Controls;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>How loudly a screen reader should interrupt for an announcement.</summary>
public enum AnnouncementUrgency
{
    /// <summary>Queued behind whatever the reader is saying.</summary>
    Polite = 0,

    /// <summary>Said at once, cutting in.</summary>
    Assertive,
}

/// <summary>One thing worth saying out loud, and how urgently.</summary>
/// <param name="Text">The sentence to announce.</param>
/// <param name="Urgency">Whether it may wait its turn.</param>
public sealed record Announcement(string Text, AnnouncementUrgency Urgency);

/// <summary>
/// Decides what a screen reader is told when the receiver's state changes (A11Y-9).
/// </summary>
/// <remarks>
/// <para>
/// A11Y-9 requires mode changes, connection changes and command results to reach a user who is not
/// looking at the window. Command results already have a surface of their own — the §9.11
/// <c>CommandOutcomeBar</c> announces itself. What is left is the pair the main window carries, and
/// the whole of the judgement is <i>which transitions are worth interrupting for</i>. That is
/// testable arithmetic over two enums, so it lives here rather than in the page.
/// </para>
/// <para>
/// The first observation is silent. A window that has just opened is being read by its user
/// already, and announcing the state it opened in would talk over that reading rather than add to
/// it — a live region reports <i>changes</i>, and there has not been one yet.
/// </para>
/// <para>
/// One announcement per observation, never a queue. Two things can change on the same poll — the
/// link comes back and the mode is no longer Disconnected — and reading both would make the user
/// wait through the one they can infer to hear the one they cannot. Connection wins, because a mode
/// is only meaningful while there is a link to have read it over.
/// </para>
/// </remarks>
public sealed class StateAnnouncer
{
    private ConnectionStatus? _connection;
    private ReceiverMode _mode = ReceiverMode.Disconnected;
    private bool _coasting;

    /// <summary>
    /// Takes the current state and returns what to say about it, or <see langword="null"/> when
    /// nothing has changed that a listener needs to hear.
    /// </summary>
    /// <param name="connection">Where the session stands.</param>
    /// <param name="mode">The mode the medallion is showing.</param>
    /// <param name="isCoasting">Whether the receiver claims lock while tracking nothing.</param>
    /// <param name="portName">The port in use, named in the connected announcement when there is one.</param>
    public Announcement? Observe(
        ConnectionStatus connection,
        ReceiverMode mode,
        bool isCoasting,
        string? portName = null)
    {
        bool first = _connection is null;
        ConnectionStatus? previousConnection = _connection;
        ReceiverMode previousMode = _mode;
        bool previouslyCoasting = _coasting;

        _connection = connection;
        _mode = mode;
        _coasting = isCoasting;

        if (first)
        {
            return null;
        }

        if (connection != previousConnection)
        {
            return ConnectionAnnouncement(connection, portName);
        }

        // §10.3 calls this the single most useful diagnostic the application surfaces, and it is
        // invisible to every other indicator: the receiver still says Locked, the merit figures are
        // still good, and the antenna has failed. It outranks the mode change that carries it.
        if (isCoasting && !previouslyCoasting)
        {
            return new Announcement(
                $"{ReceiverModes.TextOf(mode)}, but tracking no satellites. The receiver is coasting on a 1 PPS it can no longer verify.",
                AnnouncementUrgency.Assertive);
        }

        if (mode != previousMode)
        {
            return new Announcement(ReceiverModes.TextOf(mode), UrgencyOf(mode));
        }

        if (previouslyCoasting && !isCoasting)
        {
            return new Announcement("Tracking satellites again.", AnnouncementUrgency.Polite);
        }

        return null;
    }

    /// <remarks>
    /// §9.11 keeps "Disconnected" and "Connection lost" apart, and the difference is the whole
    /// point of announcing at all: one is what the user asked for and the other is what happened to
    /// them. Only the second interrupts.
    /// </remarks>
    private static Announcement ConnectionAnnouncement(ConnectionStatus connection, string? portName) =>
        connection switch
        {
            ConnectionStatus.Faulted =>
                new Announcement("Connection lost.", AnnouncementUrgency.Assertive),
            ConnectionStatus.Connected => new Announcement(
                string.IsNullOrWhiteSpace(portName) ? "Connected." : $"Connected on {portName}.",
                AnnouncementUrgency.Polite),
            ConnectionStatus.Connecting => new Announcement("Connecting.", AnnouncementUrgency.Polite),
            ConnectionStatus.Reconnecting => new Announcement("Reconnecting.", AnnouncementUrgency.Polite),
            _ => new Announcement("Disconnected.", AnnouncementUrgency.Polite),
        };

    /// <remarks>
    /// Urgency follows the §9.4.3 severity the mode already carries, rather than a second table
    /// that could disagree with the colour and the shape about how bad the same state is.
    /// </remarks>
    private static AnnouncementUrgency UrgencyOf(ReceiverMode mode) =>
        ReceiverModes.SeverityOf(mode) == Severity.Critical
            ? AnnouncementUrgency.Assertive
            : AnnouncementUrgency.Polite;
}
