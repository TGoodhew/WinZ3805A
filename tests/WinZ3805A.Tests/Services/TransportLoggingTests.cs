using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// That the transport's own log lines reach a sink at all (#524).
/// </summary>
/// <remarks>
/// <para>
/// <b>They did not, and nothing said so.</b> The session built its <see cref="LineProtocol"/> naming
/// <c>prompt:</c> and letting the logger parameter take its default, so every <c>TransportLog</c>
/// message went to <c>NullLogger</c> — the command sent, the transaction completed, the timeouts,
/// the faults, and #209's realignment line, which exists to report that the stream had to be
/// resynchronised on a link where misalignment is a known failure mode.
/// </para>
/// <para>
/// <b>Raising the minimum level did nothing for it</b>, which is the part that would waste an
/// afternoon: the messages were not filtered, they were delivered to a sink that discards. The
/// application looked healthy because every other category — the session, the poller, the tray —
/// went on logging normally, and only the wire was missing.
/// </para>
/// <para>
/// So the assertion is deliberately about <i>arrival</i> rather than about wording. A test that
/// matched message text would pass just as well against a sink nobody reads.
/// </para>
/// </remarks>
public sealed class TransportLoggingTests
{
    private const string Identity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static ControllableTransport Receiver() =>
        new(command => command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? Identity : "LOCK")
        {
            Banner = Identity,
        };

    [Fact]
    public async Task TheTransportsOwnLinesReachTheLoggerItWasGiven()
    {
        Recorder recorder = new();

        await using DeviceSessionService session = new(
            (_, _) => Receiver(),
            new FakeTimeProvider(),
            protocolLogger: new RecordingLogger<LineProtocol>(recorder));

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));

        // The connect sequence writes, so something must have been recorded. Which message and in
        // what words is TransportLog's business and is allowed to change.
        Assert.NotEmpty(recorder.Snapshot());
    }

    /// <summary>
    /// A session given no protocol logger stays silent, which is what every test composition and
    /// every headless construction site relies on.
    /// </summary>
    [Fact]
    public async Task ASessionGivenNoProtocolLoggerLogsNothingFromTheTransport()
    {
        Recorder recorder = new();

        await using DeviceSessionService session = new(
            (_, _) => Receiver(),
            new FakeTimeProvider(),
            logger: new RecordingLogger<DeviceSessionService>(recorder));

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));

        // The session's own category still logs — this is not a test that logging is off.
        Assert.DoesNotContain(recorder.Snapshot(), entry => entry.Message.StartsWith("-> ", StringComparison.Ordinal));
    }

    /// <summary>Collects what was logged, at the level it was logged at.</summary>
    private sealed class Recorder : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <summary>Everything, because the per-transaction pair is <c>Trace</c> (#524).</summary>
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        public (LogLevel Level, string Message)[] Snapshot()
        {
            lock (_entries)
            {
                return [.. _entries];
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
}
