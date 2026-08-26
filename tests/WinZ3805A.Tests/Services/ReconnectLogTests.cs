using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// What the log says while the link is being re-established (#14, §6.4, §7.2).
/// </summary>
/// <remarks>
/// <para>
/// P0-14's only verification is a person unplugging the adapter once and watching, and its
/// acceptance is two durations: Disconnected within 10 s, reconnected within 30 s of replug. The
/// log timestamps to the millisecond, so both are measurable from it — but only if it says what
/// happened between the two status lines, and a one-shot physical test is the worst moment to
/// discover it did not.
/// </para>
/// <para>
/// So the log is treated here as an output with a contract rather than as decoration. These
/// assertions are the reason tomorrow's observation can be evidence instead of an impression.
/// </para>
/// </remarks>
public class ReconnectLogTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>Collects what was logged, at the level it was logged at.</summary>
    private sealed class Recorder : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        public (LogLevel Level, string Message)[] Snapshot()
        {
            lock (Entries)
            {
                return [.. Entries];
            }
        }
    }

    private sealed class RecordingLogger<T>(Recorder recorder) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => recorder.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => recorder.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            recorder.Log(logLevel, eventId, state, exception, formatter);
    }

    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// The shape of a replug: the adapter is back, so the port opens, but the receiver is still
    /// coming up and does not answer. That path returns false rather than throwing, and before #14
    /// it logged <b>nothing at all</b> — a recovery that took forty-five seconds left forty-five
    /// seconds of silence between two status lines, with no way to tell one slow attempt from
    /// fifteen fast ones.
    /// </remarks>
    [Fact]
    public async Task EveryFailedAttemptIsRecordedWhereTheApplicationCanSeeIt()
    {
        Recorder recorder = new();
        FakeTimeProvider clock = new();

        await using DeviceSessionService session = Losing(recorder, clock, out Func<ControllableTransport?> live);

        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(Settle);
        live()!.Behaviour = TransportBehaviour.Faulting;
        await LoseTheLinkAsync(session);

        await PumpUntilAsync(clock, () => CountAttempts(recorder) >= 3);

        (LogLevel Level, string Message)[] entries = recorder.Snapshot();
        (LogLevel Level, string Message)[] attempts =
            [.. entries.Where(e => e.Message.Contains("Reconnect attempt", StringComparison.Ordinal))];

        Assert.True(attempts.Length >= 3, $"expected at least 3 attempt lines, saw {attempts.Length}");

        // Information, not Debug. The application ships at Information (App.xaml.cs), so a Debug
        // line is a line nobody reading app.log at a bench will ever see.
        Assert.All(attempts, a => Assert.Equal(LogLevel.Information, a.Level));
    }

    /// <remarks>
    /// The attempts are numbered so a reader can tell how many there were without counting lines,
    /// and each names the interval before the next, so the log demonstrates §7.2's 2 / 4 / 8 /
    /// capped-at-30 schedule rather than merely obeying it. #14's acceptance quotes that schedule.
    /// </remarks>
    [Fact]
    public async Task EachAttemptNamesItsNumberAndTheIntervalBeforeTheNext()
    {
        Recorder recorder = new();
        FakeTimeProvider clock = new();

        await using DeviceSessionService session = Losing(recorder, clock, out Func<ControllableTransport?> live);

        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(Settle);
        live()!.Behaviour = TransportBehaviour.Faulting;
        await LoseTheLinkAsync(session);

        await PumpUntilAsync(clock, () => CountAttempts(recorder) >= 4);

        string[] attempts =
        [
            .. recorder.Snapshot()
                .Select(e => e.Message)
                .Where(m => m.Contains("Reconnect attempt", StringComparison.Ordinal))
        ];

        Assert.True(attempts.Length >= 4, $"expected at least 4 attempt lines, saw {attempts.Length}");

        Assert.Contains("attempt 1 to COM3", attempts[0], StringComparison.Ordinal);
        Assert.Contains("attempt 2 to COM3", attempts[1], StringComparison.Ordinal);

        // §7.2 doubles from 2 s, so the first line promises 4 s and the second 8 s.
        Assert.Contains("00:00:04", attempts[0], StringComparison.Ordinal);
        Assert.Contains("00:00:08", attempts[1], StringComparison.Ordinal);
    }

    /// <summary>The two status lines a bench reader measures the durations between.</summary>
    /// <remarks>
    /// Both are already at Information and were before #14; asserted here so the pair that makes
    /// P0-14 measurable cannot be quietly demoted, which is what had happened to everything
    /// between them.
    /// </remarks>
    [Fact]
    public async Task TheStatusTransitionsThemselvesAreAtInformation()
    {
        Recorder recorder = new();
        FakeTimeProvider clock = new();

        await using DeviceSessionService session = Losing(recorder, clock, out _);

        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(Settle);

        (LogLevel Level, string Message)[] status =
            [.. recorder.Snapshot().Where(e => e.Message.Contains("is now", StringComparison.Ordinal))];

        Assert.NotEmpty(status);
        Assert.All(status, e => Assert.Equal(LogLevel.Information, e.Level));
        Assert.Contains(status, e => e.Message.Contains("COM3", StringComparison.Ordinal));
    }

    private const string Identity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";

    private static ScpiCommand StatusQuery => CommandCatalog.Find(":SYNC:STAT?")!;

    /// <summary>
    /// A session that connects once and then cannot get back, which is the window the log has to
    /// describe.
    /// </summary>
    /// <remarks>
    /// The first port answers, so the session reaches Connected honestly. Every port after it is
    /// silent — the shape of a replug where the adapter is back but the receiver is still coming
    /// up, and the path that returns false rather than throwing. That is the one that logged
    /// nothing at all before #14.
    /// </remarks>
    private static DeviceSessionService Losing(
        Recorder recorder,
        FakeTimeProvider clock,
        out Func<ControllableTransport?> live)
    {
        ControllableTransport? current = null;
        int opens = 0;

        DeviceSessionService session = new(
            (_, _) =>
            {
                opens++;
                current = opens == 1
                    ? new ControllableTransport(
                        command => command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? Identity : "LOCK")
                    {
                        Banner = Identity,
                    }
                    : new ControllableTransport(_ => null) { Behaviour = TransportBehaviour.Silent };

                return current;
            },
            clock,
            new RecordingLogger<DeviceSessionService>(recorder))
        { StayConnected = true };

        ControllableTransport? Live() => current;
        live = Live;
        return session;
    }

    /// <summary>Runs one command over a link that is about to fail, tolerating either shape.</summary>
    private static async Task LoseTheLinkAsync(DeviceSessionService session)
    {
        try
        {
            await session.ExecuteAsync(StatusQuery).WaitAsync(Settle);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception)
        {
            // Expected: the link failed, which is the point.
        }
    }

    /// <remarks>
    /// Steps larger than §7.2's 30 s cap, so one advance always clears whatever the retry loop is
    /// waiting on, and returns as soon as the condition holds rather than after a fixed budget —
    /// #192's lesson, applied at the point of writing rather than after a month of flakes.
    /// </remarks>
    private static async Task PumpUntilAsync(FakeTimeProvider clock, Func<bool> until)
    {
        using CancellationTokenSource giveUp = new(Settle);

        while (!until() && !giveUp.IsCancellationRequested)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(5, CancellationToken.None);
        }
    }

    private static int CountAttempts(Recorder recorder) =>
        recorder.Snapshot().Count(e => e.Message.Contains("Reconnect attempt", StringComparison.Ordinal));
}
