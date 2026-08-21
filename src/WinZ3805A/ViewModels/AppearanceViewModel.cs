using WinZ3805A.Controls;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// What the accent opt-in should do, decided without touching a <c>Brush</c>.
/// </summary>
/// <remarks>
/// The applier that walks the resource dictionary cannot be tested headlessly, so as little as
/// possible lives in it. Everything here — which ramp wins, whether the warning is owed, what the
/// acknowledgement records — is a function of two values and is exercised by
/// <c>AppearanceViewModelTests</c>.
/// </remarks>
public static class AppearanceViewModel
{
    /// <summary>The ramp to draw the accent from.</summary>
    /// <param name="preferences">What the user has chosen.</param>
    /// <param name="system">The Windows accent ramp, or null if it could not be read.</param>
    /// <returns>The system ramp when opted in and available, otherwise the brand ramp.</returns>
    /// <remarks>
    /// Falling back to the brand ramp rather than failing is the whole reason this returns a value
    /// instead of throwing: an accent is decoration, and an application that would not start
    /// because it could not read one would have its priorities backwards.
    /// </remarks>
    public static AccentRamp Resolve(AppearancePreferences preferences, AccentRamp? system) =>
        preferences.UseSystemAccent && system is AccentRamp ramp ? ramp : AccentRamp.Brand;

    /// <summary>
    /// The collision to warn about, or null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things have to be true at once. The user has to have opted in — the brand ramp is safe
    /// by construction and warning about it would be nonsense. The accent has to actually collide.
    /// And they must not already have been told <i>about this accent</i>.
    /// </para>
    /// <para>
    /// That last clause is why the acknowledgement stores a colour. A user who dismissed the
    /// warning for a red accent and later switched Windows to a green one should not carry the
    /// dismissal forward — and, more to the point, the reverse: dismissing it for a safe accent
    /// must not silence the warning for a dangerous one they choose next week.
    /// </para>
    /// </remarks>
    public static AccentCollision? WarningFor(AppearancePreferences preferences, AccentRamp? system)
    {
        if (!preferences.UseSystemAccent || system is not AccentRamp ramp)
        {
            return null;
        }

        AccentCollision? collision = AccentGuard.Check(ramp.Base.R, ramp.Base.G, ramp.Base.B);

        if (collision is null)
        {
            return null;
        }

        bool alreadyTold = preferences.HasAcknowledgedCollision
            && string.Equals(
                preferences.AcknowledgedAccent,
                ramp.Base.ToString(),
                StringComparison.OrdinalIgnoreCase);

        return alreadyTold ? null : collision;
    }

    /// <summary>The preferences after the user dismisses the warning for an accent.</summary>
    public static AppearancePreferences Acknowledge(
        AppearancePreferences preferences,
        AccentRamp system) =>
        preferences with
        {
            HasAcknowledgedCollision = true,
            AcknowledgedAccent = system.Base.ToString(),
        };

    /// <summary>
    /// The preferences after the user takes the warning's offer and reverts to the brand accent.
    /// </summary>
    /// <remarks>
    /// This clears the acknowledgement rather than setting it. The user did not dismiss the
    /// warning — they acted on it, so there is nothing outstanding to remember, and if they opt in
    /// again later the warning is owed again.
    /// </remarks>
    public static AppearancePreferences Revert(AppearancePreferences preferences) =>
        preferences with
        {
            UseSystemAccent = false,
            HasAcknowledgedCollision = false,
            AcknowledgedAccent = null,
        };
}
