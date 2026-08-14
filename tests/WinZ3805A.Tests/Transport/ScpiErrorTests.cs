using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Transport;

/// <summary>
/// Splitting the <c>:SYST:ERR?</c> reply §7.2 requires after every tier C command.
/// </summary>
public class ScpiErrorTests
{
    [Theory]
    [InlineData("-221,\"Settings conflict\"", -221, "Settings conflict")]
    [InlineData("-222,\"Data out of range\"", -222, "Data out of range")]
    [InlineData("0,\"No error\"", 0, "No error")]
    [InlineData("+0,\"No error\"", 0, "No error")]
    [InlineData("  -113 , \"Undefined header\"  ", -113, "Undefined header")]
    [InlineData("512,\"Oscillator unlocked\"", 512, "Oscillator unlocked")]
    public void SplitsTheNumberFromTheMessage(string response, int code, string message)
    {
        ScpiError? error = ScpiError.TryParse(response);

        Assert.NotNull(error);
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
    }

    /// <summary>Only zero means the queue was empty. Everything else is worth telling the user.</summary>
    [Theory]
    [InlineData("0,\"No error\"", false)]
    [InlineData("-222,\"Data out of range\"", true)]
    public void ZeroIsNotAnError(string response, bool expected) =>
        Assert.Equal(expected, ScpiError.TryParse(response)!.IsError);

    /// <summary>§11.1: never throw. An unrecognisable reply is absent, not an exception.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no comma here")]
    [InlineData("not a number,\"Message\"")]
    public void ReturnsNullRatherThanThrowing(string? response) =>
        Assert.Null(ScpiError.TryParse(response));

    /// <summary>A dropped quote loses the punctuation, not the reading.</summary>
    [Fact]
    public void SurvivesAnUnquotedMessage()
    {
        ScpiError? error = ScpiError.TryParse("-410,Query INTERRUPTED");

        Assert.NotNull(error);
        Assert.Equal(-410, error.Code);
        Assert.Equal("Query INTERRUPTED", error.Message);
    }

    /// <summary>An empty message still leaves a usable number.</summary>
    [Fact]
    public void NamesTheGapWhenThereIsNoMessage()
    {
        ScpiError? error = ScpiError.TryParse("-100,\"\"");

        Assert.NotNull(error);
        Assert.Equal(-100, error.Code);
        Assert.Equal("no description given", error.Message);
    }

    /// <summary>§9.11: the number and the meaning, in one sentence.</summary>
    [Fact]
    public void DescribesItselfWithBothTheNumberAndTheMeaning()
    {
        string sentence = ScpiError.TryParse("-222,\"Data out of range\"")!.Describe();

        Assert.Contains("-222", sentence, StringComparison.Ordinal);
        Assert.Contains("Data out of range", sentence, StringComparison.Ordinal);
    }
}
