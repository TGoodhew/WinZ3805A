using WinZ3805A.Services;

namespace WinZ3805A.Controls;

/// <summary>
/// §9.11's first-run surface: what it says, and when it is the right thing to say (#253).
/// </summary>
/// <remarks>
/// <para>
/// §9.11's row gives the copy and the shape — "Full-page centred: 32 px icon,
/// <c>WzTitleLargeTextStyle</c> headline, one line of <c>WzBodyTextStyle</c>, primary button", and
/// "No tour, no carousel, no dismissible tips". What it does not give is <b>when</b>, and that is the
/// only real decision here.
/// </para>
/// <para>
/// <b>First run ends when a port has been chosen, not when one has connected.</b> The two adjacent
/// rows of §9.11 answer this between them, because they are written for different readers:
/// </para>
/// <list type="table">
/// <item>
/// <term>First run</term>
/// <description>"This app talks to HP and Symmetricom GPS receivers over a serial port." — explains
/// what the application <i>is</i>, to somebody who has never used it.</description>
/// </item>
/// <item>
/// <term>Disconnected</term>
/// <description>"Not connected. Choose a serial port to connect." — assumes they know, and reports a
/// state.</description>
/// </item>
/// </list>
/// <para>
/// So the likeliest first run of all — somebody who opens the application before plugging the
/// adapter in, picks a port, and fails to connect — lands on <i>Disconnected</i> afterwards. They
/// have a connection problem, not a comprehension problem, and being told again what the application
/// is would be answering a question they no longer have. Had the rule been "until a connection
/// succeeds", that user would see the introduction repeatedly while trying to fix a cable.
/// </para>
/// <para>
/// It also means the state is durable rather than sticky: it is a function of stored preferences, so
/// it cannot be dismissed, cannot be re-shown by a failure, and needs no "seen it" flag of its own.
/// </para>
/// <para>
/// <b>The main window, not the Details window.</b> The main window is what opens at launch;
/// the Details window is opened deliberately by somebody who is already using the application.
/// </para>
/// </remarks>
public static class FirstRun
{
    /// <summary>§9.11's headline.</summary>
    public const string Headline = "Connect your receiver";

    /// <summary>§9.11's one line of body copy.</summary>
    public const string Body =
        "This app talks to HP and Symmetricom GPS receivers over a serial port. "
        + "Pick the port your receiver is on to begin.";

    /// <summary>§9.11's primary button.</summary>
    public const string ActionLabel = "Choose a port";

    /// <summary>
    /// Whether this is a first run — no port has ever been chosen on this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The status is consulted as well, so the surface cannot appear over a live session. That is
    /// not reachable through the ordinary path — <c>ConnectOnLaunchAsync</c> returns early with no
    /// stored port, so nothing auto-connects on a first run — but a port can be chosen and connected
    /// within one session without preferences having been written yet, and covering a full-page
    /// takeover over a working receiver is worth one extra condition.
    /// </para>
    /// <para>
    /// Whitespace counts as absent. A stored port of <c>" "</c> is not a port anybody chose.
    /// </para>
    /// </remarks>
    /// <param name="storedPortName">The port in saved preferences, if any.</param>
    /// <param name="status">Where the session stands.</param>
    public static bool ShouldShow(string? storedPortName, ConnectionStatus status) =>
        string.IsNullOrWhiteSpace(storedPortName) && status == ConnectionStatus.Disconnected;
}
