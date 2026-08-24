using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinZ3805A.Services;

/// <summary>
/// §9.2's backdrop: Mica Alt where the platform supports it, a solid where it does not.
/// </summary>
/// <remarks>
/// <b>The safe state is the default, not the branch.</b> The windows declare
/// <c>WzPageBackgroundFallbackBrush</c> on their root in XAML and this upgrades them to Mica Alt.
/// The obvious arrangement is the other way round - declare <c>MicaBackdrop</c> in markup and strip
/// it when unsupported - but then every path that fails to run leaves a window with a backdrop that
/// cannot render, which §9.2 names explicitly: <i>never let an unsupported backdrop produce a
/// transparent or black window</i>. Written this way, a helper that is never called yields a solid
/// window in the right colour for the theme rather than a black one (#191).
/// <para>
/// <b>Mica Alt, not Mica.</b> §9.2 requires <c>MicaKind.BaseAlt</c>, because its stronger tint is
/// what separates the card surfaces from the backdrop in an application that is mostly cards on a
/// backdrop. A bare <c>MicaBackdrop</c> is <c>MicaKind.Base</c>, which is what both windows had.
/// </para>
/// <para>
/// <b>The support check cannot be made on the backdrop object.</b> <c>MicaBackdrop</c> exposes only
/// <c>Kind</c> and <c>KindProperty</c>, so the question goes to
/// <see cref="MicaController.IsSupported"/>. §6.1 keeps <c>TargetPlatformMinVersion</c> at Windows
/// 10 1809, so the unsupported case is a real machine and not a hypothetical.
/// </para>
/// </remarks>
internal static class WindowBackdrop
{
    /// <summary>Applies Mica Alt to <paramref name="window"/> where the platform supports it.</summary>
    /// <param name="window">The window whose backdrop is being set.</param>
    /// <param name="root">
    /// The window's root panel, which carries the fallback solid. Its background is cleared when
    /// Mica is applied, because an opaque root would hide the backdrop entirely.
    /// </param>
    /// <param name="logger">Where a fallback is recorded. Optional.</param>
    /// <returns><see langword="true"/> if Mica Alt was applied.</returns>
    internal static bool Apply(Window window, Panel root, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(root);

        ILogger log = logger ?? NullLogger.Instance;

        if (!MicaController.IsSupported())
        {
            // Nothing to do: the root already carries WzPageBackgroundFallbackBrush from XAML, and
            // it is a ThemeResource there, so it follows a theme change on its own.
            log.LogInformation(
                "Mica is not supported on this system, so the window keeps §9.2's solid fallback.");
            return false;
        }

        window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };

        // Clear the local value rather than assigning Transparent: this restores the property to
        // unset, which is what lets the backdrop show through.
        root.ClearValue(Panel.BackgroundProperty);
        return true;
    }
}
