using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Parsing;

/// <summary>
/// The §10.10 status register bit maps and their decoding — OQ-1's answer, in code.
/// </summary>
/// <remarks>
/// From the 58503A/59551A guide, Command Reference 5-36 to 5-39. Every figure here is transcribed
/// from that document rather than inferred, which is the whole reason #34 was blocking.
/// </remarks>
public sealed class StatusRegisterTests
{
    [Fact]
    public void TheFiveRegistersMatchTheCatalogsNodes() =>
        Assert.Equal(
            ["OPER", "OPER:HARD", "OPER:HOLD", "OPER:POW", "QUES"],
            StatusRegisterMaps.All.Select(register => register.Node));

    /// <remarks>
    /// The two bits anything else in the app would want: locked, and position hold. Both are
    /// conditions rather than events, so they track state and clear themselves.
    /// </remarks>
    [Fact]
    public void TheOperationRegisterCarriesLockAndPositionHold()
    {
        Assert.Equal("Locked to GPS", StatusRegisterMaps.Operation.BitAt(1)?.Meaning);
        Assert.Contains("Position hold", StatusRegisterMaps.Operation.BitAt(3)?.Meaning);
        Assert.False(StatusRegisterMaps.Operation.BitAt(1)?.IsEvent);
    }

    /// <summary>
    /// Every Hardware bit is a fault: set means the named bad thing is true.
    /// </summary>
    /// <remarks>
    /// This is the opposite polarity to §10.4's health monitor, which draws ticks. That card
    /// inverts these, and its six labels each cover more than one bit — so anything reading this
    /// register straight into a tick would show a healthy receiver as entirely broken.
    /// </remarks>
    [Fact]
    public void EveryHardwareBitIsAFault() =>
        Assert.All(StatusRegisterMaps.Hardware.Bits, bit => Assert.True(bit.IsFault));

    /// <remarks>
    /// The guide marks bit 5 "not used", so it is absent from the table rather than present with an
    /// invented label — and the decoder shows it as undocumented.
    /// </remarks>
    [Fact]
    public void HardwareBitFiveIsNotDocumented()
    {
        Assert.Null(StatusRegisterMaps.Hardware.BitAt(5));
        Assert.Equal(12, StatusRegisterMaps.Hardware.HighestDocumentedBit);
    }

    /// <remarks>
    /// Two of the Hardware entries are events rather than conditions — a failed time-interval
    /// measurement and a failed EEPROM write both happen rather than persist, so there is no
    /// condition to read afterwards and only the latch records them.
    /// </remarks>
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void TheTwoHardwareEventsAreMarkedAsEvents(int bit) =>
        Assert.True(StatusRegisterMaps.Hardware.BitAt(bit)?.IsEvent);

    /// <remarks>
    /// The Power-up register is the opposite of the Hardware one: each bit is something good that
    /// has happened since power was applied, so none of them is a fault.
    /// </remarks>
    [Fact]
    public void NoPowerUpBitIsAFault() =>
        Assert.All(StatusRegisterMaps.PowerUp.Bits, bit => Assert.False(bit.IsFault));

    // ---- Decoding ---------------------------------------------------------------------------

    /// <remarks>
    /// §10.10's own worked example: the wireframe shows CONDition +13 against the Operation
    /// register, which is bits 0, 2 and 3.
    /// </remarks>
    [Fact]
    public void TheWireframesConditionValueDecodesToItsBits()
    {
        StatusRegisterReading reading = new()
        {
            Register = StatusRegisterMaps.Operation,
            Condition = 13,
        };

        Assert.True(reading.Rows[0].Condition);
        Assert.False(reading.Rows[1].Condition);
        Assert.True(reading.Rows[2].Condition);
        Assert.True(reading.Rows[3].Condition);
        Assert.False(reading.Rows[4].Condition);
    }

