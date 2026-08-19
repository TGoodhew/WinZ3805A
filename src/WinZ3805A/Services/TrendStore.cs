using Microsoft.Data.Sqlite;

using WinZ3805A.Controls;

namespace WinZ3805A.Services;

/// <summary>One persisted trend sample: everything the fast tier reads, kept.</summary>
/// <param name="Ticks">UTC ticks, and the primary key — one sample per instant.</param>
/// <param name="Efc">Relative oscillator control, per cent, or <see langword="null"/> if unread.</param>
/// <param name="TimeIntervalNanoseconds">1 PPS time interval, or <see langword="null"/>.</param>
/// <param name="SyncState">The receiver's own mode keyword, or <see langword="null"/>.</param>
/// <param name="TrackedCount">Satellites tracked, or <see langword="null"/>.</param>
public readonly record struct TrendRecord(
    long Ticks,
    double? Efc,
    double? TimeIntervalNanoseconds,
    string? SyncState,
    int? TrackedCount);

/// <summary>
/// The durable trend history behind P1-2 (#50), and the series #49 and #137 read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only.</b> These rows are a record of what the instrument did, and nothing rewrites
/// history. Compaction reduces resolution and prunes age; it never edits a value.
/// </para>
/// <para>
/// <b>Weeks, not the seven days §12's ring buffer holds.</b> That buffer is the in-memory window
/// the medallion and the chart draw from; this is the file behind it. #137 exists to measure an
/// oscillator walking toward its tuning limit at a slope of a per-cent or so a day, and days of
/// data cannot establish that — which is the whole reason the retention here is longer than the
/// range selector's longest setting.
/// </para>
/// <para>
/// <b>It never throws at the caller.</b> Appending happens on the poll loop, and §7.3's cadence
/// must not be at the mercy of a locked file or a full disk — the same rule
/// <see cref="FileLogWriter"/> follows and for the same reason. A dropped sample is a gap in a
/// trend; a propagated exception is a receiver that stops being polled.
/// </para>
/// </remarks>
public sealed class TrendStore : IDisposable
{
    /// <summary>Beyond this age, samples are thinned to <see cref="CoarseInterval"/> (§12).</summary>
    public static readonly TimeSpan FullResolutionWindow = TimeSpan.FromHours(24);

    /// <summary>The resolution kept beyond <see cref="FullResolutionWindow"/> (§12).</summary>
    public static readonly TimeSpan CoarseInterval = TimeSpan.FromSeconds(10);

    private readonly SqliteConnection _connection;
    private readonly TimeSpan _retention;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Opens or creates the store.</summary>
    /// <param name="path">The database file. <c>:memory:</c> is accepted, which the tests use.</param>
    /// <param name="retention">
    /// How far back to keep anything at all. Defaults to eight weeks — long enough for #137's
    /// drift slope to mean something, and still only a few megabytes once compaction has run.
    /// </param>
    public TrendStore(string path, TimeSpan? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _retention = retention ?? TimeSpan.FromDays(56);

        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();

        // WAL so a reader charting the series does not block the poll loop appending to it, and
        // NORMAL so a 1 Hz append is not a 1 Hz fsync. The cost is losing the last few samples to
        // a power cut, which for a trend is a rounding error.
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");

        // Ticks is the primary key rather than a surrogate: it makes the series ordered by
        // definition, makes a repeated append idempotent rather than a duplicate row, and is what
        // range queries seek on.
        Execute("""
            CREATE TABLE IF NOT EXISTS sample (
                ticks   INTEGER PRIMARY KEY,
                efc     REAL    NULL,
                tint    REAL    NULL,
                sync    TEXT    NULL,
                tracked INTEGER NULL
            );
            """);
    }

