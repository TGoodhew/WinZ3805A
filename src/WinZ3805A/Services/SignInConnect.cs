namespace WinZ3805A.Services;

/// <summary>
/// Keeps trying the remembered receiver after a sign-in start, until it answers (#548).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sign-in start retries and a launch by hand does not.</b> At sign-in, the one attempt
/// connect-on-launch makes is the one most likely to fail: a USB serial adapter can enumerate after
/// the application has started, and a receiver powered up with the PC can take a while to answer.
/// With the window hidden and the icon in the notification area's overflow, a single failed attempt
/// leaves an application that is running, shows nothing and polls nothing, with nobody there to press
/// Connect. That is worse than not starting at all. A person who starts the application is looking at
/// it, and for them §10.12's single attempt stands.
/// </para>
/// <para>
/// <b>Indefinitely, not for a while.</b> An unattended machine is the case this exists for, and a
/// receiver switched on an hour after the PC is as much its business as one that is slow to answer.
/// It stops when the receiver answers, or when the caller cancels it, which the main window does the
/// moment the user connects or disconnects by hand.
/// </para>
/// </remarks>
public static class SignInConnect
{
    /// <summary>The wait between attempts.</summary>
    /// <remarks>
    /// Long enough that a port held through auto-detect's walk is released for a good while between
    /// attempts, and short enough that a receiver switched on is being monitored within a minute.
    /// </remarks>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>Tries until an attempt succeeds or <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="attempt">One connection attempt; true when the receiver answered.</param>
    /// <param name="afterFailure">
    /// Called after each failed attempt with the number of attempts so far, before the wait. The main
    /// window clears the session's fault here, as it does after a failed launch attempt, and logs.
    /// </param>
    /// <param name="time">The clock the wait is measured on.</param>
    /// <param name="cancellationToken">Stops the retrying.</param>
    /// <returns>True when connected; false when cancelled first.</returns>
    /// <remarks>
    /// No <c>ConfigureAwait(false)</c>, deliberately: the callbacks build a connection view model and
    /// act on the session, and they run on the main window's thread, as the single launch attempt
    /// always has. Leaving the thread here would move them off it between one attempt and the next.
    /// </remarks>
    public static async Task<bool> RunAsync(
        Func<CancellationToken, Task<bool>> attempt,
        Func<int, Task> afterFailure,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(afterFailure);
        ArgumentNullException.ThrowIfNull(time);

        for (int attempts = 1; !cancellationToken.IsCancellationRequested; attempts++)
        {
            if (await attempt(cancellationToken))
            {
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            await afterFailure(attempts);

            try
            {
                await Task.Delay(RetryInterval, time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }
}
