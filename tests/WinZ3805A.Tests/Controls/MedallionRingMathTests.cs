using WinZ3805A.Controls;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;

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

    /// <summary>The shipped driver, whose table §10.3 tabulates and which now owns it (#304).</summary>
    private static SmartClockDriver SmartClock() => new(TimeProvider.System);
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
        Assert.Equal(mode, SmartClock().InterpretSyncState(keyword));
        Assert.Equal(severity, ReceiverModes.SeverityOf(mode));
        Assert.False(string.IsNullOrWhiteSpace(ReceiverModes.TextOf(mode)));
        Assert.False(string.IsNullOrWhiteSpace(ReceiverModes.GlyphOf(mode)));
    }

    [Fact]
    public void TheKeywordIsReadThroughItsLeadingSpaceAndCase()
    {
        Assert.Equal(ReceiverMode.Locked, SmartClock().InterpretSyncState(" LOCK"));
        Assert.Equal(ReceiverMode.Locked, SmartClock().InterpretSyncState("lock"));
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
        Assert.Equal(ReceiverMode.Disconnected, SmartClock().InterpretSyncState(keyword));
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
    // -------------------------------------------------------------------------------------
    // Glyph size (#48)
    // -------------------------------------------------------------------------------------

    /// <summary>§10.3's wireframe figure comes back exactly at the size it was drawn for.</summary>
    /// <remarks>
    /// §10.3 states one number — "glyph 56 px" inside a 160 px medallion — and a ratio is only
    /// defensible if it reproduces it. The glyph previously had no FontSize at all and inherited
    /// the body size, rendering about 12 px.
    /// </remarks>
    [Fact]
    public void TheGlyphIsTheSizeTheWireframeDraws() =>
        Assert.Equal(56.0, MedallionRingMath.GlyphSize(160), 3);

    /// <summary>The other §9.10.2 diameters follow the same proportion.</summary>
    /// <remarks>
    /// Kept as a ratio so a medallion size added later cannot arrive without a glyph size. Under
    /// high contrast the glyph is the only non-textual carrier of severity, so "it scales with the
    /// medallion" is the property that matters rather than any one pair (#48).
    /// </remarks>
    [Theory]
    [InlineData(64, 22.4)]
    [InlineData(96, 33.6)]
    [InlineData(160, 56.0)]
    public void TheGlyphScalesWithTheMedallion(double diameter, double expected) =>
        Assert.Equal(expected, MedallionRingMath.GlyphSize(diameter), 3);

    // -------------------------------------------------------------------------------------
    // Count size (#279, #307)
    // -------------------------------------------------------------------------------------

    /// <summary>The numeral is half the diameter at every size, so two digits fit inside the ring.</summary>
    /// <remarks>
    /// #279 derived the ratio at 64 px; #307 puts the count in the 160 px medallion as well, where
    /// the same ratio gives 80 — larger than the 56 px glyph it replaces, which is the point: G1
    /// measures the count's legibility at two metres and the glyph's only at arm's length. The
    /// ratio is the rule because the reason for it — two lining figures inside the ring — holds at
    /// every diameter, and a size-specific cap would make the count smallest where there is most room.
    /// </remarks>
    [Theory]
    [InlineData(64, 32.0)]
    [InlineData(96, 48.0)]
    [InlineData(160, 80.0)]
    public void TheCountIsHalfTheDiameter(double diameter, double expected) =>
        Assert.Equal(expected, MedallionRingMath.CountSize(diameter), 3);

    // -------------------------------------------------------------------------------------
    // Mark extent (§9.10.2, #307)
    // -------------------------------------------------------------------------------------

    /// <summary>A sparkline mark runs from the baseline by the reading's share of half the band.</summary>
    [Theory]
    [InlineData(1.0, 35.0)]
    [InlineData(-1.0, 25.0)]
    [InlineData(0.5, 32.5)]
    public void ASparklineMarkReachesItsShareOfTheBand(double fraction, double expectedOuter)
    {
        (double inner, double outer) = MedallionRingMath.SparklineMark(fraction, 30, 10);

        Assert.Equal(30.0, inner, 3);
        Assert.Equal(expectedOuter, outer, 3);
    }

    /// <summary>A reading of zero is a mark, not a gap: a perfect loop must not look like a dead one.</summary>
    [Fact]
    public void AReadingOfZeroStillGetsAOnePixelMark()
    {
        (double inner, double outer) = MedallionRingMath.SparklineMark(0, 30, 10);

        Assert.Equal(1.0, outer - inner, 3);
    }

    /// <summary>A reading too small for a pixel is lifted to one, on its own side of the baseline.</summary>
    [Theory]
    [InlineData(0.05, 1.0)]
    [InlineData(-0.05, -1.0)]
    public void ATinyReadingIsLiftedToOnePixelOnItsOwnSide(double fraction, double expectedLength)
    {
        (double inner, double outer) = MedallionRingMath.SparklineMark(fraction, 30, 10);

        Assert.Equal(expectedLength, outer - inner, 3);
    }

    /// <summary>
    /// The compact ring is uniform: every mark the same length, centred on the baseline, whatever
    /// the loop is doing (#307).
    /// </summary>
    /// <remarks>
    /// Sixty marks of differing length make a 64 px circle read as lumpy, and the circle is the one
    /// shape §9.7 relies on the eye finding without focusing. The sparkline is a property of the two
    /// larger sizes; this mark takes no reading at all, which is the property under test.
    /// </remarks>
    [Fact]
    public void AUniformMarkIsCentredOnTheBaselineAndHalfTheBandLong()
    {
        (double inner, double outer) = MedallionRingMath.UniformMark(30, 10);

        Assert.Equal(27.5, inner, 3);
        Assert.Equal(32.5, outer, 3);
    }

    /// <summary>The uniform mark stays inside the depth a full sparkline reading would use.</summary>
    /// <remarks>
    /// So the two rings share an outline, and switching the medallion between sizes changes its
    /// diameter and nothing about its silhouette.
    /// </remarks>
    [Fact]
    public void AUniformMarkNeverReachesPastAFullReading()
    {
        (double uniformInner, double uniformOuter) = MedallionRingMath.UniformMark(30, 10);
        (_, double fullOuter) = MedallionRingMath.SparklineMark(1.0, 30, 10);
        (_, double fullInner) = MedallionRingMath.SparklineMark(-1.0, 30, 10);

        Assert.True(uniformOuter <= fullOuter);
        Assert.True(uniformInner >= fullInner);
    }

    // -------------------------------------------------------------------------------------
    // §9.9's custom icon set (#320)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Holdover, and only holdover, has a custom icon.
    /// </summary>
    /// <remarks>
    /// It is the reason the set was authored. The medallion had been drawing a generic Warning
    /// glyph for it, which says <i>something is wrong</i> — and that is not what holdover means:
    /// the receiver is still producing a disciplined 10 MHz, from the oscillator's memory rather
    /// than from GPS. Every other mode keeps the stock glyph §10.3 chose for it, and asserting the
    /// nulls is what stops the custom set quietly spreading to modes that never asked for one.
    /// </remarks>
    [Theory]
    [InlineData(ReceiverMode.Holdover, "WzIconHoldover")]
    [InlineData(ReceiverMode.Locked, null)]
    [InlineData(ReceiverMode.Recovering, null)]
    [InlineData(ReceiverMode.Waiting, null)]
    [InlineData(ReceiverMode.PowerUp, null)]
    [InlineData(ReceiverMode.Off, null)]
    [InlineData(ReceiverMode.Disconnected, null)]
    public void OnlyHoldoverCarriesACustomGeometry(ReceiverMode mode, string? expected) =>
        Assert.Equal(expected, ReceiverModes.GeometryKeyOf(mode));

    /// <remarks>
    /// The stock glyph stays even where a custom one exists. §9.9 makes the Fluent font the baseline
    /// and the custom set the exception, so a key that fails to resolve leaves the medallion drawing
    /// what it drew last week rather than an empty centre.
    /// </remarks>
    [Fact]
    public void HoldoverKeepsItsFallbackGlyph() =>
        Assert.False(string.IsNullOrWhiteSpace(ReceiverModes.GlyphOf(ReceiverMode.Holdover)));
}
