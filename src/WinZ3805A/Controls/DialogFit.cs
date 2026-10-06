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

        if (FindNamed(sender, "BackgroundElement") is FrameworkElement background && cap < background.MaxHeight)
        {
            background.MaxHeight = cap;
        }
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
