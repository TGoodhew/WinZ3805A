namespace WinZ3805A.Controls;

/// <summary>
/// What may be handed to the elevation-mask <c>Slider</c>, and what may be taken back from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the obvious code crashed the application.</b> §9.10.1's slider and
/// §10.5's <c>NumberBox</c> are two controls over one value, and the natural way to write that is
/// <c>Value="{x:Bind MaskBox.Value, Mode=TwoWay}"</c>. It is fatal. An empty <c>NumberBox</c> holds
/// <see cref="double.NaN"/> — that is <c>NumberBox</c>'s own representation of "no value", and this
/// page sets it deliberately, because a mask the receiver has not reported must not be shown as a
/// number it did not say. <c>Slider</c> inherits <c>RangeBase</c>, whose <c>Value</c> setter throws
/// <see cref="ArgumentException"/> on NaN. The binding runs during <c>Loading</c>, before any code
/// can put a number in, so opening the Satellites page killed the process — every time, for every
/// receiver, with nothing in the log until the application learned to write its own crashes down.
/// </para>
/// <para>
/// <b>Why a class rather than two lines in the page.</b> The rule is arithmetic, and arithmetic can
/// be tested without a window; the defect it replaces could only be found by running the app and
/// watching it die. A guard living inline in a view is one refactor away from being lost, and its
/// absence is invisible until someone navigates.
/// </para>
/// <para>
/// <b>An empty field parks the slider at its minimum, and that is not the same as a mask of
/// minimum.</b> A slider has no way to show "nothing" — it is a position on a track. The page
/// therefore never reads a parked slider back into the field on its own; only a deliberate move by
/// the user does that. Losing this distinction would silently invent a mask, which is the exact
/// failure #320 records for the duration limit: a default that is right by luck is a default nobody
/// checks.
/// </para>
/// </remarks>
public static class MaskSliderMath
{
    /// <summary>
    /// The value to give the slider for a field holding <paramref name="value"/>, never NaN and
    /// never outside the track.
    /// </summary>
    /// <param name="value">The field's value, which may be <see cref="double.NaN"/> when empty.</param>
    /// <param name="minimum">The slider's minimum, from the driver's catalog entry.</param>
    /// <param name="maximum">The slider's maximum, from the same entry.</param>
    /// <remarks>
    /// Infinity is coerced as well as NaN: <c>RangeBase</c> rejects both with the same exception,
    /// and a parser that can produce one can produce the other.
    /// </remarks>
    public static double Coerce(double value, double minimum, double maximum)
    {
        // A reversed or degenerate range would otherwise make Math.Clamp throw, which would trade
        // one crash on this page for another.
        if (maximum < minimum)
        {
            (minimum, maximum) = (maximum, minimum);
        }

        return double.IsNaN(value) || double.IsInfinity(value)
            ? minimum
            : Math.Clamp(value, minimum, maximum);
    }

    /// <summary>
    /// Whether a slider movement should be written back into the field.
    /// </summary>
    /// <param name="sliderValue">Where the slider now is.</param>
    /// <param name="fieldValue">What the field holds, possibly <see cref="double.NaN"/>.</param>
    /// <param name="minimum">The slider's minimum.</param>
    /// <remarks>
    /// The one case this exists to refuse is a slider sitting parked at its minimum against an
    /// empty field: that position was never chosen by anybody, and writing it back would turn "the
    /// receiver has not told us" into "the mask is 0 degrees".
    /// </remarks>
    public static bool ShouldWriteBack(double sliderValue, double fieldValue, double minimum) =>
        !(double.IsNaN(fieldValue) && sliderValue.Equals(minimum)) &&
        !sliderValue.Equals(fieldValue);
}
