using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Controls;

/// <summary>§10.6's advice when the receiver declines a survey (#229, #12).</summary>
public class SurveyRefusalAdviceTests
{
    private static readonly ScpiCommand Start = CommandCatalog.Safe
        .First(c => c.Mnemonic.Contains("SURV", StringComparison.Ordinal));

    private static CommandOutcome Failed(int code, string message) => new()
    {
        Kind = CommandOutcomeKind.Rejected,
        Command = Start,
        Message = "Couldn't start position survey.",
        Error = new ScpiError(code, message),
    };

    /// <summary>The refusal this exists for gets the advice.</summary>
    /// <remarks>
    /// −300 with the survey-start command is the 27 Aug signature: reproduced four times across two
    /// spellings, at partial and at full lock, with the same <c>:GPS:</c> subtree answering queries
    /// throughout.
    /// </remarks>
    [Fact]
    public void ADeviceSpecificRefusalIsWorthExplaining()
    {
        string? advice = SurveyRefusalAdvice.ForFailedStart(Failed(-300, "Device-specific error"));

        Assert.NotNull(advice);
        Assert.Contains("already holding a position", advice, StringComparison.Ordinal);
        Assert.Contains("power-cycle", advice, StringComparison.Ordinal);
    }

    /// <summary>Every other failure gets nothing, which is the point.</summary>
    /// <remarks>
    /// <b>The half that keeps this honest.</b> −300 is device-specific by definition, so the receiver
    /// has not said why. Attaching the same sentence to a timeout or a −113 would send someone to
    /// power-cycle an instrument over a loose cable — and the failure most likely to be a loose cable
    /// is the one #14 is about to create deliberately.
    /// </remarks>
    [Theory]
    [InlineData(-113, "Undefined header")]
    [InlineData(-222, "Data out of range")]
    [InlineData(-350, "Queue overflow")]
    [InlineData(1, "Device-specific positive")]
    public void AnyOtherErrorGetsNoAdvice(int code, string message) =>
        Assert.Null(SurveyRefusalAdvice.ForFailedStart(Failed(code, message)));

    /// <summary>A failure with no error queue entry gets nothing either.</summary>
    /// <remarks>
    /// A transport timeout produces an outcome that failed with no <c>ScpiError</c> at all, because
    /// nothing was read back. There is no code to recognise and nothing to advise.
    /// </remarks>
    [Fact]
    public void AFailureWithNoErrorCodeGetsNoAdvice() =>
        Assert.Null(SurveyRefusalAdvice.ForFailedStart(new CommandOutcome
        {
            Kind = CommandOutcomeKind.Failed,
            Command = Start,
            Message = "The receiver did not answer.",
        }));

    /// <summary>Success and cancellation are not occasions for advice.</summary>
    [Fact]
    public void SuccessAndCancellationGetNoAdvice()
    {
        Assert.Null(SurveyRefusalAdvice.ForFailedStart(null));
        Assert.Null(SurveyRefusalAdvice.ForFailedStart(new CommandOutcome
        {
            Kind = CommandOutcomeKind.Succeeded,
            Command = Start,
            Message = "Position survey started.",
        }));
    }

    /// <summary>The advice names a control that is actually on the card.</summary>
    /// <remarks>
    /// §10.6's wireframe puts "Survey on power-up" in the same card as the button that produces this
    /// error, so the instruction can be followed without leaving the page. Pinned because the advice
    /// becomes wrong the moment that checkbox is renamed or moved, and nothing else would notice.
    /// </remarks>
    [Fact]
    public void TheAdviceNamesTheCheckboxOnTheSameCard() =>
        Assert.Contains(
            "Survey on power-up",
            SurveyRefusalAdvice.ForFailedStart(Failed(-300, "Device-specific error")),
            StringComparison.Ordinal);
}
