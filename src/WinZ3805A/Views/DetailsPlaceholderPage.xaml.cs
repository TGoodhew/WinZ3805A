using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// Stands in for a Details page that has not been built yet.
/// </summary>
/// <remarks>
/// One type for all of them rather than eight near-identical files that would each be deleted
/// unread: which destination it is arrives as the navigation parameter, so the pane, the header and
/// the page stay in step without anything to keep in sync by hand. As each real page lands it takes
/// its destination's place and this shrinks; when the last one lands it goes.
/// </remarks>
public sealed partial class DetailsPlaceholderPage : Page
{
    /// <summary>Creates the page.</summary>
    public DetailsPlaceholderPage()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        switch (e?.Parameter)
        {
            // A destination this receiver can never fill (#435). Same page, different claim: the
            // note below is a statement about the receiver rather than a promise about a future
            // release, and saying "not built yet" here would be simply false.
            case DetailsUnavailable unavailable:
                PageTitle.Text = unavailable.Destination.Label;
                PageSummary.Text = unavailable.Reason;
                PageNote.Text =
                    $"{unavailable.Destination.Summary} None of that reaches this application from the receiver " +
                    "on the port, so the page would show nothing but dashes. Connect a receiver whose protocol " +
                    "carries it and this entry becomes available again.";
                break;

            case DetailsDestination destination:
                PageTitle.Text = destination.Label;
                PageSummary.Text = destination.Summary;
                break;

            default:
                break;
        }
    }
}
