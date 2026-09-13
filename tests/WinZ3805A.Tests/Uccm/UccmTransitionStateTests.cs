using WinZ3805A.Device.Drivers.Uccm;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The state bytes of the <c>C5</c> time code, in the states a receiver only reaches while someone
/// is standing at the bench with their hand on the antenna.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other time-code fixture in this project was taken locked and settled.</b> Forty of the
/// forty-four bytes never moved, so the corpus could confirm that the parser reads Lady Heather's
/// claims correctly and nothing else — a fixture where a field never varies cannot show that the
/// field was read from the right offset.
/// </para>
/// <para>
/// These three frames come from the 13 Sep 2026 transition sitting, archived with their provenance
/// as <c>Uccm/Captures/transitions-13sep2026.*</c>. They were taken minutes apart from the same
/// module across a power cycle, a cold acquisition, and a genuine holdover, and between them they
/// move five state bytes that had been constant in every capture ever taken here.
/// </para>
/// <para>
/// <b>What these tests are for is the discrimination at the bottom</b>, not the field-by-field
/// assertions above it. Holdover and cold settling carry the <i>same</i> lock byte, so a driver
/// reading only that byte cannot tell a module that has lost its reference from one that has never
/// had one. #534 is the audit that fixes it; this is the ground truth it has to satisfy.
/// </para>
/// </remarks>
public class UccmTransitionStateTests
{
    /// <summary>
    /// Powered up with the antenna disconnected: the module has never had a fix.
    /// </summary>
    /// <remarks>
    /// The first frame the module emitted after the 10:03:03 power cycle, two seconds after boot.
    /// Its counter reads from a base of 1999-08-22 00:00:00 UTC, which is what this firmware counts
    /// from when it has no fix at all.
    /// </remarks>
    private static ReadOnlySpan<byte> ColdNoAntenna =>
    [
        0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
        0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x24, 0xEA, 0x00, 0x02, 0x00, 0x00,
        0x41, 0x08, 0x4F, 0x90, 0x00, 0x00, 0x00, 0x00, 0x47, 0xE7, 0xCA,
    ];

    /// <summary>
    /// True holdover: locked with a valid fix at 10:11:08, antenna pulled at 10:12:40.
    /// </summary>
    /// <remarks>
    /// Captured at 10:26:56 +10:00, fourteen minutes into the holdover. Through all of it the
    /// module reported <c>UCCM A Status[ACTIVE]</c>, never degraded TFOM or FFOM past 2, and
    /// answered <c>LED:GPSL?</c> with <c>1</c> — so <b>nothing in the text said holdover</b>, and
    /// these bytes are the only evidence the receiver gave that anything was wrong.
    /// </remarks>
    private static ReadOnlySpan<byte> Holdover =>
    [
        0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
        0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0xD0, 0xB0, 0x62, 0x00, 0x12,
        0x60, 0x0C, 0x4F, 0x90, 0x00, 0x00, 0x00, 0x00, 0xC7, 0x74, 0xCA,
    ];

    /// <summary>
    /// Locked again, eleven seconds after the antenna went back on at 10:28:18.
    /// </summary>
    private static ReadOnlySpan<byte> Relocked =>
    [
        0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
        0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0xD0, 0xB0, 0xD0, 0x00, 0x12,
        0x60, 0x04, 0x45, 0x80, 0x00, 0x00, 0x00, 0x00, 0xA5, 0x97, 0xCA,
    ];

    /// <summary>
    /// With no fix, the leap offset is absent and the clock counts from the firmware's own base.
    /// </summary>
    /// <remarks>
    /// <b>The absent leap offset is what keeps the bogus date off the screen.</b>
    /// <c>LeapSecondOffset</c> is null, so <c>UtcTime</c> is null, so the card renders an em dash
    /// rather than 1999. The screen's own <c>(?)</c> marker independently breaks the positional
    /// date parse. Two unrelated accidents produce the correct outcome, which is worth a test
    /// precisely because neither was designed to.
    /// </remarks>
    [Fact]
    public void WithNoFixTheLeapOffsetIsAbsentAndTheClockRunsFromTheFirmwareBase()
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(ColdNoAntenna);

