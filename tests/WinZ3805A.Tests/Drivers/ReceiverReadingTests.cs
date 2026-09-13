using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Drivers.Uccm;

namespace WinZ3805A.Tests.Drivers;

/// <summary>
/// What a family can never supply, as opposed to what it merely has not supplied yet (#435).
/// </summary>
/// <remarks>
/// <para>
/// The audit behind #435 drove the running application against a real talker and found the
/// interface unable to tell those two apart: a reading NMEA cannot carry drew the same em dash as
/// one still in flight, and several pages went further and asserted things that were false — the
/// Time page reporting that the receiver had not answered queries never sent, Diagnostics claiming
/// a status screen had parsed for a receiver with no status screen.
/// </para>
/// <para>
/// <b>These tests guard the direction of the risk.</b> A wrong <c>false</c> is worse than a blank
/// field: it tells a user their receiver can never report something it may report perfectly well,
/// and so stops them looking. That is why the interface default is <c>true</c> and why the test
/// below pins it.
/// </para>
/// </remarks>
public sealed class ReceiverReadingTests
{
    private static readonly DateTimeOffset Whenever = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A driver written before #435 claims nothing, and keeps the behaviour it had.</summary>
    /// <remarks>
    /// The default is the safe direction. A family that has not thought about a reading must not
    /// silently start telling users it can never produce one.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryReading))]
    public void ADriverThatSaysNothingIsAssumedToReportEverything(ReceiverReading reading) =>
        Assert.True(((IReceiverDriver)new SilentDriver()).Reports(reading));

    /// <summary>The SmartClock answers to everything its status screen carries.</summary>
    /// <remarks>
    /// <para>
    /// <b>This used to say "every reading", and the enum outgrew it (#435).</b> The claim was that
    /// the interface default is right for the family the application was written for, and it was —
    /// while every member of <see cref="ReceiverReading"/> was a SmartClock concept. The three
    /// fix-quality readings added since are not: they are what a GNSS receiver computes per fix and
    /// broadcasts over NMEA, and the captured status screens carry no dilution figure and no geoid
    /// separation anywhere.
    /// </para>
    /// <para>
    /// So the narrower claim is the true one, and it is still worth pinning: everything the screen
    /// prints is reported, and the exclusions are exactly the GNSS fix statistics. A driver that
    /// began declining something the screen does print would fail here, which is the direction the
    /// interface warns about.
    /// </para>
    /// <para>
    /// <b>Two more joined the list at #516</b>, and they are the same kind of thing as the first
    /// three rather than a new kind: <c>GST</c>'s position uncertainty and <c>GBS</c>'s RAIM
    /// integrity are computed per fix by a GNSS engine, and the captured screens carry neither. The
    /// exclusion is a reading of those screens, not a guess about the family.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryReading))]
    public void TheSmartClockReportsEveryReadingItsStatusScreenCarries(ReceiverReading reading)
    {
        bool onTheScreen = reading is not (
            ReceiverReading.DilutionOfPrecision or
            ReceiverReading.GeoidSeparation or
            ReceiverReading.SatellitesUsed or
            ReceiverReading.PositionUncertainty or
            ReceiverReading.FixIntegrity);

        Assert.Equal(
            onTheScreen,
            ((IReceiverDriver)new SmartClockDriver(new FakeTimeProvider(Whenever))).Reports(reading));
    }

    /// <summary>
    /// Every disciplined-oscillator reading is refused by a talker, because a talker has no
    /// oscillator to discipline.
    /// </summary>
    /// <remarks>
    /// <b><c>LeapSecond</c> left this list at #508, and it never quite belonged on it.</b> It is a
    /// fact about GPS rather than about an oscillator, and it sat here only because a talker could
    /// not be asked: <c>$PUBX,04</c> carries it and there was no send path. There is one now, so the
    /// family can report it on u-blox hardware and the honest answer flipped to true. The claim is
    /// family-level while the reading is per-device, so a MediaTek talker will dash for ever — the
    /// interface says which way to err, and a wrong <c>false</c> stops a user looking for something
    /// their receiver reports perfectly well.
    /// </remarks>
    [Theory]
    [InlineData(ReceiverReading.Tfom)]
    [InlineData(ReceiverReading.Ffom)]
    [InlineData(ReceiverReading.OnePpsTimeInterval)]
    [InlineData(ReceiverReading.OscillatorControl)]
    [InlineData(ReceiverReading.Holdover)]
    [InlineData(ReceiverReading.AntennaDelay)]
    [InlineData(ReceiverReading.OutputValidity)]
    [InlineData(ReceiverReading.DeviceIdentity)]
    [InlineData(ReceiverReading.TimeCodeFormat)]
    [InlineData(ReceiverReading.PowerOnHours)]
    [InlineData(ReceiverReading.HealthMonitor)]
    [InlineData(ReceiverReading.StatusRegisters)]
    [InlineData(ReceiverReading.DiagnosticLog)]
    [InlineData(ReceiverReading.ErrorQueue)]
    [InlineData(ReceiverReading.StatusScreen)]
    [InlineData(ReceiverReading.ElevationMask)]
    [InlineData(ReceiverReading.GpsEngineIdentity)]
    public void ATalkerRefusesTheDisciplinedOscillatorReadings(ReceiverReading reading) =>
        Assert.False(((IReceiverDriver)new NmeaDriver(new FakeTimeProvider(Whenever))).Reports(reading));

