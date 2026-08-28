using WinZ3805A.Services;

namespace WinZ3805A.Controls;

/// <summary>What the Details window says across the top when it is not connected (§9.11, #252).</summary>
/// <param name="IsOpen">Whether the bar shows at all.</param>
/// <param name="IsError">Error severity when true, informational when false.</param>
/// <param name="Message">The sentence, already carrying the port name where there is one.</param>
/// <param name="ActionLabel">The action button, or <see langword="null"/> for none.</param>
public readonly record struct ConnectionBannerState(
    bool IsOpen,
    bool IsError,
    string Message,
    string? ActionLabel)
{
    /// <summary>Nothing to say, because the link is fine or is in the middle of being made.</summary>
    public static ConnectionBannerState None { get; } = new(false, false, string.Empty, null);
}

/// <summary>
/// Turns a <see cref="ConnectionStatus"/> into §9.11's banner for the Details window.
/// </summary>
/// <remarks>
/// <para>
/// §9.11 gives the Details window a bar below the title bar in two different states, and it is
/// emphatic that they are not one state: <i>"an intentional disconnect is not a fault"</i>. The
/// <c>ConnectionStatus</c> enum says the same thing in its own remarks — <i>"collapsing the two into
/// one 'not connected' is the shortcut that makes an app cry wolf"</i>.
/// </para>
/// <list type="table">
/// <listheader><term>State</term><description>§9.11 row</description></listheader>
/// <item>
/// <term><see cref="ConnectionStatus.Disconnected"/></term>
/// <description>Informational, "Not connected. Choose a serial port to connect." / <b>Choose a port</b></description>
/// </item>
/// <item>
/// <term><see cref="ConnectionStatus.Reconnecting"/></term>
/// <description>Error, with a retry countdown and <b>Retry now</b> · <b>Stop retrying</b> — <b>#248</b></description>
/// </item>
/// </list>
/// <para>
/// <b>Why the decision is here rather than in the window.</b> The two rows differ in severity, in
/// copy and in how many actions they carry, and getting that wrong looks like a styling choice
/// rather than a defect. Pulling it out makes each row assertable without a XAML runtime, the same
/// reason <see cref="CommandRetryPolicy"/> and <see cref="SurveyRefusalAdvice"/> sit out here.
/// </para>
/// <para>
/// <b>#248 belongs in this switch.</b> It needs the countdown, which is a second input rather than a
/// second control, and a second action label — so the shape returned here will grow rather than be
/// replaced. Building it in the window instead would have made that a rewrite, which is the argument
/// #254 makes for doing these rows together.
/// </para>
/// </remarks>
public static class ConnectionBanner
{
    /// <summary>§9.11's action for the disconnected row.</summary>
    public const string ChoosePortLabel = "Choose a port";

    /// <summary>§9.11's copy for the disconnected row.</summary>
    public const string DisconnectedMessage = "Not connected. Choose a serial port to connect.";

    /// <summary>What to show, if anything, for the given connection state.</summary>
    /// <remarks>
    /// <see cref="ConnectionStatus.Connecting"/> shows nothing deliberately. It is a transient the
    /// user asked for and it resolves within a couple of seconds; a bar that appears and vanishes on
    /// its own is noise, and §9.11 gives it no row.
    /// </remarks>
    /// <param name="status">Where the session stands.</param>
    public static ConnectionBannerState For(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Disconnected =>
            new(true, IsError: false, DisconnectedMessage, ChoosePortLabel),

        // Reconnecting and Faulted are §9.11's "Connection lost" row and are #248's. They show
        // nothing yet rather than borrowing the informational treatment above, because that is the
        // collapse both §9.11 and ConnectionStatus warn against - and a wrong bar would be harder to
        // notice than an absent one.
        _ => ConnectionBannerState.None,
    };
}
