using System.Globalization;

using WinZ3805A.Services;

namespace WinZ3805A.Controls;

/// <summary>What a window says across the top when the link is not healthy (§9.11, #252, #248).</summary>
/// <param name="IsOpen">Whether the bar shows at all.</param>
/// <param name="IsError">Error severity when true, informational when false.</param>
/// <param name="Message">The sentence, already carrying the port name and countdown where there is one.</param>
/// <param name="ActionLabel">The primary action, or <see langword="null"/> for none.</param>
/// <param name="SecondaryActionLabel">The second action, or <see langword="null"/> for none.</param>
public readonly record struct ConnectionBannerState(
    bool IsOpen,
    bool IsError,
    string Message,
    string? ActionLabel,
    string? SecondaryActionLabel = null)
{
    /// <summary>Nothing to say, because the link is fine or is in the middle of being made.</summary>
    public static ConnectionBannerState None { get; } = new(false, false, string.Empty, null);
}

/// <summary>
/// Turns a <see cref="ConnectionStatus"/> into §9.11's banner.
/// </summary>
/// <remarks>
/// <para>
/// §9.11 gives this one slot two rows and is emphatic that they are not one state:
/// <i>"an intentional disconnect is not a fault"</i>. The <c>ConnectionStatus</c> enum says the same
/// in its own remarks — <i>"collapsing the two into one 'not connected' is the shortcut that makes
/// an app cry wolf"</i>.
/// </para>
/// <list type="table">
/// <listheader><term>State</term><description>§9.11 row</description></listheader>
/// <item>
/// <term><see cref="ConnectionStatus.Disconnected"/></term>
/// <description>Informational — "Not connected. Choose a serial port to connect." / <b>Choose a port</b></description>
/// </item>
/// <item>
/// <term><see cref="ConnectionStatus.Reconnecting"/></term>
/// <description>Error — "Lost the connection to COM3. Retrying in 4 seconds." / <b>Retry now</b> · <b>Stop retrying</b></description>
/// </item>
/// <item>
/// <term><see cref="ConnectionStatus.Faulted"/></term>
/// <description>Error, and no countdown, because nothing is coming.</description>
/// </item>
/// </list>
/// <para>
/// <b>Why the decision is here rather than in a window.</b> The rows differ in severity, in copy and
/// in how many actions they carry, and getting that wrong looks like a styling choice rather than a
/// defect. Out here each row is assertable without a XAML runtime, the same reason
/// <see cref="CommandRetryPolicy"/> and <see cref="SurveyRefusalAdvice"/> sit beside it.
/// </para>
/// </remarks>
public static class ConnectionBanner
{
    /// <summary>§9.11's action for the disconnected row.</summary>
    public const string ChoosePortLabel = "Choose a port";

    /// <summary>§9.11's copy for the disconnected row.</summary>
    public const string DisconnectedMessage = "Not connected. Choose a serial port to connect.";

    /// <summary>§9.11's first action for the connection-lost row.</summary>
    public const string RetryNowLabel = "Retry now";

    /// <summary>§9.11's second action for the connection-lost row.</summary>
    public const string StopRetryingLabel = "Stop retrying";

    /// <summary>What to show, if anything, for the given connection state.</summary>
    /// <remarks>
    /// <see cref="ConnectionStatus.Connecting"/> shows nothing deliberately. It is a transient the
    /// user asked for and it resolves within a couple of seconds; a bar that appears and vanishes on
    /// its own is noise, and §9.11 gives it no row.
    /// </remarks>
    /// <param name="status">Where the session stands.</param>
    /// <param name="portName">The port, for copy that names it. Falls back to "the receiver".</param>
    /// <param name="retryIn">
    /// How long until the next attempt, for the countdown. Null when none is scheduled — during the
    /// attempt itself, for instance — in which case the sentence says a retry is under way rather
    /// than inventing a number.
    /// </param>
    public static ConnectionBannerState For(
        ConnectionStatus status,
        string? portName = null,
        TimeSpan? retryIn = null)
    {
        string where = string.IsNullOrWhiteSpace(portName) ? "the receiver" : portName;

        return status switch
        {
            ConnectionStatus.Disconnected =>
                new(true, IsError: false, DisconnectedMessage, ChoosePortLabel),

            ConnectionStatus.Reconnecting => new(
                true,
                IsError: true,
                $"Lost the connection to {where}. {Countdown(retryIn)}",
                RetryNowLabel,
                StopRetryingLabel),

            // No countdown, because nothing is coming. Retry now is still offered - it is the way
            // back for somebody who stopped retrying and changed their mind, or whose receiver has
            // since been switched on - but Stop retrying is not, because it is already stopped.
            ConnectionStatus.Faulted => new(
                true,
                IsError: true,
                $"Lost the connection to {where}. Not retrying.",
                RetryNowLabel),

            _ => ConnectionBannerState.None,
        };
    }

    /// <summary>The countdown clause of §9.11's connection-lost sentence.</summary>
    /// <remarks>
    /// Rounded <i>up</i>, so a bar that says "1 second" is never followed by a second of silence at
    /// zero — and so the first tick after a 4 s backoff begins reads "4 seconds" rather than "3".
    /// Singular at one, because "Retrying in 1 seconds" is the kind of detail that makes an
    /// interface look unfinished.
    /// </remarks>
    private static string Countdown(TimeSpan? retryIn)
    {
        if (retryIn is not TimeSpan remaining || remaining <= TimeSpan.Zero)
        {
            return "Retrying now.";
        }

        int seconds = (int)Math.Ceiling(remaining.TotalSeconds);

        return seconds == 1
            ? "Retrying in 1 second."
            : string.Create(CultureInfo.CurrentCulture, $"Retrying in {seconds} seconds.");
    }
}