    [Fact]
    public void EveryFieldDecodesIndependently()
    {
        StatusRegisterReading reading = new()
        {
            Register = StatusRegisterMaps.Operation,
            Condition = 0b0000_0010,
            Events = 0b0000_0100,
            Enable = 0b0000_1000,
            PositiveTransition = 0b0001_0000,
            NegativeTransition = 0b0010_0000,
        };

        Assert.True(reading.Rows[1].Condition);
        Assert.True(reading.Rows[2].Event);
        Assert.True(reading.Rows[3].Enable);
        Assert.True(reading.Rows[4].PositiveTransition);
        Assert.True(reading.Rows[5].NegativeTransition);
    }

    /// <remarks>
    /// A field that was not read is not the same as a field that read zero. The first means the
    /// query failed or was never made; the second means the receiver said "nothing set".
    /// </remarks>
    [Fact]
    public void AnUnreadFieldIsNullRatherThanFalse()
    {
        StatusRegisterReading reading = new()
        {
            Register = StatusRegisterMaps.Operation,
            Condition = 0,
        };

        Assert.False(reading.Rows[0].Condition);
        Assert.Null(reading.Rows[0].Event);
        Assert.True(reading.HasAnyValue);
        Assert.False(new StatusRegisterReading { Register = StatusRegisterMaps.Operation }.HasAnyValue);
    }

    /// <remarks>
    /// §10.10 requires an undocumented bit to show its raw state rather than be hidden. A firmware
    /// revision that sets bit 14 of the Questionable register must not simply vanish from the page.
    /// </remarks>
    [Fact]
    public void AnUndocumentedBitThatIsSetStillGetsARow()
    {
        StatusRegisterReading reading = new()
        {
            Register = StatusRegisterMaps.Questionable,
            Condition = 1 << 14,
        };

        Assert.Equal(15, reading.BitCount);
        Assert.True(reading.Rows[14].Condition);
        Assert.False(reading.Rows[14].IsDocumented);
        Assert.Equal("(see documentation)", reading.Rows[14].MeaningText);
    }

    [Fact]
    public void ADocumentedRegisterShowsItsWholeTableEvenWhenNothingIsSet()
    {
        StatusRegisterReading reading = new()
        {
            Register = StatusRegisterMaps.Hardware,
            Condition = 0,
        };

        Assert.Equal(13, reading.BitCount);
        Assert.All(reading.Rows, row => Assert.False(row.Condition));

        // Bit 5 is the documented gap, and it reads as undocumented rather than as a fault.
        Assert.False(reading.Rows[5].IsDocumented);
        Assert.False(reading.Rows[5].IsFault);
    }

    /// <summary>A fault is raised by its condition, or by its latch when it has no condition.</summary>
    [Fact]
    public void AFaultIsRaisedByConditionOrByLatchedEvent()
    {
        StatusRegisterReading reading = new()
        {
            Register = StatusRegisterMaps.Hardware,
            Condition = 1 << 9,   // GPS failure, a condition
            Events = 1 << 10,     // Time interval measurement failed, an event
        };

        Assert.True(reading.Rows[9].IsRaised);
        Assert.True(reading.Rows[10].IsRaised);

        // Set in the enable mask only, which says what would be reported, not what is happening.
        StatusRegisterReading enabled = new()
        {
            Register = StatusRegisterMaps.Hardware,
            Condition = 0,
            Enable = 0xFFF,
        };

        Assert.All(enabled.Rows, row => Assert.False(row.IsRaised));
    }

    /// <remarks>
    /// A set bit in a register where it means something good is not a fault, so nothing about
    /// "locked to GPS" or "oven warm" may render as one.
    /// </remarks>
    [Fact]
    public void AGoodConditionIsNeverRaisedAsAFault()
    {
        StatusRegisterReading reading = new()
        {
            Register = StatusRegisterMaps.PowerUp,
            Condition = 0b111,
        };

        Assert.All(reading.Rows, row => Assert.False(row.IsRaised));
    }

    [Fact]
    public void RegistersAreFoundByNodeCaseInsensitively()
    {
        Assert.Same(StatusRegisterMaps.Hardware, StatusRegisterMaps.ByNode("OPER:HARD"));
        Assert.Same(StatusRegisterMaps.Hardware, StatusRegisterMaps.ByNode("oper:hard"));
        Assert.Null(StatusRegisterMaps.ByNode("NOPE"));
        Assert.Null(StatusRegisterMaps.ByNode(null));
    }
}
