namespace WinZ3805A.Device.Models;

/// <summary>
/// What the antenna supervisor reports, where a receiver has one (#515).
/// </summary>
/// <remarks>
/// u-blox spells these in <c>$GxTXT</c> as <c>ANTSTATUS=</c>. The values are the supervisor's, not
/// ours: a module without antenna supervision says <c>DONTKNOW</c> rather than staying silent, and
/// that is a different statement from never having reported at all — hence
/// <see cref="Unknown"/> beside <see cref="DoNotKnow"/>.
/// </remarks>
public enum AntennaState
{
    /// <summary>Nothing has been said about the antenna.</summary>
    Unknown = 0,

    /// <summary>The supervisor is still deciding — <c>INIT</c>.</summary>
    Initialising,

    /// <summary>The antenna is present and drawing a sensible current — <c>OK</c>.</summary>
    Ok,

    /// <summary>Short circuit, which on an active antenna usually means the feed is shorted.</summary>
    ShortCircuit,

    /// <summary>Open circuit: no antenna, or a broken feed.</summary>
    OpenCircuit,

    /// <summary>The module has antenna supervision turned off, or cannot tell — <c>DONTKNOW</c>.</summary>
    DoNotKnow,
}

/// <summary>
/// The power-on banner a talker prints once: what it is, what it runs, and how its antenna looks
/// (#515).
/// </summary>
/// <remarks>
/// <para>
/// <b>Said once and never repeated</b>, which is the whole difficulty. Measured on the bench: one
/// burst of twelve <c>$GxTXT</c> sentences and then nothing across the next five cycles. So a value
/// here is remembered by whoever holds it rather than re-read, and a cycle that carries no banner
/// leaves the previous one standing rather than clearing it.
/// </para>
/// <para>
/// <b><see cref="Antenna"/> is the exception and is re-sent on change</b>, which is what makes it
/// worth showing at all: u-blox emits <c>ANTSTATUS</c> again when the supervisor's opinion changes,
/// and <c>form8n-fix-lost.nmea</c> carries <c>INIT</c> followed later by <c>OK</c>. So the rule is
/// remember-and-update, not remember-once.
/// </para>
/// <para>
/// Every member is nullable or <c>Unknown</c> because a talker need not send any of this — most do
/// not — and §11.1's contract is that an absent field is absent rather than guessed.
/// </para>
/// </remarks>
public sealed record TalkerBanner
{
    /// <summary>Nothing has been heard.</summary>
    public static TalkerBanner None { get; } = new();

    /// <summary>The antenna supervisor's opinion, where there is one.</summary>
    public AntennaState Antenna { get; init; }

    /// <summary>
    /// The hardware the module actually is, such as <c>UBX-M8130</c>.
    /// </summary>
    /// <remarks>
    /// <b>Worth more than the label on the case.</b> A USB GPS puck is several receivers depending on
    /// the week it was made, and this is the only place it says which.
    /// </remarks>
    public string? Hardware { get; init; }

    /// <summary>The firmware version, such as <c>SPG 3.01</c>.</summary>
    public string? Firmware { get; init; }

    /// <summary>
    /// The NMEA protocol version the module speaks, such as <c>18.00</c>.
    /// </summary>
    /// <remarks>
    /// Not decoration: it is how anything that wants to send a vendor sentence can know whether this
    /// module understands it (#508).
    /// </remarks>
    public string? ProtocolVersion { get; init; }

    /// <summary>True when nothing at all has been heard, so a consumer can skip it.</summary>
    public bool IsEmpty =>
        Antenna is AntennaState.Unknown &&
        Hardware is null &&
        Firmware is null &&
        ProtocolVersion is null;

    /// <summary>
    /// This banner updated by anything <paramref name="heard"/> actually carries.
    /// </summary>
    /// <remarks>
    /// <b>Field by field, because a later burst is not always a whole banner.</b> An
    /// <c>ANTSTATUS</c> re-sent on change arrives alone, and replacing the record wholesale would
    /// throw away the hardware and firmware that came with the power-on burst. So each field is
    /// taken only when the new one says something.
    /// </remarks>
    public TalkerBanner MergedWith(TalkerBanner? heard) => heard is null || heard.IsEmpty
        ? this
        : new TalkerBanner
        {
            Antenna = heard.Antenna is AntennaState.Unknown ? Antenna : heard.Antenna,
            Hardware = heard.Hardware ?? Hardware,
            Firmware = heard.Firmware ?? Firmware,
            ProtocolVersion = heard.ProtocolVersion ?? ProtocolVersion,
        };
}
