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
        ReceiverMode.Holdover => "\uE7BA",      // Warning, standing in for §10.3's custom holdover icon
        ReceiverMode.PowerUp => "\uE823",       // Clock
        ReceiverMode.Off => "\uE7E8",           // PowerButton
        _ => "\uE8CD",                          // DisconnectDrive
    };

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
