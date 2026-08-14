using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Parsing;

/// <summary>
/// P0-11's antenna cable delay calculator.
/// </summary>
/// <remarks>
/// The receiver cannot know how far its antenna is; it subtracts whatever it is told. An error here
/// becomes a systematic offset on the 1 PPS output of exactly that size, and nothing downstream
/// flags it — which is why the arithmetic is worth pinning rather than eyeballing.
/// </remarks>
public sealed class AntennaCableTests
{
    /// <remarks>P0-11, word for word: "Given LMR-400 at 20 m, then computed delay is 78.7 ns ±0.5".</remarks>
    [Fact]
    public void TheP011AcceptanceCriterionHolds()
    {
        double? delay = AntennaCable.Lmr400.DelayFor(20);

        Assert.NotNull(delay);
        Assert.InRange(delay.Value, 78.2, 79.2);
    }

    /// <remarks>
    /// From the 58503A guide, page 2-12: RG-213 is 1.54 ns/ft, Belden 9913 is 1.2 ns/ft. Both
    /// convert to the per-metre figures used here.
    /// </remarks>
    [Fact]
    public void ThePresetsMatchTheManual()
    {
        Assert.Equal(5.05, AntennaCable.Rg213.DelayNanosecondsPerMetre, 3);
        Assert.Equal(3.94, AntennaCable.Belden9913.DelayNanosecondsPerMetre, 3);

        // 1.54 ns/ft and 1.2 ns/ft, converted. The guide gives both forms and they agree.
        Assert.Equal(1.54, AntennaCable.Rg213.DelayNanosecondsPerMetre * 0.3048, 2);
        Assert.Equal(1.20, AntennaCable.Belden9913.DelayNanosecondsPerMetre * 0.3048, 2);
    }

    /// <summary>
    /// The guide's own cable assemblies carry a labelled nominal delay, and the table reproduces it.
    /// </summary>
    /// <remarks>
    /// Table 2-1: the 58506A is 50 ft of RG-213 and is labelled 77 ns; the 58507A is 100 ft and 154
    /// ns; the 58508A is 175 ft and 270 ns. Anyone with HP's own cable can check the calculator
    /// against the label on it, so it had better agree.
    /// </remarks>
    [Theory]
    [InlineData(15.2, 77)]
    [InlineData(30.5, 154)]
    [InlineData(53.3, 270)]
    public void TheHpCableAssembliesComputeToTheirLabelledDelay(double metres, double labelled)
    {
        double? delay = AntennaCable.Rg213.DelayFor(metres);

        Assert.NotNull(delay);
        Assert.InRange(delay.Value, labelled - 3, labelled + 3);
    }

    /// <remarks>
    /// §10.7's custom option: 3.3356 / VF ns/m. 3.3356 ns is one metre at the speed of light, so a
    /// velocity factor of 1 is vacuum and anything real is slower.
    /// </remarks>
    [Theory]
    [InlineData(0.85, 3.924)]
    [InlineData(0.66, 5.054)]
    [InlineData(0.80, 4.170)]
    public void ACustomCableComesFromItsVelocityFactor(double velocityFactor, double expected)
    {
        AntennaCable? cable = AntennaCable.FromVelocityFactor(velocityFactor);

        Assert.NotNull(cable);
        Assert.Equal(expected, cable.DelayNanosecondsPerMetre, 2);
    }

    /// <remarks>
    /// A velocity factor of 0.66 is solid-polyethylene coax, which is what RG-213 is — and it lands
    /// within a hundredth of the manual's own figure for RG-213. The two routes agree.
    /// </remarks>
    [Fact]
    public void TheVelocityFactorRouteAgreesWithTheNamedPreset()
    {
        AntennaCable? computed = AntennaCable.FromVelocityFactor(0.66);

        Assert.NotNull(computed);

        // Within a hundredth of a nanosecond per metre. Comparing to a rounded decimal place would
        // fail on 5.054 against 5.05 for no reason a reader would accept.
        Assert.True(
            Math.Abs(AntennaCable.Rg213.DelayNanosecondsPerMetre - computed.DelayNanosecondsPerMetre) < 0.01,
            $"{computed.DelayNanosecondsPerMetre} is not within 0.01 of {AntennaCable.Rg213.DelayNanosecondsPerMetre}");
    }

    /// <remarks>
    /// Null rather than a throw: this comes straight from a text box, and a user halfway through
    /// typing has not made an error worth an exception.
    /// </remarks>
    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(1.5)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void AnImpossibleVelocityFactorGivesNoCable(double velocityFactor) =>
        Assert.Null(AntennaCable.FromVelocityFactor(velocityFactor));

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnImpossibleLengthGivesNoDelay(double metres) =>
        Assert.Null(AntennaCable.Lmr400.DelayFor(metres));

    [Fact]
    public void ZeroLengthIsZeroDelay() =>
        Assert.Equal(0, AntennaCable.Lmr400.DelayFor(0));

    /// <remarks>
    /// §10.7 gives <c>:GPS:REF:ADEL</c> a range of 0 – 999 999 ns. Rejecting client-side is §10.6's
    /// rule for position and applies here too: a device error for a value the app could have caught
    /// tells the user nothing they can act on.
    /// </remarks>
    [Theory]
    [InlineData(0d, true)]
    [InlineData(78.6, true)]
    [InlineData(999_999d, true)]
    [InlineData(1_000_000d, false)]
    [InlineData(-1d, false)]
    [InlineData(null, false)]
    public void TheAcceptableRangeIsTheDevicesOwn(double? nanoseconds, bool acceptable) =>
        Assert.Equal(acceptable, AntennaCable.IsAcceptableDelay(nanoseconds));

    /// <remarks>
    /// A very long run is still a legitimate figure — the guide's own 58509A line amplifier exists
    /// for runs past 53 m — so nothing here caps the length short of the device's own limit.
    /// </remarks>
    [Fact]
    public void ALongRunIsStillComputed()
    {
        double? delay = AntennaCable.Rg213.DelayFor(300);

        Assert.NotNull(delay);
        Assert.True(AntennaCable.IsAcceptableDelay(delay));
    }

    [Fact]
    public void EveryPresetIsNamedAndSourced() =>
        Assert.All(AntennaCable.Presets, cable =>
        {
            Assert.False(string.IsNullOrWhiteSpace(cable.Name));
            Assert.False(string.IsNullOrWhiteSpace(cable.Source));
            Assert.InRange(cable.DelayNanosecondsPerMetre, AntennaCable.VacuumDelayNanosecondsPerMetre, 10);
        });
}
