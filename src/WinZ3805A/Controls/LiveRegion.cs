using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;

using WinZ3805A.ViewModels;

namespace WinZ3805A.Controls;

/// <summary>
/// Puts an <see cref="Announcement"/> where a screen reader will read it (A11Y-9).
/// </summary>
/// <remarks>
/// <para>
/// Setting <c>AutomationProperties.LiveSetting</c> is necessary and not sufficient: WinUI does not
/// raise <see cref="AutomationEvents.LiveRegionChanged"/> for you when the text underneath changes,
/// so a live region that is only declared in XAML is silent. The event has to be raised by hand,
/// and this is the one place that does it.
/// </para>
/// <para>
/// <see cref="FrameworkElementAutomationPeer.CreatePeerForElement"/> rather than
/// <c>FromElement</c>: peers are built lazily as an assistive tool walks the tree, so an element it
/// has not reached yet has no peer, and <c>FromElement</c> returns null for exactly the
/// announcement that most needs making — the first one.
/// </para>
/// </remarks>
public static class LiveRegion
{
    /// <summary>Announces through a host element, replacing whatever it last said.</summary>
    /// <param name="host">The element standing in for the live region.</param>
    /// <param name="announcement">What to say, and how urgently.</param>
    public static void Announce(FrameworkElement host, Announcement announcement)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(announcement);

        AutomationProperties.SetLiveSetting(
            host,
            announcement.Urgency == AnnouncementUrgency.Assertive
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);

        // The name is what gets read. A TextBlock would be read from its Text, but a host that is
        // deliberately unreadable on screen has none worth setting, and naming it works for both.
        AutomationProperties.SetName(host, announcement.Text);

        AutomationPeer? peer = FrameworkElementAutomationPeer.CreatePeerForElement(host);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
