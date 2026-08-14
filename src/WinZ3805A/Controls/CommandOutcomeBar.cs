using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using WinZ3805A.Services;

namespace WinZ3805A.Controls;

/// <summary>
/// Reports what a tier C command did, on the card that command belongs to (§9.11).
/// </summary>
/// <remarks>
/// <para>
/// §9.11 gives tier C results two surfaces and one silence. A consequential success gets a success
/// <c>InfoBar</c> that dismisses itself after five seconds; a recoverable error gets an error
/// <c>InfoBar</c> inline at the top of the affected card, which stays until the user closes it,
/// because an error that vanishes on a timer is an error the user has to reproduce to read. Routine
/// successes get nothing at all — but every tier C command is consequential by definition, so this
/// control never has to decide that.
/// </para>
/// <para>
/// An <c>InfoBar</c> subclass rather than a wrapper: it is an <c>InfoBar</c> in every respect
/// except knowing when to close, and a wrapper would mean re-exposing the whole of its surface to
/// get its layout back.
/// </para>
/// </remarks>
public sealed partial class CommandOutcomeBar : InfoBar
{
    /// <summary>§9.11's auto-dismiss interval for a consequential success.</summary>
    private static readonly TimeSpan SuccessDwell = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _dismiss = new() { Interval = SuccessDwell };

    /// <summary>Creates the bar, closed.</summary>
    public CommandOutcomeBar()
    {
        IsOpen = false;
        IsClosable = true;

        _dismiss.Tick += OnDismissTick;

        // A page that navigates away mid-dwell would otherwise leave the timer running against a
        // control nothing can see.
        Unloaded += (_, _) => _dismiss.Stop();
    }

    /// <summary>Shows an outcome, or clears the bar when there is none (the user cancelled).</summary>
    /// <param name="outcome">What happened, or null when the user cancelled.</param>
    /// <param name="detail">
    /// What the receiver <i>answered</i>, for the tier C commands that are queries. The success
    /// sentence comes from the catalog and says the command ran; only the caller knows how to read
    /// the number that came back, so it appends rather than replaces.
    /// </param>
    public void Show(CommandOutcome? outcome, string? detail = null)
    {
        _dismiss.Stop();

        if (outcome is null)
        {
            IsOpen = false;
            return;
        }

        Message = string.IsNullOrWhiteSpace(detail) || !outcome.Succeeded
            ? outcome.Message
            : $"{outcome.Message} {detail}";
        Severity = outcome.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        Title = outcome.Succeeded ? string.Empty : outcome.Command.DisplayName;
        IsOpen = true;

        if (outcome.Succeeded)
        {
            _dismiss.Start();
        }
    }

    /// <summary>Closes the bar and stops any dwell in progress.</summary>
    public void Clear()
    {
        _dismiss.Stop();
        IsOpen = false;
    }

    private void OnDismissTick(object? sender, object e)
    {
        _dismiss.Stop();
        IsOpen = false;
    }
}
