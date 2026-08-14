namespace WinZ3805A.Device.Models;

/// <summary>
/// What one bit of a status register means.
/// </summary>
/// <param name="Bit">Its position, zero-based.</param>
/// <param name="Meaning">What the receiver is saying when it is set.</param>
/// <param name="IsEvent">
/// Whether it is a latched event rather than a live condition. An event bit is set when the thing
/// happens and cleared at power-up or when the event register is read; a condition bit tracks the
/// state and clears itself when the state ends.
/// </param>
/// <param name="IsFault">
/// Whether "set" is bad news. Most of the Hardware register is faults, most of Operation is not,
/// and rendering them the same way would put a red mark against a locked receiver.
/// </param>
public readonly record struct StatusBit(int Bit, string Meaning, bool IsEvent = false, bool IsFault = false);

/// <summary>
/// One status register: its SCPI node, its name, and what its bits mean.
/// </summary>
public sealed record StatusRegisterMap
{
    /// <summary>The SCPI node under <c>:STAT:</c> — <c>OPER</c>, <c>OPER:HARD</c>, and so on.</summary>
    public required string Node { get; init; }

    /// <summary>What the register is called.</summary>
    public required string Name { get; init; }

    /// <summary>One line on what the register is for.</summary>
    public required string Summary { get; init; }

    /// <summary>The documented bits, in order.</summary>
    public required IReadOnlyList<StatusBit> Bits { get; init; }

    /// <summary>
    /// The meaning of a bit, or <see langword="null"/> when this register does not document one.
    /// </summary>
    public StatusBit? BitAt(int bit)
    {
        foreach (StatusBit candidate in Bits)
        {
            if (candidate.Bit == bit)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The highest documented bit, which is how far a table needs to go.</summary>
    public int HighestDocumentedBit
    {
        get
        {
            int highest = -1;
            foreach (StatusBit bit in Bits)
            {
                highest = Math.Max(highest, bit.Bit);
            }

            return highest;
        }
    }
}

/// <summary>
/// The five condition registers and every documented bit in them.
/// </summary>
/// <remarks>
/// <para>
/// <b>From the 58503A/59551A Operating and Programming Guide, Command Reference 5-36 to 5-39</b>
/// ("Status Reporting System", Figure 5-1). This is the answer to OQ-1, which §10.10 defers to for
/// exactly this table and which was open until the guide reached the manual library.
/// </para>
/// <para>
/// §10.10 says that where a bit meaning is unknown, the page shows the raw state and "(see
/// documentation)" rather than inventing a label. That fallback stays — Hardware bit 5 is
/// documented as not used, and a firmware revision may set something no table here covers — but it
/// is now the exception rather than most of the page.
/// </para>
/// </remarks>
public static class StatusRegisterMaps
{
    /// <summary>The Operation register: what the receiver is doing.</summary>
    public static StatusRegisterMap Operation { get; } = new()
    {
        Node = "OPER",
        Name = "Operation",
        Summary = "What the receiver is doing, and summaries of the three subgroups below it.",
        Bits =
        [
            new(0, "Power-up summary"),
            new(1, "Locked to GPS"),
            new(2, "Holdover summary"),
            new(3, "Position hold (clear = surveying)"),
            new(4, "1 PPS reference valid"),
            new(5, "Hardware summary"),
            new(6, "Diagnostic log almost full", IsFault: true),
        ],
    };

    /// <summary>
    /// The Hardware register: continuously monitored health.
    /// </summary>
    /// <remarks>
    /// <b>Every bit here is a fault.</b> Set means the named bad thing is true, which is the
    /// opposite polarity to the ticks §10.4's health monitor draws — that card inverts these, and
    /// its six labels each cover more than one bit.
    /// </remarks>
    public static StatusRegisterMap Hardware { get; } = new()
    {
        Node = "OPER:HARD",
        Name = "Hardware",
        Summary = "Continuously monitored hardware health. Every bit is a fault: set means the fault is present.",
        Bits =
        [
            new(0, "Self-test failure", IsFault: true),
            new(1, "+15 V supply out of tolerance", IsFault: true),
            new(2, "−15 V supply out of tolerance", IsFault: true),
            new(3, "+5 V supply out of tolerance", IsFault: true),
            new(4, "Oven supply out of tolerance", IsFault: true),
            new(6, "EFC voltage near full scale", IsFault: true),
            new(7, "EFC voltage at full scale", IsFault: true),
            new(8, "GPS 1 PPS failure", IsFault: true),
            new(9, "GPS failure", IsFault: true),
            new(10, "Time interval measurement failed", IsEvent: true, IsFault: true),
            new(11, "EEPROM write failed", IsEvent: true, IsFault: true),
            new(12, "Internal reference failure", IsFault: true),
        ],
    };

    /// <summary>The Holdover register: which holdover state, and whether it is over threshold.</summary>
    public static StatusRegisterMap Holdover { get; } = new()
    {
        Node = "OPER:HOLD",
        Name = "Holdover",
        Summary = "Which holdover state the receiver is in, and whether it has passed the user threshold.",
        Bits =
        [
            new(0, "Holding", IsFault: true),
            new(1, "Waiting to recover"),
            new(2, "Recovering"),
            new(3, "Exceeding user threshold", IsFault: true),
        ],
    };

    /// <summary>The Power-up register: what has been achieved since power was applied.</summary>
    /// <remarks>
    /// These are the opposite of faults — each is something good that has happened since power-up,
    /// cleared at power-up and set when it occurs.
    /// </remarks>
    public static StatusRegisterMap PowerUp { get; } = new()
    {
        Node = "OPER:POW",
        Name = "Power-up",
        Summary = "What the receiver has achieved since power was applied. Cleared at power-up, set as each happens.",
        Bits =
        [
            new(0, "First satellite tracked"),
            new(1, "Oscillator oven warm"),
            new(2, "Date and time valid", IsEvent: true),
        ],
    };

    /// <summary>The Questionable register.</summary>
    public static StatusRegisterMap Questionable { get; } = new()
    {
        Node = "QUES",
        Name = "Questionable",
        Summary = "Conditions that call the receiver's own output into question.",
        Bits =
        [
            new(0, "Time reset against the satellites", IsEvent: true, IsFault: true),
            new(1, "User-reported"),
        ],
    };

    /// <summary>Every register, in the order §10.10's picker lists them.</summary>
    public static IReadOnlyList<StatusRegisterMap> All { get; } =
        [Operation, Hardware, Holdover, PowerUp, Questionable];

    /// <summary>Finds a register by its SCPI node.</summary>
    public static StatusRegisterMap? ByNode(string? node)
    {
        foreach (StatusRegisterMap map in All)
        {
            if (string.Equals(map.Node, node, StringComparison.OrdinalIgnoreCase))
            {
                return map;
            }
        }

        return null;
    }
}
