using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using Windows.UI.ViewManagement;

using WinZ3805A.Controls;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Services;

/// <summary>How much of the ramp a call to <see cref="AccentPalette.Apply"/> actually reached.</summary>
/// <param name="Applied">Brushes found and set.</param>
/// <param name="Expected">Brushes the ramp names.</param>
/// <param name="Base">The accent that was applied.</param>
/// <remarks>
/// Reported so a mismatch is visible in the log rather than only on screen. A key renamed in
/// <c>Colors.xaml</c> and not here fails silently — the brush simply is not found, the accent
/// applies everywhere except one control, and nothing says so.
/// </remarks>
public readonly record struct AppliedCount(int Applied, int Expected, Rgb Base)
{
    /// <summary>Nothing was applied, because there was nothing sensible to apply.</summary>
    public static AppliedCount None { get; } = new(0, 0, default);

    /// <summary>Whether every brush the ramp names was found.</summary>
    public bool IsComplete => Applied == Expected;
}

/// <summary>
/// Puts a resolved <see cref="AccentRamp"/> onto the live resource dictionary (§9.4.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>It sets <c>Color</c> on the existing brushes rather than replacing them.</b> Every consumer
/// binds with <c>{ThemeResource WzAccentBaseBrush}</c> and holds the <see cref="SolidColorBrush"/>
/// that key resolved to. Assigning a new brush into the dictionary would leave all of them pointing
/// at the old instance, so the palette would change only where a page happened to be rebuilt —
/// which is to say, inconsistently and differently on every run. Mutating the brush already in use
/// updates everything drawn from it at once.
/// </para>
/// <para>
/// <b>HighContrast is left alone.</b> Its accent brushes are <c>SystemColorHighlightColor</c>, which
/// is the user's own choice arriving through the system rather than a colour this application picked.
/// Overwriting it with a ramp would override an accessibility setting with a preference, and the
/// preference is the less important of the two.
/// </para>
/// </remarks>
public static class AccentPalette
{
    /// <summary>Reads the Windows accent ramp, or null if it is unavailable.</summary>
    /// <remarks>
    /// <see cref="UISettings"/> reaches out to the shell, and a WinAppSDK process that is starting,
    /// shutting down, or running without a user session can fail to reach it. There is no useful
    /// recovery beyond using the brand ramp, which is a perfectly good accent — so this reports
    /// "no" rather than propagating.
    /// </remarks>
    public static AccentRamp? ReadSystemRamp()
    {
        try
        {
            UISettings settings = new();

            return new AccentRamp(
                Convert(settings.GetColorValue(UIColorType.AccentDark3)),
                Convert(settings.GetColorValue(UIColorType.AccentDark2)),
                Convert(settings.GetColorValue(UIColorType.AccentDark1)),
                Convert(settings.GetColorValue(UIColorType.Accent)),
                Convert(settings.GetColorValue(UIColorType.AccentLight1)),
                Convert(settings.GetColorValue(UIColorType.AccentLight2)),
                Convert(settings.GetColorValue(UIColorType.AccentLight3)));
        }
        catch (Exception)
        {
            return null;
        }

        static Rgb Convert(Windows.UI.Color colour) => new(colour.R, colour.G, colour.B);
    }

    /// <summary>
    /// Applies the ramp the preferences select to the resources of one element's tree.
    /// </summary>
    /// <param name="root">
    /// Any loaded element, used for its <see cref="FrameworkElement.ActualTheme"/>. The brushes
    /// themselves come from the application's resources, which is where the token dictionary is
    /// merged.
    /// </param>
    /// <param name="preferences">What the user has chosen.</param>
    /// <remarks>
    /// Call this on startup and again on every theme change. A theme change swaps in the other
    /// theme dictionary's brush instances, which are untouched by any earlier call — without the
    /// second call, switching from light to dark would silently restore the brand ramp.
    /// </remarks>
    /// <returns>How many brushes were reached, for the log to report.</returns>
    public static AppliedCount Apply(FrameworkElement root, AppearancePreferences preferences)
    {
        // Nothing sensible to substitute: see the remarks on the class.
        if (Application.Current is not Application application || IsHighContrast())
        {
            return AppliedCount.None;
        }

        // No ramp means the token dictionary could not be read, so there is nothing to substitute
        // and - importantly - nothing to restore: the brushes still hold the colours the
        // dictionary gave them, which is exactly what the brand ramp would have set.
        if (AppearanceViewModel.Resolve(preferences, ReadSystemRamp()) is not AccentRamp ramp)
        {
            return AppliedCount.None;
        }

        IReadOnlyList<KeyValuePair<string, Rgb>> assignments =
            ramp.BrushAssignments(root.ActualTheme != ElementTheme.Dark);

        int applied = 0;

        foreach ((string key, Rgb colour) in assignments)
        {
            if (application.Resources.TryGetValue(key, out object? found)
                && found is SolidColorBrush brush)
            {
                brush.Color = Windows.UI.Color.FromArgb(0xFF, colour.R, colour.G, colour.B);
                applied++;
            }
        }

        return new AppliedCount(applied, assignments.Count, ramp.Base);

        static bool IsHighContrast()
        {
            try
            {
                return new AccessibilitySettings().HighContrast;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
