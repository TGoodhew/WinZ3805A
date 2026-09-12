using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// What happens to readings when the session moves to a different receiver (#492).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these exist to make impossible.</b> A VK-162 on one port and a forM8N on another,
/// switched between by disconnecting and reconnecting rather than restarting the application,
/// reported the <i>same</i> satellite count for both. Their true counts differed by four — the
/// second receiver's own bytes, run through the shipped driver, gave 15 in every one of 24 cycles
/// while the display showed the first one's 11.
/// </para>
/// <para>
/// <b>§9.11's "stale data is kept, not blanked" is right, and it is about one receiver going
/// quiet.</b> An old reading with an honest timestamp beats an empty field, because the age is on
/// screen and the user can judge it. None of that holds for a <i>different</i> receiver: nothing
/// distinguishes "this one has not answered yet" from "this belongs to the instrument you were
/// looking at a minute ago", and the age shown is the new connection's, so the borrowed reading
/// looks fresh.
/// </para>
/// </remarks>
public sealed class DeviceChangeTests
{
    private const FastFields Everything =
        FastFields.SyncState | FastFields.Tfom | FastFields.Ffom
        | FastFields.TimeInterval | FastFields.OscillatorControl | FastFields.SatellitesTracked;

    /// <summary>What every NMEA talker answers to — no serial number, so no two pucks differ.</summary>
    private static readonly DeviceIdentity Talker =
        new("NMEA 0183", "GNSS talker", string.Empty, string.Empty, ReceiverModel.Unknown);

    private static readonly DeviceIdentity SmartClock =
        new("SYMMETRICOM", "Z3805A", "3625A02931", "1.01.03-A", ReceiverModel.Unknown);

    /// <summary>A store holding a full set of readings from the receiver named by <paramref name="key"/>.</summary>
    private static ReceiverStateStore Holding(string key, int tracked = 11)
    {
        ReceiverStateStore store = new(new FakeTimeProvider());
        store.BeginDevice(key);
        store.UpdateFast("LOCK", 3, 0, 2.8, 47.5, tracked, Everything);
        store.UpdateFull(new ReceiverStatus { Tfom = 3 }, Everything);
        return store;
    }

    // ---- The key ------------------------------------------------------------------------------

    /// <summary>
    /// THE BUG, AS A KEY. Two NMEA pucks answer to the same identity, so only the port tells them
    /// apart — a key built from the identity alone would have called them one receiver and changed
    /// nothing at all.
    /// </summary>
    [Fact]
    public void TwoTalkersThatShareAnIdentityAreDifferentReceivers()
    {
        string vk162 = ReceiverStateStore.KeyFor("COM5", Talker);
        string form8n = ReceiverStateStore.KeyFor("COM7", Talker);

        Assert.NotEqual(vk162, form8n);
    }

    /// <summary>
    /// And the other half: the same receiver reached twice is the same receiver, or every reconnect
    /// would blank a display §9.11 says to keep.
    /// </summary>
    [Fact]
    public void TheSameReceiverOnTheSamePortIsTheSameReceiver() =>
        Assert.Equal(
            ReceiverStateStore.KeyFor("COM3", SmartClock),
            ReceiverStateStore.KeyFor("COM3", SmartClock));

    /// <summary>
    /// A different receiver substituted on one port is noticed where the hardware says who it is.
    /// </summary>
    [Fact]
    public void ADifferentReceiverOnOnePortIsNoticedWhenItSaysWhoItIs() =>
        Assert.NotEqual(
            ReceiverStateStore.KeyFor("COM3", SmartClock),
            ReceiverStateStore.KeyFor("COM3", Talker));

    /// <summary>
    /// An unidentified connection is not silently equal to an identified one on the same port.
    /// </summary>
    [Fact]
    public void AnUnidentifiedConnectionIsNotTheSameAsAnIdentifiedOne() =>
        Assert.NotEqual(
            ReceiverStateStore.KeyFor("COM3", null),
            ReceiverStateStore.KeyFor("COM3", SmartClock));

