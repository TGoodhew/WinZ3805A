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
    /// <param name="rolloverEpochs">
    /// How many 1024-week epochs the receiver's date is behind, from
    /// <see cref="ReceiverStatus.WeekRolloverEpochs"/>. Zero on a receiver that has not rolled over,
    /// which leaves the corrected column empty.
    /// <para>
    /// <b>One figure for the whole export, not one per entry.</b> The epoch count is derived on the
    /// status screen by comparing the receiver's date against the host clock, and a log entry from
    /// years ago has no such comparison available — it carries a date and nothing to check it
    /// against. Correcting every entry by the count the receiver is behind <i>now</i> is right for
    /// any log that does not itself span a rollover boundary, which is every log this receiver can
    /// produce: the boundary is 1024 weeks apart and the log holds a few hundred entries. Per-entry
    /// derivation would be more general and could not be verified against anything.
    /// </para>
    /// </param>
    /// <returns>
    /// A document, or <see langword="null"/> when there is nothing to write. Null rather than an
    /// empty document so the caller can leave the command disabled rather than offering a file
    /// picker that produces a header and no rows.
    /// </returns>
    public static CsvDocument? From(IReadOnlyList<DiagnosticLogEntry>? entries, int rolloverEpochs = 0)
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
        //
        // CorrectedTimestamp is a fifth column rather than a repair of the second. A CSV outlives
        // the window it was exported from, and the caption on the log card that explains the 2006
        // dates does not travel with the file — but neither may the file stop saying what the
        // receiver said (§11.1). Both, side by side, is the only version that is true twice.
        //
        // It stays empty when no correction applies. Repeating the uncorrected value there would
        // imply a correction had been computed and come to zero, which is a different claim from
        // "this receiver has not rolled over".
        CsvDocument document = new("Entry", "Timestamp", "CorrectedTimestamp", "Message", "Raw");

        foreach (DiagnosticLogEntry entry in entries)
        {
            document.AddRow(
                entry.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CsvDocument.Timestamp(entry.Timestamp),
                CsvDocument.Timestamp(GpsWeekRollover.Correct(entry.Timestamp, rolloverEpochs)),
                entry.Message,
                entry.RawText);
        }

        return document;
    }

    /// <summary>The file name offered in the save dialog.</summary>
    /// <remarks>
    /// Dated so that a user exporting a log a week apart does not overwrite the first one, and
    /// sortable so a folder of them reads in order. The clock is passed in rather than read here:
    /// §12 forbids <c>DateTime.Now</c> in the Device library, and the app follows the same rule by
    /// choice wherever a test needs to pin the clock.
    /// </remarks>
    public static string SuggestedFileName(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        return $"receiver-log-{timeProvider.GetLocalNow():yyyy-MM-dd-HHmm}";
    }
}
