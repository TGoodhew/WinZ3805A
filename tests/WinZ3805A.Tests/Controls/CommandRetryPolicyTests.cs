using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Controls;

/// <summary>§9.11's Try again action, and the half that matters — when not to offer it (#251).</summary>
public class CommandRetryPolicyTests
{
    /// <summary>The antenna delay setter, which is §9.11's own worked example of this row.</summary>
    /// <remarks>
    /// Named rather than picked by predicate. The specification illustrates the Try again action with
    /// exactly this command refused for an out-of-range value, so a test that happens to select some
    /// other setter would be testing the rule without testing the case the rule was written for.
    /// </remarks>
    private static readonly ScpiCommand Parameterised = CommandCatalog.Confirm
        .Single(c => c.Mnemonic == ":GPS:REF:ADELay");

    /// <summary>The survey start, which the receiver refuses with nothing for the user to change.</summary>
    private static readonly ScpiCommand Parameterless = CommandCatalog.Confirm
        .Single(c => c.Mnemonic == ":GPS:POSition:SURVey:STATe ONCE");

    private static CommandOutcome Outcome(ScpiCommand command, CommandOutcomeKind kind, int? code = null) => new()
    {
        Kind = kind,
        Command = command,
        Message = "A message.",
        Error = code is int c ? new ScpiError(c, "Something") : null,
    };

    /// <summary>A command that never got an answer is exactly what the button is for.</summary>
    /// <remarks>
    /// Nothing was decided — a timeout or a dropped link — so repeating the request is right whether
    /// or not the command carries parameters. §7.2's reconnect logic does not cover this: the link
    /// may be perfectly healthy and the receiver merely slow to answer one command.
    /// </remarks>
    [Fact]
    public void ACommandThatGotNoAnswerIsWorthRepeating()
    {
        Assert.True(CommandRetryPolicy.ShouldOffer(Outcome(Parameterised, CommandOutcomeKind.Failed)));
        Assert.True(CommandRetryPolicy.ShouldOffer(Outcome(Parameterless, CommandOutcomeKind.Failed)));
    }

    /// <summary>A refused setter is worth repeating, because the user changes the value first.</summary>
    /// <remarks>
    /// §9.11's own example: "Couldn't set antenna delay. The receiver returned error −222, data out
    /// of range. Enter a value between 0 and 999,999 ns." / <b>Try again</b>. The second attempt is
    /// a different request even though it is the same command.
    /// </remarks>
    [Theory]
    [InlineData(-222)]
    [InlineData(-221)]
    [InlineData(-300)]
    public void ARefusedCommandWithParametersIsWorthRepeating(int code) =>
        Assert.True(CommandRetryPolicy.ShouldOffer(
            Outcome(Parameterised, CommandOutcomeKind.Rejected, code)));

    /// <summary>A refused command with nothing to change is not worth repeating.</summary>
    /// <remarks>
    /// <b>The case this policy exists for.</b> <c>:GPS:POSition:SURVey:STATe ONCE</c> is declined
    /// with −300 while the receiver holds a position (#229), and will be declined identically every
    /// time — there is no argument for the user to correct between presses. Offering an action that
    /// is certain to fail invites someone to press it twice and conclude the application is broken,
    /// when the receiver has already said something useful and
    /// <see cref="SurveyRefusalAdvice"/> is answering with the route through.
    /// </remarks>
    [Theory]
    [InlineData(-300)]
    [InlineData(-113)]
    [InlineData(-221)]
    public void ARefusedCommandWithNothingToChangeIsNotWorthRepeating(int code) =>
        Assert.False(CommandRetryPolicy.ShouldOffer(
            Outcome(Parameterless, CommandOutcomeKind.Rejected, code)));

    /// <summary>Success and cancellation offer nothing.</summary>
    /// <remarks>
    /// A success bar dismisses itself after five seconds; an action button on it would be an
    /// invitation to run a tier C command a second time for no reason. Cancellation means the user
    /// declined the confirmation, and re-offering it is the opposite of what they said.
    /// </remarks>
    [Fact]
    public void SuccessAndCancellationOfferNothing()
    {
        Assert.False(CommandRetryPolicy.ShouldOffer(null));
        Assert.False(CommandRetryPolicy.ShouldOffer(Outcome(Parameterised, CommandOutcomeKind.Succeeded)));
        Assert.False(CommandRetryPolicy.ShouldOffer(Outcome(Parameterless, CommandOutcomeKind.Succeeded)));
    }

    /// <summary>The rule reads the command, not a table of error codes.</summary>
    /// <remarks>
    /// Pinned because the tempting implementation is a list of "retryable" SCPI codes, which would
    /// need extending every time the catalog or the firmware grew, and would be wrong the first time
    /// a code meant different things for two commands. The same code decides differently here purely
    /// on whether there is something to change.
    /// </remarks>
    [Fact]
    public void TheSameErrorCodeDecidesDifferentlyByCommand()
    {
        Assert.True(CommandRetryPolicy.ShouldOffer(
            Outcome(Parameterised, CommandOutcomeKind.Rejected, -300)));
        Assert.False(CommandRetryPolicy.ShouldOffer(
            Outcome(Parameterless, CommandOutcomeKind.Rejected, -300)));
    }

    /// <summary>The label is §9.11's, not an invention.</summary>
    [Fact]
    public void TheLabelIsTheOneTheSpecificationGives() =>
        Assert.Equal("Try again", CommandRetryPolicy.ActionLabel);
}
