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
/// <para>
/// <b>The time code's shape is the exception, and is measured.</b> Since #738 it goes on the wire as
/// the 44 binary bytes a Trimble UCCM-P sends, laid out as the captures have it; only
/// <see cref="Respond"/>, which a test hands to a parser as a string, still renders it as hex text.
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
    private const byte LeapSeconds = 18;

    /// <summary>A time code's length in bytes, marker and terminator included.</summary>
    private const int FrameLength = 44;

    /// <summary>
    /// Offsets 1 to 26 of every measured frame, which no state the bench produced ever moved.
    /// </summary>
    /// <remarks>
    /// Copied from <c>tests/WinZ3805A.Tests/Uccm/Captures/frames-13sep2026.txt</c>; the same 26
    /// bytes open all 935 frames there and in <c>transitions-13sep2026.frames.txt</c>, locked, cold,
    /// acquiring and in holdover. What they mean is unknown, which is why they are reproduced rather
    /// than modelled.
    /// </remarks>
    private static ReadOnlySpan<byte> MeasuredConstantPrefix =>
    [
        0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00, 0x00, 0x20, 0x60,
        0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

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

    /// <summary>The prompt the module prints after every reply, with no trailing space.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>UCCM-P &gt;</c> is measured</b> — 9 of 9 replies in the first sitting with a Trimble
    /// UCCM-P and 50 of 50 in <c>hypothesis2-12sep2026</c> — and <c>UCCM &gt;</c> for a plain UCCM is
    /// the driver's own claim (#513), which no plain module has confirmed.
    /// </para>
    /// <para>
    /// <b>Not part of <see cref="Respond"/></b>, whose callers parse a reply rather than read a
    /// stream; the program's port and pipe modes write it after each reply. Until they did, nothing
    /// could connect the application to this simulator at all: since #470 the driver waits for this
    /// prompt, every transaction ran to its timeout, and auto-detect said no receiver had answered
    /// (found by the QA pass's <c>receiver-families</c> scenario, the first thing to try, #633).
    /// </para>
    /// </remarks>
    public string Prompt => variant == UccmVariant.UccmP ? "UCCM-P >" : "UCCM >";

    /// <summary>Answers one command, with any time code rendered as a line of hex text.</summary>
    /// <remarks>
    /// <para>
    /// Lines are CRLF-terminated, echo first. An unknown command produces the error reply a real
    /// module gives rather than silence, because silence and a refusal look identical to a parser
    /// and only one of them is a bug.
    /// </para>
    /// <para>
    /// <b>This is the parser's view, not the wire's.</b> A time code here is the hex-text line
    /// Heather renders in her logs, which is what lets a test hand the reply straight to a parser as
    /// a string. No module sends that shape: <see cref="RespondOnWire"/> is what the port and pipe
    /// modes write (#738).
    /// </para>
    /// </remarks>
    public string Respond(string command) =>
        Encoding.ASCII.GetString(Compose(command, binaryTimeCodes: false));

    /// <summary>
    /// Answers one command exactly as the port and pipe modes put it on the wire: the reply, any
    /// time code in it as the measured 44-byte binary frame, and then <see cref="Prompt"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Until #738 the wire carried <see cref="Respond"/>'s hex text</b>, which no module sends.
    /// The driver lifts a binary frame out of the byte stream before splitting lines, so the text
    /// form went straight past that and arrived as a line of its own — and the application, polling
    /// <c>LED:GPSL?</c>, sometimes took that line for the answer and showed a connected, locked
    /// receiver as <i>Disconnected</i>. Writing the frame as bytes is what puts the transport's real
    /// framing in the path at all.
    /// </para>
    /// <para>
    /// A frame here carries <b>no line terminator</b>, as the measured one does, and sits wherever
    /// <see cref="Respond"/> would have put its line: after the echo with
    /// <see cref="InterleaveTimeCode"/>, and inside a status reply.
    /// </para>
    /// </remarks>
    public byte[] RespondOnWire(string command)
    {
        byte[] reply = Compose(command, binaryTimeCodes: true);
        byte[] prompt = Encoding.ASCII.GetBytes(Prompt);

        byte[] wire = new byte[reply.Length + prompt.Length];
        reply.CopyTo(wire, 0);
        prompt.CopyTo(wire, reply.Length);
        return wire;
    }

    /// <summary>
    /// The unsolicited time code as the module broadcasts it: 44 bytes, <c>0xC5</c> through
    /// <c>0xCA</c>, with no separator and no line terminator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The layout is the captures', not Heather's rendering of it</b> (#738). Offsets 1 to 26, 31
    /// and 37 to 40 never moved in any of the 935 frames captured on 13 Sep 2026, so they are
    /// reproduced byte for byte. Offsets 27 to 30 are the GPS second counter, big-endian, which is
    /// confirmed against hardware; 32 to 36 are the leap, PPS, antenna, lock and date bytes, taken
    /// from this simulator's state exactly as before — the README lists where those disagree with
    /// the module.
    /// </para>
    /// <para>
    /// <b>Offsets 41 and 42 are left zero.</b> On the module they move with every frame and look like
    /// a checksum, but the function is unidentified — <c>frames-13sep2026.md</c> rules out every CRC
    /// and the common arithmetic sums — so there is nothing true to put there. The driver validates
    /// a frame by its length and its two marker bytes only, which zeros satisfy; a driver that ever
    /// starts checking those two bytes will reject this simulator's frames, and should.
    /// </para>
    /// </remarks>
    public byte[] TimeCodeFrame()
    {
        byte[] frame = new byte[FrameLength];
        frame[0] = 0xC5;
        MeasuredConstantPrefix.CopyTo(frame.AsSpan(1));

        long seconds = (long)(timeProvider.GetUtcNow() - GpsEpoch).TotalSeconds + LeapSeconds;
        frame[27] = (byte)((seconds >> 24) & 0xFF);
        frame[28] = (byte)((seconds >> 16) & 0xFF);
        frame[29] = (byte)((seconds >> 8) & 0xFF);
        frame[30] = (byte)(seconds & 0xFF);

        frame[32] = LeapSeconds;
        frame[33] = (byte)(State == UccmSimulatedState.Locked ? 0x60 : 0x41);
        frame[34] = (byte)(AntennaConnected ? 0x04 : 0x0C);
        frame[35] = (byte)LockByte();
        frame[36] = (byte)DateByte();

        frame[FrameLength - 1] = 0xCA;
        return frame;
    }

    /// <summary>
    /// <see cref="TimeCodeFrame"/> as hex text, two digits per byte with a space between — the
    /// shape Heather logs and <see cref="Respond"/> uses, and the shape no module sends.
    /// </summary>
    public string TimeCodeLine() =>
        string.Join(' ', TimeCodeFrame().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>One reply's bytes, with its time codes as hex-text lines or as binary frames.</summary>
    private byte[] Compose(string command, bool binaryTimeCodes)
    {
        ArgumentNullException.ThrowIfNull(command);

        ReplyWriter reply = new(this, binaryTimeCodes);
        reply.Line(command);

        if (InterleaveTimeCode)
        {
            reply.TimeCode();
        }

        string mnemonic = command.Trim();
        if (Is(mnemonic, UccmCommands.Status))
        {
            WriteStatus(reply);
        }
        else
        {
            string? payload = PayloadFor(mnemonic);

            if (payload is null)
            {
                reply.Line("UNDEFINED HEADER");
                return reply.ToArray();
            }

            if (payload.Length > 0)
            {
                reply.Line(payload);
            }
        }

        reply.Line("COMMAND COMPLETE");
        return reply.ToArray();
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
    private void WriteStatus(ReplyWriter body)
    {
        switch (State)
        {
            case UccmSimulatedState.PowerUp:
                body.Line("WARMUP");
                break;
            case UccmSimulatedState.Settling:
                body.Line("SETTLING");
                break;
            case UccmSimulatedState.Holdover:
                body.Line("NO REF");
                break;
            default:
                break;
        }

        if (!AntennaConnected)
        {
            body.Line("WAIT FOR GPS");
        }

        body.Line(string.Create(CultureInfo.InvariantCulture, $"TFOM {Tfom()} FFOM {Ffom()}"));

        // The time code lands INSIDE the status, which is where Heather says it turns up.
        body.TimeCode();

        body.Line("PRN  EL  AZ  SS");
        for (int i = 0; i < SatelliteCount; i++)
        {
            body.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"{i + 1,3} {20 + (i * 5),3} {(i * 40) % 360,4} {35 + i,3}"));
        }

        body.Line("ELEV MASK 10");
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

    /// <summary>One reply's bytes, with each time code in whichever of its two shapes was asked for.</summary>
    private sealed class ReplyWriter(UccmModuleSimulator module, bool binaryTimeCodes)
    {
        private readonly List<byte> _bytes = [];

        /// <summary>Appends a line of text and its CRLF.</summary>
        public void Line(string text)
        {
            _bytes.AddRange(Encoding.ASCII.GetBytes(text));
            _bytes.AddRange("\r\n"u8);
        }

        /// <summary>
        /// Appends a time code: its 44 bytes with nothing after them, as the module sends it, or a
        /// hex-text line for <see cref="Respond"/>.
        /// </summary>
        public void TimeCode()
        {
            if (binaryTimeCodes)
            {
                _bytes.AddRange(module.TimeCodeFrame());
            }
            else
            {
                Line(module.TimeCodeLine());
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }

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
