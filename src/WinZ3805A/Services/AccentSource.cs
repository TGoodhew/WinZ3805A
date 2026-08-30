namespace WinZ3805A.Services;

/// <summary>
/// Which accent ramp was actually applied (#290).
/// </summary>
/// <remarks>
/// The distinction that matters is between this and <c>AppearancePreferences.UseSystemAccent</c>,
/// which records what the user <i>asked</i> for. They differ whenever the Windows accent cannot be
/// read — and from 21 Aug 2026 (#165) until 29 Aug (#290) the startup log reported the preference
/// while its message said <c>source</c>.
/// </remarks>
public enum AccentSource
{
    /// <summary>Nothing was applied — high contrast, or the token dictionary could not be read.</summary>
    Unchanged,

    /// <summary>The built-in brand ramp: either chosen, or fallen back to.</summary>
    BuiltIn,

    /// <summary>The user's Windows accent, read successfully and applied.</summary>
    Windows,
}

/// <summary>
/// Decides which ramp an accent application actually used.
/// </summary>
/// <remarks>
/// <para>
/// <b>Split out so it can be tested.</b> <c>AccentPalette</c> speaks WinUI — <c>Application</c>,
/// <c>FrameworkElement</c>, <c>SolidColorBrush</c> — and cannot be linked into a headless test run.
/// This decision is a boolean pair and nothing else, so it lives here on the same reasoning
/// <c>SignalStrengthScale</c> was split out of its control: the palette speaks WinUI, the rule does
/// not.
/// </para>
/// <para>
/// <b>It exists because the interesting case cannot be observed by running the application.</b>
/// When the Windows accent reads successfully the source and the preference agree, so a log line
/// reporting either looks identical — which is how #290 survived. The case that separates them is a
/// read that fails, and that cannot be produced on demand. Asserting it here makes the fix provable
/// instead of merely plausible.
/// </para>
/// </remarks>
public static class AccentSources
{
    /// <summary>The ramp that was applied, given the preference and whether the read succeeded.</summary>
    /// <param name="useSystemAccent">What the user asked for.</param>
    /// <param name="systemRampWasRead">
    /// Whether <c>AccentPalette.ReadSystemRamp</c> returned a ramp. False covers both a throwing
    /// read and a machine with no accent to report.
    /// </param>
    public static AccentSource For(bool useSystemAccent, bool systemRampWasRead) =>
        useSystemAccent && systemRampWasRead ? AccentSource.Windows : AccentSource.BuiltIn;
}
