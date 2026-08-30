namespace WinZ3805A.Controls;

/// <summary>
/// What the receiver is doing, from the <c>:SYNC:STAT?</c> responses §10.3 tabulates.
/// </summary>
public enum ReceiverMode
{
    /// <summary>Nothing is connected, or the link has gone.</summary>
    Disconnected = 0,

    /// <summary>Locked to GPS.</summary>
    Locked,

    /// <summary>Recovering toward lock.</summary>
    Recovering,

    /// <summary>Waiting before it may recover.</summary>
    Waiting,

    /// <summary>Running on the oscillator alone.</summary>
    Holdover,

    /// <summary>Warming up after power was applied.</summary>
    PowerUp,

    /// <summary>In diagnostics, or with outputs off.</summary>
    Off,
}

/// <summary>
/// Maps the receiver's <c>:SYNC:STAT?</c> keyword onto the §10.3 presentation triple.
/// </summary>
/// <remarks>
/// The mapping lives in one place because §10.3 states it as one table, and because severity,
/// glyph and text must change together — a mode whose colour and glyph disagree is worse than
/// either alone. <c>StatusMedallion</c> derives all three from <see cref="ReceiverMode"/> rather
/// than taking them separately, so a caller cannot set two of the three.
/// </remarks>
public static class ReceiverModes
{
    /// <summary>Interprets the keyword <c>:SYNC:STAT?</c> answered with.</summary>
    /// <remarks>
    /// Anything unrecognised becomes <see cref="ReceiverMode.Disconnected"/> rather than being
    /// guessed at: a mode the application does not understand is one it cannot describe honestly,
    /// and showing "locked" on a maybe would be the worst possible default.
    /// </remarks>
    public static ReceiverMode FromSyncState(string? syncState) => syncState?.Trim().ToUpperInvariant() switch
    {
        "LOCK" => ReceiverMode.Locked,
        "REC" => ReceiverMode.Recovering,
        "WAIT" => ReceiverMode.Waiting,
        "HOLD" => ReceiverMode.Holdover,
        "POW" => ReceiverMode.PowerUp,
        "OFF" => ReceiverMode.Off,
        _ => ReceiverMode.Disconnected,
    };

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