    // ---- What the store does with it ----------------------------------------------------------

    [Fact]
    public void MovingToADifferentReceiverBlanksEveryReading()
    {
        ReceiverStateStore store = Holding(ReceiverStateStore.KeyFor("COM5", Talker));

        Assert.True(store.BeginDevice(ReceiverStateStore.KeyFor("COM7", Talker)));

        Assert.Null(store.SyncState);
        Assert.Null(store.Tfom);
        Assert.Null(store.Ffom);
        Assert.Null(store.OnePpsTiNanoseconds);
        Assert.Null(store.OscillatorControl);
        Assert.Null(store.TrackedCount);
        Assert.Null(store.Status);
    }

    /// <summary>
    /// The timestamps go too, and this is the half that is easy to leave behind.
    /// </summary>
    /// <remarks>
    /// §9.11's whole staleness treatment is built on the age of a reading. A blanked display still
    /// carrying the previous receiver's poll time would report the new receiver's silence as
    /// "updated just now" — which is the same lie in a different field.
    /// </remarks>
    [Fact]
    public void TheTimestampsAreForgottenWithTheReadings()
    {
        ReceiverStateStore store = Holding(ReceiverStateStore.KeyFor("COM5", Talker));
        Assert.NotNull(store.LastFastPoll);
        Assert.NotNull(store.LastFullPoll);

        store.BeginDevice(ReceiverStateStore.KeyFor("COM7", Talker));

        Assert.Null(store.LastFastPoll);
        Assert.Null(store.LastFullPoll);
        Assert.Null(store.AgeOf(store.LastFastPoll));
    }

    /// <summary>
    /// §9.10.2's medallion ring is sixty seconds of one receiver's 1 PPS history, and it must not
    /// carry over — a ring drawn from two instruments is a shape that never happened.
    /// </summary>
    [Fact]
    public void TheTimeIntervalRingIsEmptiedToo()
    {
        ReceiverStateStore store = new(new FakeTimeProvider());
        store.BeginDevice(ReceiverStateStore.KeyFor("COM3", SmartClock));
        for (int i = 0; i < 10; i++)
        {
            store.UpdateFast("LOCK", 3, 0, i, 47.5, 8, Everything);
        }

        Assert.Equal(10, store.RecentTimeInterval.Count);

        store.BeginDevice(ReceiverStateStore.KeyFor("COM5", Talker));

        Assert.Empty(store.RecentTimeInterval);
    }

    [Fact]
    public void ReconnectingToTheSameReceiverKeepsItsReadings()
    {
        string key = ReceiverStateStore.KeyFor("COM3", SmartClock);
        ReceiverStateStore store = Holding(key, tracked: 8);

        Assert.False(store.BeginDevice(key));

        // §9.11: an old reading with an honest timestamp beats an empty field. A dropped cable that
        // comes back must not wipe the window.
        Assert.Equal("LOCK", store.SyncState);
        Assert.Equal(8, store.TrackedCount);
        Assert.NotNull(store.LastFastPoll);
    }

    /// <summary>
    /// A first connection has nothing to carry over, so it is not a change worth a log line.
    /// </summary>
    /// <remarks>
    /// The return value is what the poller logs on. Reporting the first connect would put a line
    /// saying readings were blanked into every session's log, where it would mean nothing and teach
    /// the reader to skip the one that matters.
    /// </remarks>
    [Fact]
    public void AFirstConnectionIsNotAChangeWorthReporting()
    {
        ReceiverStateStore store = new(new FakeTimeProvider());

        Assert.False(store.BeginDevice(ReceiverStateStore.KeyFor("COM3", SmartClock)));
        Assert.Equal(ReceiverStateStore.KeyFor("COM3", SmartClock), store.DeviceKey);
    }

