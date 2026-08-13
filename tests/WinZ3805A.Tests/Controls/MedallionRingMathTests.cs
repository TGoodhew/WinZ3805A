using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// The medallion ring's scaling (§9.10.2, P0-18).
/// </summary>
/// <remarks>
/// A ring that fails to draw is obvious. A ring that draws at the wrong scale looks entirely
/// plausible and misleads the user who trusts it most, which is why the arithmetic is separated
/// from the control and asserted here.
/// </remarks>
public class MedallionRingMathTests
{
    /// <summary>
    /// The floor is what makes the ring trustworthy. A receiver holding to a couple of nanoseconds
    /// has almost no spread, and a purely relative scale would amplify that into a ring full of
    /// teeth — showing alarm where there is none.
    /// </summary>
    [Fact]
    public void ACalmLoopIsScaledByTheFloorRatherThanAmplifiedIntoNoise()
    {
        double?[] calm = [.. Enumerable.Range(0, 60).Select(i => (double?)(i % 2 == 0 ? 0.4 : -0.4))];

        double halfRange = MedallionRingMath.HalfRange(calm);

        Assert.Equal(MedallionRingMath.MinimumHalfRangeNanoseconds, halfRange);

        // And the bars stay tiny, which is the visible consequence.
        double? fraction = MedallionRingMath.Fraction(0.4, halfRange);
        Assert.NotNull(fraction);
        Assert.True(Math.Abs(fraction.Value) < 0.01, $"A calm loop drew at {fraction.Value:F3} of full scale.");
    }

    /// <summary>
    /// A hunting loop is what the ring is for. Once three sigma exceeds the floor the scale follows
    /// the data, so the teeth appear.
    /// </summary>
    [Fact]
    public void AHuntingLoopScalesToThreeSigma()
    {
        double?[] hunting = [.. Enumerable.Range(0, 60).Select(i => (double?)(i % 2 == 0 ? 300d : -300d))];

        double halfRange = MedallionRingMath.HalfRange(hunting);

        // Sigma is 300 for a square wave about zero, so three sigma is 900.
        Assert.Equal(900d, halfRange, 6);
        Assert.True(halfRange > MedallionRingMath.MinimumHalfRangeNanoseconds);
    }

    /// <summary>
    /// Zero-anchored, not mean-anchored. Centring on the window mean would hide a receiver sitting
    /// steadily 40 ns off — precisely the fault worth seeing.
    /// </summary>
    [Fact]
    public void ASteadyOffsetIsShownRatherThanNormalisedAway()
    {
        double?[] offset = [.. Enumerable.Repeat((double?)40d, 60)];

        double halfRange = MedallionRingMath.HalfRange(offset);
        double? fraction = MedallionRingMath.Fraction(40d, halfRange);

        Assert.NotNull(fraction);
        Assert.True(fraction.Value > 0.5, $"A steady 40 ns offset drew at only {fraction.Value:F3} of full scale.");
    }

    /// <summary>
    /// Gaps and zeros mean opposite things — "we did not hear" against "we heard, and it was
    /// perfect". Drawing them alike would be a lie about the second.
    /// </summary>
    [Fact]
    public void AGapIsNotTheSameAsAReadingOfZero()
    {
        double halfRange = MedallionRingMath.HalfRange([1d, 2d, null, 3d]);

        Assert.Null(MedallionRingMath.Fraction(null, halfRange));
        Assert.Equal(0d, MedallionRingMath.Fraction(0d, halfRange));
    }

    /// <summary>
    /// Gaps are excluded from the statistics rather than counted as zero, which would drag sigma
    /// down and make a hunting loop look calmer than it is. The poller drops ticks during every
    /// full-screen fetch, so this is the normal case rather than an edge one.
    /// </summary>
    [Fact]
    public void GapsDoNotDragTheScaleDown()
    {
        double?[] withoutGaps = [.. Enumerable.Range(0, 20).Select(i => (double?)(i % 2 == 0 ? 300d : -300d))];
        double?[] withGaps = [.. withoutGaps.Select((v, i) => i % 3 == 0 ? null : v)];

        double clean = MedallionRingMath.HalfRange(withoutGaps);
        double gappy = MedallionRingMath.HalfRange(withGaps);

        // Not identical - fewer samples - but the same order, not halved by phantom zeros.
        Assert.True(gappy > clean * 0.7, $"Gaps collapsed the scale from {clean:F0} to {gappy:F0}.");
    }

