using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.ApplicationModel.DataTransfer;

using WinZ3805A.Services;

namespace WinZ3805A.Controls;

/// <summary>
/// §9.7.4's right-click layer: <i>copy value</i> on a readout, <i>copy as CSV</i> on a table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing unique lives here</b> — that is §9.7.4's rule for this placement, and it is what
/// makes the layer safe to add. Every value a menu copies is on screen already, and every table it
/// copies is the same document <c>Ctrl+E</c> writes to a file. A user who never discovers the
/// right-click loses a keystroke and no capability, which is the only shape a hidden menu is
/// allowed to have.
/// </para>
/// <para>
/// An attached property rather than a flyout written out at each site, because a readout that
/// gains one should do so with one attribute and a page that adds a value should not have to
/// remember a code-behind handler. The alternative — a <c>MenuFlyout</c> per field in XAML — was
/// tried on paper and needs six lines and a named handler per value, which is how a convenience
/// layer ends up applied to a third of the fields and looking broken on the rest.
/// </para>
/// </remarks>
public static class CopyMenu
{
    /// <summary>
    /// Set to <see langword="true"/> to give an element a <i>Copy value</i> right-click menu.
    /// </summary>
    /// <remarks>
    /// Named <c>IsCopyable</c> and not <c>Value</c>: an attached property called <c>Value</c> would
    /// want <c>SetValue</c>/<c>GetValue</c> accessors, which are <see cref="DependencyObject"/>'s
    /// own methods, and hiding those from a static class is a trap for whoever reads it next.
    /// </remarks>
    public static readonly DependencyProperty IsCopyableProperty = DependencyProperty.RegisterAttached(
        "IsCopyable",
        typeof(bool),
        typeof(CopyMenu),
        new PropertyMetadata(false, OnIsCopyableChanged));

    /// <summary>Reads whether an element carries the copy menu.</summary>
    public static bool GetIsCopyable(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsCopyableProperty);
    }

    /// <summary>Gives an element the copy menu, or takes it away.</summary>
    public static void SetIsCopyable(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsCopyableProperty, value);
    }

    /// <summary>
    /// Gives a table a <i>Copy table as CSV</i> right-click menu, backed by the page that owns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from a page rather than set in XAML, because the source is the page itself and an
    /// attached property cannot be handed <c>this</c> from markup. It also puts the wiring next to
    /// the <see cref="ICsvExportSource"/> implementation it depends on, so a page that stops being
    /// exportable stops compiling rather than quietly offering a menu item that returns nothing.
    /// </para>
    /// <para>
    /// The item is disabled while <see cref="ICsvExportSource.CanExport"/> is false, evaluated as
    /// the menu opens. §9.11 is explicit that a visible, enabled command which silently does
    /// nothing is worse than a greyed-out one, because the user cannot tell it from a failure.
    /// </para>
    /// </remarks>
    /// <param name="table">The element the user will right-click.</param>
    /// <param name="source">The page that can build the document.</param>
    public static void AttachCsv(FrameworkElement table, ICsvExportSource source)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(source);

        MenuFlyoutItem item = new() { Text = "Copy table as CSV" };
        item.Click += (_, _) =>
        {
            if (source.BuildCsv() is CsvDocument document)
            {
                Copy(document.ToText());
            }
        };

        MenuFlyout flyout = new();
        flyout.Items.Add(item);
        flyout.Opening += (_, _) => item.IsEnabled = source.CanExport;

        table.ContextFlyout = flyout;
    }

    private static void OnIsCopyableChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement target)
        {
            return;
        }

        if (e.NewValue is not true)
        {
            target.ContextFlyout = null;
            return;
        }

        MenuFlyoutItem item = new() { Text = "Copy value" };
        item.Click += (_, _) =>
        {
            if (ValueOf(target) is string value)
            {
                Copy(value);
            }
        };

        MenuFlyout flyout = new();
        flyout.Items.Add(item);

        // Evaluated as the menu opens rather than when it is built: the field this hangs on is a
        // readout, so it is empty on navigation and full a second later, and a menu item whose
        // enabled state was decided at construction would be permanently greyed out.
        flyout.Opening += (_, _) => item.IsEnabled = ValueOf(target) is not null;

        target.ContextFlyout = flyout;
    }

    /// <summary>
    /// What a copy of this element should put on the clipboard, or null when there is nothing.
    /// </summary>
    /// <remarks>
    /// The typesetting is undone by <see cref="ReadoutFormatter.ToMachineText"/>, which is where
    /// that rule belongs and where it can be tested without a window.
    /// </remarks>
    private static string? ValueOf(FrameworkElement element) => ReadoutFormatter.ToMachineText(
        element switch
        {
            TextBlock block => block.Text,
            ReadoutTile tile => ReadoutFormatter.Format(tile.Value, tile.DecimalPlaces),
            _ => null,
        });

    /// <summary>Puts text on the clipboard.</summary>
    private static void Copy(string text)
    {
        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