    /// <summary>Where the file lives by default, beside the other stores.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinZ3805A",
        "trend.db");

    /// <summary>Appends one sample, or replaces the one already at that instant.</summary>
    /// <returns><see langword="false"/> if it could not be stored, which the caller may ignore.</returns>
    public bool Append(TrendRecord record)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO sample (ticks, efc, tint, sync, tracked)
                    VALUES ($ticks, $efc, $tint, $sync, $tracked)
                    ON CONFLICT(ticks) DO UPDATE SET
                        efc = excluded.efc, tint = excluded.tint,
                        sync = excluded.sync, tracked = excluded.tracked;
                    """;

                command.Parameters.AddWithValue("$ticks", record.Ticks);
                command.Parameters.AddWithValue("$efc", (object?)record.Efc ?? DBNull.Value);
                command.Parameters.AddWithValue("$tint", (object?)record.TimeIntervalNanoseconds ?? DBNull.Value);
                command.Parameters.AddWithValue("$sync", (object?)record.SyncState ?? DBNull.Value);
                command.Parameters.AddWithValue("$tracked", (object?)record.TrackedCount ?? DBNull.Value);

                command.ExecuteNonQuery();
                return true;
            }
            catch (SqliteException)
            {
                // See the class remarks: a dropped sample is a gap; a thrown one stops the polling.
                return false;
            }
        }
    }

    /// <summary>Reads a window, oldest first.</summary>
    public IReadOnlyList<TrendRecord> Read(long fromTicks, long toTicks)
    {
        lock (_gate)
        {
            if (_disposed || toTicks < fromTicks)
            {
                return [];
            }

            try
            {
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT ticks, efc, tint, sync, tracked FROM sample
                    WHERE ticks >= $from AND ticks <= $to
                    ORDER BY ticks;
                    """;
                command.Parameters.AddWithValue("$from", fromTicks);
                command.Parameters.AddWithValue("$to", toTicks);

                List<TrendRecord> records = [];
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new TrendRecord(
                        reader.GetInt64(0),
                        reader.IsDBNull(1) ? null : reader.GetDouble(1),
                        reader.IsDBNull(2) ? null : reader.GetDouble(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4)));
                }

                return records;
            }
            catch (SqliteException)
            {
                return [];
            }
        }
    }

    /// <summary>The samples in a window as the chart wants them, already reduced to one field.</summary>
    /// <param name="fromTicks">The left edge of the window, in UTC ticks.</param>
    /// <param name="toTicks">The right edge.</param>
    /// <param name="selector">Which quantity to plot — EFC or time interval.</param>
    /// <remarks>
    /// Rows whose chosen field is null are dropped rather than zero-filled, so a period the
    /// receiver did not answer for stays a gap in the plot. <see cref="TrendDecimation"/> then
    /// omits the column entirely rather than drawing a reading nobody took.
    /// </remarks>
    public IReadOnlyList<TrendSample> ReadSeries(long fromTicks, long toTicks, Func<TrendRecord, double?> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        List<TrendSample> samples = [];
        foreach (TrendRecord record in Read(fromTicks, toTicks))
        {
            if (selector(record) is double value)
            {
                samples.Add(new TrendSample(record.Ticks, value));
            }
        }

        return samples;
    }

    /// <summary>How many samples are stored.</summary>
    public long Count()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            try
            {
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sample;";
                return (long)(command.ExecuteScalar() ?? 0L);
            }
            catch (SqliteException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Thins old samples to <see cref="CoarseInterval"/> and drops anything past the retention.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §12's rule: full resolution for 24 hours, 10 s beyond it. A week at 1 s is 604 800 rows; the
    /// same week compacted is about 138 000, which is what makes multi-week retention affordable.
    /// </para>
    /// <para>
    /// <b>Thinning keeps one real sample per bucket rather than averaging.</b> An averaged sample
    /// is a reading the instrument never produced, and #49's whole decimation argument is that
    /// invented or dropped extremes are how a one-second excursion disappears. The row that
    /// survives is one the receiver actually reported.
    /// </para>
    /// </remarks>
    /// <param name="nowTicks">The current instant, injected so a test can pin it (§12).</param>
    /// <returns>How many rows were removed.</returns>
    public int Compact(long nowTicks)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            try
            {
                long coarseBefore = nowTicks - FullResolutionWindow.Ticks;
                long dropBefore = nowTicks - _retention.Ticks;

                using SqliteTransaction transaction = _connection.BeginTransaction();
                int removed = 0;

                using (SqliteCommand prune = _connection.CreateCommand())
                {
                    prune.Transaction = transaction;
                    prune.CommandText = "DELETE FROM sample WHERE ticks < $before;";
                    prune.Parameters.AddWithValue("$before", dropBefore);
                    removed += prune.ExecuteNonQuery();
                }

                using (SqliteCommand thin = _connection.CreateCommand())
                {
                    thin.Transaction = transaction;

                    // Keep the earliest row in each 10 s bucket and delete the rest. MIN(ticks)
                    // picks a row that exists rather than synthesising one.
                    thin.CommandText = """
                        DELETE FROM sample
                        WHERE ticks < $coarse
                          AND ticks NOT IN (
                              SELECT MIN(ticks) FROM sample
                              WHERE ticks < $coarse
                              GROUP BY ticks / $bucket);
                        """;
                    thin.Parameters.AddWithValue("$coarse", coarseBefore);
                    thin.Parameters.AddWithValue("$bucket", CoarseInterval.Ticks);
                    removed += thin.ExecuteNonQuery();
                }

                transaction.Commit();
                return removed;
            }
            catch (SqliteException)
            {
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