    [Fact]
    public void ValuesBeyondTheScaleClampRatherThanOverflowTheRing()
    {
        double halfRange = MedallionRingMath.HalfRange([1d, 2d]);

        Assert.Equal(1d, MedallionRingMath.Fraction(1e9, halfRange));
        Assert.Equal(-1d, MedallionRingMath.Fraction(-1e9, halfRange));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public void TooFewSamplesFallBackToTheFloorRatherThanDividingByNothing(int? count)
    {
        double?[]? samples = count is null ? null : [.. Enumerable.Repeat((double?)7d, count.Value)];

        Assert.Equal(MedallionRingMath.MinimumHalfRangeNanoseconds, MedallionRingMath.HalfRange(samples));
    }

    [Fact]
    public void NotANumberIsTreatedAsAGapRatherThanPoisoningTheScale()
    {
        double?[] samples = [10d, double.NaN, 20d, double.PositiveInfinity, 30d];

        double halfRange = MedallionRingMath.HalfRange(samples);

        Assert.False(double.IsNaN(halfRange));
        Assert.True(halfRange >= MedallionRingMath.MinimumHalfRangeNanoseconds);
        Assert.Null(MedallionRingMath.Fraction(double.NaN, halfRange));
    }

    // -------------------------------------------------------------------------------------
    // §10.3's mode table
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("LOCK", ReceiverMode.Locked, Severity.Success)]
    [InlineData("REC", ReceiverMode.Recovering, Severity.Caution)]
    [InlineData("WAIT", ReceiverMode.Waiting, Severity.Caution)]
    [InlineData("HOLD", ReceiverMode.Holdover, Severity.Critical)]
    [InlineData("POW", ReceiverMode.PowerUp, Severity.Neutral)]
    [InlineData("OFF", ReceiverMode.Off, Severity.Neutral)]
    public void EveryModeInTheTableMapsToItsSeverity(string keyword, ReceiverMode mode, Severity severity)
    {
        Assert.Equal(mode, ReceiverModes.FromSyncState(keyword));
        Assert.Equal(severity, ReceiverModes.SeverityOf(mode));
        Assert.False(string.IsNullOrWhiteSpace(ReceiverModes.TextOf(mode)));
        Assert.False(string.IsNullOrWhiteSpace(ReceiverModes.GlyphOf(mode)));
    }

    [Fact]
    public void TheKeywordIsReadThroughItsLeadingSpaceAndCase()
    {
        Assert.Equal(ReceiverMode.Locked, ReceiverModes.FromSyncState(" LOCK"));
        Assert.Equal(ReceiverMode.Locked, ReceiverModes.FromSyncState("lock"));
    }

    /// <summary>
    /// A mode the application does not understand is one it cannot describe honestly. Showing
    /// "locked" on a maybe would be the worst available default.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SOMETHING NEW")]
    public void AnUnrecognisedModeIsReportedAsDisconnectedRatherThanGuessed(string? keyword)
    {
        Assert.Equal(ReceiverMode.Disconnected, ReceiverModes.FromSyncState(keyword));
        Assert.Equal(Severity.Neutral, ReceiverModes.SeverityOf(ReceiverMode.Disconnected));
    }

    /// <summary>Every mode has a distinct glyph, or the table would be conveying less than it claims.</summary>
    [Fact]
    public void EveryModeHasItsOwnGlyph()
    {
        ReceiverMode[] modes = Enum.GetValues<ReceiverMode>();

        string[] glyphs = [.. modes.Select(ReceiverModes.GlyphOf)];

        Assert.Equal(glyphs.Length, glyphs.Distinct(StringComparer.Ordinal).Count());
    }
}
