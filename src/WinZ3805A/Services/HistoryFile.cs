using System.Globalization;

using Microsoft.Data.Sqlite;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>What an exported history says about itself (#551).</summary>
/// <param name="FormatVersion">The export format; see <see cref="HistoryFile.FormatVersion"/>.</param>
/// <param name="AppVersion">The package version that exported it, if recorded.</param>
/// <param name="ExportedAt">When it was exported, if recorded.</param>
/// <param name="ReceiverIdentity">
/// The connected receiver's <c>*IDN?</c> answer at export, or <see langword="null"/> if none was
/// connected. Trend samples carry no identity of their own, so this is the only record of which
/// receiver they came from.
/// </param>
/// <param name="Rows">How many samples the file holds.</param>
/// <param name="FirstTicks">The oldest sample, in UTC ticks, or <see langword="null"/> when empty.</param>
/// <param name="LastTicks">The newest sample, in UTC ticks, or <see langword="null"/> when empty.</param>
public sealed record HistoryManifest(
    int FormatVersion,
    string? AppVersion,
    DateTimeOffset? ExportedAt,
    string? ReceiverIdentity,
    long Rows,
    long? FirstTicks,
    long? LastTicks);

/// <summary>What an import did (#551).</summary>
/// <param name="Added">Samples that were not here before.</param>
/// <param name="AlreadyPresent">Samples skipped because this history already had them.</param>
public sealed record HistoryImportResult(long Added, long AlreadyPresent);

/// <summary>Whether an exported history came from the receiver connected now (#551).</summary>
public enum ReceiverMatch
{
    /// <summary>The same model and serial number.</summary>
    Same,

    /// <summary>A different receiver.</summary>
    Different,

    /// <summary>One side or both is not known: nothing was connected, or the file does not say.</summary>
    Unknown,
}

/// <summary>What a file offered for import turned out to be, before anything touches the history.</summary>
/// <param name="Problem">Why it cannot be imported, or <see langword="null"/> if it can.</param>
/// <param name="Manifest">Its manifest, or <see langword="null"/> for a <c>trend.db</c> copied by hand.</param>
/// <param name="Rows">How many samples it holds.</param>
/// <param name="FirstTicks">Its oldest sample, in UTC ticks.</param>
/// <param name="LastTicks">Its newest sample, in UTC ticks.</param>
/// <param name="UnknownColumns">Columns from a newer version, which the import will leave out.</param>
/// <param name="RowsOutsideRetention">Samples too old to keep, which the import will skip.</param>
public sealed record HistoryInspection(
    string? Problem,
    HistoryManifest? Manifest,
    long Rows,
    long? FirstTicks,
    long? LastTicks,
    IReadOnlyList<string> UnknownColumns,
    long RowsOutsideRetention)
{
    /// <summary>Whether the file can be imported at all.</summary>
    public bool IsValid => Problem is null;

    /// <summary>The samples recent enough to keep: at most this many are added.</summary>
    public long RowsInRetention => Rows - RowsOutsideRetention;

    /// <summary>A file that cannot be imported, and why.</summary>
    public static HistoryInspection Refused(string problem) =>
        new(problem, null, 0, null, null, [], 0);
}

/// <summary>
/// Reads and writes exported history files, so replacing the package does not lose the history
/// (#551).
/// </summary>
/// <remarks>
/// <para>
/// <b>An export is a SQLite database</b>: a consistent copy of <c>trend.db</c> made by
/// <see cref="TrendStore.ExportTo"/>, with a small <c>history_manifest</c> table added. One file,
/// openable with any SQLite tool, and checked on import exactly the way the live store would be.
/// A <c>trend.db</c> copied by hand, the workaround before this existed, imports too, as a file
/// with no manifest.
/// </para>
/// <para>
/// <b>The file is checked before the history is touched.</b> <see cref="Inspect"/> works on a
/// private copy, answers every question the user is asked before the import, and never throws: a
/// file it cannot read is a <see cref="HistoryInspection.Problem"/>, in words.
/// </para>
/// </remarks>
public static class HistoryFile
{
    /// <summary>The export format this version writes.</summary>
    /// <remarks>
    /// Raise it only for a change a reader must know about. A new column is not one:
    /// <see cref="Inspect"/> names columns it does not know and the import leaves them out.
    /// </remarks>
    public const int FormatVersion = 1;

    /// <summary>The extension offered by the save and open pickers.</summary>
    public const string Extension = ".sqlite";

    private const string ManifestTable = "history_manifest";

    /// <summary>
    /// Adds the manifest to a copy <see cref="TrendStore.ExportTo"/> has just written, and turns it
    /// into a plain single-file database.
    /// </summary>
    internal static HistoryManifest WriteManifest(
        string path,
        string? receiverIdentity,
        string appVersion,
        DateTimeOffset exportedAt)
    {
        using SqliteConnection connection = Open(path);

        // The copy inherits WAL mode from trend.db. A single file that is going to be moved,
        // mailed or synced should not grow -wal and -shm companions the moment someone opens it.
        Execute(connection, "PRAGMA journal_mode=DELETE;");
        Execute(connection, $"CREATE TABLE {ManifestTable} (key TEXT PRIMARY KEY, value TEXT NULL);");

        (long rows, long? first, long? last) = Span(connection);

        HistoryManifest manifest = new(
            FormatVersion, appVersion, exportedAt, receiverIdentity, rows, first, last);

        Dictionary<string, string?> values = new()
        {
            ["format_version"] = FormatVersion.ToString(CultureInfo.InvariantCulture),
            ["app_version"] = appVersion,
            ["exported_at"] = exportedAt.ToString("O", CultureInfo.InvariantCulture),
            ["receiver_identity"] = receiverIdentity,
            ["rows"] = rows.ToString(CultureInfo.InvariantCulture),
            ["first_ticks"] = first?.ToString(CultureInfo.InvariantCulture),
            ["last_ticks"] = last?.ToString(CultureInfo.InvariantCulture),
        };

        foreach ((string key, string? value) in values)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = $"INSERT INTO {ManifestTable} (key, value) VALUES ($key, $value);";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        return manifest;
    }

    /// <summary>Reads what a file offered for import is, without changing anything it matters to.</summary>
    /// <param name="path">
    /// The application's own copy of the chosen file. Opened read-write because a file in WAL mode
    /// cannot be read without its shared-memory companion, which may have to be created - so it must
    /// never be the user's original.
    /// </param>
    /// <param name="cutoffTicks">The oldest sample the history keeps, in UTC ticks.</param>
    public static HistoryInspection Inspect(string path, long cutoffTicks)
    {
        if (!File.Exists(path))
        {
            return HistoryInspection.Refused("The file could not be found.");
        }

        try
        {
            using SqliteConnection connection = Open(path);

            using (SqliteCommand check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA integrity_check;";
                if (check.ExecuteScalar() is not string verdict || !string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return HistoryInspection.Refused(
                        "The file is damaged: SQLite's integrity check did not pass, so none of it can be trusted.");
                }
            }

            List<string> columns = ColumnsOf(connection, "sample");
            if (!columns.Contains("ticks", StringComparer.OrdinalIgnoreCase))
            {
                return HistoryInspection.Refused(
                    "The file is a database, but it has no receiver history in it.");
            }

            List<string> unknown = [.. columns.Where(c => !TrendStore.Columns.Contains(c, StringComparer.OrdinalIgnoreCase))];

            (long rows, long? first, long? last) = Span(connection);

            using SqliteCommand old = connection.CreateCommand();
            old.CommandText = "SELECT COUNT(*) FROM sample WHERE ticks < $cutoff;";
            old.Parameters.AddWithValue("$cutoff", cutoffTicks);
            long outside = (long)(old.ExecuteScalar() ?? 0L);

            return new HistoryInspection(null, ReadManifest(connection, rows, first, last), rows, first, last, unknown, outside);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return HistoryInspection.Refused(
                $"The file is not a history this application can read. {exception.Message}");
        }
    }

    /// <summary>Whether a history came from the receiver connected now.</summary>
    /// <param name="exported">The identity recorded at export.</param>
    /// <param name="connected">The identity of the receiver connected now.</param>
    /// <remarks>
    /// By model and serial number, not the whole <c>*IDN?</c> answer: a firmware update changes the
    /// fourth field and not the receiver. An answer that is not the standard four fields - a talker's,
    /// say - is compared whole.
    /// </remarks>
    public static ReceiverMatch Compare(string? exported, string? connected)
    {
        if (string.IsNullOrWhiteSpace(exported) || string.IsNullOrWhiteSpace(connected))
        {
            return ReceiverMatch.Unknown;
        }

        if (DeviceIdentity.Parse(exported) is DeviceIdentity was && DeviceIdentity.Parse(connected) is DeviceIdentity now)
        {
            return string.Equals(was.Model.Trim(), now.Model.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(was.SerialNumber.Trim(), now.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? ReceiverMatch.Same
                    : ReceiverMatch.Different;
        }

        return string.Equals(exported.Trim(), connected.Trim(), StringComparison.OrdinalIgnoreCase)
            ? ReceiverMatch.Same
            : ReceiverMatch.Different;
    }

    private static HistoryManifest? ReadManifest(SqliteConnection connection, long rows, long? first, long? last)
    {
        if (ColumnsOf(connection, ManifestTable).Count == 0)
        {
            return null;
        }

        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

        using (SqliteCommand read = connection.CreateCommand())
        {
            read.CommandText = $"SELECT key, value FROM {ManifestTable};";
            using SqliteDataReader reader = read.ExecuteReader();

            while (reader.Read())
            {
                values[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        int format = int.TryParse(values.GetValueOrDefault("format_version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) ? f : 0;
        DateTimeOffset? exportedAt = DateTimeOffset.TryParse(
            values.GetValueOrDefault("exported_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at)
            ? at
            : null;

        // The counts are measured rather than read back, so a manifest can never contradict the rows.
        return new HistoryManifest(
            format, values.GetValueOrDefault("app_version"), exportedAt, values.GetValueOrDefault("receiver_identity"), rows, first, last);
    }

    private static (long Rows, long? First, long? Last) Span(SqliteConnection connection)
    {
        using SqliteCommand span = connection.CreateCommand();
        span.CommandText = "SELECT COUNT(*), MIN(ticks), MAX(ticks) FROM sample;";
        using SqliteDataReader reader = span.ExecuteReader();
        reader.Read();

        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static List<string> ColumnsOf(SqliteConnection connection, string table)
    {
        using SqliteCommand columns = connection.CreateCommand();
        columns.CommandText = "SELECT name FROM pragma_table_info($table);";
        columns.Parameters.AddWithValue("$table", table);
        using SqliteDataReader reader = columns.ExecuteReader();

        List<string> names = [];
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>Opens a file this class owns: never pooled, so it can be moved or deleted the moment it is closed.</summary>
    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());

        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// The words an export and an import say (#551), kept apart from the page so every case can be
/// read in a test.
/// </summary>
/// <remarks>
/// <para>
/// <b>The confirmation is the whole safeguard</b>, because the import cannot be undone:
/// <c>trend.db</c> keeps no record of which receiver a sample came from, so two receivers'
/// histories merged together stay merged. Everything the user needs to decide is said before the
/// Import button, in the order they would ask it: what is in the file, what will not come across,
/// and whose it is.
/// </para>
/// <para>
/// Invariant culture, like the rest of the application's English text: the dates are written
/// out in words so that no reader has to guess which way round 03/09 is.
/// </para>
/// </remarks>
public static class HistoryText
{
    /// <summary>What the import will do, for the confirmation shown before it.</summary>
    /// <param name="inspection">The file, as <see cref="HistoryFile.Inspect"/> found it.</param>
    /// <param name="connected">The identity of the receiver connected now, if any.</param>
    /// <param name="retention">How long this history keeps samples.</param>
    /// <param name="zone">The time zone the dates are shown in.</param>
    public static string Describe(
        HistoryInspection inspection,
        string? connected,
        TimeSpan retention,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(zone);

        List<string> paragraphs = [];

        string held = $"This file holds {Readings(inspection.Rows)}{Span(inspection.FirstTicks, inspection.LastTicks, zone)}.";
        if (inspection.Manifest is { AppVersion: string version, ExportedAt: DateTimeOffset at })
        {
            held += $" It was exported by version {version} on {Date(at.UtcTicks, zone)}.";
        }

        paragraphs.Add(held);

        if (inspection.RowsOutsideRetention > 0)
        {
            paragraphs.Add(
                $"{Count(inspection.RowsOutsideRetention)} of them {(inspection.RowsOutsideRetention == 1 ? "is" : "are")} older than the "
                + $"{retention.TotalDays:0} days kept here, and won't be imported.");
        }

        if (inspection.UnknownColumns.Count > 0)
        {
            paragraphs.Add(
                "It was exported by a newer version of the application, which kept readings this version "
                + $"doesn't understand. These will be left out: {string.Join(", ", inspection.UnknownColumns)}. "
                + "To keep them, update the application first and import again.");
        }

        paragraphs.Add(Receiver(inspection.Manifest?.ReceiverIdentity, connected));

        paragraphs.Add("Readings already here are kept exactly as they are; only the missing ones are added.");

        return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
    }

    /// <summary>What an import did, for the page afterwards.</summary>
    public static string Imported(HistoryImportResult result, string fileName)
    {
        ArgumentNullException.ThrowIfNull(result);

        string added = $"Imported {Readings(result.Added)} from {fileName}.";

        return result.AlreadyPresent == 0
            ? added
            : $"{added} {Count(result.AlreadyPresent)} {(result.AlreadyPresent == 1 ? "was" : "were")} already here.";
    }

    /// <summary>What an export wrote, for the page afterwards.</summary>
    public static string Exported(HistoryManifest manifest, string fileName, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return manifest.Rows == 0
            ? $"Exported to {fileName}. There were no readings yet."
            : $"Exported {Readings(manifest.Rows)}{Span(manifest.FirstTicks, manifest.LastTicks, zone)} to {fileName}.";
    }

    /// <summary>A receiver as a person reads it: model and serial number, or the raw answer.</summary>
    public static string Name(string identity) =>
        DeviceIdentity.Parse(identity) is DeviceIdentity parsed
            ? $"{parsed.Model.Trim()} serial {parsed.SerialNumber.Trim()}"
            : identity.Trim();

    private static string Receiver(string? exported, string? connected)
    {
        const string CannotSeparate =
            "Once imported, the two histories can't be separated: their readings share the same charts.";

        return HistoryFile.Compare(exported, connected) switch
        {
            ReceiverMatch.Same => $"It came from the receiver connected now, {Name(connected!)}.",
            ReceiverMatch.Different =>
                $"It came from a different receiver, {Name(exported!)}, and the one connected now is "
                + $"{Name(connected!)}. {CannotSeparate}",
            _ => (exported, connected) switch
            {
                (null or "", null or "") =>
                    "It doesn't say which receiver it came from, and no receiver is connected now to compare it with. "
                    + "If it came from a different receiver, the two histories can't be separated afterwards.",
                (null or "", _) =>
                    $"It doesn't say which receiver it came from. If that wasn't {Name(connected!)}, the one connected "
                    + "now, the two histories can't be separated afterwards.",
                _ =>
                    $"It came from {Name(exported!)}, and no receiver is connected now to compare it with. If it isn't "
                    + "the receiver this history belongs to, the two can't be separated afterwards.",
            },
        };
    }

    private static string Span(long? first, long? last, TimeZoneInfo zone) =>
        first is long from && last is long to
            ? Date(from, zone) == Date(to, zone)
                ? $" from {Date(from, zone)}"
                : $" from {Date(from, zone)} to {Date(to, zone)}"
            : string.Empty;

    private static string Date(long utcTicks, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(utcTicks, TimeSpan.Zero), zone)
            .ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Readings(long value) => $"{Count(value)} {(value == 1 ? "reading" : "readings")}";
}
