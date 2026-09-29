using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #551 — what the import confirmation says, which is the only safeguard an import has.
/// </summary>
/// <remarks>
/// An import cannot be undone: samples carry no receiver identity, so two receivers' histories
/// merged together stay merged. Each case below is a thing the user must be told before pressing
/// Import, and the test is that it is said - and, as much, that it is not said when it is not so.
/// </remarks>
public sealed class HistoryTextTests
{
    private const string Here = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";
    private const string Other = "SYMMETRICOM,Z3805A,3542A00123,1.01.03-A";

    private static readonly long First = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero).UtcTicks;
    private static readonly long Last = new DateTimeOffset(2026, 9, 28, 17, 0, 0, TimeSpan.Zero).UtcTicks;

    private static HistoryInspection File(
        string? exportedFrom,
        long rows = 1200,
        long tooOld = 0,
        string[]? unknown = null) =>
        new(
            null,
            new HistoryManifest(1, "1.2.0.0", new DateTimeOffset(Last, TimeSpan.Zero), exportedFrom, rows, First, Last),
            rows,
            First,
            Last,
            unknown ?? [],
            tooOld);

    private static string Describe(HistoryInspection file, string? connected) =>
        HistoryText.Describe(file, connected, TimeSpan.FromDays(56), TimeZoneInfo.Utc);

    [Fact]
    public void ItSaysWhatTheFileHoldsInWords()
    {
        string text = Describe(File(Here), Here);

        Assert.Contains("1,200 readings from 3 August 2026 to 28 September 2026", text, StringComparison.Ordinal);
        Assert.Contains("exported by version 1.2.0.0 on 28 September 2026", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameReceiverIsNamedAndNotWarnedAbout()
    {
        string text = Describe(File(Here), Here);

        Assert.Contains("the receiver connected now, Z3805A serial 3625A02931", text, StringComparison.Ordinal);
        Assert.DoesNotContain("can't be separated", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The case the warning exists for</b> (Tony, 29 Sep 2026): both receivers named, and the
    /// consequence said plainly, before the choice is made.
    /// </summary>
    [Fact]
    public void ADifferentReceiverIsNamedAndTheConsequenceSaid()
    {
        string text = Describe(File(Other), Here);

        Assert.Contains("a different receiver, Z3805A serial 3542A00123", text, StringComparison.Ordinal);
        Assert.Contains("the one connected now is Z3805A serial 3625A02931", text, StringComparison.Ordinal);
        Assert.Contains("can't be separated", text, StringComparison.Ordinal);
    }

    /// <summary>When it cannot be known, that is said too, with the same consequence.</summary>
    [Theory]
    [InlineData(null, Here, "doesn't say which receiver")]
    [InlineData(Other, null, "no receiver is connected now")]
    [InlineData(null, null, "no receiver is connected now")]
    public void AnUnknownReceiverIsSaidToBeUnknown(string? exportedFrom, string? connected, string expected)
    {
        string text = Describe(File(exportedFrom), connected);

        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.Contains("can't be separated", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingsTooOldToKeepAreCountedOnlyWhenThereAreAny()
    {
        Assert.Contains("300 of them are older than the 56 days", Describe(File(Here, tooOld: 300), Here), StringComparison.Ordinal);
        Assert.Contains("1 of them is older", Describe(File(Here, tooOld: 1), Here), StringComparison.Ordinal);
        Assert.DoesNotContain("older than", Describe(File(Here), Here), StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnsFromANewerVersionAreNamed()
    {
        string text = Describe(File(Here, unknown: ["antenna", "loop"]), Here);

        Assert.Contains("newer version", text, StringComparison.Ordinal);
        Assert.Contains("antenna, loop", text, StringComparison.Ordinal);
        Assert.Contains("update the application first", text, StringComparison.Ordinal);
        Assert.DoesNotContain("newer version", Describe(File(Here), Here), StringComparison.Ordinal);
    }

    /// <summary>A <c>trend.db</c> copied by hand has no manifest, and the text still reads.</summary>
    [Fact]
    public void AFileWithNoManifestStillReads()
    {
        HistoryInspection bare = new(null, null, 1, First, First, [], 0);

        string text = Describe(bare, Here);

        Assert.StartsWith("This file holds 1 reading from 3 August 2026.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("exported by version", text, StringComparison.Ordinal);
        Assert.Contains("doesn't say which receiver", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ItAlwaysSaysNothingHereIsChanged() =>
        Assert.EndsWith("only the missing ones are added.", Describe(File(Here), Here), StringComparison.Ordinal);

    [Theory]
    [InlineData(1200, 0, "Imported 1,200 readings from export.sqlite.")]
    [InlineData(1, 0, "Imported 1 reading from export.sqlite.")]
    [InlineData(10, 5, "Imported 10 readings from export.sqlite. 5 were already here.")]
    [InlineData(0, 1, "Imported 0 readings from export.sqlite. 1 was already here.")]
    public void AnImportSaysWhatItDid(long added, long present, string expected) =>
        Assert.Equal(expected, HistoryText.Imported(new HistoryImportResult(added, present), "export.sqlite"));

    [Fact]
    public void AnExportSaysWhatItWrote()
    {
        HistoryManifest manifest = new(1, "1.2.0.0", null, Here, 1200, First, Last);

        Assert.Equal(
            "Exported 1,200 readings from 3 August 2026 to 28 September 2026 to history.sqlite.",
            HistoryText.Exported(manifest, "history.sqlite", TimeZoneInfo.Utc));
    }

    [Fact]
    public void AnEmptyExportSaysSo() =>
        Assert.Equal(
            "Exported to history.sqlite. There were no readings yet.",
            HistoryText.Exported(new HistoryManifest(1, "1.2.0.0", null, null, 0, null, null), "history.sqlite", TimeZoneInfo.Utc));

    [Theory]
    [InlineData(Here, "Z3805A serial 3625A02931")]
    [InlineData("u-blox NEO-M8N ", "u-blox NEO-M8N")]
    public void AReceiverIsNamedAsAPersonReadsIt(string identity, string expected) =>
        Assert.Equal(expected, HistoryText.Name(identity));
}
