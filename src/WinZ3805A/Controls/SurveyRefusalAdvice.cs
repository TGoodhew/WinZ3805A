using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Controls;

/// <summary>
/// What to add to §10.6's survey card when the receiver declines to start a survey (#229).
/// </summary>
/// <remarks>
/// <para>
/// A receiver that is already holding a position refuses <c>:GPS:POSition:SURVey:STATe ONCE</c> with
/// <b>−300, Device-specific error</b>, and there is no command that releases the hold. Established on
/// the 27 Aug 2026 backyard sitting and recorded in #229: the refusal survived full lock with nine
/// satellites and outputs valid, survived the Z3801A manual's alternative spelling, and sat alongside
/// a working query in the same <c>:GPS:</c> subtree — so the command is understood and declined
/// rather than unrecognised, which would have been −113.
/// </para>
/// <para>
/// The route through it is survey-on-power-up, and it is not a guess: the receiver was power-cycled
/// at ≈22:10 that evening with nothing connected to it, and had a survey 3.8 % along by the time the
/// application reached it four minutes later.
/// </para>
/// <para>
/// <b>Why this is advice and not a diagnosis.</b> −300 is by definition device-specific, so the
/// receiver is not saying *why*. The sentence therefore says a held position *may* be the cause and
/// names the route, which is true and actionable whether or not the hold is what stopped this
/// particular attempt. Claiming more would be asserting something the error code does not carry.
/// </para>
/// <para>
/// Separate from the page so it can be tested without a XAML runtime, which is the same reason
/// <see cref="TrendDecimation"/> and <see cref="MedallionRingMath"/> live out here.
/// </para>
/// </remarks>
public static class SurveyRefusalAdvice
{
    /// <summary>The code the receiver answers with when it declines a survey it understands.</summary>
    /// <remarks>
    /// Not −113. An undefined header would mean the spelling is wrong for this unit, which was the
    /// first hypothesis and is ruled out; −300 means it parsed the command and said no.
    /// </remarks>
    public const int DeclinedCode = -300;

    /// <summary>The sentence appended to the failure, or null when none applies.</summary>
    private const string HoldAdvice =
        "A receiver already holding a position may refuse to start a new survey. "
        + "Switch on Survey on power-up, then power-cycle the receiver.";

    /// <summary>
    /// The advice to append to a failed survey start, or <see langword="null"/> for a failure this
    /// has nothing useful to say about.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. A timeout, a transport fault or any other error code gets nothing added:
    /// an explanation offered for the wrong failure sends the user to power-cycle an instrument over
    /// a loose cable, and §9.11's copy rules are about the user being told what actually happened.
    /// </remarks>
    /// <param name="outcome">The outcome of the survey-start command, or null if the user cancelled.</param>
    public static string? ForFailedStart(CommandOutcome? outcome) =>
        outcome is { Succeeded: false, Error: ScpiError error } && error.Code == DeclinedCode
            ? HoldAdvice
            : null;
}
