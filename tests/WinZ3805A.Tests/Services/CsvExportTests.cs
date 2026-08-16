using System.Globalization;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// P0-13: "Clear is tier C; export writes UTF-8 CSV."
/// </summary>
/// <remarks>
/// The escaping is the half worth asserting. A log of well-behaved lines exports correctly under
/// any implementation; it is the entry containing a comma that decides whether the file loads with
/// the right number of columns, and the receiver's own messages contain commas — "Holdover started,
/// not tracking GPS" is in every capture taken from this unit.
/// </remarks>
public sealed class CsvExportTests
{
    private static DiagnosticLogEntry Entry(int number, string message, DateTime? at = null) => new()
    {
        Number = number,
        Timestamp = at ?? new DateTime(2026, 8, 15, 9, 2, 14, DateTimeKind.Unspecified),
        Message = message,
        RawText = $"Log {number:d3}:20260815.09:02:14:  {message}",
    };

    // ---------------------------------------------------------------------------- CsvDocument

    [Fact]
    public void TheHeaderRowComesFirstAndRowsFollowInOrder()
    {
        CsvDocument document = new("A", "B");
        document.AddRow("1", "2");
        document.AddRow("3", "4");

        Assert.Equal("A,B\r\n1,2\r\n3,4\r\n", document.ToText());
    }

    /// <summary>RFC 4180 wants CRLF regardless of what the host platform uses.</summary>
    [Fact]
    public void RecordsAreSeparatedByCarriageReturnLineFeed()
    {
        CsvDocument document = new("A");
        document.AddRow("x");

        Assert.EndsWith("\r\n", document.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", document.ToText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("has\nnewline", "\"has\nnewline\"")]
    [InlineData("has\r\nbreak", "\"has\r\nbreak\"")]
    [InlineData("\"", "\"\"\"\"")]
    public void FieldsAreQuotedOnlyWhenTheyHaveToBe(string value, string expected)
    {
        CsvDocument document = new("A");
        document.AddRow(value);

        Assert.Equal($"A\r\n{expected}\r\n", document.ToText());
    }

    /// <summary>
    /// A row of the wrong width loads happily into a spreadsheet and is silently wrong, which is
    /// worse than not loading. It is a programming error, so it throws.
    /// </summary>
    [Fact]
    public void ARowThatDoesNotMatchTheColumnsIsRefused()
    {
        CsvDocument document = new("A", "B");

        Assert.Throws<ArgumentException>(() => document.AddRow("only one"));
        Assert.Throws<ArgumentException>(() => document.AddRow("one", "two", "three"));
    }

    [Fact]
    public void ANullValueBecomesAnEmptyFieldRatherThanTheWordNull()
    {
        CsvDocument document = new("A", "B");
        document.AddRow(null, "x");

        Assert.Equal("A,B\r\n,x\r\n", document.ToText());
    }

    /// <summary>
    /// §6.4 and §12 keep culture out of parsing; this is the same rule on the way out. Under a
    /// culture that writes 33,1 an unqualified format would turn one column into two.
    /// </summary>
    [Fact]
    public void NumbersAreFormattedInvariantlyWhateverTheCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("-33.10", CsvDocument.Number(-33.1, 2));
            Assert.Equal("0.001", CsvDocument.Number(0.001, 3));
            Assert.Equal(string.Empty, CsvDocument.Number(null, 2));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// The export is data, not a readout. §9.5.3's U+2212 minus belongs on screen and would make a
    /// spreadsheet read the cell as text.
    /// </summary>
    [Fact]
    public void NegativeNumbersUseAnAsciiHyphenNotTheTypographicMinus()
    {
        Assert.DoesNotContain('−', CsvDocument.Number(-33.1, 1));
        Assert.StartsWith("-", CsvDocument.Number(-33.1, 1), StringComparison.Ordinal);
    }

    /// <summary>P0-13 names the encoding, so it is asserted rather than assumed.</summary>
    [Fact]
    public void TheEncodingIsUtf8WithAByteOrderMark()
    {
        Assert.Equal("utf-8", CsvDocument.Encoding.WebName);

        byte[] preamble = CsvDocument.Encoding.GetPreamble();
        Assert.Equal([0xEF, 0xBB, 0xBF], preamble);
    }

