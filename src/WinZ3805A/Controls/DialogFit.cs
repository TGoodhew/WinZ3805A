using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinZ3805A.Controls;

/// <summary>
/// Keeps a <c>ContentDialog</c> out from under its window's caption buttons (#725).
/// </summary>
/// <remarks>
/// <para>
/// The rule is <see cref="DialogHeight.MaxForWindow"/>'s, and it is tested there; this only applies it.
/// It acts once the dialog has opened, because that is when its template exists, and it sets
/// <c>MaxHeight</c> on the template's <c>BackgroundElement</c>, the part that carries the cap in
/// WinUI's own template - #506 found that the instance resource alone changed nothing.
/// </para>
/// <para>
/// <b>It only ever lowers the cap.</b> A dialog that raised its own, as the connection dialog does for
/// #506, keeps it wherever the window is wide enough not to need lowering.
/// </para>
/// <para>
/// <b>And it places the dialog</b> (<see cref="DialogHeight.TopForDialog"/>, #773): a dialog tall
/// enough to reach the title bar is set under it rather than centred, which is what lets the cap use
/// the foot of the window. The template centres <c>BackgroundElement</c> by its own
/// <c>VerticalAlignment</c>, so that and a top margin are what change. Placed again on every change of
/// the dialog's height, and the handler removed when it closes.
/// </para>
/// </remarks>
public static class DialogFit
{
    /// <summary>Applies the cap when the dialog opens. Call before <c>ShowAsync</c>.</summary>
    public static ContentDialog KeepBelowTitleBar(this ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        dialog.Opened += OnOpened;
        return dialog;
    }

    private static void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        sender.Opened -= OnOpened;
        Windows.Foundation.Size window = sender.XamlRoot?.Size ?? default;
        double cap = DialogHeight.MaxForWindow(window.Height, window.Width);

        if (FindNamed(sender, "BackgroundElement") is not FrameworkElement background)
        {
            return;
        }

        if (cap < background.MaxHeight)
        {
            background.MaxHeight = cap;
        }

        Place(background);
        background.SizeChanged += OnBackgroundSizeChanged;
        sender.Closed += OnClosed;
    }

    private static void OnBackgroundSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is FrameworkElement background && args.NewSize.Height != args.PreviousSize.Height)
        {
            Place(background);
        }
    }

    private static void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        sender.Closed -= OnClosed;
        if (FindNamed(sender, "BackgroundElement") is FrameworkElement background)
        {
            background.SizeChanged -= OnBackgroundSizeChanged;
        }
    }

    private static void Place(FrameworkElement background)
    {
        Windows.Foundation.Size window = background.XamlRoot?.Size ?? default;
        double? top = DialogHeight.TopForDialog(background.ActualHeight, window.Height, window.Width);

        background.VerticalAlignment = top is null ? VerticalAlignment.Center : VerticalAlignment.Top;
        background.Margin = new Thickness(0, top ?? 0, 0, 0);
    }

    private static DependencyObject? FindNamed(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var found } && found == name)
            {
                return child;
            }

            if (FindNamed(child, name) is DependencyObject deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
