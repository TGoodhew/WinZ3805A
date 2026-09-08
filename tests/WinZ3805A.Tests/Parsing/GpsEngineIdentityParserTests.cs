using WinZ3805A.Device.Parsing;

namespace WinZ3805A.Tests.Parsing;

/// <summary>
/// <c>:DIAG:IDEN:GPS?</c>, against what the bench receiver actually printed (#443).
/// </summary>
/// <remarks>
/// Unlike most of this folder, the reply below is a <b>measurement</b> and not a reading of a
/// manual: captured 7 Sep 2026 from the Z3805A, serial 3625A02931, firmware <c>1.01.03-A</c>, with
/// the error queue verified empty immediately before and after so the answer is attributable to
/// this command alone.
/// </remarks>
public sealed class GpsEngineIdentityParserTests
{
    /// <summary>Exactly what came back off the wire, byte for byte.</summary>
    private const string BenchReply =
        "\"--\",\"SFTW P/N # 4850266\",\"SOFTWARE VER # 005\",\"--\",\"--\"," +
        "\"MODEL # FURUNO GT-80\",\"--\",\"--\",\"--\",\"--\"";

    [Fact]
    public void TheBenchReplyYieldsItsThreePopulatedFieldsInReceiverOrder()
    {
        IReadOnlyList<string> fields = GpsEngineIdentityParser.Parse(BenchReply);

        Assert.Equal(
            ["SFTW P/N # 4850266", "SOFTWARE VER # 005", "MODEL # FURUNO GT-80"],
            fields);
    }

    /// <summary>
    /// The manual promises a serial number; this receiver does not give one.
    /// </summary>
    /// <remarks>
    /// The Z3801A guide describes the reply as "the model number, serial number, and revision of
    /// the internal GPS receiver". Seven of the ten slots are <c>--</c> and the serial number is
    /// among them. This test exists to keep anyone from later reintroducing a positional reading of
    /// the reply: there is no slot index that means "serial number" here, and a parser that
    /// invented one would be asserting a fact the receiver declined to state.
    /// </remarks>
    [Fact]
    public void PlaceholderFieldsAreDroppedRatherThanCountedOrLabelled()
    {
        IReadOnlyList<string> fields = GpsEngineIdentityParser.Parse(BenchReply);

        Assert.DoesNotContain("--", fields);
        Assert.All(fields, f => Assert.NotEqual("--", f));
        Assert.Equal(3, fields.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"--\",\"--\",\"--\"")]
    public void NothingUsableYieldsAnEmptyListRatherThanThrowing(string? reply)
    {
        Assert.Empty(GpsEngineIdentityParser.Parse(reply));
    }

    /// <summary>A comma inside a quoted field is part of the field, not a separator.</summary>
    [Fact]
    public void CommasInsideQuotesDoNotSplitAField()
    {
        IReadOnlyList<string> fields =
            GpsEngineIdentityParser.Parse("\"MODEL # ACME, INC. GT-80\",\"--\"");

        Assert.Equal(["MODEL # ACME, INC. GT-80"], fields);
    }

    /// <summary>An unquoted reply is still read, because §11.1 forbids the parser to give up.</summary>
    [Fact]
    public void UnquotedFieldsAreStillRead()
    {
        IReadOnlyList<string> fields = GpsEngineIdentityParser.Parse("--,SOFTWARE VER # 005,--");

        Assert.Equal(["SOFTWARE VER # 005"], fields);
    }

    /// <summary>
    /// A firmware that fills more slots gains more lines, with no change here.
    /// </summary>
    /// <remarks>
    /// The point of refusing to read by position: this is the shape the manual describes, and the
    /// parser handles it without having been told which field is which.
    /// </remarks>
    [Fact]
    public void AFullerReplyFromAnotherFirmwareNeedsNoPositionalKnowledge()
    {
        IReadOnlyList<string> fields = GpsEngineIdentityParser.Parse(
            "\"MODEL # FURUNO GT-8031\",\"SERIAL # 12345\",\"SOFTWARE VER # 007\"");

        Assert.Equal(
            ["MODEL # FURUNO GT-8031", "SERIAL # 12345", "SOFTWARE VER # 007"],
            fields);
    }
}
