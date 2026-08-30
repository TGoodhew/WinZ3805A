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
    public static Severity SeverityOf(ReceiverMode mode) => mode switch
    {
        ReceiverMode.Locked => Severity.Success,
        ReceiverMode.Recovering or ReceiverMode.Waiting => Severity.Caution,
        ReceiverMode.Holdover => Severity.Critical,
        _ => Severity.Neutral,
    };

    /// <summary>The Segoe Fluent glyph §10.3 gives this mode.</summary>
    public static string GlyphOf(ReceiverMode mode) => mode switch
    {
        ReceiverMode.Locked => "\uE73E",        // CheckMark
        ReceiverMode.Recovering => "\uE72C",    // Refresh
        ReceiverMode.Waiting => "\uE769",       // Pause
        ReceiverMode.Holdover => "\uE7BA",      // Warning, the fallback behind §9.9's custom holdover icon
        ReceiverMode.PowerUp => "\uE823",       // Clock
        ReceiverMode.Off => "\uE7E8",           // PowerButton
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
    /// </remarks>
    public static string? GeometryKeyOf(ReceiverMode mode) =>
        mode == ReceiverMode.Holdover ? "WzIconHoldover" : null;

    /// <summary>The sentence-case label §10.3 gives this mode.</summary>
    public static string TextOf(ReceiverMode mode) => mode switch
    {
        ReceiverMode.Locked => "Locked to GPS",
        ReceiverMode.Recovering => "Recovering",
        ReceiverMode.Waiting => "Waiting to recover",
        ReceiverMode.Holdover => "Holdover",
        ReceiverMode.PowerUp => "Power-up",
        ReceiverMode.Off => "Diagnostic / off",
        _ => "Disconnected",
    };
}
