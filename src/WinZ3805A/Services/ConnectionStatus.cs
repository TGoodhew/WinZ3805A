namespace WinZ3805A.Services;

/// <summary>
/// Where the session stands with the receiver (§9.11).
/// </summary>
/// <remarks>
/// <b>Disconnected is not the same state as Faulted</b>, and §9.11 treats them differently on
/// purpose: an intentional disconnect is informational, while a link that dropped underneath the
/// application is critical — <see cref="ConnectionStatus.Reconnecting"/> while the §7.2 backoff is
/// counting down, with both "retry now" and "stop retrying", and
/// <see cref="ConnectionStatus.Faulted"/> once it no longer is.
/// Collapsing the two into one "not connected" is the shortcut that makes an app cry wolf.
/// </remarks>
public enum ConnectionStatus
{
    /// <summary>No link, and none wanted. The user has not connected, or disconnected deliberately.</summary>
    Disconnected = 0,

    /// <summary>Opening the port, or walking the auto-detect sequence.</summary>
    Connecting,

    /// <summary>Open, synchronised, and answering.</summary>
    Connected,

    /// <summary>The link dropped and is being retried on the §7.2 backoff. Data on screen is stale, not gone.</summary>
    Reconnecting,

    /// <summary>The link failed and is not being retried, either because retry is off or it was given up on.</summary>
    Faulted,
}
