using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace WinZ3805A.Controls;

/// <summary>
/// Children left to right, wrapping onto a new row when the next one would not fit.
/// </summary>
/// <remarks>
/// <para>
/// WinUI has no WrapPanel and this project does not take the Toolkit dependency for one. The
/// <c>ItemsRepeater</c> the Overview page uses is the right answer for a collection, but a sky-plot
/// legend and a card header are literal children in XAML, and a uniform grid would give
/// "tracked" the width of "elevation mask".
/// </para>
/// <para>
/// <b>Why it exists (#664).</b> The Satellites card's header was a <c>*</c>/<c>Auto</c> grid and its
/// legend a horizontal <c>StackPanel</c>. At the width the Details window first opens at, the
/// <c>Auto</c> column kept its full width and the heading's column shrank under it, so the Plot choice
/// was drawn over the heading; and the legend ran past the card and lost its last entry. Neither
/// layout can do anything when it runs out of room but overlap or clip.
/// </para>
/// <para>
/// Children are measured at the panel's own width, not at infinity, so a child that can wrap itself
/// (another <c>FlowPanel</c>, a wrapping <c>TextBlock</c>) reports the height it will really take.
/// The arithmetic is <see cref="FlowMath"/>, which the tests reach.
/// </para>
/// </remarks>
public sealed partial class FlowPanel : Panel
{
    /// <summary>Identifies the <see cref="ColumnSpacing"/> dependency property.</summary>
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing),
        typeof(double),
        typeof(FlowPanel),
        new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    /// <summary>Identifies the <see cref="RowSpacing"/> dependency property.</summary>
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing),
        typeof(double),
        typeof(FlowPanel),
        new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    /// <summary>Identifies the <see cref="PushLastToEnd"/> dependency property.</summary>
    public static readonly DependencyProperty PushLastToEndProperty = DependencyProperty.Register(
        nameof(PushLastToEnd),
        typeof(bool),
        typeof(FlowPanel),
        new PropertyMetadata(false, OnLayoutPropertyChanged));

    /// <summary>Gap between neighbours in a row. §9.6's scale, by resource.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>Gap between rows.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>
    /// Whether the last child sits at the right edge while it shares a row: a heading on the left and
    /// its actions on the right, until there is no room for both.
    /// </summary>
    public bool PushLastToEnd
    {
        get => (bool)GetValue(PushLastToEndProperty);
        set => SetValue(PushLastToEndProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        List<UIElement> visible = Visible();
        foreach (UIElement child in visible)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        FlowLayout layout = Layout(visible, availableSize.Width);
        return new Size(layout.Width, layout.Height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        List<UIElement> visible = Visible();
        FlowLayout layout = Layout(visible, finalSize.Width);

        for (int i = 0; i < visible.Count; i++)
        {
            FlowPlacement place = layout.Placements[i];
            Size size = visible[i].DesiredSize;
            visible[i].Arrange(new Rect(place.Left, place.Top, size.Width, size.Height));
        }

        return new Size(finalSize.Width, layout.Height);
    }

    private List<UIElement> Visible() => [.. Children.Where(c => c.Visibility != Visibility.Collapsed)];

    private FlowLayout Layout(List<UIElement> visible, double width) => FlowMath.Arrange(
        [.. visible.Select(c => (c.DesiredSize.Width, c.DesiredSize.Height))],
        width,
        ColumnSpacing,
        RowSpacing,
        PushLastToEnd);

    private static void OnLayoutPropertyChanged(DependencyObject element, DependencyPropertyChangedEventArgs e) =>
        (element as FlowPanel)?.InvalidateMeasure();
}
