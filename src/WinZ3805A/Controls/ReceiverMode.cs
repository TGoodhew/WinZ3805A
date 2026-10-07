using WinZ3805A.Device.Models;

namespace WinZ3805A.Controls;

/// <summary>
/// Draws §10.3's presentation triple for a <see cref="ReceiverMode"/>.
/// </summary>
/// <remarks>
/// <para>
/// The mapping lives in one place because §10.3 states it as one table, and because severity,
/// glyph and text must change together — a mode whose colour and glyph disagree is worse than
/// either alone. <c>StatusMedallion</c> derives all three from <see cref="ReceiverMode"/> rather
/// than taking them separately, so a caller cannot set two of the three.
/// </para>
/// <para>
/// <b>What is no longer here is the token.</b> Turning a receiver's <c>:SYNC:STAT?</c> answer into a
/// mode moved to <c>IReceiverDriver.InterpretSyncState</c> (#304): the six keywords are one
/// family's vocabulary, and reading them here made every other family render as
/// <see cref="ReceiverMode.Disconnected"/>. What stays is the half §9 owns — how a mode is drawn —
/// which no driver has an opinion about.
/// </para>
/// </remarks>
public static class ReceiverModes
{
    /// <summary>The §9.4.3 severity this mode carries.</summary>
    /// <remarks>
    /// <see cref="ReceiverMode.Waiting"/> is a holdover and is critical like one (#642). The receiver
    /// answers <c>WAIT</c> when it has lost GPS and is running on its oscillator, waiting for GPS to
    /// return, with its own screen saying <c>Holdover: GPS 1PPS invalid</c>; <c>HOLD</c> is the forced
    /// kind. This drew amber as "Waiting to recover" until 2 Oct 2026, so the commonest real holdover
    /// looked milder than a deliberate one.
    /// </remarks>
    public static Severity SeverityOf(ReceiverMode mode) => mode switch
    {
        ReceiverMode.Locked => Severity.Success,
        ReceiverMode.Recovering => Severity.Caution,
        ReceiverMode.Holdover or ReceiverMode.Waiting => Severity.Critical,
        _ => Severity.Neutral,
    };

    /// <summary>The Segoe Fluent glyph §10.3 gives this mode.</summary>
    public static string GlyphOf(ReceiverMode mode) => mode switch
    {
        ReceiverMode.Locked => "\uE73E",        // CheckMark
        ReceiverMode.Recovering => "\uE72C",    // Refresh
        ReceiverMode.Waiting => "\uE7BA",       // as Holdover: the fallback behind the custom icon (#642)
        ReceiverMode.Holdover => "\uE7BA",      // Warning, the fallback behind §9.9's custom holdover icon
        ReceiverMode.PowerUp => "\uE823",       // Clock
        ReceiverMode.Off => "\uE7E8",           // PowerButton
        ReceiverMode.AwaitingReading => "\uE8CE", // MapDrive: DisconnectDrive without the slash (#752)
        _ => "\uE8CD",                          // DisconnectDrive
    };

    /// <summary>
    /// The <c>Themes/Shapes.xaml</c> key for §9.9's custom icon, where this mode has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holdover alone, and it is the reason §9.9's icon set was authored at all (#320). The
    /// medallion had been drawing a generic Warning glyph for it — which says <i>something is
    /// wrong</i>, and that is not what holdover means. The receiver is still producing a
    /// disciplined 10 MHz; it is doing it from the oscillator's memory rather than from GPS, and a
    /// pause inside a clock face says that where an exclamation mark says the opposite.
    /// </para>
    /// <para>
    /// A key and not a geometry, because this file compiles into the headless test assembly where
    /// no XAML type exists. Null for every other mode, whose stock glyphs §10.3 chose deliberately.
    /// </para>
    /// <para>
    /// Both holdovers carry it: the forced one (<c>HOLD</c>) and the one waiting for GPS
    /// (<c>WAIT</c>, #642).
    /// </para>
    /// </remarks>
    public static string? GeometryKeyOf(ReceiverMode mode) =>
        mode is ReceiverMode.Holdover or ReceiverMode.Waiting ? "WzIconHoldover" : null;

    /// <summary>The sentence-case label §10.3 gives this mode.</summary>
    /// <remarks>
    /// <see cref="ReceiverMode.AwaitingReading"/> says only what is known — the link is up — and
    /// leaves "waiting for the first reading" to the sub-line, so compact mode, which shows this
    /// label alone, still fits it beside the medallion (#752).
    /// </remarks>
    public static string TextOf(ReceiverMode mode) => mode switch
    {
        ReceiverMode.Locked => "Locked to GPS",
        ReceiverMode.Recovering => "Recovering",
        ReceiverMode.Waiting => "Holdover — waiting for GPS",
        ReceiverMode.Holdover => "Holdover",
        ReceiverMode.PowerUp => "Power-up",
        ReceiverMode.Off => "Diagnostic / off",
        ReceiverMode.AwaitingReading => "Connected",
        _ => "Disconnected",
    };

    /// <summary>
    /// The sub-line under <see cref="ReceiverMode.AwaitingReading"/>'s label (#752), shared by the
    /// main window and the Overview page so the two say it alike.
    /// </summary>
    public const string AwaitingReadingDetail = "Waiting for the first reading";
}
