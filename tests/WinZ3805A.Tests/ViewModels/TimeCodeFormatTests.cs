using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// §10.14's time code format read: <c>:PTIM:TCOD:FORMat?</c> and what it decodes to.
/// </summary>
/// <remarks>
/// The point this pins is that the format is <i>read</i>, never assumed. <c>z3801.pdf</c> states
/// that T1 is the default and the bench Z3805A answers <c>F2</c>, so a decoder written against the
/// documented default would mis-parse every message that receiver sends (#37).
/// </remarks>
public sealed class TimeCodeFormatTests
{
    // ------------------------------------------------------------------------------- the catalog

    [Fact]
    public void TheFormatQueryIsCatalogedAsASafeQuery()
    {
        ScpiCommand? command = CommandCatalog.Find(TimeCodeFormats.Query);

        Assert.NotNull(command);
        Assert.Equal(SafetyTier.Safe, command.Tier);
        Assert.True(command.IsQuery);
        Assert.Empty(command.Parameters);
        Assert.Null(command.ConfirmationText);
    }

    /// <remarks>
    /// The §8.5 opt-in set is the undocumented queries. This one is documented — in `z3801.pdf`
    /// rather than the 58503A guide the catalog was first derived from — and answers on the bench
    /// receiver, so it is an ordinary tier S query and must not be hidden behind the opt-in.
    /// </remarks>
    [Fact]
    public void TheFormatQueryIsNotExperimental()
    {
        ScpiCommand command = Assert.IsType<ScpiCommand>(CommandCatalog.Find(TimeCodeFormats.Query));

        Assert.False(command.IsExperimental);
        Assert.DoesNotContain(CommandCatalog.Experimental, c => c.Mnemonic == command.Mnemonic);
    }

    // -------------------------------------------------------------------------------- the decode

    /// <remarks>
    /// The bench receiver answers <c>F2</c> bare and with the leading space every response carries.
    /// The manual describes the answer as a quoted string. All three spellings decode.
    /// </remarks>
    [Theory]
    [InlineData("F1", TimeCodeFormat.T1)]
    [InlineData("F2", TimeCodeFormat.T2)]
    [InlineData(" F2", TimeCodeFormat.T2)]
    [InlineData("\"F2\"", TimeCodeFormat.T2)]
    [InlineData("f2", TimeCodeFormat.T2)]
    public void TheDocumentedAnswersDecode(string response, TimeCodeFormat expected) =>
        Assert.Equal(expected, TimeCodeFormats.Parse(response));

    /// <remarks>
    /// The command's parameter is <c>F1</c>/<c>F2</c> while the message header is <c>T1</c>/<c>T2</c>.
    /// Both name the same two formats, so both decode rather than one being read as unknown.
    /// </remarks>
    [Theory]
    [InlineData("T1", TimeCodeFormat.T1)]
    [InlineData("T2", TimeCodeFormat.T2)]
    public void TheHeaderSpellingDecodesToo(string response, TimeCodeFormat expected) =>
        Assert.Equal(expected, TimeCodeFormats.Parse(response));

    /// <remarks>§11.1: the parser never throws, and an unreadable answer becomes Unknown.</remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("F3")]
    [InlineData("E-113")]
    [InlineData("+2")]
    public void AnythingElseIsUnknownRatherThanAGuess(string? response) =>
        Assert.Equal(TimeCodeFormat.Unknown, TimeCodeFormats.Parse(response));

    [Theory]
    [InlineData(TimeCodeFormat.T1, 19)]
    [InlineData(TimeCodeFormat.T2, 23)]
    public void EachFormatKnowsItsMessageLength(TimeCodeFormat format, int expected) =>
        Assert.Equal(expected, TimeCodeFormats.MessageLength(format));

    [Fact]
    public void AnUnknownFormatPredictsNoLength() =>
        Assert.Null(TimeCodeFormats.MessageLength(TimeCodeFormat.Unknown));

    // ------------------------------------------------------------------------------ the readings

    /// <remarks>
    /// Both spellings are shown together deliberately: <c>F2</c> selects the format whose messages
    /// begin <c>T2</c>, and a user comparing this page against a raw time code needs to recognise
    /// them as the same thing.
    /// </remarks>
    [Fact]
    public void TheReadingNamesBothSpellings()
    {
        TimeCodeReading reading = new(TimeCodeFormat.T2, null);

        Assert.Contains("F2", reading.FormatText, StringComparison.Ordinal);
        Assert.Contains("T2", reading.FormatText, StringComparison.Ordinal);
        Assert.NotNull(reading.ContentText);
    }

    [Fact]
    public void AnUnreadFormatRendersAsTheNoValueDashAndExplainsNothing()
    {
        Assert.Equal("—", TimeCodeReading.Unknown.FormatText);
        Assert.Null(TimeCodeReading.Unknown.ContentText);
        Assert.Null(TimeCodeReading.Unknown.Error);
    }

    /// <remarks>
    /// A failed read keeps the dash rather than naming a format, because the receiver is in *some*
    /// format and the read did not establish which — a different claim from naming one.
    /// </remarks>
    [Fact]
    public void AFailedReadCarriesItsReasonWithoutNamingAFormat()
    {
        TimeCodeReading reading = TimeCodeReading.Unknown with { Error = "No answer." };

        Assert.Equal("—", reading.FormatText);
        Assert.Equal("No answer.", reading.Error);
    }
}
