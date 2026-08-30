using WinZ3805A.Device.Models;

namespace WinZ3805A.Controls;

/// <summary>Everything the shell needs to draw and describe the tray icon for one mode.</summary>
/// <param name="Severity">Which §9.4.3 shape.</param>
/// <param name="Tooltip">The words, which are the whole message for a screen reader.</param>
public readonly record struct TrayIconState(Severity Severity, string Tooltip);

/// <summary>
/// Turns a receiver mode into a tray icon (P1-10).
/// </summary>
/// <remarks>
/// A separate type from <see cref="ReceiverModes"/> because the tray has one constraint nothing
/// else in the application has: the tooltip is capped, and it is the only text there is. Everywhere
/// else a severity shape sits beside a label the user can read at leisure.
/// </remarks>
public static class TrayIconStates
{
    /// <summary>
    /// What the shell truncates a tooltip to.
    /// </summary>
    /// <remarks>
    /// 128 including the terminator, so 127 usable — the <c>NOTIFYICONDATAW.szTip</c> field is 128
    /// <c>WCHAR</c>. Windows silently cuts anything longer, so the check is here rather than left
    /// to be discovered as a sentence that stops mid-word on someone's taskbar.
    /// </remarks>
    public const int MaximumTooltipLength = 127;

    /// <summary>The icon and tooltip for a mode.</summary>
    /// <param name="mode">The receiver's current mode.</param>
    /// <param name="displayName">
    /// The application's name, from <c>Package.Current.DisplayName</c>. Passed in rather than read
    /// here because §6.3 forbids hard-coding it and this file is compiled into a headless test
    /// assembly with no package identity to read it from.
    /// </param>
    /// <remarks>
    /// The tooltip names the state in words, which is P1-10's acceptance criterion and also the
    /// only thing a screen reader has: the shape is invisible to it and the colour doubly so.
    /// </remarks>
    public static TrayIconState For(ReceiverMode mode, string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        string tooltip = $"{displayName} — {ReceiverModes.TextOf(mode)}";

        if (tooltip.Length > MaximumTooltipLength)
        {
            // Trimmed from the name rather than from the state. A user with a long display name
            // still needs to know the receiver is in holdover; they already know what they
            // installed.
            int room = MaximumTooltipLength - ReceiverModes.TextOf(mode).Length - 4;

            tooltip = room > 1
                ? $"{displayName[..room]}… — {ReceiverModes.TextOf(mode)}"
                : ReceiverModes.TextOf(mode)[..Math.Min(MaximumTooltipLength, ReceiverModes.TextOf(mode).Length)];
        }

        return new TrayIconState(ReceiverModes.SeverityOf(mode), tooltip);
    }
}
