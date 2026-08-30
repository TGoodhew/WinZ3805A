using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

using WinZ3805A.Services;

namespace WinZ3805A.Controls;

/// <summary>
/// §9.8.2's <i>card enter on page load</i>: opacity and an 8 px rise, staggered 30 ms, four cards.
/// </summary>
/// <remarks>
/// <para>
/// A composition implicit show animation rather than a <c>Storyboard</c>, because that is what the
/// row asks for and because it costs nothing when it does not run: the animation is attached to the
/// visual once and the compositor plays it whenever the element becomes visible, so navigating away
/// and back does not need the page to remember anything.
/// </para>
/// <para>
/// <b>Four cards and then nothing.</b> §9.8.2 caps it, and the cap is the point rather than a
/// budget: a page of nine cards staggered at 30 ms takes a quarter of a second to finish arriving,
/// and by the fifth the movement has stopped reading as one gesture and started reading as a list
/// loading slowly. Cards past the fourth appear at once, which is also what everything does under
/// reduced motion.
/// </para>
/// <para>
/// <b>The reduced-motion fallback is opacity alone, and it is not optional</b> (§9.8). No fallback
/// may produce a different layout, and this one does not: the rise is a render transform, so the
/// card occupies its final position throughout either way.
/// </para>
/// </remarks>
public static class CardEntrance
{
    /// <summary>How far a card rises into place, in effective pixels (§9.8.2).</summary>
    private const float RiseDistance = 8;

    /// <summary>§9.8.2's cap on how many cards are staggered.</summary>
    private const int MaxStaggered = 4;

    /// <summary>
    /// Set to <see langword="true"/> on a panel to animate its children in as the page appears.
    /// </summary>
    /// <remarks>
    /// On the panel and not on each card, so that adding a card to a page joins it to the sequence
    /// rather than requiring anyone to remember the attribute — and so that the stagger indices come
    /// from one place and cannot drift out of order.
    /// </remarks>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(CardEntrance),
        new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Reads whether a panel animates its children in.</summary>
    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    /// <summary>Sets whether a panel animates its children in.</summary>
    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Panel panel)
        {
            return;
        }

        if (e.NewValue is true)
        {
            panel.Loaded += OnPanelLoaded;
        }
        else
        {
            panel.Loaded -= OnPanelLoaded;
            Clear(panel);
        }
    }

    /// <remarks>
    /// <c>Loaded</c> rather than the attached-property callback: a panel declared in XAML has no
    /// children yet when its properties are set, so attaching there would stagger an empty list and
    /// silently do nothing.
    /// </remarks>
    private static void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Panel panel)
        {
            Attach(panel);
        }
    }

    private static void Attach(Panel panel)
    {
        bool animate = App.Services?.GetService(typeof(IMotionService)) is IMotionService motion
            ? motion.AnimationsEnabled
            : true;

        for (int index = 0; index < panel.Children.Count; index++)
        {
            if (panel.Children[index] is not UIElement card)
            {
                continue;
            }

            Compositor compositor = ElementCompositionPreview.GetElementVisual(card).Compositor;

            // Past the cap every card shares the fourth one's delay rather than continuing to grow,
            // so a long page finishes arriving in the same quarter second a short one does.
            TimeSpan delay = TimeSpan.FromMilliseconds(
                30 * Math.Min(index, MaxStaggered - 1));

            CompositionAnimationGroup group = compositor.CreateAnimationGroup();

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Target = "Opacity";
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, Ease(compositor));
            fade.Duration = TimeSpan.FromMilliseconds(250);
            fade.DelayTime = animate ? delay : TimeSpan.Zero;
            group.Add(fade);

            if (animate)
            {
                // Reduced motion keeps the fade and drops this. §9.8 requires the fallback to reach
                // the same layout, which it does: the rise is a transform on the visual, so the card
                // occupies its final position from the first frame either way.
                Vector3KeyFrameAnimation rise = compositor.CreateVector3KeyFrameAnimation();
                rise.Target = "Translation";
                rise.InsertKeyFrame(0f, new System.Numerics.Vector3(0, RiseDistance, 0));
                rise.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, Ease(compositor));
                rise.Duration = TimeSpan.FromMilliseconds(250);
                rise.DelayTime = delay;
                group.Add(rise);

                ElementCompositionPreview.SetIsTranslationEnabled(card, true);
            }

            ElementCompositionPreview.SetImplicitShowAnimation(card, group);
        }
    }

    private static void Clear(Panel panel)
    {
        foreach (UIElement card in panel.Children.OfType<UIElement>())
        {
            ElementCompositionPreview.SetImplicitShowAnimation(card, null);
        }
    }

    /// <summary>
    /// §9.8.1's <c>WzEaseDecelerate</c>, which this row names.
    /// </summary>
    /// <remarks>
    /// Rebuilt from the same two control points the token holds rather than read from it: a
    /// <c>KeySpline</c> is a XAML animation type and the compositor wants a
    /// <c>CubicBezierEasingFunction</c>, so there is no conversion between them. The numbers are the
    /// authority in <c>Themes/Motion.xaml</c> and are repeated here with a pointer back to it, which
    /// is the same arrangement <c>build/fluent-stock-colours.txt</c> has with §9.4.1.
    /// </remarks>
    private static CompositionEasingFunction Ease(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.1f, 0.9f),
            new System.Numerics.Vector2(0.2f, 1f));
}
