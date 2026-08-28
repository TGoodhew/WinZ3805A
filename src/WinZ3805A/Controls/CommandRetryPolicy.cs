using WinZ3805A.Services;

namespace WinZ3805A.Controls;

/// <summary>
/// Whether a failed tier C command is worth offering <b>Try again</b> for (§9.11, #251).
/// </summary>
/// <remarks>
/// <para>
/// §9.11's <i>Error — recoverable</i> row puts an action button on the error bar, and its example is
/// a setter refused with −222, data out of range: the user corrects the field and presses the button.
/// </para>
/// <para>
/// <b>The interesting half is when not to offer it.</b> An action certain to fail is worse than no
/// action — it invites the user to press a button twice and conclude the application is broken, when
/// the receiver has already told them something useful. The survey-start refusal is exactly that
/// case: <c>:GPS:POSition:SURVey:STATe ONCE</c> is declined with −300 while the receiver holds a
/// position (#229), and it will be declined identically every time, which is why
/// <see cref="SurveyRefusalAdvice"/> answers it with the route through instead.
/// </para>
/// <para>
/// The rule is drawn from the command rather than from a list of error codes, so it needs no
/// maintenance as the catalog grows:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>The receiver never answered</b> — a timeout or a dropped link. Nothing was decided, so
/// repeating the request is exactly the right offer. This is the case the button is most obviously
/// for, and the one §7.2's reconnect logic cannot cover because the link may be fine and the
/// receiver merely slow.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>The receiver refused, and the command carries parameters</b> — offer it. What the user changes
/// between presses is the value, so the second attempt is a different request even though it is the
/// same command. §9.11's own example is one of these.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>The receiver refused a command with no parameters</b> — do not offer it. There is nothing for
/// the user to change, so the second attempt is byte-for-byte the first and the receiver has already
/// given its answer.
/// </description>
/// </item>
/// </list>
/// <para>
/// Separate from the bar so it can be tested without a XAML runtime, the same reason
/// <see cref="SurveyRefusalAdvice"/> lives out here.
/// </para>
/// </remarks>
public static class CommandRetryPolicy
{
    /// <summary>The label §9.11 gives the action.</summary>
    public const string ActionLabel = "Try again";

    /// <summary>Whether <see cref="ActionLabel"/> should appear for this outcome.</summary>
    /// <param name="outcome">What happened, or null when the user cancelled the confirmation.</param>
    public static bool ShouldOffer(CommandOutcome? outcome) => outcome?.Kind switch
    {
        // Nothing was decided; the request simply did not land.
        CommandOutcomeKind.Failed => true,

        // Refused. Worth repeating only if there is something the user can change first.
        CommandOutcomeKind.Rejected => outcome.Command.Parameters.Count > 0,

        // Succeeded, or cancelled before it ran.
        _ => false,
    };
}
