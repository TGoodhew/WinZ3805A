using System.Numerics;

using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

using WinZ3805A.Services;

namespace WinZ3805A.Controls;

/// <summary>
/// §9.8.2's pressed scale: 0.98 on pointer-down, back on release, over <c>WzDurationFast</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An attached behaviour rather than a control template.</b> §9.8.2 asks for the scale on
/// hover / pressed / focus generally, and the stock WinUI templates have no scale in them — so the
/// alternative was forking the template of every interactive control in the application, which
/// would also fork every focus visual with it and put §9.12's coverage gate in the position of
/// checking copies. This attaches to any <c>UIElement</c> and touches nothing else.
/// </para>
/// <para>
/// <b>Scale is the only channel it moves, and it carries no information.</b> §9.4.3's
/// colour-plus-shape-plus-text rule does not apply, and the §9.13 colour-only-states gate exempts
/// pointer feedback for the same reason: this says the pointer is where the user thinks it is, and
/// nothing else. The brush half of the row is the stock template's and is untouched.
/// </para>
/// <para>
/// <b>Reduced motion means no scale at all</b>, not a faster one — §9.8.2's fallback column reads
/// "brush change only, no scale". Read once when the behaviour attaches and again whenever Windows
/// says it changed, because a user who turns animations off does not expect to restart the
/// application to be believed.
/// </para>
/// <para>
/// The centre point is set from the element's actual size on every press rather than once: a button
/// whose label is set at run time — the Exit button on §10.13 reads the product name — is zero-sized
/// when the behaviour attaches, and a scale about the origin would slide it rather than press it.
/// </para>
/// </remarks>
public static class PressEffect
{
    /// <summary>§9.8.2's pressed scale.</summary>
    private const float PressedScale = 0.98f;

    /// <summary>§9.8.1's <c>WzDurationFast</c>, which this row names.</summary>
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);

    /// <summary>Set to <see langword="true"/> to give an element the pressed scale.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(PressEffect),
        new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Reads whether an element has the pressed scale.</summary>
    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    /// <summary>Gives an element the pressed scale, or takes it away.</summary>
    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.PointerPressed -= OnPressed;
        target.PointerReleased -= OnReleased;
        target.PointerCaptureLost -= OnReleased;
        target.PointerExited -= OnReleased;
        target.PointerCanceled -= OnReleased;

        if (e.NewValue is not true)
        {
            Scale(target, 1f);
            return;
        }

        target.PointerPressed += OnPressed;

        // Every way a press can end, and not only the happy one. A pointer released outside the
        // element raises PointerExited or PointerCaptureLost and never PointerReleased, and an
        // element left at 0.98 for the rest of the session is a defect nobody would think to look
        // for — it reads as a rendering glitch rather than as a stuck state.
        target.PointerReleased += OnReleased;
        target.PointerCaptureLost += OnReleased;
        target.PointerExited += OnReleased;
        target.PointerCanceled += OnReleased;
    }

    private static void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement target)
        {
            Scale(target, PressedScale);
        }
    }

    private static void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement target)
        {
            Scale(target, 1f);
        }
    }

    private static void Scale(UIElement target, float to)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(target);

        if (target is FrameworkElement sized)
        {
            visual.CenterPoint = new Vector3(
                (float)sized.ActualWidth / 2,
                (float)sized.ActualHeight / 2,
                0);
        }

        if (!Animates())
        {
            visual.Scale = Vector3.One;
            return;
        }

        Compositor compositor = visual.Compositor;
        Vector3KeyFrameAnimation animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Scale";
        animation.InsertKeyFrame(1f, new Vector3(to, to, 1f), compositor.CreateCubicBezierEasingFunction(
            // §9.8.1's WzEaseStandard, rebuilt from the same control points the token holds — a
            // KeySpline is a XAML type and the compositor wants a CubicBezierEasingFunction, so
            // there is no conversion between them. Themes/Motion.xaml stays the authority.
            new Vector2(0.8f, 0f),
            new Vector2(0.2f, 1f)));
        animation.Duration = Duration;

        visual.StartAnimation("Scale", animation);
    }

    /// <summary>Whether §9.8's reduced-motion rule permits this at all.</summary>
    /// <remarks>
    /// Asked on every press rather than cached, so a change to the Windows setting takes effect on
    /// the next click rather than on the next launch. It is one property read against a service that
    /// already holds the answer, which is cheaper than the animation it is guarding.
    /// </remarks>
    private static bool Animates() =>
        App.Services?.GetService(typeof(IMotionService)) is not IMotionService motion
        || motion.AnimationsEnabled;
}
