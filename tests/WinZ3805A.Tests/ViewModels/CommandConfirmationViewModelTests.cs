using WinZ3805A.Device.Commands;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// What a tier C dialog says and when it will let the command through (P0-8, §8.3, §9.7.4).
/// </summary>
public class CommandConfirmationViewModelTests
{
    private static ScpiCommand Command(string mnemonic) => CommandCatalog.Find(mnemonic)!;

    // -------------------------------------------------------------------------------------
    // P0-8's acceptance criterion
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// <b>P0-8, verbatim:</b> given the user clicks <em>Force holdover</em>, when the dialog
    /// appears, then the confirm button is disabled until "I understand" is ticked.
    /// </summary>
    [Fact]
    public void ForceHoldoverCannotBeConfirmedUntilItIsAcknowledged()
    {
        CommandConfirmationViewModel model = new(Command(":SYNC:HOLDover:INITiate"));

        Assert.True(model.RequiresAcknowledgement);
        Assert.False(model.CanConfirm);

        model.IsAcknowledged = true;

        Assert.True(model.CanConfirm);
    }

    /// <summary>Unticking it closes the gate again — the state is the tick, not a one-way latch.</summary>
    [Fact]
    public void UntickingTheAcknowledgementClosesTheGate()
    {
        CommandConfirmationViewModel model = new(Command(":SYST:PRESet")) { IsAcknowledged = true };

        model.IsAcknowledged = false;

        Assert.False(model.CanConfirm);
    }

    /// <summary>§9.7.4 names four strong variants, and the catalog agrees on exactly those four.</summary>
    [Theory]
    [InlineData(":SYST:PRESet")]
    [InlineData(":SYNC:HOLDover:INITiate")]
    [InlineData(":GPS:SAT:TRAC:IGNore ALL")]
    [InlineData(":GPS:SAT:TRAC:INCLude NONE")]
    public void TheFourStrongVariantsAllRequireTheTick(string mnemonic) =>
        Assert.True(new CommandConfirmationViewModel(Command(mnemonic)).RequiresAcknowledgement);

    /// <summary>Every other tier C command confirms on the button alone.</summary>
    [Fact]
    public void AnOrdinaryTierCCommandConfirmsWithoutATick()
    {
        CommandConfirmationViewModel model = new(Command(":DIAG:LOG:CLEar"));

        Assert.False(model.RequiresAcknowledgement);
        Assert.True(model.CanConfirm);
    }

    /// <summary>
    /// §10.8's guard: a page that cannot establish the time since power-up can demand the tick on a
    /// command that would not otherwise need one.
    /// </summary>
    [Fact]
    public void APageCanDemandTheTickOnACommandThatWouldNotNeedOne()
    {
        CommandConfirmationViewModel model = new(
            Command(":DIAG:LOG:CLEar"), requireAcknowledgement: true);

        Assert.True(model.RequiresAcknowledgement);
        Assert.False(model.CanConfirm);
    }

    /// <summary><c>CanConfirm</c> is what the dialog binds to, so it has to announce its own change.</summary>
    [Fact]
    public void TickingAnnouncesThatConfirmationBecamePossible()
    {
        CommandConfirmationViewModel model = new(Command(":SYST:PRESet"));
        List<string?> changed = [];
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.IsAcknowledged = true;

        Assert.Contains(nameof(model.CanConfirm), changed);
    }

    // -------------------------------------------------------------------------------------
    // Copy
    // -------------------------------------------------------------------------------------

    /// <summary>§9.11: a verb keeps its identity from the button into the dialog.</summary>
    [Fact]
    public void TheTitleAndTheConfirmButtonCarryTheCommandsOwnVerb()
    {
        CommandConfirmationViewModel model = new(Command(":GPS:POSition:SURVey:STATe ONCE"));

        Assert.Equal("Start position survey", model.ConfirmLabel);
        Assert.Equal("Start position survey?", model.Title);
    }

    /// <summary>The §8.3 sentence reaches the dialog as the table writes it.</summary>
    [Fact]
    public void TheBodyIsTheSection83TextVerbatim()
    {
        ScpiCommand command = Command(":SYNC:IMMediate");

        Assert.Equal(
            "Force immediate resynchronisation? This causes a step change in the 1 PPS output.",
            new CommandConfirmationViewModel(command).Message);
        Assert.Equal(command.ConfirmationText, new CommandConfirmationViewModel(command).Message);
    }

    /// <summary>
    /// §8.1's <c>{0}</c> exists so the dialog states the number about to be sent rather than a
    /// placeholder. A dialog that said "Set elevation mask to {0}°" would be worse than useless.
    /// </summary>
    [Fact]
    public void TheValueIsSubstitutedIntoTheConsequence()
    {
        CommandConfirmationViewModel model = new(
            Command(":GPS:SAT:TRAC:EMANgle"), argument: "1.5E+001", displayValue: "15");

        Assert.StartsWith("Set elevation mask to 15°?", model.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", model.Message, StringComparison.Ordinal);
    }

    /// <summary>With no value to hand, the sentence still has to read as English.</summary>
    [Fact]
    public void APlaceholderWithNoValueDoesNotReachTheScreen()
    {
        CommandConfirmationViewModel model = new(Command(":GPS:REF:ADELay"));

        Assert.DoesNotContain("{0}", model.Message, StringComparison.Ordinal);
    }

    /// <summary>The display value is what the user typed, not what goes on the wire.</summary>
    [Fact]
    public void TheDialogQuotesTheDisplayValueRatherThanTheWireFormat()
    {
        CommandConfirmationViewModel model = new(
            Command(":GPS:REF:ADELay"), argument: "8.5E-008", displayValue: "85");

        Assert.Contains("85 ns", model.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("8.5E-008", model.Message, StringComparison.Ordinal);
    }

    /// <summary>A page's own warning is carried through, and its absence is visible.</summary>
    [Fact]
    public void ThePagesCautionIsOptionalAndReported()
    {
        Assert.False(new CommandConfirmationViewModel(Command(":DIAG:LOG:CLEar")).HasCaution);

        CommandConfirmationViewModel warned = new(
            Command(":SYNC:HOLDover:INITiate"), caution: "Powered up 3 h ago.");

        Assert.True(warned.HasCaution);
        Assert.Equal("Powered up 3 h ago.", warned.Caution);
    }

    /// <summary>Whitespace is not a caution.</summary>
    [Fact]
    public void ABlankCautionIsNoCaution() =>
        Assert.False(new CommandConfirmationViewModel(Command(":DIAG:LOG:CLEar"), caution: "   ").HasCaution);

    // -------------------------------------------------------------------------------------
    // The tier boundary
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A safe command has no consequence to state, and a dialog built over one would be a
    /// confirmation with nothing in it — which is how a meaningless "are you sure" gets in.
    /// </summary>
    [Fact]
    public void ASafeCommandCannotBeGivenAConfirmationDialog() =>
        Assert.Throws<ArgumentException>(() => new CommandConfirmationViewModel(Command(":SYNC:STAT?")));
}