        Assert.NotNull(code);
        Assert.Null(code.LeapSecondOffset);
        Assert.Null(code.UtcTime);
        Assert.Equal(
            new DateTimeOffset(1999, 8, 22, 0, 0, 2, TimeSpan.Zero),
            code.GpsTime);
    }

    /// <summary>
    /// The holdover frame's own clock agrees with the wall clock when it was captured.
    /// </summary>
    /// <remarks>
    /// The file recorded this frame at 10:26:56 +10:00, which is 00:26:56 UTC. The frame says GPS
    /// 00:27:14 and carries a retained leap offset of 18, so <c>UtcTime</c> lands exactly on the
    /// capture timestamp. That confirms offsets 27-30, the byte order, the epoch <i>and</i> the
    /// direction the leap correction is applied — independently of the 12 Sep sitting that first
    /// established them, and from a module that had lost its reference a quarter of an hour
    /// earlier.
    /// </remarks>
    [Fact]
    public void TheHoldoverFrameKeepsTimeThroughTheRetainedLeapOffset()
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(Holdover);

        Assert.NotNull(code);
        Assert.Equal(18, code.LeapSecondOffset);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 0, 27, 14, TimeSpan.Zero), code.GpsTime);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 0, 26, 56, TimeSpan.Zero), code.UtcTime);
    }

    /// <summary>
    /// Heather's Trimble lock values, both of them, off real hardware.
    /// </summary>
    /// <remarks>
    /// <c>45</c> locked and <c>4F</c> settling were read out of her comments and carried as a
    /// hypothesis until this sitting. <c>41</c>, which the driver also carries as Trimble's
    /// power-up value, is <b>not</b> a UCCM-P value: her comments distinguish <c>41 -&gt; 4F -&gt;
    /// 45</c> on a plain UCCM from <c>4F -&gt; 45</c> on a UCCM-P, and this module's first frame
    /// after power-on already read <c>4F</c>.
    /// </remarks>
    [Theory]
    [InlineData(0x45, true)]
    [InlineData(0x4F, false)]
    public void TheTrimbleLockValuesAreWhatHeatherRecorded(int lockState, bool locked)
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(locked ? Relocked : Holdover);

        Assert.NotNull(code);
        Assert.Equal(lockState, code.LockState);
    }

    /// <summary>
    /// The antenna byte carries a value neither Heather nor this driver has a name for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Heather records <c>00</c> at power-up, <c>04</c> normal, <c>0C</c> open or shorted, and
    /// <c>06</c> normal(?). Powered up with no antenna attached, this module reads <b><c>0x08</c></b>.
    /// </para>
    /// <para>
    /// <b>The two disconnects took different paths and that is the finding, not a curiosity.</b>
    /// Pulling the antenna from a locked module gave <c>04 -&gt; 0C</c> and reconnecting gave
    /// <c>0C -&gt; 04</c> at once, exactly as she documents. Powering up with no antenna gave
    /// <c>08</c>, and reconnecting from there went <c>08 -&gt; 00 -&gt; 04</c> over about three
    /// minutes. Her table describes the warm path correctly; the cold path is what nobody recorded.
    /// </para>
    /// <para>
    /// <b>This test was written pinning the defect, and then failed on the fix, which is what it
    /// was for.</b> It originally asserted <c>AntennaOk</c> was <c>null</c> for <c>0x08</c> — the
    /// behaviour at the time — with a note saying the pin existed so that #534 would change it
    /// deliberately rather than by accident. Adding <c>0x08</c> to the table broke it on the next
    /// run. The assertion below is the corrected expectation and the remark is kept so the sequence
    /// is legible: the value was measured, pinned as wrong, and then fixed.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheColdAntennaValueIsNotInEitherTable()
    {
        UccmTimeCode? cold = UccmTimeCode.TryParse(ColdNoAntenna);
        UccmTimeCode? warm = UccmTimeCode.TryParse(Holdover);

        Assert.NotNull(cold);
        Assert.NotNull(warm);

        Assert.Equal(0x08, cold.AntennaState);
        Assert.False(cold.AntennaOk);

        Assert.Equal(0x0C, warm.AntennaState);
        Assert.False(warm.AntennaOk);
    }

    /// <summary>
    /// <b>Holdover and cold settling are the same lock byte, and different frames.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test the sitting was taken for. A module that has lost its reference and one
    /// that has never had one both report <c>0x4F</c> at offset 35 and <c>0x90</c> at offset 36, so
    /// <c>ModeFromLockState</c> — which reads offset 35 and nothing else — maps both to
    /// <c>Recovery</c>. Neither is reported as holdover, and in fact nothing in the UCCM status
    /// parser ever assigns <c>SmartClockMode.Holdover</c> at all.
    /// </para>
    /// <para>
    /// But the frames are not ambiguous. Holdover carries a leap offset and a stable PPS state, so
    /// the module <i>had</i> a fix; the date has gone invalid, so it has since lost it. A module
    /// that has never locked has no leap offset and a PPS state still settling. That combination
    /// cannot occur on a receiver that has never had a fix, which makes it a sound discriminator
    /// rather than a heuristic.
    /// </para>
    /// </remarks>
    [Fact]
    public void HoldoverIsIndistinguishableByLockByteAndPlainInTheRest()
    {
        UccmTimeCode? cold = UccmTimeCode.TryParse(ColdNoAntenna);
        UccmTimeCode? holdover = UccmTimeCode.TryParse(Holdover);

        Assert.NotNull(cold);
        Assert.NotNull(holdover);

        // The bytes the driver reads today. Identical, which is the defect.
        Assert.Equal(cold.LockState, holdover.LockState);
        Assert.Equal(cold.DateValidityState, holdover.DateValidityState);

        // The bytes it does not read. "Had a fix" versus "never had one".
        Assert.Null(cold.LeapSecondOffset);
        Assert.Equal(18, holdover.LeapSecondOffset);
        Assert.Equal(0x41, cold.PpsState);
        Assert.Equal(0x60, holdover.PpsState);
    }

    /// <summary>
    /// <b>Exactly one of the eight captured state combinations is holdover.</b>
    /// </summary>
    /// <remarks>
    /// The discriminator is only worth anything if it is specific. These are every distinct
    /// combination of offsets 32 to 36 in the 13 Sep 2026 capture — leap, PPS, antenna, lock, date —
    /// with the number of frames each accounted for. Seven of them are a receiver that has never
    /// locked, is acquiring, is transitional, or is locked; one is a module that had a fix and lost
    /// it. A rule that said yes to two of these would be no better than the lock byte it replaces.
    /// </remarks>
    [Theory]
    [InlineData(0x00, 0x41, 0x08, 0x4F, 0x90, false)] // cold, antenna never present     (111 frames)
    [InlineData(0x00, 0x41, 0x00, 0x4F, 0x90, false)] // antenna back, not yet validated  (64 frames)
    [InlineData(0x00, 0x41, 0x00, 0x4F, 0x80, false)] // transitional                      (5 frames)
    [InlineData(0x12, 0x41, 0x00, 0x4F, 0x80, false)] // transitional                     (25 frames)
    [InlineData(0x12, 0x41, 0x04, 0x4F, 0x80, false)] // acquiring, antenna good          (50 frames)
    [InlineData(0x12, 0x60, 0x04, 0x45, 0x80, false)] // locked                           (59 frames)
    [InlineData(0x12, 0x60, 0x04, 0x4F, 0x90, true)]  // holdover, antenna byte lagging    (9 frames)
    [InlineData(0x12, 0x60, 0x0C, 0x4F, 0x90, true)]  // holdover, settled               (462 frames)
    [InlineData(0x12, 0x70, 0x09, 0x4F, 0x90, true)]  // holdover, four hours in          (30 frames)
    public void OnlyTheHoldoverCombinationsReadAsHoldover(
        int leap, int pps, int antenna, int lockState, int date, bool expected)
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(FrameWith(leap, pps, antenna, lockState, date));

        Assert.NotNull(code);
        Assert.Equal(expected, code.InHoldover);
    }

    /// <summary>
    /// The antenna-disconnected value the bench measured now reads as a fault rather than a shrug.
    /// </summary>
    [Theory]
    [InlineData(0x04, true)]
    [InlineData(0x06, true)]
    [InlineData(0x08, false)] // measured 13 Sep 2026; in neither Heather's table nor ours before
    [InlineData(0x09, false)] // measured four hours into holdover; also in neither table
    [InlineData(0x0C, false)]
    [InlineData(0x00, null)]  // power-up, and also a reconnected antenna still being validated
    public void TheAntennaByteIsRead(int antenna, bool? expected)
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(FrameWith(0x12, 0x60, antenna, 0x45, 0x80));

        Assert.NotNull(code);
        Assert.Equal(expected, code.AntennaOk);
    }

    /// <summary>
    /// An unknown vendor answers null rather than guessing which byte means a valid date.
    /// </summary>
    /// <remarks>
    /// Symmetricom reads <c>40</c> valid and Trimble <c>80</c>, with no bit in common, so a byte
    /// belonging to neither family cannot be decided. #418 is the issue about exactly this class of
    /// guess.
    /// </remarks>
    [Fact]
    public void AnUnknownVendorWillNotGuessAtHoldover()
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(FrameWith(0x12, 0x60, 0x0C, 0x22, 0x22));

        Assert.NotNull(code);
        Assert.Null(code.DateValid);
        Assert.Null(code.InHoldover);
    }

    /// <summary>
    /// A frame with the five state bytes set, and everything else as the module actually sends it.
    /// </summary>
    private static byte[] FrameWith(int leap, int pps, int antenna, int lockState, int date)
    {
        byte[] frame = Relocked.ToArray();
        frame[32] = (byte)leap;
        frame[33] = (byte)pps;
        frame[34] = (byte)antenna;
        frame[35] = (byte)lockState;
        frame[36] = (byte)date;
        return frame;
    }
}
