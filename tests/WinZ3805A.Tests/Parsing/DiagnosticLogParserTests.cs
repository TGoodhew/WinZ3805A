using WinZ3805A.Device.Parsing;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Parsing;

/// <summary>
/// The diagnostic log entry format, from the 58503A guide's Command Reference 5-33.
/// </summary>
/// <remarks>
/// <c>"Log NNN: YYYYMMDD.HH:MM:SS: &lt;log_message&gt;"</c>. Documented rather than reverse
/// engineered, which matters because the log is one of the §11.1 captures still waiting for bench
/// time — there is no fixture to check this against yet, only the manual.
/// </remarks>
public sealed class DiagnosticLogParserTests
{
    [Fact]
    public void TheDocumentedFormatParsesIntoItsThreeParts()
    {
        DiagnosticLogEntry entry = DiagnosticLogParser.Parse("\"Log 047: 20260811.09:02:14: GPS lock started\"");

        Assert.Equal(47, entry.Number);
        Assert.Equal(new DateTime(2026, 8, 11, 9, 2, 14, DateTimeKind.Unspecified), entry.Timestamp);
        Assert.Equal("GPS lock started", entry.Message);
        Assert.True(entry.IsStructured);
    }

    [Fact]
    public void QuotesAreOptional()
    {
        DiagnosticLogEntry entry = DiagnosticLogParser.Parse("Log 1: 20260809.20:11:02: Power on");

        Assert.Equal(1, entry.Number);
        Assert.Equal("Power on", entry.Message);
    }

    /// <remarks>
    /// The timestamp carries colons of its own, so anything that split on ':' would cut it in half.
    /// The guide gives it a fixed width, which is what makes this reliable.
    /// </remarks>
    [Fact]
    public void TheTimestampsOwnColonsDoNotConfuseIt()
    {
        DiagnosticLogEntry entry = DiagnosticLogParser.Parse("Log 12: 20260813.23:59:59: Holdover ended");

        Assert.Equal(new DateTime(2026, 8, 13, 23, 59, 59), entry.Timestamp);
        Assert.Equal("Holdover ended", entry.Message);
    }

    /// <remarks>
    /// A message may itself contain a colon, and everything after the timestamp belongs to it.
    /// </remarks>
    [Fact]
    public void AColonInTheMessageIsPartOfTheMessage()
    {
        DiagnosticLogEntry entry = DiagnosticLogParser.Parse(
            "Log 8: 20260810.11:22:33: Antenna: open circuit detected");

        Assert.Equal("Antenna: open circuit detected", entry.Message);
    }

    /// <remarks>
    /// §11.1: nothing throws, and what could not be decomposed survives whole. A firmware revision
    /// that reorders the prefix must cost the user the sort order, not the log.
    /// </remarks>
    [Theory]
    [InlineData("Something entirely different")]
    [InlineData("Log without a colon")]
    [InlineData("Log 5: not a timestamp at all")]
    public void AnUnrecognisedEntryKeepsItsText(string line)
    {
        DiagnosticLogEntry entry = DiagnosticLogParser.Parse(line);

        Assert.False(entry.IsStructured);
        Assert.Contains(entry.Message, line, StringComparison.Ordinal);
        Assert.Equal(line, entry.RawText);
    }

    [Fact]
    public void NothingAtAllIsHandled()
    {
        Assert.Equal(string.Empty, DiagnosticLogParser.Parse(null).Message);
        Assert.Equal(string.Empty, DiagnosticLogParser.Parse("   ").Message);
        Assert.Empty(DiagnosticLogParser.ParseAll(null));
        Assert.Empty(DiagnosticLogParser.ParseAll("  "));
    }

    // ---- The whole log -----------------------------------------------------------------------

    [Fact]
    public void TheWholeLogSplitsIntoItsEntries()
    {
        IReadOnlyList<DiagnosticLogEntry> entries = DiagnosticLogParser.ParseAll(
            "\"Log 3: 20260809.20:11:02: Power on\","
            + "\"Log 4: 20260809.20:11:57: Survey mode started\","
            + "\"Log 5: 20260809.22:15:03: Position hold mode started\"");

        Assert.Equal(3, entries.Count);
        Assert.Equal([3, 4, 5], entries.Select(entry => entry.Number));
        Assert.Equal("Survey mode started", entries[1].Message);
    }

