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

        if (e?.Parameter is DetailsDestination destination)
        {
            PageTitle.Text = destination.Label;
            PageSummary.Text = destination.Summary;
        }
    }
}
