using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Device.Models;
using WinZ3805A.Simulation;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The UCCM driver against the simulated module (#416), which is the only end-to-end check
/// available until hardware is.
/// </summary>
/// <remarks>
/// <para>
/// <b>These prove the driver and the simulator agree with each other, and nothing more.</b> Both
/// were written from the same third-party source, so a shared misreading passes here silently.
/// That is worth saying plainly rather than letting a row of green ticks imply otherwise.
/// </para>
/// <para>
/// What they do buy: the awkward parts of this family — the command echo, the interleaved time
/// codes, the two vendors' loop shapes, and the undefined-header reply a plain UCCM gives to a
/// UCCM-P query — are exercised as a sequence rather than as strings handed to a parser. Those are
/// the parts most likely to be wrong, and the parts a unit test of a parser cannot reach.
/// </para>
/// </remarks>
public sealed class UccmSimulatedSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static (UccmDriver Driver, UccmModuleSimulator Module, FakeTimeProvider Clock) Bench(
        UccmVendor vendor = UccmVendor.Symmetricom,
        UccmVariant variant = UccmVariant.Uccm)
    {
        FakeTimeProvider clock = new(Start);
        return (new UccmDriver(clock), new UccmModuleSimulator(vendor, variant, clock), clock);
    }

    private static SweepInterpretation Sweep(UccmDriver driver, UccmModuleSimulator module) =>
        driver.InterpretSweep([.. driver.Plan.FastTier.Select(module.Respond)]);

    // ---- the fast sweep, through the module's real reply shape ------------------------------------

    [Fact]
    public void AFastSweepAgainstALockedModuleReadsEveryFieldItCarries()
    {
        (UccmDriver driver, UccmModuleSimulator module, _) = Bench();

        SweepInterpretation result = Sweep(driver, module);

        Assert.Null(result.Rejection);
        Assert.Equal("1", result.Readings.SyncState);
        Assert.Equal(ReceiverMode.Locked, driver.InterpretSyncState(result.Readings.SyncState));
        Assert.Equal(-1.23, result.Readings.TimeIntervalNanoseconds!.Value, 6);
        Assert.Equal(12.5, result.Readings.EfcPercent!.Value, 6);
    }

    [Fact]
    public void AnInterleavedTimeCodeDoesNotDisturbTheSweep()
    {
        // The case Heather has a dedicated routine for. A time code lands between the echo and the
        // value in EVERY reply here, which is harsher than a real module and deliberately so.
        (UccmDriver driver, UccmModuleSimulator module, _) = Bench();
        module.InterleaveTimeCode = true;

        SweepInterpretation result = Sweep(driver, module);

        Assert.Null(result.Rejection);
        Assert.Equal("1", result.Readings.SyncState);
        Assert.Equal(-1.23, result.Readings.TimeIntervalNanoseconds!.Value, 6);
    }

    [Fact]
    public void AModuleThatIsNotLockedReportsSo()
    {
        (UccmDriver driver, UccmModuleSimulator module, _) = Bench();
        module.State = UccmSimulatedState.PowerUp;

        SweepInterpretation result = Sweep(driver, module);

        Assert.Equal(ReceiverMode.PowerUp, driver.InterpretSyncState(result.Readings.SyncState));
    }

    // ---- the full status -------------------------------------------------------------------------

    [Fact]
    public void TheStatusReplyYieldsMeritsSatellitesTheMaskAndTheClock()
    {
        (UccmDriver driver, UccmModuleSimulator module, _) = Bench();
        module.SatelliteCount = 9;

        ReceiverStatus status = driver.Parse(module.Respond(UccmCommands.Status));

        Assert.Equal(3, status.Tfom);
        Assert.Equal(0, status.Ffom);
        Assert.Equal(9, status.Tracked.Count);
        Assert.Equal(10, status.ElevationMaskDegrees);
        Assert.Equal(Start, status.DeviceDateTime);
        Assert.Empty(status.ParseWarnings);
    }

    [Fact]
    public void TheClockInTheStatusFollowsTheModulesOwnTime()
    {
        (UccmDriver driver, UccmModuleSimulator module, FakeTimeProvider clock) = Bench();
        clock.Advance(TimeSpan.FromHours(3));

        ReceiverStatus status = driver.Parse(module.Respond(UccmCommands.Status));

        Assert.Equal(Start.AddHours(3), status.DeviceDateTime);
    }

    [Theory]
    [InlineData(UccmSimulatedState.PowerUp, SmartClockMode.PowerUp)]
    [InlineData(UccmSimulatedState.Settling, SmartClockMode.Recovery)]
    [InlineData(UccmSimulatedState.Holdover, SmartClockMode.PowerUp)]
    public void TheStatusTextLinesDriveTheMode(UccmSimulatedState state, SmartClockMode expected)
    {
        // Holdover maps to PowerUp because the text line for it is "NO REF", which Heather reads as
        // acquiring. That is a mapping worth revisiting on hardware: a UCCM-P reports holdover
        // properly through :ROSC:HOLD:DUR?, and this text line is the poorer signal of the two.
        (UccmDriver driver, UccmModuleSimulator module, _) = Bench();
        module.State = state;

        Assert.Equal(expected, driver.Parse(module.Respond(UccmCommands.Status)).Mode);
    }

    [Fact]
    public void AnAntennaFaultIsVisibleInTheStatus()
    {
        (UccmDriver driver, UccmModuleSimulator module, _) = Bench();
        module.AntennaConnected = false;

        ReceiverStatus status = driver.Parse(module.Respond(UccmCommands.Status));

        Assert.False(status.GpsOnePpsValid);
    }

    // ---- the vendors ------------------------------------------------------------------------------

    [Theory]
    [InlineData(UccmVendor.Symmetricom)]
    [InlineData(UccmVendor.Trimble)]
    public void EachVendorsLoopReplyEstablishesThatVendor(UccmVendor vendor)
    {
        (UccmDriver driver, UccmModuleSimulator module, _) = Bench(vendor);

        Assert.False(driver.Profile.VendorKnown);

        driver.ReadLoop(module.Respond(UccmCommands.Loop));

        Assert.Equal(vendor, driver.Profile.Vendor);
    }

    [Fact]
    public void OnlySymmetricomReportsATemperature()
    {
        (UccmDriver symmetricom, UccmModuleSimulator symModule, _) = Bench(UccmVendor.Symmetricom);
        (UccmDriver trimble, UccmModuleSimulator trimModule, _) = Bench(UccmVendor.Trimble);

        Assert.NotNull(symmetricom.ReadLoop(symModule.Respond(UccmCommands.Loop)).TemperatureCorrection);
        Assert.Null(trimble.ReadLoop(trimModule.Respond(UccmCommands.Loop)).TemperatureCorrection);
    }

    [Fact]
    public void ALockedModuleOfEitherVendorReadsAsLockedOnceTheVendorIsKnown()
    {
        // The #418 failure, end to end: a locked Trimble must not read as unlocked.
        foreach (UccmVendor vendor in new[] { UccmVendor.Symmetricom, UccmVendor.Trimble })
        {
            (UccmDriver driver, UccmModuleSimulator module, _) = Bench(vendor);
            driver.ReadLoop(module.Respond(UccmCommands.Loop));

            ReceiverStatus status = driver.Parse(module.Respond(UccmCommands.Status));

            Assert.Equal(SmartClockMode.Locked, status.Mode);
        }
    }

    // ---- the variants -----------------------------------------------------------------------------

    [Fact]
    public void APlainUccmRefusesTheUccmPQueriesAndThatIsHowTheVariantIsTold()
    {
        (_, UccmModuleSimulator plain, _) = Bench(variant: UccmVariant.Uccm);
        (_, UccmModuleSimulator uccmP, _) = Bench(variant: UccmVariant.UccmP);

        foreach (string query in UccmCommands.UccmPOnly)
        {
            Assert.True(UccmReply.IsError(plain.Respond(query), query));
            Assert.False(UccmReply.IsError(uccmP.Respond(query), query));
        }
    }

    [Fact]
    public void AUccmPInHoldoverReportsItsDurationAndThatItIsInIt()
    {
        (_, UccmModuleSimulator module, _) = Bench(variant: UccmVariant.UccmP);
        module.State = UccmSimulatedState.Holdover;

        (TimeSpan? duration, bool? active) =
            UccmScalars.ReadHoldover(module.Respond(UccmCommands.HoldoverDuration));

        Assert.Equal(TimeSpan.FromSeconds(412), duration);
        Assert.True(active);
    }

    [Fact]
    public void APlainUccmsHoldoverReadsAsAbsentRatherThanAsZero()
    {
        // §11.1: the refusal means "no reading", which must not render as a real zero holdover.
        (_, UccmModuleSimulator module, _) = Bench(variant: UccmVariant.Uccm);

        (TimeSpan? duration, bool? active) =
            UccmScalars.ReadHoldover(module.Respond(UccmCommands.HoldoverDuration));

        Assert.Null(duration);
        Assert.Null(active);
    }

    // ---- the scalars ------------------------------------------------------------------------------

    [Fact]
    public void ThePositionIsReadFromDegreesMinutesAndSeconds()
    {
        (_, UccmModuleSimulator module, _) = Bench();

        GeoPosition? position = UccmScalars.ReadPosition(module.Respond("GPS:POS?"));

        Assert.NotNull(position);
        Assert.Equal(37 + (26 / 60.0) + (15.30 / 3600.0), position.LatitudeDegrees!.Value, 9);
        Assert.Equal(-(122 + (10 / 60.0) + (30.10 / 3600.0)), position.LongitudeDegrees!.Value, 9);
        Assert.Equal(25.4, position.HeightMetres!.Value, 6);
    }

    [Fact]
    public void TheCableDelayAndElevationMaskAreRead()
    {
        (_, UccmModuleSimulator module, _) = Bench();

        Assert.Equal(120.0, UccmScalars.ReadCableDelayNanoseconds(module.Respond("GPS:REF:ADEL?"))!.Value, 6);
        Assert.Equal(10, UccmScalars.ReadElevationMaskDegrees(module.Respond("GPS:SAT:TRAC:EMAN?")));
    }

    [Fact]
    public void AnIdentityFromEitherVendorIsRecognisedAsAUccm()
    {
        (UccmDriver driver, UccmModuleSimulator module, _) = Bench(UccmVendor.Trimble, UccmVariant.UccmP);
        string? identity = UccmReply.FirstPayload(module.Respond("*IDN?"), "*IDN?");

        Assert.NotNull(identity);
        string[] fields = identity.Split(',');
        Assert.True(driver.Recognises(new(fields[0], fields[1], fields[2], fields[3], ReceiverModel.Unknown)));
    }

    // ---- the never-throw rule, against everything the module can say -------------------------------

    [Fact]
    public void NoReplyTheModuleCanProduceMakesTheDriverThrow()
    {
        foreach (UccmVendor vendor in Enum.GetValues<UccmVendor>())
        {
            foreach (UccmVariant variant in Enum.GetValues<UccmVariant>())
            {
                foreach (UccmSimulatedState state in Enum.GetValues<UccmSimulatedState>())
                {
                    (UccmDriver driver, UccmModuleSimulator module, _) = Bench(vendor, variant);
                    module.State = state;
                    module.AntennaConnected = state != UccmSimulatedState.Holdover;
                    module.InterleaveTimeCode = true;

                    foreach (string command in driver.Commands.Select(c => c.Mnemonic))
                    {
                        string reply = module.Respond(command);
                        Assert.NotNull(driver.Parse(reply));
                        Assert.NotNull(driver.InterpretSweep([reply, reply, reply]));
                        Assert.NotNull(driver.ReadLoop(reply));
                    }
                }
            }
        }
    }

    // ---- holdover, which the lamp alone cannot see (#534) -----------------------------------------

    /// <summary>
    /// A real holdover frame overrides a lamp that still says locked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the defect measured on the bench on 13 Sep 2026, end to end through the driver. A
    /// Trimble UCCM-P was locked with a valid fix, its antenna was physically disconnected, and for
    /// fourteen minutes <c>LED:GPSL?</c> went on answering <c>1</c> — so the mode the shell shows,
    /// which comes from <c>InterpretSyncState</c> on the sweep's sync token, stayed
    /// <see cref="ReceiverMode.Locked"/> and the primary window read "Locked to GPS" about a
    /// free-running oscillator.
    /// </para>
    /// <para>
    /// The frame below is that receiver, captured at 10:26:56 and archived in
    /// <c>Uccm/Captures/transitions-13sep2026.frames.txt</c>. The simulator's lamp answers <c>1</c>,
    /// exactly as the hardware did, so the only thing that can change the outcome is the frame.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnOverheardHoldoverFrameOutranksALampThatSaysLocked()
    {
        (UccmDriver driver, UccmModuleSimulator module, _) = Bench();

        // Locked first, and from the same lamp reply - so the difference below is the frame alone.
        Assert.Equal(ReceiverMode.Locked, driver.InterpretSyncState(Sweep(driver, module).Readings.SyncState));

        driver.Observe([[
            0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
            0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0xD0, 0xB0, 0x62, 0x00, 0x12,
            0x60, 0x0C, 0x4F, 0x90, 0x00, 0x00, 0x00, 0x00, 0xC7, 0x74, 0xCA,
        ]]);

        SweepInterpretation result = Sweep(driver, module);

        Assert.Null(result.Rejection);
        Assert.Equal(ReceiverMode.Holdover, driver.InterpretSyncState(result.Readings.SyncState));
    }

    /// <summary>
    /// A frame from a receiver that has never locked does not read as holdover.
    /// </summary>
    /// <remarks>
    /// The complement, and the reason the rule is about history rather than about the lock byte:
    /// this frame carries the <i>same</i> lock byte <c>4F</c> and the <i>same</i> date byte <c>90</c>
    /// as the holdover frame above. It is the cold module from the same morning, powered up with no
    /// antenna, and it must not be mistaken for one that has lost a reference it never had.
    /// </remarks>
    [Fact]
    public void AFrameFromAReceiverThatHasNeverLockedIsNotHoldover()
    {
        (UccmDriver driver, UccmModuleSimulator module, _) = Bench();

        driver.Observe([[
            0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
            0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x24, 0xEA, 0x00, 0x02, 0x00, 0x00,
            0x41, 0x08, 0x4F, 0x90, 0x00, 0x00, 0x00, 0x00, 0x47, 0xE7, 0xCA,
        ]]);

        SweepInterpretation result = Sweep(driver, module);

        Assert.NotEqual(ReceiverMode.Holdover, driver.InterpretSyncState(result.Readings.SyncState));
    }
}