    /// <summary>Losing the receiver entirely is a change, and blanks what the last one left.</summary>
    [Fact]
    public void MovingToNoReceiverAtAllAlsoBlanks()
    {
        ReceiverStateStore store = Holding(ReceiverStateStore.KeyFor("COM3", SmartClock));

        Assert.True(store.BeginDevice(null));
        Assert.Null(store.TrackedCount);
        Assert.Null(store.DeviceKey);
    }

    /// <summary>
    /// The change is announced to the UI, or the display would keep painting the old numbers until
    /// something else happened to raise a notification.
    /// </summary>
    [Fact]
    public void BlankingRaisesPropertyChangedForWhatItBlanked()
    {
        ReceiverStateStore store = Holding(ReceiverStateStore.KeyFor("COM5", Talker));

        List<string?> changed = [];
        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.BeginDevice(ReceiverStateStore.KeyFor("COM7", Talker));

        Assert.Contains(nameof(ReceiverStateStore.TrackedCount), changed);
        Assert.Contains(nameof(ReceiverStateStore.SyncState), changed);
        Assert.Contains(nameof(ReceiverStateStore.Status), changed);
        Assert.Contains(nameof(ReceiverStateStore.RecentTimeInterval), changed);
    }

    // ---- The wiring, and the log ---------------------------------------------------------------

    /// <summary>Collects what was logged, at the level it was logged at.</summary>
    /// <remarks>Same shape as <c>ReconnectLogTests.Recorder</c>, for the same reason.</remarks>
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
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class RecordingFactory(Recorder recorder) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => recorder;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The poller tells the store which receiver answered, and says so in the log (#492).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tests above pin the rule; this pins that anything calls it. A correct
    /// <see cref="ReceiverStateStore.KeyFor"/> nobody invokes fixes nothing, and that is the shape
    /// the original defect had — the store was blameless and simply never told.
    /// </para>
    /// <para>
    /// <b>The log line is asserted, not just the state.</b> §492 is a class of report — "the reading
    /// was wrong after I switched receivers" — that cannot be diagnosed from a log that does not say
    /// which receiver the application believed it had. <c>ReconnectLogTests</c> makes the same
    /// argument for the reconnect timeline: the log is an output with a contract.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ThePollerTellsTheStoreWhichReceiverAnsweredAndLogsTheChange()
    {
        Recorder recorder = new();
        FakeTimeProvider clock = new();
        ReceiverStateStore store = new(clock);

        // Two ports, one fake receiver: the same identity on both, which is exactly the case that
        // defeated an identity-only key.
        await using DeviceSessionService session = new(
            (_, _) => new ControllableTransport(_ => " 0") { Banner = null },
            clock);

        await using PollingService poller = new(
            session,
            store,
            clock,
            new RecordingFactory(recorder).CreateLogger<PollingService>());

        // Readings from the first receiver, put there directly - this test is about the poller's
        // announcement, not about driving two full sweeps through the clock.
        store.BeginDevice(ReceiverStateStore.KeyFor("COM5", Talker));
        store.UpdateFast("LOCK", 3, 0, 2.8, 47.5, 11, Everything);

        Assert.Equal(11, store.TrackedCount);

        await session.ConnectAsync("COM7", SerialSettings.Default).WaitAsync(TimeSpan.FromSeconds(10));
        poller.Start();

        // One sweep is enough: the announcement happens before any reading is written.
        DateTimeOffset giveUp = clock.GetUtcNow().AddSeconds(30);
        while (store.DeviceKey == ReceiverStateStore.KeyFor("COM5", Talker) && clock.GetUtcNow() < giveUp)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }

        await poller.StopAsync();

        Assert.NotEqual(ReceiverStateStore.KeyFor("COM5", Talker), store.DeviceKey);
        Assert.Contains("COM7", store.DeviceKey, StringComparison.Ordinal);

        (LogLevel Level, string Message) line = Assert.Single(
            recorder.Entries,
            e => e.Message.Contains("Readings blanked", StringComparison.Ordinal));

        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("COM5", line.Message, StringComparison.Ordinal);
        Assert.Contains("COM7", line.Message, StringComparison.Ordinal);
    }
}
