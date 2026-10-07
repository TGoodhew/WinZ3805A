using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// The mode every shell surface shows, against the session as well as the receiver (#752).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Between a successful Connect and the first stored sweep the store holds
/// no sync state, every driver reads a missing one as <see cref="ReceiverMode.Disconnected"/>, and
/// the main window said Disconnected beside a Disconnect button and a footer saying
/// <i>updating…</i>. Disconnected must mean the session is not connected.
/// </para>
/// <para>
/// Run against every shipped driver, because each maps an unknown token its own way and the fix is
/// meant to hold whatever they do: it lives in <see cref="ShellMode"/>, not in any mapping.
/// </para>
/// </remarks>
public sealed class ShellModeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Drivers => new(["SmartClock", "NMEA", "UCCM"]);

    private static IReceiverDriver DriverNamed(string name, TimeProvider clock) => name switch
    {
        "NMEA" => new NmeaDriver(clock),
        "UCCM" => new UccmDriver(clock),
        _ => new SmartClockDriver(clock),
    };

    [Theory]
    [MemberData(nameof(Drivers))]
    public void ConnectedWithNoReadingYetIsAwaitingTheFirstReadingNotDisconnected(string name)
    {
        FakeTimeProvider clock = new(Now);
        ReceiverStateStore store = new(clock);

        ReceiverMode mode = ShellMode.For(DriverNamed(name, clock), store, ConnectionStatus.Connected);

        Assert.Equal(ReceiverMode.AwaitingReading, mode);
        Assert.NotEqual(ReceiverMode.Disconnected, mode);
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Connecting)]
    [InlineData(ConnectionStatus.Reconnecting)]
    [InlineData(ConnectionStatus.Faulted)]
    public void WithoutALinkTheModeIsDisconnectedWithOrWithoutAReading(ConnectionStatus connection)
    {
        FakeTimeProvider clock = new(Now);
        ReceiverStateStore store = new(clock);
        SmartClockDriver driver = new(clock);

        Assert.Equal(ReceiverMode.Disconnected, ShellMode.For(driver, store, connection));

        store.UpdateFast("LOCK", 3, 0, -5.4, -16.8, 6);

        Assert.Equal(ReceiverMode.Disconnected, ShellMode.For(driver, store, connection));
    }

    [Fact]
    public void TheFirstStoredSweepReplacesTheWaitWithTheReceiversOwnMode()
    {
        FakeTimeProvider clock = new(Now);
        ReceiverStateStore store = new(clock);
        SmartClockDriver driver = new(clock);

        store.UpdateFast("HOLD", 5, 3, -120, -16.8, 0);

        Assert.Equal(ReceiverMode.Holdover, ShellMode.For(driver, store, ConnectionStatus.Connected));
    }

    /// <summary>
    /// The full screen can arrive before the first sweep (#479), and it carries no sync state, so
    /// the mode has still not been read.
    /// </summary>
    [Fact]
    public void AScreenAloneIsNotYetAMode()
    {
        FakeTimeProvider clock = new(Now);
        ReceiverStateStore store = new(clock);

        store.UpdateFull(new ReceiverStatus { CapturedAt = Now }, FastFields.All);

        Assert.Equal(
            ReceiverMode.AwaitingReading,
            ShellMode.For(new SmartClockDriver(clock), store, ConnectionStatus.Connected));
    }

    /// <summary>
    /// Once a sweep is stored, a token the driver does not recognise is still the driver's
    /// Disconnected. That mapping is §11.1's and unchanged: only the absence of a sweep is new.
    /// </summary>
    [Fact]
    public void AnUnrecognisedTokenAfterTheFirstSweepIsStillTheDriversAnswer()
    {
        FakeTimeProvider clock = new(Now);
        ReceiverStateStore store = new(clock);

        store.UpdateFast("SOMETHING NEW", 3, 0, -5.4, -16.8, 6);

        Assert.Equal(
            ReceiverMode.Disconnected,
            ShellMode.For(new SmartClockDriver(clock), store, ConnectionStatus.Connected));
    }

    /// <summary>
    /// Reconnecting to the same receiver keeps its readings (#492), so the last mode shows until the
    /// next sweep, as before #752; a different receiver's readings are blanked and it waits again.
    /// </summary>
    [Fact]
    public void OnlyANewReceiverWaitsAgain()
    {
        FakeTimeProvider clock = new(Now);
        ReceiverStateStore store = new(clock);
        SmartClockDriver driver = new(clock);

        store.BeginDevice("COM2|HP/Z3805A/1");
        store.UpdateFast("LOCK", 3, 0, -5.4, -16.8, 6);

        store.BeginDevice("COM2|HP/Z3805A/1");
        Assert.Equal(ReceiverMode.Locked, ShellMode.For(driver, store, ConnectionStatus.Connected));

        store.BeginDevice("COM3|HP/Z3805A/2");
        Assert.Equal(ReceiverMode.AwaitingReading, ShellMode.For(driver, store, ConnectionStatus.Connected));
    }

    /// <summary>The session's member is never a driver's answer, whatever the token.</summary>
    [Theory]
    [MemberData(nameof(Drivers))]
    public void NoDriverEverAnswersAwaitingReading(string name)
    {
        IReceiverDriver driver = DriverNamed(name, new FakeTimeProvider(Now));
        string?[] tokens = [null, "", " ", "LOCK", "HOLD", "POW", "0", "1", "A", "V", "Initializing", "AwaitingReading", "SOMETHING NEW"];

        foreach (string? token in tokens)
        {
            Assert.NotEqual(ReceiverMode.AwaitingReading, driver.InterpretSyncState(token));
        }
    }

    /// <summary>
    /// Drawn as §10.3's new row: neutral, its own glyph, and a label that says what is known.
    /// </summary>
    [Fact]
    public void TheWaitIsDrawnNeutralWithItsOwnGlyphAndWords()
    {
        Assert.Equal(Severity.Neutral, ReceiverModes.SeverityOf(ReceiverMode.AwaitingReading));
        Assert.Equal("Connected", ReceiverModes.TextOf(ReceiverMode.AwaitingReading));
        Assert.Equal("", ReceiverModes.GlyphOf(ReceiverMode.AwaitingReading));
        Assert.NotEqual(
            ReceiverModes.GlyphOf(ReceiverMode.Disconnected),
            ReceiverModes.GlyphOf(ReceiverMode.AwaitingReading));
    }
}
