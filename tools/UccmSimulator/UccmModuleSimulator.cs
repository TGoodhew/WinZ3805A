using System.Globalization;
using System.Text;

using WinZ3805A.Device.Drivers.Uccm;

namespace WinZ3805A.Simulation;

/// <summary>
/// A UCCM module that answers the way this project believes one does (#416, #418).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a model of a belief, not a receiver.</b> Every behaviour below is something read out
/// of Lady Heather's source rather than seen on a wire. Running the driver against it proves the
/// two agree with each other, which is worth having — it exercises the echo handling, the
/// interleaved time codes, the vendor-specific reply shapes and the error paths end to end — but it
/// proves nothing about hardware. When a module disagrees, the disagreement is the finding, and
/// this file is as likely to be wrong as the driver is.
/// </para>
/// <para>
/// It reproduces the three conventions that make this family awkward, because those are exactly
/// what a parser-only test cannot reach: the command is <b>echoed</b> before the answer, a reply
/// ends with <c>COMMAND COMPLETE</c>, and an unsolicited <c>C5</c> time code can be
/// <b>interleaved</b> anywhere — including between the echo and the value.
/// </para>
/// </remarks>
/// <param name="vendor">Which vendor's reply shapes to produce.</param>
/// <param name="variant">Whether to answer the three UCCM-P-only queries.</param>
/// <param name="timeProvider">Drives the clock in the time code, so a test can pin it.</param>
public sealed class UccmModuleSimulator(
    UccmVendor vendor,
    UccmVariant variant,
    TimeProvider timeProvider)
{
    private static readonly DateTimeOffset GpsEpoch = new(1980, 1, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Leap seconds between GPS and UTC. 18 since 2017.</summary>
    private const int LeapSeconds = 18;

    /// <summary>Which vendor this module reports as.</summary>
    public UccmVendor Vendor => vendor;

    /// <summary>Which variant this module reports as.</summary>
    public UccmVariant Variant => variant;

    /// <summary>What the module is doing, which drives the status bytes and the text lines.</summary>
    public UccmSimulatedState State { get; set; } = UccmSimulatedState.Locked;

    /// <summary>Whether the antenna reads as connected.</summary>
    public bool AntennaConnected { get; set; } = true;

    /// <summary>
    /// Whether to interleave an unsolicited time code into the next reply.
    /// </summary>
    /// <remarks>
    /// Off by default so that a test asking for the awkward case gets it deliberately. Heather's
    /// comment says it is the Symmetricom units that do this, but that is one observation and not a
    /// rule, so it is settable for either vendor.
    /// </remarks>
    public bool InterleaveTimeCode { get; set; }

    /// <summary>How many satellites the status table lists.</summary>
    public int SatelliteCount { get; set; } = 7;

    /// <summary>Answers one command, exactly as the module would put it on the wire.</summary>
    /// <remarks>
    /// Lines are CRLF-terminated, echo first. An unknown command produces the error reply a real
    /// module gives rather than silence, because silence and a refusal look identical to a parser
    /// and only one of them is a bug.
    /// </remarks>
    public string Respond(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        StringBuilder reply = new();
        reply.Append(command).Append("\r\n");

        if (InterleaveTimeCode)
        {
            reply.Append(TimeCodeLine()).Append("\r\n");
        }

        string mnemonic = command.Trim();
        string? payload = PayloadFor(mnemonic);

        if (payload is null)
        {
            reply.Append("UNDEFINED HEADER\r\n");
            return reply.ToString();
        }

        if (payload.Length > 0)
        {
            reply.Append(payload).Append("\r\n");
        }

        reply.Append("COMMAND COMPLETE\r\n");
        return reply.ToString();
    }

    /// <summary>The unsolicited time code the module emits on its own.</summary>
    public string TimeCodeLine()
    {
        int[] values = new int[44];
        values[0] = 0xC5;

        long seconds = (long)(timeProvider.GetUtcNow() - GpsEpoch).TotalSeconds + LeapSeconds;
        values[27] = (int)((seconds >> 24) & 0xFF);
        values[28] = (int)((seconds >> 16) & 0xFF);
        values[29] = (int)((seconds >> 8) & 0xFF);
        values[30] = (int)(seconds & 0xFF);

        values[32] = LeapSeconds;
        values[33] = State == UccmSimulatedState.Locked ? 0x60 : 0x41;
        values[34] = AntennaConnected ? 0x04 : 0x0C;
        values[35] = LockByte();
        values[36] = DateByte();

        return string.Join(
            ' ', values.Select(v => v.ToString("X2", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The manufacturer-dependent lock byte — the one value #418 exists for.
    /// </summary>
    private int LockByte() => (vendor, State) switch
    {
        (UccmVendor.Symmetricom, UccmSimulatedState.Locked) => 0x85,
        (UccmVendor.Symmetricom, _) => 0x8F,
        (UccmVendor.Trimble, UccmSimulatedState.Locked) => 0x45,
        (UccmVendor.Trimble, UccmSimulatedState.PowerUp) => 0x41,
        (UccmVendor.Trimble, _) => 0x4F,

        // A vendor we do not model still has to put something on the wire. The low nibble is the
        // half both vendors share, so an unknown vendor emits that with a high nibble neither uses,
        // which is precisely the case the driver must not read as either vendor.
        (_, UccmSimulatedState.Locked) => 0x05,
        (_, _) => 0x0F,
    };

    private int DateByte() => vendor switch
    {
        // Note the high nibbles run the OPPOSITE way to the lock byte. That is not a typo, it is
        // what Heather recorded, and it is why "high nibble means vendor" is not a rule.
        UccmVendor.Symmetricom => AntennaConnected ? 0x40 : 0x50,
        UccmVendor.Trimble => AntennaConnected ? 0x80 : 0x90,
        _ => 0x00,
    };

    /// <summary>The payload lines for a command, empty for none, or null for "not understood".</summary>
    private string? PayloadFor(string mnemonic)
    {
        if (Is(mnemonic, UccmCommands.Status))
        {
            return StatusBody();
        }

        if (Is(mnemonic, UccmCommands.Loop))
        {
            return LoopBody();
        }

        if (Is(mnemonic, UccmCommands.TimeInterval))
        {
            return State == UccmSimulatedState.Locked ? "-1.23E-9" : "0.0E0";
        }

        if (Is(mnemonic, UccmCommands.EfcRelative))
        {
            return "12.5";
        }

        if (Is(mnemonic, UccmCommands.LockLed))
        {
            return State == UccmSimulatedState.Locked ? "1" : "0";
        }

        if (Is(mnemonic, "*IDN?"))
        {
            string maker = vendor == UccmVendor.Trimble ? "Trimble" : "Symmetricom";
            string model = variant == UccmVariant.UccmP ? "UCCM-P" : "UCCM";
            return $"{maker},{model},SN 1234567,1.0";
        }

        if (Is(mnemonic, "GPS:POS?"))
        {
            return "N,37,26,15.30,W,122,10,30.10,25.4";
        }

        if (Is(mnemonic, "GPS:REF:ADEL?"))
        {
            return "1.2E-7";
        }

        if (Is(mnemonic, "GPS:SAT:TRAC:EMAN?"))
        {
            return "10";
        }

        // The three a plain UCCM does not understand. Returning null gives the caller the
        // UNDEFINED HEADER reply, which is how the variant is meant to be discovered.
        if (UccmCommands.UccmPOnly.Any(p => Is(mnemonic, p)))
        {
            if (variant != UccmVariant.UccmP)
            {
                return null;
            }

            if (Is(mnemonic, UccmCommands.HoldoverDuration))
            {
                return State == UccmSimulatedState.Holdover ? "412,1" : "0,0";
            }

            return Is(mnemonic, UccmCommands.SurveyState) ? "0" : "100";
        }

        return null;
    }

    /// <summary>
    /// The multi-line status reply: state text, the satellite table, and a time code.
    /// </summary>
    private string StatusBody()
    {
        StringBuilder body = new();

        switch (State)
        {
            case UccmSimulatedState.PowerUp:
                body.Append("WARMUP\r\n");
                break;
            case UccmSimulatedState.Settling:
                body.Append("SETTLING\r\n");
                break;
            case UccmSimulatedState.Holdover:
                body.Append("NO REF\r\n");
                break;
            default:
                break;
        }

        if (!AntennaConnected)
        {
            body.Append("WAIT FOR GPS\r\n");
        }

        body.Append(CultureInfo.InvariantCulture, $"TFOM {Tfom()} FFOM {Ffom()}\r\n");

        // The time code lands INSIDE the status, which is where Heather says it turns up.
        body.Append(TimeCodeLine()).Append("\r\n");

        body.Append("PRN  EL  AZ  SS\r\n");
        for (int i = 0; i < SatelliteCount; i++)
        {
            body.Append(CultureInfo.InvariantCulture, $"{i + 1,3} {20 + (i * 5),3} {(i * 40) % 360,4} {35 + i,3}\r\n");
        }

        body.Append("ELEV MASK 10\r\n");
        return body.ToString().TrimEnd('\r', '\n');
    }

    private int Tfom() => State == UccmSimulatedState.Locked ? 3 : 9;

    private int Ffom() => State == UccmSimulatedState.Locked ? 0 : 3;

    /// <summary>The loop reply, in whichever shape this vendor uses. #418's central divergence.</summary>
    private string LoopBody() => vendor switch
    {
        UccmVendor.Symmetricom =>
            "------------------------------\r\n" +
            "FREQ COR = 3.1E-11\r\n" +
            "TEMP COR = 42.5",

        UccmVendor.Trimble =>
            "  DAC_LINK   DAC_AVG   DAC_GPS  FREQ_CORR  LINK_OFF  FREQ_DIFF  FREQ_FRAC\r\n" +
            "  32768      32770     32768    1.5E-11    0.0       4.2E-11    0.25",

        _ => string.Empty,
    };

    private static bool Is(string mnemonic, string command) =>
        string.Equals(mnemonic, command, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the simulated module is doing.</summary>
public enum UccmSimulatedState
{
    /// <summary>Warming up after power was applied.</summary>
    PowerUp = 0,

    /// <summary>Settling towards lock, or FFOM above zero.</summary>
    Settling,

    /// <summary>Locked and disciplining.</summary>
    Locked,

    /// <summary>Running without a GPS reference.</summary>
    Holdover,
}
