namespace WinZ3805A.Device.Models;

/// <summary>
/// A coaxial cable's propagation delay per metre, for the §10.7 antenna-delay calculator.
/// </summary>
/// <remarks>
/// <para>
/// The receiver cannot know how far its antenna is; it subtracts whatever delay it is told
/// (<c>:GPS:REF:ADEL</c>). Getting that number wrong shifts the 1 PPS output by exactly the error,
/// so 20 m of guessing is 78 ns of systematic offset that nothing downstream will flag.
/// </para>
/// <para>
/// <b>Sourced from the 58503A guide, page 2-12.</b> "The RG 213 propagation delay is 1.54
/// nanoseconds per foot (5.05 ns/meter). The 9913 propagation delay is 1.2 nanoseconds per foot
/// (3.94 ns/meter)." Those are the two cables HP recommends for this antenna system. LMR-400 is not
/// in that manual — §10.7 substitutes it, reasonably, since it is what a modern installation is
/// likely to use and Belden 9913 is long out of production — so both are offered here rather than
/// one replacing the other.
/// </para>
/// </remarks>
public sealed record AntennaCable
{
    /// <summary>The speed of light expressed as a delay: 1 / c, in nanoseconds per metre.</summary>
    /// <remarks>
    /// 3.3356 ns/m is one metre at the speed of light in vacuum. A cable's delay is this divided by
    /// its velocity factor, which is where every figure in this table comes from and how a custom
    /// cable is computed (§10.7).
    /// </remarks>
    public const double VacuumDelayNanosecondsPerMetre = 3.3356;

    /// <summary>What the cable is called.</summary>
    public required string Name { get; init; }

    /// <summary>Propagation delay in nanoseconds per metre.</summary>
    public required double DelayNanosecondsPerMetre { get; init; }

    /// <summary>Where the figure came from, shown beside the choice.</summary>
    public required string Source { get; init; }

    /// <summary>RG-213, the 58503A guide's first recommendation.</summary>
    public static AntennaCable Rg213 { get; } = new()
    {
        Name = "RG-213 / Belden 8267",
        DelayNanosecondsPerMetre = 5.05,
        Source = "58503A guide, 1.54 ns/ft",
    };

    /// <summary>Belden 9913, the guide's second recommendation.</summary>
    public static AntennaCable Belden9913 { get; } = new()
    {
        Name = "Belden 9913",
        DelayNanosecondsPerMetre = 3.94,
        Source = "58503A guide, 1.2 ns/ft",
    };

    /// <summary>LMR-400, which §10.7 offers in the guide's place for a modern installation.</summary>
    public static AntennaCable Lmr400 { get; } = new()
    {
        Name = "LMR-400",
        DelayNanosecondsPerMetre = 3.93,
        Source = "§10.7, velocity factor 0.85",
    };

    /// <summary>The presets.</summary>
    /// <remarks>
    /// §10.7 lists RG-213, LMR-400 and Custom, in that order, and the first two lead here. Belden
    /// 9913 is offered as well, from the guide's own second recommendation (see the remarks above);
    /// §10.7 does not yet list it.
    /// </remarks>
    public static IReadOnlyList<AntennaCable> Presets { get; } = [Rg213, Lmr400, Belden9913];

    /// <summary>
    /// A cable described by its velocity factor rather than by name.
    /// </summary>
    /// <param name="velocityFactor">
    /// The fraction of the speed of light the signal travels at, between 0 and 1 exclusive. Foam
    /// dielectric coax is around 0.85, solid polyethylene around 0.66.
    /// </param>
    /// <returns>The cable, or <see langword="null"/> if the factor is not a usable one.</returns>
    /// <remarks>
    /// Null rather than an exception for a bad factor: this is fed straight from a text box, and a
    /// user halfway through typing "0." has not made an error worth throwing over.
    /// </remarks>
    public static AntennaCable? FromVelocityFactor(double velocityFactor)
    {
        if (double.IsNaN(velocityFactor) || velocityFactor <= 0 || velocityFactor >= 1)
        {
            return null;
        }

        return new AntennaCable
        {
            Name = $"Custom, velocity factor {velocityFactor:0.00}",
            DelayNanosecondsPerMetre = VacuumDelayNanosecondsPerMetre / velocityFactor,
            Source = "computed from the velocity factor",
        };
    }

    /// <summary>
    /// The delay for a given run of this cable, in nanoseconds.
    /// </summary>
    /// <param name="metres">Cable length. Negative lengths and nonsense give no answer.</param>
    /// <remarks>
    /// P0-11's acceptance criterion: LMR-400 at 20 m gives 78.7 ns ± 0.5, and 20 × 3.93 = 78.6.
    /// </remarks>
    public double? DelayFor(double metres)
    {
        if (double.IsNaN(metres) || double.IsInfinity(metres) || metres < 0)
        {
            return null;
        }

        return metres * DelayNanosecondsPerMetre;
    }

    /// <summary>
    /// The delay the receiver will accept, clamped to <c>:GPS:REF:ADEL</c>'s range.
    /// </summary>
    /// <remarks>
    /// §10.7 gives the field a range of 0 – 999 999 ns. Rejecting client-side is §10.6's rule for
    /// position and applies just as well here: a device error for a value the app could have caught
    /// tells the user nothing they can act on.
    /// </remarks>
    public static bool IsAcceptableDelay(double? nanoseconds) =>
        nanoseconds is double value
        && !double.IsNaN(value)
        && value >= 0
        && value <= 999_999;
}
