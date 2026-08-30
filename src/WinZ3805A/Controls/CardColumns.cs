using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace WinZ3805A.Controls;

/// <summary>
/// §9.6.1's content grid: cards flow into as many columns as the width will hold.
/// </summary>
/// <remarks>
/// <para>
/// §9.6.1 has always said 1 column at Compact and 2 at Medium and Wide, and every Details page was a
/// single <c>StackPanel</c> — so the pages were permanently in the Compact arrangement and half of a
/// wide window was empty. Tony's call (#345 item 8) is that the columns should follow the width
/// rather than a breakpoint: as the window grows the cards should rearrange into whatever number of
/// columns fits.
/// </para>
/// <para>
/// <b>Shortest column wins, which is why the cards do not simply alternate.</b> Each card goes to
/// whichever column is currently shortest, so a tall first card sits alone in column one while two
/// short ones stack beside it — which is the arrangement Tony described. Round-robin would put card
/// three under card one regardless of how tall card one is, and leave a ragged column beside a full
/// one.
/// </para>
/// <para>
/// <b>Order is preserved down each column, not across.</b> Reading order is the order the page
/// declares its cards in, and the automation tree follows the children collection rather than the
/// arrangement — so a screen reader hears §10.x's order whatever the width happens to be.
/// </para>
/// <para>
/// A panel rather than an <c>ItemsRepeater</c> with a layout: the pages declare their cards as
/// literal children with names the code-behind binds to, and moving them into an items source would
/// mean a view model per page for no gain. <c>MeasureOverride</c> and <c>ArrangeOverride</c> are the
/// whole implementation.
/// </para>
/// </remarks>
public sealed partial class CardColumns : Panel
{
    /// <summary>Identifies the <see cref="MinColumnWidth"/> dependency property.</summary>
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth),
        typeof(double),
        typeof(CardColumns),
        new PropertyMetadata(420.0, OnLayoutPropertyChanged));

    /// <summary>Identifies the <see cref="ColumnSpacing"/> dependency property.</summary>
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing),
        typeof(double),
        typeof(CardColumns),
        new PropertyMetadata(24.0, OnLayoutPropertyChanged));

    /// <summary>Identifies the <see cref="RowSpacing"/> dependency property.</summary>
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing),
        typeof(double),
        typeof(CardColumns),
        new PropertyMetadata(24.0, OnLayoutPropertyChanged));

    /// <summary>Identifies the <see cref="MaxColumns"/> dependency property.</summary>
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns),
        typeof(int),
        typeof(CardColumns),
        new PropertyMetadata(2, OnLayoutPropertyChanged));

    /// <summary>
    /// The narrowest a column may be before the panel drops one.
    /// </summary>
    /// <remarks>
    /// A card holds label-and-value rows and a chart or two. Below about 420 effective pixels the
    /// labels start wrapping and §9.5.3's readouts lose their alignment, so a third column bought by
    /// squeezing is a worse page than two comfortable ones.
    /// </remarks>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>Gap between columns. §9.6's scale, so the page keeps its rhythm.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>Gap between cards within a column.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>
    /// The most columns to use, however wide the window gets.
    /// </summary>
    /// <remarks>
    /// §9.6.1's grid is two columns, and §9.6's content max-width caps the region at 1320 px for a
    /// reason it states: label-value pairs separated by a hand's width are measurably worse to scan.
    /// The cap is a property rather than a constant so a page whose cards are genuinely narrow can
    /// raise it without every page following.
    /// </remarks>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = CardColumnMath.ColumnsThatFit(availableSize.Width, MinColumnWidth, ColumnSpacing, MaxColumns);
        double columnWidth = ColumnWidthFor(availableSize.Width, columns);

        double[] heights = new double[columns];

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(new Size(columnWidth, double.PositiveInfinity));

            int target = CardColumnMath.ShortestColumn(heights);
            heights[target] += (heights[target] > 0 ? RowSpacing : 0) + child.DesiredSize.Height;
        }

        double width = columns == 1 && double.IsInfinity(availableSize.Width)
            ? Children.Max(c => c.DesiredSize.Width)
            : (columnWidth * columns) + (ColumnSpacing * (columns - 1));

        return new Size(width, heights.Length == 0 ? 0 : heights.Max());
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = CardColumnMath.ColumnsThatFit(finalSize.Width, MinColumnWidth, ColumnSpacing, MaxColumns);
        double columnWidth = ColumnWidthFor(finalSize.Width, columns);

        double[] heights = new double[columns];

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            int target = CardColumnMath.ShortestColumn(heights);
            double top = heights[target] + (heights[target] > 0 ? RowSpacing : 0);
            double left = target * (columnWidth + ColumnSpacing);

            child.Arrange(new Rect(left, top, columnWidth, child.DesiredSize.Height));

            heights[target] = top + child.DesiredSize.Height;
        }

        return new Size(finalSize.Width, heights.Length == 0 ? 0 : heights.Max());
    }

    private double ColumnWidthFor(double available, int columns) =>
        CardColumnMath.ColumnWidth(available, columns, ColumnSpacing, MinColumnWidth);

    private static void OnLayoutPropertyChanged(DependencyObject element, DependencyPropertyChangedEventArgs e) =>
        (element as CardColumns)?.InvalidateMeasure();
}