    /// <summary>
    /// A comma inside a message does not split the entry.
    /// </summary>
    /// <remarks>
    /// §10.9's own wireframe contains "Holdover started, not tracking GPS". A naive split on commas
    /// would cut that one entry into two, the second of them timestamp-less and meaningless — and
    /// it would do it to the single most interesting line in the log.
    /// </remarks>
    [Fact]
    public void ACommaInsideAMessageDoesNotSplitTheEntry()
    {
        IReadOnlyList<DiagnosticLogEntry> entries = DiagnosticLogParser.ParseAll(
            "\"Log 46: 20260811.08:58:41: Holdover started, not tracking GPS\","
            + "\"Log 47: 20260811.09:02:14: GPS lock started\"");

        Assert.Equal(2, entries.Count);
        Assert.Equal("Holdover started, not tracking GPS", entries[0].Message);
        Assert.Equal(47, entries[1].Number);
    }

    [Fact]
    public void SpacingAroundTheSeparatorsIsTolerated()
    {
        IReadOnlyList<DiagnosticLogEntry> entries = DiagnosticLogParser.ParseAll(
            " \"Log 1: 20260809.20:11:02: Power on\" , \"Log 2: 20260809.20:12:00: Oven warm\" ");

        Assert.Equal(2, entries.Count);
        Assert.Equal("Oven warm", entries[1].Message);
    }

    /// <summary>
    /// The format the reference unit actually returns, which is not the one the guide describes.
    /// </summary>
    /// <remarks>
    /// Captured from the Z3805A on COM3. The guide says <c>"XYZ", ...</c> — quoted strings,
    /// comma-separated. The unit returns them <b>unquoted</b>, wrapped across lines, with no space
    /// between the entry number's colon and the timestamp, and with messages that contain commas of
    /// their own. Splitting on commas cut "Holdover started, not tracking GPS" in half.
    /// </remarks>
    [Fact]
    public void TheFormatTheReferenceUnitActuallyReturnsParses()
    {
        IReadOnlyList<DiagnosticLogEntry> entries = DiagnosticLogParser.ParseAll(
            "Log 220:20061229.01:09:31:  GPS lock started "
            + "Log 221:20061229.03:35:14:  Holdover started, not tracking GPS "
            + "Log 222:20061229.03:38:42:  GPS lock started");

        Assert.Equal(3, entries.Count);
        Assert.Equal([220, 221, 222], entries.Select(entry => entry.Number));
        Assert.All(entries, entry => Assert.True(entry.IsStructured));

        Assert.Equal("GPS lock started", entries[0].Message);
        Assert.Equal("Holdover started, not tracking GPS", entries[1].Message);
        Assert.Equal(new DateTime(2006, 12, 29, 3, 35, 14), entries[1].Timestamp);
    }

    /// <summary>
    /// Text with no entry prefix continues the entry before it.
    /// </summary>
    /// <remarks>
    /// Which is what a wrapped line is. The unit wraps long messages across the response, and
    /// treating each fragment as its own entry would produce timestamp-less rows that look like
    /// data. Only text before the first prefix has nothing to belong to.
    /// </remarks>
    [Fact]
    public void TextWithoutAPrefixContinuesThePreviousEntry()
    {
        IReadOnlyList<DiagnosticLogEntry> entries = DiagnosticLogParser.ParseAll(
            "Log 1: 20260809.20:11:02: Power on and then some wrapped continuation "
            + "Log 3: 20260809.20:13:00: Locked");

        Assert.Equal(2, entries.Count);
        Assert.Contains("wrapped continuation", entries[0].Message);
        Assert.Equal(3, entries[1].Number);
    }

    /// <remarks>
    /// A response with no recognisable prefix at all is one unparsed entry rather than nothing:
    /// §11.1's rule that what could not be parsed still survives.
    /// </remarks>
    [Fact]
    public void AResponseWithNoPrefixAtAllIsKeptWhole()
    {
        IReadOnlyList<DiagnosticLogEntry> entries = DiagnosticLogParser.ParseAll("nothing recognisable here");

        Assert.Single(entries);
        Assert.False(entries[0].IsStructured);
        Assert.Equal("nothing recognisable here", entries[0].Message);
    }

    /// <remarks>
    /// The guide gives the log a range of 1 to 222 entries, "maximum subject to change", so nothing
    /// here caps the count.
    /// </remarks>
    [Fact]
    public void ALongLogIsParsedWhole()
    {
        string response = string.Join(',', Enumerable.Range(1, 250)
            .Select(n => $"\"Log {n}: 20260809.20:11:02: Entry {n}\""));

        IReadOnlyList<DiagnosticLogEntry> entries = DiagnosticLogParser.ParseAll(response);

        Assert.Equal(250, entries.Count);
        Assert.Equal(250, entries[^1].Number);
    }
}
