namespace WinZ3805A.Device.Models;

/// <summary>
/// What the receiver is doing, in the vocabulary every family is presented in.
/// </summary>
/// <remarks>
/// <para>
/// §10.3 tabulates these against the SmartClock's <c>:SYNC:STAT?</c> answers, which is where they
/// came from — but the medallion, the tray icon, the taskbar badge and the announcer all switch on
/// this enum rather than on a token, so it is the application's vocabulary and not one receiver's.
/// It lives in the Device library so a driver can name a mode without the library referencing the
/// app (#304): the classification is the driver's, and the severity, glyph and label the mode is
/// drawn with stay in <c>Controls/ReceiverMode.cs</c> where §9 can reach them.
/// </para>
/// <para>
/// <b>The set is deliberately closed.</b> A family whose states do not fit is a family whose driver
/// must choose the nearest honest member — the NMEA driver calls a fix <see cref="Locked"/> and no
/// fix <see cref="PowerUp"/>, and says so — rather than one this enum grows a member for. Growing
/// it means a new severity, a new glyph and a new label, which is §9's decision and not a driver
/// author's.
/// </para>
/// </remarks>
public enum ReceiverMode
{
    /// <summary>Nothing is connected, the link has gone, or the receiver said something unrecognised.</summary>
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