    /// <summary>
    /// A UCCM refuses the GPS engine identity, and claims everything else (#443).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are the assertion. <c>:DIAG:IDEN:GPS?</c> is a SmartClock node absent from
    /// <c>UccmCommands</c>, so the driver knows without hardware that it cannot answer — and a
    /// driver that cannot ask must not let the card imply the value is merely unread.
    /// </para>
    /// <para>
    /// <b>The set grew on 13 Sep 2026 and the guard did not.</b> This pinned exactly one entry,
    /// on the grounds that the driver had never met a receiver and a fuller switch would be twenty
    /// guesses presented as knowledge. It has met one since — five sittings, captures in
    /// <c>Uccm/Captures/</c> — so four more are now measured absences rather than guesses (#483).
    /// The rest staying <c>true</c> is still pinned here, for the same reason as before: so nobody
    /// completes the switch on the way past with entries no sitting supports.
    /// </para>
    /// </remarks>
    [Fact]
    public void AUccmRefusesOnlyTheReadingsMeasuredToBeAbsent()
    {
        IReceiverDriver driver = new UccmDriver(new FakeTimeProvider(Whenever));

        // :DIAG:IDEN:GPS? is a SmartClock node; the other four never answered in any sitting.
        ReceiverReading[] absent =
        [
            ReceiverReading.GpsEngineIdentity,
            ReceiverReading.StatusRegisters,
            ReceiverReading.LeapSecond,
            ReceiverReading.TimeCodeFormat,
            ReceiverReading.HealthMonitor,
        ];

        foreach (ReceiverReading reading in absent)
        {
            Assert.False(driver.Reports(reading), $"{reading} is a measured absence.");
        }

        foreach (ReceiverReading reading in Enum.GetValues<ReceiverReading>())
        {
            if (!absent.Contains(reading))
            {
                Assert.True(driver.Reports(reading), $"{reading} should be left unclaimed either way.");
            }
        }
    }

    /// <summary>
    /// But not the fix itself, which is the one thing NMEA is actually for.
    /// </summary>
    /// <remarks>
    /// The guard against over-claiming absence. `GGA`'s quality indicator and `GSA`'s mode say
    /// whether there is a fix and of what kind, so the Position page's readings are real; only its
    /// survey half is missing, and that is gated by commands rather than by this.
    /// </remarks>
    [Fact]
    public void ATalkerStillReportsTheFix() =>
        Assert.True(((IReceiverDriver)new NmeaDriver(new FakeTimeProvider(Whenever))).Reports(ReceiverReading.PositionHold));

    /// <summary>Every value of the enum is answered, so a reading added later cannot be forgotten.</summary>
    /// <remarks>
    /// A <c>switch</c> with a default arm answers a new enum member without anyone deciding what the
    /// answer should be. This does not catch that — nothing can, from outside — but it does catch a
    /// driver whose expression throws or fails to be exhaustive, which is the failure that would
    /// take a page down on navigation.
    /// </remarks>
    [Fact]
    public void EveryDriverAnswersEveryReadingWithoutThrowing()
    {
        FakeTimeProvider clock = new(Whenever);
        IReceiverDriver[] drivers =
            [new NmeaDriver(clock), new SmartClockDriver(clock), new UccmDriver(clock), new SilentDriver()];

        foreach (IReceiverDriver driver in drivers)
        {
            foreach (ReceiverReading reading in Enum.GetValues<ReceiverReading>())
            {
                _ = driver.Reports(reading);
            }
        }
    }

    public static TheoryData<ReceiverReading> EveryReading
    {
        get
        {
            TheoryData<ReceiverReading> data = [];
            foreach (ReceiverReading reading in Enum.GetValues<ReceiverReading>())
            {
                data.Add(reading);
            }

            return data;
        }
    }

    /// <summary>A driver from before #435: it implements nothing and must keep working.</summary>
    private sealed class SilentDriver : IReceiverDriver
    {
        public string Family => "Silent";

        public IReadOnlyList<Device.Commands.ScpiCommand> Commands { get; } = [];

        public PollCadence Cadence { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

        public IReadOnlyList<Device.Transport.SerialSettings> AutoDetectSequence { get; } = [];

        public PollPlan Plan { get; } = new([], RefusableIndex: null, FullStatus: "STATUS")
        {
            FastTierCarries = FastFields.All,
        };

        public bool Recognises(Device.Models.DeviceIdentity? identity) => false;

        public Device.Commands.ScpiCommand? Find(string? mnemonic) => null;

        public bool IsBlocked(string? header) => false;

        public TimeSpan TimeoutFor(string? mnemonic) => TimeSpan.FromSeconds(1);

        public Device.Models.ReceiverStatus Parse(string? response) =>
            new() { CapturedAt = Whenever };

        public SweepInterpretation InterpretSweep(IReadOnlyList<string?> answers) =>
            new(new FastReadings(null, null, null, null, null, null), Rejection: null);

        public Device.Models.ReceiverMode InterpretSyncState(string? syncState) =>
            Device.Models.ReceiverMode.Disconnected;
    }
}
