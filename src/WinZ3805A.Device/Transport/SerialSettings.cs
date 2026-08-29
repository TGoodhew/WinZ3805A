using System.IO.Ports;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// The RS-232 line parameters for one receiver connection (§7.1).
/// </summary>
/// <remarks>
/// Every parameter is settable because the SmartClock family is not consistent: the Z3805A ships
/// 9600-8-N-1 while a Z3801A leaves the factory at 19200-7-O-1. Nothing in the transport may
/// assume a default.
/// Handshake is deliberately absent — §7.1 permits <see cref="Handshake.None"/> only, so it is not a
/// choice the user or the caller gets to make.
/// </remarks>
public sealed record SerialSettings
{
    /// <summary>Baud rate. §7.1 permits 1200, 2400, 9600 and 19200.</summary>
    public int BaudRate { get; init; } = 9600;

    /// <summary>Data bits, 7 or 8.</summary>
    public int DataBits { get; init; } = 8;

    /// <summary>Parity.</summary>
    public Parity Parity { get; init; } = Parity.None;

    /// <summary>Stop bits, one or two.</summary>
    public StopBits StopBits { get; init; } = StopBits.One;

    /// <summary>The Z3805A factory configuration, 9600-8-N-1.</summary>
    public static SerialSettings Default { get; } = new();

    /// <summary>The baud rates offered by the connection dialog (§7.1).</summary>
    public static IReadOnlyList<int> SupportedBaudRates { get; } = [1200, 2400, 9600, 19200];

    /// <summary>The data-bit counts offered by the connection dialog (§7.1).</summary>
    public static IReadOnlyList<int> SupportedDataBits { get; } = [7, 8];

    /// <summary>
    /// The eight combinations auto-detect walks, most-likely-first, so a Z3805A answers on the
    /// first attempt and a Z3801A on the second. Each attempt sends <c>*IDN?</c> with the
    /// <see cref="TransactionTimeouts.AutoDetectProbe"/> timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Second place was 19200-7-E-1 until 28 Aug 2026, and the Z3801A guide says odd.</b> It
    /// gives the factory default twice — "Baud Rate: 19200 / Parity: Odd / Data Bits: 7/char /
    /// Stop Bits: 1", and again as "19200 — 7 data bits, 1 start bit, 1 stop bit, odd parity". The
    /// even-parity spelling had propagated into §7.1, §10.12 and Appendix B of the specification
    /// and into this list, with no source behind it.
    /// </para>
    /// <para>
    /// The cost was not cosmetic. Odd sat <b>eighth</b>, so a Z3801A was found on the last attempt
    /// of eight rather than the second — around fourteen extra seconds at the probe timeout, and
    /// nothing at all if a future change ever shortened the walk.
    /// </para>
    /// <para>
    /// <b>Even is kept, one place lower, rather than removed.</b> The specification called it
    /// "commonly" that, which is a claim about units in the field rather than about the factory,
    /// and second-hand receivers are exactly this project's audience (§4). Documented default
    /// first, folklore immediately after — that ordering costs one probe if the folklore is right
    /// and saves six if it is not.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SerialSettings> AutoDetectSequence { get; } =
    [
        new() { BaudRate = 9600, DataBits = 8, Parity = Parity.None, StopBits = StopBits.One },
        new() { BaudRate = 19200, DataBits = 7, Parity = Parity.Odd, StopBits = StopBits.One },
        new() { BaudRate = 19200, DataBits = 7, Parity = Parity.Even, StopBits = StopBits.One },
        new() { BaudRate = 9600, DataBits = 7, Parity = Parity.Even, StopBits = StopBits.One },
        new() { BaudRate = 19200, DataBits = 8, Parity = Parity.None, StopBits = StopBits.One },
        new() { BaudRate = 2400, DataBits = 8, Parity = Parity.None, StopBits = StopBits.One },
        new() { BaudRate = 1200, DataBits = 8, Parity = Parity.None, StopBits = StopBits.One },
        new() { BaudRate = 9600, DataBits = 7, Parity = Parity.Odd, StopBits = StopBits.One },
    ];

    /// <summary>Renders the settings the way instrument documentation writes them, e.g. <c>9600-8-N-1</c>.</summary>
    public override string ToString() => $"{BaudRate}-{DataBits}-{ParityLetter}-{StopBitDigit}";

    private char ParityLetter => Parity switch
    {
        Parity.None => 'N',
        Parity.Even => 'E',
        Parity.Odd => 'O',
        Parity.Mark => 'M',
        Parity.Space => 'S',
        _ => '?',
    };

    private string StopBitDigit => StopBits switch
    {
        StopBits.One => "1",
        StopBits.Two => "2",
        StopBits.OnePointFive => "1.5",
        _ => "?",
    };
}
