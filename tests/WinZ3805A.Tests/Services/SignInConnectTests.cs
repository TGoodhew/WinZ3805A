using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #548 — after a start at sign-in, the remembered receiver is tried until it answers.
/// </summary>
/// <remarks>
/// The clock is fake, so a receiver that takes three attempts to answer takes no time here, and the
/// assertions can say exactly when each attempt happened rather than that it happened eventually.
/// </remarks>
public sealed class SignInConnectTests
{
    [Fact]
    public async Task AReceiverThatAnswersAtOnceIsTriedOnce()
    {
        FakeTimeProvider time = new();
        int attempts = 0;
        List<int> failures = [];

        bool connected = await SignInConnect.RunAsync(
            _ => { attempts++; return Task.FromResult(true); },
            n => { failures.Add(n); return Task.CompletedTask; },
            time,
            CancellationToken.None);

        Assert.True(connected);
        Assert.Equal(1, attempts);
        Assert.Empty(failures);
    }

    /// <summary>
    /// <b>The case #548 is for</b>: the adapter or the receiver is not ready at sign-in, and answers a
    /// little later. Each failure is reported before the wait, and nothing is tried again until the
    /// interval has passed.
    /// </summary>
    [Fact]
    public async Task AReceiverThatIsLateIsTriedEveryIntervalUntilItAnswers()
    {
        FakeTimeProvider time = new();
        int attempts = 0;
        List<int> failures = [];

        Task<bool> running = SignInConnect.RunAsync(
            _ => Task.FromResult(++attempts == 3),
            n => { failures.Add(n); return Task.CompletedTask; },
            time,
            CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal([1], failures);

        time.Advance(SignInConnect.RetryInterval - TimeSpan.FromSeconds(1));
        Assert.Equal(1, attempts);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, attempts);
        Assert.Equal([1, 2], failures);

        time.Advance(SignInConnect.RetryInterval);

        Assert.True(await running);
        Assert.Equal(3, attempts);
        Assert.Equal([1, 2], failures);
    }

    /// <summary>Indefinitely, not for a while: an unattended machine is what this exists for.</summary>
    [Fact]
    public async Task ItDoesNotGiveUpOnItsOwn()
    {
        FakeTimeProvider time = new();
        int attempts = 0;
        using CancellationTokenSource stop = new();

        Task<bool> running = SignInConnect.RunAsync(
            _ => Task.FromResult(++attempts < 0),
            _ => Task.CompletedTask,
            time,
            stop.Token);

        for (int hour = 0; hour < 24 * 3; hour++)
        {
            for (int tick = 0; tick < 120; tick++)
            {
                time.Advance(SignInConnect.RetryInterval);
            }
        }

        Assert.False(running.IsCompleted);
        Assert.Equal((24 * 3 * 120) + 1, attempts);

        await stop.CancelAsync();
        Assert.False(await running);
    }

    /// <summary>
    /// The user taking over stops it at once, mid-wait, with no further attempt on a port the dialog is
    /// about to open.
    /// </summary>
    [Fact]
    public async Task CancellingDuringTheWaitStopsWithoutAnotherAttempt()
    {
        FakeTimeProvider time = new();
        int attempts = 0;
        using CancellationTokenSource stop = new();

        Task<bool> running = SignInConnect.RunAsync(
            _ => Task.FromResult(++attempts < 0),
            _ => Task.CompletedTask,
            time,
            stop.Token);

        await stop.CancelAsync();

        Assert.False(await running);

        time.Advance(SignInConnect.RetryInterval * 4);
        Assert.Equal(1, attempts);
    }

    /// <summary>
    /// Cancelled while an attempt is in flight, the attempt's failure is not reported as a failure to
    /// retry after: the caller has already taken over.
    /// </summary>
    [Fact]
    public async Task CancellingDuringAnAttemptReportsNoFailure()
    {
        FakeTimeProvider time = new();
        using CancellationTokenSource stop = new();
        List<int> failures = [];

        bool connected = await SignInConnect.RunAsync(
            async _ =>
            {
                await stop.CancelAsync();
                return false;
            },
            n => { failures.Add(n); return Task.CompletedTask; },
            time,
            stop.Token);

        Assert.False(connected);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task AlreadyCancelledMeansNoAttemptAtAll()
    {
        int attempts = 0;

        bool connected = await SignInConnect.RunAsync(
            _ => Task.FromResult(++attempts > 0),
            _ => Task.CompletedTask,
            new FakeTimeProvider(),
            new CancellationToken(canceled: true));

        Assert.False(connected);
        Assert.Equal(0, attempts);
    }
}
