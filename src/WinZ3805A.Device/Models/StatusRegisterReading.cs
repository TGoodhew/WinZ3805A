namespace WinZ3805A.Device.Models;

/// <summary>
/// The five readable fields of one status register, as the receiver last answered them.
/// </summary>
/// <remarks>
/// <b>Reading the event field clears it.</b> That is SCPI's rule and the 58503A guide restates it:
/// an event bit is cleared when the event register is read. So a page that polled this on a timer
/// would consume the very latches a user opened it to see, which is why §10.10 gives the page a
/// Refresh button rather than a cadence.
/// </remarks>
public sealed record StatusRegisterReading
{
    /// <summary>Which register this is.</summary>
    public required StatusRegisterMap Register { get; init; }

    /// <summary>Live conditions.</summary>
    public int? Condition { get; init; }

    /// <summary>Latched events, cleared by the read that returned them.</summary>
    public int? Events { get; init; }

    /// <summary>Which bits are enabled to reach the summary bit.</summary>
    public int? Enable { get; init; }

    /// <summary>Which false-to-true transitions latch an event.</summary>
    public int? PositiveTransition { get; init; }

    /// <summary>Which true-to-false transitions latch an event.</summary>
    public int? NegativeTransition { get; init; }

    /// <summary>Whether anything at all was read.</summary>
    public bool HasAnyValue =>
        Condition is not null || Events is not null || Enable is not null
        || PositiveTransition is not null || NegativeTransition is not null;

    /// <summary>
    /// How many bits the table should show.
    /// </summary>
    /// <remarks>
    /// The documented bits, extended to cover anything actually set that the table does not
    /// document. §10.10 requires an undocumented bit to be shown with its raw state rather than
    /// hidden, and a firmware revision that sets bit 14 must not simply vanish from the page.
    /// </remarks>
    public int BitCount
    {
        get
        {
            int highest = Register.HighestDocumentedBit;

            // Not named "field": in C# 14 that is a contextual keyword inside a property accessor
            // and binds to a synthesized backing field.
            foreach (int? reading in new[] { Condition, Events, Enable, PositiveTransition, NegativeTransition })
            {
                if (reading is int value)
                {
                    for (int bit = 31; bit > highest; bit--)
                    {
                        if ((value & (1 << bit)) != 0)
                        {
                            highest = bit;
                            break;
                        }
                    }
                }
            }

            return highest + 1;
        }
    }

    /// <summary>The decoded rows, one per bit.</summary>
    public IReadOnlyList<StatusBitReading> Rows
    {
        get
        {
            List<StatusBitReading> rows = new(BitCount);

            for (int bit = 0; bit < BitCount; bit++)
            {
                rows.Add(new StatusBitReading
                {
                    Bit = bit,
                    Definition = Register.BitAt(bit),
                    Condition = Test(Condition, bit),
                    Event = Test(Events, bit),
                    Enable = Test(Enable, bit),
                    PositiveTransition = Test(PositiveTransition, bit),
                    NegativeTransition = Test(NegativeTransition, bit),
                });
            }

            return rows;
        }
    }

    private static bool? Test(int? register, int bit) =>
        register is int value ? (value & (1 << bit)) != 0 : null;
}

/// <summary>One row of the §10.10 register table.</summary>
public sealed record StatusBitReading
{
    /// <summary>Which bit this is.</summary>
    public required int Bit { get; init; }

    /// <summary>What it means, or <see langword="null"/> when this register does not document it.</summary>
    public StatusBit? Definition { get; init; }

    /// <summary>Whether the condition is present.</summary>
    public bool? Condition { get; init; }

    /// <summary>Whether an event is latched.</summary>
    public bool? Event { get; init; }

    /// <summary>Whether the bit is enabled to reach the summary.</summary>
    public bool? Enable { get; init; }

    /// <summary>Whether a false-to-true transition latches an event.</summary>
    public bool? PositiveTransition { get; init; }

    /// <summary>Whether a true-to-false transition latches an event.</summary>
    public bool? NegativeTransition { get; init; }

    /// <summary>
    /// What the meaning column shows.
    /// </summary>
    /// <remarks>
    /// §10.10: where a bit meaning is unknown, show the raw state and "(see documentation)" rather
    /// than inventing a label. With OQ-1 answered this is now the exception — Hardware bit 5 is
    /// documented as unused, and anything past a register's table is unmapped.
    /// </remarks>
    public string MeaningText => Definition?.Meaning ?? "(see documentation)";

    /// <summary>Whether this bit's meaning is known.</summary>
    public bool IsDocumented => Definition is not null;

    /// <summary>Whether this bit being set is bad news.</summary>
    public bool IsFault => Definition?.IsFault ?? false;

    /// <summary>Whether it is a latched event rather than a live condition.</summary>
    public bool IsEvent => Definition?.IsEvent ?? false;

    /// <summary>
    /// Whether this bit is currently reporting a fault.
    /// </summary>
    /// <remarks>
    /// A fault bit counts as raised by its condition, or — for the two Hardware entries that are
    /// events rather than conditions — by its latched event, since a time-interval measurement
    /// failure has no lasting condition to read.
    /// </remarks>
    public bool IsRaised => IsFault && (Condition == true || (IsEvent && Event == true));
}
