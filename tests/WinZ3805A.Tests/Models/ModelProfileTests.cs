using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Models;

/// <summary>
/// P2-4's model detection and per-model masking (#64, §8.6).
/// </summary>
/// <remarks>
/// Only the Z3805A's identity string has ever been observed. The others are the model numbers §8.6
/// and the manuals use, and no <c>*IDN?</c> example is published for any of them — so what is
/// asserted here is that recognition is <i>conservative</i> when wrong, not that the strings are
/// confirmed.
/// </remarks>
public class ModelProfileTests
{
    /// <summary>The live receiver's actual answer, transcribed.</summary>
    private const string LiveIdentity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";

    [Fact]
    public void TheLiveIdentityParsesIntoItsFourFields()
    {
        DeviceIdentity identity = DeviceIdentity.Parse(LiveIdentity)!;

        Assert.Equal("SYMMETRICOM", identity.Manufacturer);
        Assert.Equal("Z3805A", identity.Model);
        Assert.Equal("3625A02931", identity.SerialNumber);
        Assert.Equal("1.01.03-A", identity.FirmwareRevision);
        Assert.Equal(ReceiverModel.Z3805A, identity.Receiver);
    }

    [Theory]
    [InlineData("SYMMETRICOM,Z3801A,1234A00001,1.00.00", ReceiverModel.Z3801A)]
    [InlineData("SYMMETRICOM,Z3816A,1234A00001,1.00.00", ReceiverModel.Z3816A)]
    [InlineData("HEWLETT-PACKARD,58503A,1234A00001,1.00.00", ReceiverModel.Hp58503)]
    [InlineData("HEWLETT-PACKARD,58503B,1234A00001,1.00.00", ReceiverModel.Hp58503)]
    [InlineData("HEWLETT-PACKARD,59551A,1234A00001,1.00.00", ReceiverModel.Hp59551)]
    public void TheFamilyIsRecognisedFromTheModelField(string response, ReceiverModel expected) =>
        Assert.Equal(expected, DeviceIdentity.Parse(response)!.Receiver);

    [Fact]
    public void AVariantSuffixTakesTheSameProfileAsItsBase()
    {
        // §11.1 already treats 58503A and 58503B as one class for the signal-strength scale, and a
        // profile that split them would contradict it.
        Assert.Equal(
            DeviceIdentity.Parse("HEWLETT-PACKARD,58503A,1,1")!.Receiver,
            DeviceIdentity.Parse("HEWLETT-PACKARD,58503B,1,1")!.Receiver);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not an identity")]
    [InlineData("TOO,FEW,FIELDS")]
    [InlineData("FIVE,FIELDS,IS,ALSO,WRONG")]
    public void AnUnparseableResponseIsNullRatherThanAThrow(string? response) =>
        Assert.Null(DeviceIdentity.Parse(response));

    [Fact]
    public void AnUnrecognisedModelStillParsesAndIsUnknown()
    {
        // The family is wider than the list. An unfamiliar model must still yield its serial number
        // and firmware — a user reading the Diagnostics page needs those whatever it is.
        DeviceIdentity identity = DeviceIdentity.Parse("SYMMETRICOM,Z9999X,1234A00001,2.00.00")!;

        Assert.Equal(ReceiverModel.Unknown, identity.Receiver);
        Assert.Equal("1234A00001", identity.SerialNumber);
    }

    // ---- Masking ------------------------------------------------------------------------------

    [Theory]
    [InlineData(":PULS:CONT:PER")]
    [InlineData(":PULSe:STARt:TIME")]
    [InlineData(":SYST:COMM:SER2:BAUD?")]
    [InlineData(":SENS:DATA:POIN?")]
    [InlineData(":SENSe:DATA:CLEar")]
    [InlineData(":SENS:TST1:EDGE")]
    [InlineData(":FORM:DATA")]
    [InlineData(":PTIM:PPS:EDGE")]
    public void A59551OnlyCommandIsHiddenOnAZ3805A(string mnemonic)
    {
        // §8.6: "hide these entirely on a Z3805A". Today this holds vacuously — none is in
        // CommandCatalog, tracked as unbuilt on #154 — so this test exists to stop adding one
        // later from quietly offering it on hardware that has no such feature.
        Assert.False(ModelProfile.For(ReceiverModel.Z3805A).Supports(mnemonic));
    }

    [Theory]
    [InlineData(":PULS:CONT:PER")]
    [InlineData(":SYST:COMM:SER2:BAUD?")]
    [InlineData(":SENS:DATA:POIN?")]
    [InlineData(":PTIM:PPS:EDGE")]
    public void TheSameCommandIsAllowedOnA59551(string mnemonic) =>
        Assert.True(ModelProfile.For(ReceiverModel.Hp59551).Supports(mnemonic));

    [Theory]
    [InlineData(":SYST:STAT?")]
    [InlineData(":PTIM:TCOD?")]
    [InlineData(":GPS:SAT:TRAC:COUN?")]
    [InlineData("*IDN?")]
    [InlineData(":DIAG:LOG:COUN?")]
    public void TheSharedCommandSetIsNeverMasked(string mnemonic)
    {
        // The masking must be narrow. A rule that swallowed ordinary commands would disable the
        // application on any receiver it did not recognise, which is the opposite of conservative.
        Assert.True(ModelProfile.For(ReceiverModel.Z3805A).Supports(mnemonic));
        Assert.True(ModelProfile.Conservative.Supports(mnemonic));
    }

    [Fact]
    public void AnUnknownModelGetsTheSmallestSurface()
    {
        // Not recognising a receiver must mean a missing feature, never a command sent to hardware
        // that may not have it.
        ModelProfile profile = ModelProfile.For(ReceiverModel.Unknown);

        Assert.False(profile.HasSecondSerialPort);
        Assert.False(profile.HasProgrammablePulseOutput);
        Assert.False(profile.HasTimestampMemory);
        Assert.False(profile.HasPpsEdgeControl);
        Assert.False(profile.Supports(":PULS:CONT:PER"));
    }

    [Fact]
    public void ANullIdentityGetsTheConservativeProfile() =>
        Assert.Equal(ModelProfile.Conservative, ModelProfile.For((DeviceIdentity?)null));

    [Fact]
    public void OnlyThe59551HasTheOptionalHardware()
    {
        // §8.6's list is one list, so the profiles must not disagree about which model owns it.
        foreach (ReceiverModel model in Enum.GetValues<ReceiverModel>())
        {
            ModelProfile profile = ModelProfile.For(model);
            bool expected = model == ReceiverModel.Hp59551;

            Assert.Equal(expected, profile.HasSecondSerialPort);
            Assert.Equal(expected, profile.HasProgrammablePulseOutput);
            Assert.Equal(expected, profile.HasTimestampMemory);
            Assert.Equal(expected, profile.HasPpsEdgeControl);
        }
    }

    [Fact]
    public void TheZ3805AProfileMatchesWhatTheReceiverItselfSaid()
    {
        // Measured, not inferred: :SYST:COMM:SER2:BAUD? answers -113 "Undefined header" on the live
        // unit, and Tony confirmed one serial connector (#62).
        Assert.False(ModelProfile.For(DeviceIdentity.Parse(LiveIdentity)).HasSecondSerialPort);
    }
}
