using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>
/// Turns the receiver's diagnostic log into P0-13's CSV export.
/// </summary>
/// <remarks>
/// Free of UI types and separate from the page for the same reason the parser is separate from the
/// window: what the columns are, and what happens to an entry the parser could not decompose, are
/// decisions worth asserting without a <c>XamlRoot</c>.
/// </remarks>
public static class DiagnosticLogCsv
{
    /// <summary>
    /// Builds the document for a set of entries, or <see langword="null"/> if there are none.
    /// </summary>
    /// <param name="entries">
    /// The entries to write, in the order they are shown. Callers pass the <i>filtered</i> list:
    /// §9.7.5 calls the command "Export current view", and a filter the user typed is part of the
    /// view. Exporting the whole log while the screen shows four lines would be a surprise, and the
    /// header row records the distinction anyway.
    /// </param>
    /// <returns>
    /// A document, or <see langword="null"/> when there is nothing to write. Null rather than an
    /// empty document so the caller can leave the command disabled rather than offering a file
    /// picker that produces a header and no rows.
    /// </returns>
    public static CsvDocument? From(IReadOnlyList<DiagnosticLogEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return null;
        }

        // "Entry" and "Timestamp" are empty for a line the parser could not decompose, and RawText
        // always carries what the receiver actually sent. That is the same contract the page
        // renders under (§11.1: unparseable becomes null, never a guess), and it is why RawText is
        // a column rather than a fallback stuffed into Message - a row where the structured columns
        // are empty is visibly unparsed rather than quietly wrong.
        CsvDocument document = new("Entry", "Timestamp", "Message", "Raw");

        foreach (DiagnosticLogEntry entry in entries)
        {
            document.AddRow(
                entry.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CsvDocument.Timestamp(entry.Timestamp),
                entry.Message,
                entry.RawText);
        }

        return document;
    }

    /// <summary>The file name offered in the save dialog.</summary>
    /// <remarks>
    /// Dated so that a user exporting a log a week apart does not overwrite the first one, and
    /// sortable so a folder of them reads in order. The clock is passed in rather than read here,
    /// because §12 forbids <c>DateTime.Now</c> in code this project tests.
    /// </remarks>
    public static string SuggestedFileName(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        return $"receiver-log-{timeProvider.GetLocalNow():yyyy-MM-dd-HHmm}";
    }
}