    /// <summary>
    /// The reason the BOM is there at all: a non-ASCII character has to survive the round trip
    /// into a reader that would otherwise guess the system code page.
    /// </summary>
    [Fact]
    public void NonAsciiTextSurvivesTheEncodingRoundTrip()
    {
        CsvDocument document = new("Message");
        document.AddRow("Antenna delay 77 ns · σ 12.4 ns · −33.1");

        byte[] bytes = CsvDocument.Encoding.GetPreamble()
            .Concat(CsvDocument.Encoding.GetBytes(document.ToText()))
            .ToArray();

        string decoded = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');

        Assert.Contains("· σ 12.4 ns · −33.1", decoded, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------- DiagnosticLogCsv

    [Fact]
    public void TheLogExportCarriesTheNumberTimestampMessageAndRawLine()
    {
        CsvDocument? document = DiagnosticLogCsv.From([Entry(222, "GPS lock started")]);

        Assert.NotNull(document);
        Assert.Equal(["Entry", "Timestamp", "Message", "Raw"], document.Columns);

        string[] row = Assert.Single(document.Rows);
        Assert.Equal("222", row[0]);
        Assert.Equal("2026-08-15 09:02:14", row[1]);
        Assert.Equal("GPS lock started", row[2]);
        Assert.Contains("Log 222", row[3], StringComparison.Ordinal);
    }

    /// <summary>
    /// The receiver really does log this line, and it is the reason the escaping is tested at all.
    /// </summary>
    [Fact]
    public void AMessageContainingACommaStaysOneField()
    {
        CsvDocument? document = DiagnosticLogCsv.From([Entry(221, "Holdover started, not tracking GPS")]);

        Assert.NotNull(document);
        Assert.Contains("\"Holdover started, not tracking GPS\"", document.ToText(), StringComparison.Ordinal);

        // One record, and the header has four columns, so a correct file has exactly four fields
        // on the data line rather than five.
        string dataLine = document.ToText().Split("\r\n")[1];
        Assert.Equal(4, CountFields(dataLine));
    }

    /// <summary>
    /// §11.1: what the parser could not decompose still has to survive. The structured columns go
    /// empty and the raw text stays, so the row is visibly unparsed rather than quietly wrong.
    /// </summary>
    [Fact]
    public void AnEntryThatDidNotParseKeepsItsRawTextAndLeavesTheOtherColumnsEmpty()
    {
        CsvDocument? document = DiagnosticLogCsv.From([
            new DiagnosticLogEntry { RawText = "something unexpected", Message = "something unexpected" },
        ]);

        Assert.NotNull(document);

        string[] row = Assert.Single(document.Rows);
        Assert.Equal(string.Empty, row[0]);
        Assert.Equal(string.Empty, row[1]);
        Assert.Equal("something unexpected", row[3]);
    }

    /// <summary>
    /// Null rather than a header-only file, so the command can be disabled instead of offering a
    /// save dialog that writes nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(NothingToExport))]
    public void ThereIsNoDocumentWhenThereAreNoEntries(IReadOnlyList<DiagnosticLogEntry>? entries) =>
        Assert.Null(DiagnosticLogCsv.From(entries));

    public static TheoryData<IReadOnlyList<DiagnosticLogEntry>?> NothingToExport()
    {
        TheoryData<IReadOnlyList<DiagnosticLogEntry>?> cases = [];
        cases.Add(null);
        cases.Add(Array.Empty<DiagnosticLogEntry>());
        return cases;
    }

    [Fact]
    public void EveryEntryGivenIsWrittenInOrder()
    {
        CsvDocument? document = DiagnosticLogCsv.From([
            Entry(3, "third"),
            Entry(2, "second"),
            Entry(1, "first"),
        ]);

        Assert.NotNull(document);
        Assert.Equal(["3", "2", "1"], document.Rows.Select(row => row[0]));
    }

    /// <summary>
    /// §12 forbids reading the clock directly, and the name is dated, so it takes a provider the
    /// test can pin — otherwise this assertion would be true only for the minute it was written in.
    /// </summary>
    [Fact]
    public void TheSuggestedFileNameIsDatedFromTheInjectedClock()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 15, 17, 40, 0, TimeSpan.Zero));

        Assert.Equal("receiver-log-2026-08-15-1740", DiagnosticLogCsv.SuggestedFileName(clock));
    }

    private static int CountFields(string line)
    {
        int fields = 1;
        bool quoted = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields++;
            }
        }

        return fields;
    }
}
