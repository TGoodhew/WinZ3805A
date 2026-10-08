namespace WinZ3805A.Controls;

/// <summary>
/// How tall a dialog may be before its content has to scroll (#506).
/// </summary>
/// <remarks>
/// <para>
/// WinUI caps a <c>ContentDialog</c> at <see cref="Stock"/> effective pixels, which is a sensible
/// default for a dialog that asks one question and a poor one for §10.12's, which carries a port
/// picker, a radio pair, four line-setting pickers, two checkboxes and a progress area. Measured on
/// a 1920×1080 laptop at 125 % scale, that dialog reaches the cap exactly: it fits with 20 px to
/// spare while idle and overflows by <b>5 px</b> the moment the progress area appears — inside a
/// window 814 px tall, so there were some 56 px of room it was not allowed to use.
/// </para>
/// <para>
/// <b>This only ever raises the cap, never lowers it.</b> A small window keeps WinUI's behaviour
/// exactly, and the <c>ScrollViewer</c> that #26 added for A11Y-6 stays the fallback for the case
/// this cannot help — 200 % text, where the content genuinely exceeds any screen. Sizing the dialog
/// to its controls and scrolling when that is impossible are two different jobs, and the second one
/// was already done.
/// </para>
/// </remarks>
public static class DialogHeight
{
    /// <summary>WinUI's stock <c>ContentDialogMaxHeight</c>, in effective pixels.</summary>
    public const double Stock = 758;

    /// <summary>
    /// Clearance left above and below the dialog together, so it never runs to the window's edges.
    /// </summary>
    /// <remarks>
    /// Two §9.6 medium steps. This is the one place that figure is restated outside
    /// <c>Themes/Spacing.xaml</c>: the alternative is a resource lookup, which needs the UI thread
    /// and would put the decision somewhere it cannot be tested.
    /// </remarks>
    public const double Clearance = 32;

    /// <summary>
    /// The tallest a dialog may be given a window of <paramref name="available"/> effective pixels.
    /// </summary>
    /// <param name="available">
    /// The height of the <c>XamlRoot</c> the dialog will appear in. Zero or negative when the root is
    /// not known yet, which is not an error — it means "no opinion", and the stock cap stands.
    /// </param>
    /// <param name="clearance">Overridable for tests; defaults to <see cref="Clearance"/>.</param>
    public static double MaxFor(double available, double clearance = Clearance)
    {
        double room = available - clearance;

        return room > Stock ? room : Stock;
    }

    /// <summary>The title bar both windows draw over the top of their content, in effective pixels.</summary>
    public const double TitleBar = 48;

    /// <summary>WinUI's stock <c>ContentDialogMaxWidth</c>, in effective pixels.</summary>
    public const double StockWidth = 548;

    /// <summary>
    /// How far in from the right edge the caption buttons reach: three of 46 at 100 % scaling.
    /// </summary>
    public const double CaptionButtons = 138;

    /// <summary>
    /// The tallest a dialog may be in a window <paramref name="availableWidth"/> by
    /// <paramref name="availableHeight"/>, keeping it out from under the caption buttons (#725).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>ContentDialog</c> is centred in its window's <c>XamlRoot</c>, which with a custom title
    /// bar includes the title strip. In a window as short as its cap the dialog reaches the very top,
    /// and in one narrow enough that a centred dialog reaches across to the caption buttons, they are
    /// drawn over its corner - the Manage satellites dialog in a 640 × 480 Details window, 5 Oct 2026.
    /// There the cap is lowered so that the dialog starts below the title bar.
    /// </para>
    /// <para>
    /// <b>Below it, not centred under it.</b> #725 lowered the cap by two title bars, so that a
    /// centred dialog cleared the strip - and left the same 48 px empty at the foot, where nothing
    /// needed clearing. #767's cue line then took its 35 px out of what was left, and at 200 % text in
    /// the Minimal window no whole row of PRNs remained (#773). A dialog tall enough to reach the strip
    /// is now placed under it (<see cref="TopForDialog"/>) and may run to half the clearance from the
    /// foot: 32 px more for every dialog that needs it.
    /// </para>
    /// <para>
    /// <b>Only where the dialog can reach the buttons.</b> In a wider window it never comes near them,
    /// and lowering the cap there would undo #506 for nothing. The dialog's content scrolls, so a
    /// lower cap costs a scroll, never a control.
    /// </para>
    /// </remarks>
    /// <param name="availableHeight">As for <see cref="MaxFor(double, double)"/>.</param>
    /// <param name="availableWidth">The <c>XamlRoot</c>'s width; zero or negative when not known.</param>
    public static double MaxForWindow(double availableHeight, double availableWidth)
    {
        double cap = MaxFor(availableHeight);
        return ReachesTheButtons(availableHeight, availableWidth)
            ? Math.Min(cap, availableHeight - TitleBar - (Clearance / 2))
            : cap;
    }

    /// <summary>
    /// Where the top of a dialog <paramref name="dialogHeight"/> tall goes, or null to leave WinUI's
    /// centring alone (#773).
    /// </summary>
    /// <remarks>
    /// Only a dialog that, centred, would start under the title bar is moved: a short one stays where
    /// every other Windows dialog is. It is asked again whenever the dialog's height changes, because
    /// Manage satellites opens small and grows once the receiver has answered.
    /// </remarks>
    public static double? TopForDialog(double dialogHeight, double availableHeight, double availableWidth)
    {
        bool centredReachesTheStrip = (availableHeight - dialogHeight) / 2 < TitleBar;
        return ReachesTheButtons(availableHeight, availableWidth) && centredReachesTheStrip ? TitleBar : null;
    }

    private static bool ReachesTheButtons(double availableHeight, double availableWidth) =>
        availableHeight > 0 && availableWidth > 0 && (availableWidth - StockWidth) / 2 < CaptionButtons;
}
