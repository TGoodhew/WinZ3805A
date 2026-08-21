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
    // The §8.3 entries that are a bare question
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Where §8.3 supplies a consequence, that sentence is the whole of the body and nothing is
    /// added beside it.
    /// </summary>
    [Fact]
    public void AConsequenceInSection83LeavesNothingToExplain() =>
        Assert.Null(new CommandConfirmationViewModel(Command(":SYNC:IMMediate")).Explanation);

    /// <summary>
    /// Where it does not, the dialog would otherwise be a title and its own echo — which is the
    /// generic "are you sure" #8 says a tier C dialog must not be. The catalog's description fills
    /// the gap, and §8.3's words are still there in full.
    /// </summary>
    [Fact]
    public void ABareQuestionGetsTheCommandsDescription()
    {
        CommandConfirmationViewModel model = new(Command(":SYNC:HOLD:DUR:THReshold"));

        Assert.Equal("Set holdover threshold?", model.Message);
        Assert.Equal("Sets how long holdover may run before it is reported as exceeded.", model.Explanation);
    }

    /// <summary>
    /// A confirmation that does not say what it is about to set asks the user to take the field on
    /// trust, and the field is behind the dialog.
    /// </summary>
    [Fact]
    public void AValueWithNowhereInTheSentenceToGoIsShownSeparately()
    {
        CommandConfirmationViewModel model = new(
            Command(":SYNC:HOLD:DUR:THReshold"), argument: "3600", displayValue: "3600");

        Assert.Equal("Threshold: 3600 s", model.ValueSummary);
    }

    /// <summary>And where §8.3's sentence already names it, repeating it would be the redundancy.</summary>
    [Fact]
    public void AValueAlreadyInTheSentenceIsNotRepeated()
    {
        CommandConfirmationViewModel model = new(
            Command(":GPS:SAT:TRAC:EMANgle"), argument: "15", displayValue: "15");

        Assert.Contains("15°", model.Message, StringComparison.Ordinal);
        Assert.Null(model.ValueSummary);
    }

    /// <summary>A command that takes no value has none to summarise.</summary>
    [Fact]
    public void ACommandWithNoValueSummarisesNothing() =>
        Assert.Null(new CommandConfirmationViewModel(Command(":DIAG:LOG:CLEar")).ValueSummary);

    /// <summary>
    /// Every tier C dialog says something beyond restating its own title — either §8.3 gives it a
    /// consequence, or the description does. This is the property #8 actually asks for, so it is
    /// asserted over the whole table rather than on the two entries that prompted it.
    /// </summary>
    [Fact]
    public void NoTierCDialogIsJustItsOwnTitleTwice()
    {
        Assert.All(CommandCatalog.Confirm, command =>
        {
            CommandConfirmationViewModel model = new(command);
            string body = $"{model.Message} {model.Explanation}".Trim();

            Assert.True(
                body.Length > model.Title.Length + 8,
                $"{command.Mnemonic}: \"{model.Title}\" over \"{body}\" says nothing extra.");
        });
    }

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
    // -------------------------------------------------------------------------------------
    // What the dialog calls the value
    // -------------------------------------------------------------------------------------

    /// <summary>One parameter names itself in the dialog.</summary>
    /// <remarks>
    /// A register mask rather than the antenna delay, which would have been the obvious
    /// choice: §8.3 gives the delay the sentence "Set antenna delay to {0} ns?", so its
    /// summary is deliberately null - repeating the value under a sentence that already
    /// carries it is the redundancy this property exists to avoid.
    /// </remarks>
    [Fact]
    public void ASingleParameterIsLabelledWithItsOwnName()
    {
        CommandConfirmationViewModel model = new(Command(":STAT:OPER:ENABle"), "255", "255");

        Assert.Equal("Mask: 255", model.ValueSummary);
    }

    /// <summary>
    /// Several parameters are labelled with the command's own word for the whole.
    /// </summary>
    /// <remarks>
    /// #147 turned the position commands from one parameter into nine, which is what let the
    /// console offer them at all. Without <c>ValueLabel</c> that would have quietly changed the
    /// Position page's confirmation dialog from "Position: N 47° …" to "Value: N 47° …" — a
    /// regression in a tier C dialog, caused by a change made somewhere else entirely.
    /// </remarks>
    [Fact]
    public void SeveralParametersAreLabelledWithTheCommandsOwnWord()
    {
        CommandConfirmationViewModel model = new(
            Command(":GPS:POSition"),
            "N,47,31,18.822,W,122,12,22.152,100",
            "N 47° 31′ 18.822″, W 122° 12′ 22.152″, 100.00 m");

        Assert.StartsWith("Position: ", model.ValueSummary, StringComparison.Ordinal);
    }
}
