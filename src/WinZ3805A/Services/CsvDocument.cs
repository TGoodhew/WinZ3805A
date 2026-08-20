using System.Globalization;
using System.Text;

namespace WinZ3805A.Services;

/// <summary>
/// A table on its way to a <c>.csv</c> file.
/// </summary>
/// <remarks>
/// <para>
/// P0-13 requires the diagnostic log to export as UTF-8 CSV, and P1-1 will want the same for trend
/// data. Building the text here rather than in a page keeps the escaping — which is the only part
/// with rules — testable without a window.
/// </para>
/// <para>
/// <b>This is data leaving the application, not a readout.</b> §9.5.3's typesetting rules do not
/// apply and must not be applied: a readout shows −33.1 with U+2212 because a hyphen reads badly at
/// a glance across a bench, but a spreadsheet parsing U+2212 gets text rather than a number. Values
/// are written with <see cref="CultureInfo.InvariantCulture"/> and an ASCII hyphen for the same
/// reason a decimal point is not a comma here.
/// </para>
/// </remarks>
public sealed class CsvDocument
{
    private readonly List<string[]> _rows = [];

    /// <summary>Creates a document with the given column headers.</summary>
    /// <param name="columns">Header row. Must have at least one column.</param>
    public CsvDocument(params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Length == 0)
        {
            throw new ArgumentException("A CSV document needs at least one column.", nameof(columns));
        }

        Columns = columns;
    }

    /// <summary>
    /// UTF-8 <b>with</b> a byte order mark.
    /// </summary>
    /// <remarks>
    /// The BOM is deliberate and is the one debatable decision in this file. Excel on Windows reads
    /// a BOM-less UTF-8 file as the system ANSI code page, so a log entry containing anything above
    /// U+007F arrives as mojibake for the audience most likely to open it. The cost is that a naive
    /// reader which opens the file as UTF-8 without handling the BOM sees U+FEFF on the first
    /// header. Excel is the more probable destination for a diagnostic log, so it wins; the tests
    /// assert the BOM so that this stays a decision rather than becoming an accident.
    /// </remarks>
    public static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>The header row.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The data rows, in the order they were added.</summary>
    public IReadOnlyList<string[]> Rows => _rows;

    /// <summary>Appends a row.</summary>
    /// <param name="values">
    /// One value per column. A row of the wrong width is a programming error rather than bad data,
    /// so it throws instead of padding — a silently ragged CSV is worse than no CSV, because it
    /// loads and is wrong.
    /// </param>
    public void AddRow(params string?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Length != Columns.Count)
        {
            throw new ArgumentException(
                $"Expected {Columns.Count} value(s) to match the columns, got {values.Length}.",
                nameof(values));
        }

        _rows.Add([.. values.Select(value => value ?? string.Empty)]);
    }

    /// <summary>Formats a number for a cell, invariantly.</summary>
    /// <remarks>
    /// Exists so callers do not each remember to pass the culture. A number formatted under a
    /// German UI writes 33,1 and silently becomes two columns.
    /// </remarks>
    public static string Number(double? value, int decimals) =>
        value is null ? string.Empty : value.Value.ToString($"F{decimals}", CultureInfo.InvariantCulture);

    /// <summary>Formats a timestamp for a cell to millisecond precision, or empty.</summary>
    /// <remarks>
    /// For a source whose samples are finer than a second. The trend is polled at roughly 1 Hz but
    /// not exactly, so two samples can land in the same second — and at whole-second precision they
    /// come out as identical rows, which reads as duplicated data rather than as two readings.
    /// The receiver's own diagnostic log has second resolution and uses
    /// <see cref="Timestamp(DateTime?)"/> instead: milliseconds there would be precision the device
    /// never claimed.
    /// </remarks>
    public static string PreciseTimestamp(DateTime? value) =>
        value is null ? string.Empty : value.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Formats a timestamp for a cell as ISO 8601, or empty.</summary>
    /// <remarks>
    /// Round-trippable and unambiguous, which a localised date is not. No offset is appended: the
    /// receiver's log does not carry one, and inventing Z would assert a time scale the device
    /// never claimed (see <c>DiagnosticLogEntry.Timestamp</c>).
    /// </remarks>
    public static string Timestamp(DateTime? value) =>
        value is null ? string.Empty : value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Renders the whole document as CSV text.</summary>
    /// <remarks>
    /// RFC 4180: CRLF between records regardless of platform, and a field is quoted only when it
    /// has to be — when it contains a comma, a quote, or a line break — with embedded quotes
    /// doubled. Quoting everything unconditionally would also be valid and is not done, because
    /// the log is mostly plain text and a file full of unnecessary quotes is harder to read in the
    /// terminal the secondary user is likely to reach for.
    /// </remarks>
    public string ToText()
    {
        StringBuilder text = new();

        Append(Columns);
        foreach (string[] row in _rows)
        {
            Append(row);
        }

        return text.ToString();

        void Append(IReadOnlyList<string> fields)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(Escape(fields[i]));
            }

            text.Append("\r\n");
        }
    }

    private static string Escape(string field)
    {
        if (field.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            return field;
        }

        return string.Concat("\"", field.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }
}
