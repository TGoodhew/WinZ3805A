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
    int? TrackedCount)
{
    /// <summary>
    /// The oscillator's measured frequency offset in parts per billion, or <see langword="null"/>
    /// (#512).
    /// </summary>
    /// <remarks>
    /// <b>A different quantity from <see cref="Efc"/></b>, which is the control voltage. Added as an
    /// init-only property so every existing construction of this record still says what it said, and
    /// a row written before the column existed reads back null rather than a fabricated zero.
    /// </remarks>
    public double? OscillatorOffsetPpb { get; init; }
}

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
/// <summary>What <see cref="TrendStore.OpenChecked"/> did (#601).</summary>
/// <param name="Store">The store, ready to use.</param>
/// <param name="SetAsidePath">Where a damaged file was moved, or null when nothing was.</param>
/// <param name="Problem">Why history is being kept in memory for this run, or null when it is on disk.</param>
public sealed record TrendStoreOpened(TrendStore Store, string? SetAsidePath, string? Problem);

public sealed class TrendStore : IDisposable
{
    /// <summary>Beyond this age, samples are thinned to <see cref="CoarseInterval"/> (§12).</summary>
    public static readonly TimeSpan FullResolutionWindow = TimeSpan.FromHours(24);

    /// <summary>The resolution kept beyond <see cref="FullResolutionWindow"/> (§12).</summary>
    public static readonly TimeSpan CoarseInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The columns of <c>sample</c> this version knows, in order - what an import copies (#551).
    /// </summary>
    /// <remarks>
    /// A column added here must also be added to the <c>CREATE TABLE</c> or
    /// <see cref="AddColumnIfMissing"/> below, and to <see cref="Append"/>. An exported file from a
    /// newer version may carry more; <see cref="HistoryFile.Inspect"/> names those, and
    /// <see cref="Import"/> leaves them out.
    /// </remarks>
    public static readonly IReadOnlyList<string> Columns = ["ticks", "efc", "tint", "sync", "tracked", "osc"];

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

        AddColumnIfMissing("osc", "REAL NULL");
    }

    /// <summary>
    /// Adds a column to <c>sample</c> when an older file does not have it yet (#512).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because <c>CREATE TABLE IF NOT EXISTS</c> is not a migration.</b> An existing trend.db
    /// keeps the shape it was created with, so a new column in the statement above reaches new
    /// installations only — and the first insert naming it fails on every file that already holds
    /// history. A user's weeks of trend are exactly what must not be lost to a schema change.
    /// </para>
    /// <para>
    /// <c>ADD COLUMN</c> with a null default is O(1) in SQLite and rewrites nothing: old rows read
    /// back null, which is the truth about them — that reading was not being taken when they were
    /// written. Failure is swallowed like every other store failure here, because a trend that
    /// cannot widen is still a trend, and the poll loop must not die of it.
    /// </para>
    /// </remarks>
    private void AddColumnIfMissing(string column, string declaration)
    {
        try
        {
            using SqliteCommand existing = _connection.CreateCommand();
            existing.CommandText = "SELECT COUNT(*) FROM pragma_table_info('sample') WHERE name = $name;";
            existing.Parameters.AddWithValue("$name", column);

            if (existing.ExecuteScalar() is long present && present > 0)
            {
                return;
            }

            Execute($"ALTER TABLE sample ADD COLUMN {column} {declaration};");
        }
        catch (SqliteException)
        {
            // An older file that cannot be widened keeps working without the new series.
        }
    }

    /// <summary>
    /// Opens the store at <paramref name="path"/>, first setting aside a file that SQLite cannot vouch
    /// for and starting a fresh one in its place (#601).
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <param name="clock">Dates the set-aside file's name.</param>
    /// <param name="retention">As for the constructor.</param>
    /// <remarks>
    /// <para>
    /// <b>The constructor alone made a damaged file fatal.</b> It opens the file and runs its pragmas
    /// with nothing around them, and it runs as the device is put together at startup - so a
    /// <c>trend.db</c> that was not a database, or failed SQLite's own check, stopped the application
    /// opening at all, for the sake of a chart. The test that shows it is
    /// <c>ADamagedFileStopsTheConstructor</c>.
    /// </para>
    /// <para>
    /// <b>Set aside, never deleted.</b> The file is renamed beside itself with the date, so it can be
    /// offered to <see cref="HistoryFile"/>'s import (#551) - which refuses a file that fails the full
    /// integrity check, so nothing damaged gets merged - or sent in. Its <c>-wal</c> and <c>-shm</c>
    /// files go with it: the store runs in WAL mode, and an old write-ahead log left beside a fresh
    /// database is one SQLite would try to apply to it.
    /// </para>
    /// <para>
    /// <b><c>quick_check</c>, not <c>integrity_check</c>.</b> It skips cross-checking indexes against
    /// their tables, which is the expensive half and the half this one-table, primary-key-only schema
    /// has least of, and it still reads every page - so a file that is not a database, or is
    /// truncated or overwritten, fails it. It runs on every start rather than only after an update:
    /// damage arrives with a power cut as readily as with an upgrade, and the history this keeps is a
    /// few megabytes.
    /// </para>
    /// <para>
    /// If even moving the file fails, the history is kept in memory for this run and the problem is
    /// reported, so the application still opens.
    /// </para>
    /// </remarks>
    public static TrendStoreOpened OpenChecked(string path, TimeProvider clock, TimeSpan? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(clock);

        string? setAside = null;
        try
        {
            if (File.Exists(path) && !PassesQuickCheck(path))
            {
                setAside = SetAside(path, clock);
            }

            return new TrendStoreOpened(new TrendStore(path, retention), setAside, null);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Opened as a file and failed on the store's own statements, or could not be moved:
            // set aside if not yet done, and otherwise keep this run's history in memory.
            try
            {
                if (setAside is null && File.Exists(path))
                {
                    setAside = SetAside(path, clock);
                    return new TrendStoreOpened(new TrendStore(path, retention), setAside, null);
                }
            }
            catch (Exception inner) when (inner is SqliteException or IOException or UnauthorizedAccessException)
            {
                exception = inner;
            }

            return new TrendStoreOpened(new TrendStore(":memory:", retention), setAside, exception.Message);
        }
    }

    /// <summary>Whether SQLite's quick check passes on the file at <paramref name="path"/>.</summary>
    private static bool PassesQuickCheck(string path)
    {
        try
        {
            // IMMUTABLE, so checking changes nothing - and that was found the hard way. Opened read-write,
            // SQLite took the damaged file's -wal as its own to recover and was gone with it before
            // the set-aside could move it, so the check destroyed the newest history exactly when it
            // was the part worth keeping. An immutable open reads the main file and touches no
            // companion. Unpooled, so the file is closed - and can be moved - the moment this returns.
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1",
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());

            connection.Open();

            using SqliteCommand check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            return check.ExecuteScalar() is string verdict && string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Renames the file, and its write-ahead log and shared memory, aside with the date.</summary>
    private static string SetAside(string path, TimeProvider clock)
    {
        SqliteConnection.ClearAllPools();

        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        string stamp = clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(path)}.damaged-{stamp}{Path.GetExtension(path)}");

        File.Move(path, target);
        foreach (string companion in new[] { "-wal", "-shm" })
        {
            if (File.Exists(path + companion))
            {
                File.Move(path + companion, target + companion);
            }
        }

        return target;
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
                    INSERT INTO sample (ticks, efc, tint, sync, tracked, osc)
                    VALUES ($ticks, $efc, $tint, $sync, $tracked, $osc)
                    ON CONFLICT(ticks) DO UPDATE SET
                        efc = excluded.efc, tint = excluded.tint,
                        sync = excluded.sync, tracked = excluded.tracked,
                        osc = excluded.osc;
                    """;

                command.Parameters.AddWithValue("$ticks", record.Ticks);
                command.Parameters.AddWithValue("$efc", (object?)record.Efc ?? DBNull.Value);
                command.Parameters.AddWithValue("$tint", (object?)record.TimeIntervalNanoseconds ?? DBNull.Value);
                command.Parameters.AddWithValue("$sync", (object?)record.SyncState ?? DBNull.Value);
                command.Parameters.AddWithValue("$tracked", (object?)record.TrackedCount ?? DBNull.Value);
                command.Parameters.AddWithValue("$osc", (object?)record.OscillatorOffsetPpb ?? DBNull.Value);

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
                    SELECT ticks, efc, tint, sync, tracked, osc FROM sample
                    WHERE ticks >= $from AND ticks <= $to
                    ORDER BY ticks;
                    """;
                command.Parameters.AddWithValue("$from", fromTicks);
                command.Parameters.AddWithValue("$to", toTicks);

                // Sized before it is filled (#390). A List that grows from empty doubles as it
                // goes, and every doubling past a few thousand records allocates an array over
                // 85 KB - which lands on the large object heap, and the LOH is not compacted. A
                // 24 h window is about 86,000 records: seventeen reallocations, roughly twice the
                // final size allocated in total, and sixteen dead arrays left fragmenting the heap
                // for each read. At the rate this was being called, that was 1.1 GB of LOH and
                // 36 MB/s of allocation on an idle instrument (#385).
                //
                // Counted rather than estimated from the range, because the store's cadence is NOT
                // uniform: it follows the poll schedule and drops to a coarse tier past 24 h, so
                // arithmetic over the window would be wrong exactly when it mattered - after a
                // reconnect, or across the tier boundary. The count is a B-tree range walk on the
                // primary key, inside the lock this method already holds, so no writer can move it
                // between the two queries.
                List<TrendRecord> records = new(CountBetween(fromTicks, toTicks));
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new TrendRecord(
                        reader.GetInt64(0),
                        reader.IsDBNull(1) ? null : reader.GetDouble(1),
                        reader.IsDBNull(2) ? null : reader.GetDouble(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4))
                    {
                        // Null on any row written before the column existed, which is what was true
                        // then: this reading was not being taken.
                        OscillatorOffsetPpb = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    });
                }

                return records;
            }
            catch (SqliteException)
            {
                return [];
            }
        }
    }

    /// <summary>
    /// How many samples lie in a window. Assumes <see cref="_gate"/> is already held (#390).
    /// </summary>
    /// <remarks>
    /// Private and lock-free by contract rather than by locking again: the public
    /// <see cref="Count"/> takes the gate and counts the whole table, and calling it from inside
    /// <see cref="Read"/> would both re-enter and answer a different question. A failure here
    /// returns zero, which costs a capacity hint and nothing else - the read that follows still
    /// works, it just grows the way it used to.
    /// </remarks>
    private int CountBetween(long fromTicks, long toTicks)
    {
        try
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sample WHERE ticks >= $from AND ticks <= $to;";
            command.Parameters.AddWithValue("$from", fromTicks);
            command.Parameters.AddWithValue("$to", toTicks);

            long count = (long)(command.ExecuteScalar() ?? 0L);
            return count is > 0 and <= int.MaxValue ? (int)count : 0;
        }
        catch (SqliteException)
        {
            return 0;
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

        IReadOnlyList<TrendRecord> window = Read(fromTicks, toTicks);

        // Sized from the window rather than grown from empty (#390). The count is an upper bound
        // rather than the answer - rows whose chosen field is null are dropped below - so this
        // trades a little slack for none of the doubling series.
        List<TrendSample> samples = new(window.Count);
        foreach (TrendRecord record in window)
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

    /// <summary>How far back anything is kept; older samples are pruned by <see cref="Compact"/>.</summary>
    public TimeSpan Retention => _retention;

    /// <summary>
    /// Writes a consistent copy of the whole history to <paramref name="path"/>, with a manifest
    /// (#551).
    /// </summary>
    /// <param name="path">A file that must not exist yet; SQLite's <c>VACUUM INTO</c> refuses to overwrite.</param>
    /// <param name="receiverIdentity">The connected receiver's <c>*IDN?</c> answer, if any.</param>
    /// <param name="appVersion">The package version doing the export.</param>
    /// <param name="exportedAt">When, for the manifest.</param>
    /// <returns>What was written.</returns>
    /// <remarks>
    /// <para>
    /// <b>Not a file copy.</b> In WAL mode the most recent samples can still be in
    /// <c>trend.db-wal</c>, and the file is written every second, so copying <c>trend.db</c> would
    /// miss the newest rows or catch a page mid-write. <c>VACUUM INTO</c> runs on this store's own
    /// connection, under its own lock, and writes a single self-contained file. The poll loop waits
    /// for the copy - a few megabytes, well under a second - rather than being interleaved with it.
    /// </para>
    /// <para>
    /// <b>It throws</b>, unlike the rest of this class. Every other member runs on the poll loop,
    /// where a failure must be a gap and not an exception. This one runs because a person asked for
    /// it, and they are owed the reason it failed: it is their only copy of data that cannot be
    /// gathered again.
    /// </para>
    /// </remarks>
    public HistoryManifest ExportTo(string path, string? receiverIdentity, string appVersion, DateTimeOffset exportedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(appVersion);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using SqliteCommand copy = _connection.CreateCommand();
            copy.CommandText = "VACUUM INTO $path;";
            copy.Parameters.AddWithValue("$path", path);
            copy.ExecuteNonQuery();
        }

        return HistoryFile.WriteManifest(path, receiverIdentity, appVersion, exportedAt);
    }

    /// <summary>
    /// Merges an exported history into this one (#551), and returns how many samples were added.
    /// </summary>
    /// <param name="path">A file <see cref="HistoryFile.Inspect"/> has already passed.</param>
    /// <param name="nowTicks">Now, in UTC ticks; samples older than <see cref="Retention"/> are skipped.</param>
    /// <remarks>
    /// <para>
    /// <b>Merged, never replaced.</b> <c>ticks</c> is the primary key, so <c>INSERT OR IGNORE</c>
    /// adds what is missing and leaves every existing sample exactly as it was: importing the same
    /// file twice changes nothing, and nothing recorded since this installation started is lost.
    /// That keeps the store's first rule, that nothing rewrites history.
    /// </para>
    /// <para>
    /// <b>One transaction.</b> A file that fails halfway leaves the store as it was, not half-merged.
    /// Columns this version does not know are left out, which the caller has already told the user;
    /// columns the file lacks (it predates them) read back null, as they do for old rows here.
    /// Samples older than the retention window are not copied, because the next compaction would
    /// delete them anyway, and the user has been told that too.
    /// </para>
    /// <para>
    /// Throws, for the reason <see cref="ExportTo"/> gives.
    /// </para>
    /// </remarks>
    public HistoryImportResult Import(string path, long nowTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        long cutoff = nowTicks - _retention.Ticks;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using (SqliteCommand attach = _connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS history;";
                attach.Parameters.AddWithValue("$path", path);
                attach.ExecuteNonQuery();
            }

            try
            {
                List<string> shared = [];
                using (SqliteCommand columns = _connection.CreateCommand())
                {
                    columns.CommandText = "SELECT name FROM pragma_table_info('sample', 'history');";
                    using SqliteDataReader reader = columns.ExecuteReader();
                    HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);

                    while (reader.Read())
                    {
                        present.Add(reader.GetString(0));
                    }

                    shared.AddRange(Columns.Where(present.Contains));
                }

                string list = string.Join(", ", shared);

                using SqliteTransaction transaction = _connection.BeginTransaction();

                using SqliteCommand eligible = _connection.CreateCommand();
                eligible.Transaction = transaction;
                eligible.CommandText = "SELECT COUNT(*) FROM history.sample WHERE ticks >= $cutoff;";
                eligible.Parameters.AddWithValue("$cutoff", cutoff);
                long candidates = (long)(eligible.ExecuteScalar() ?? 0L);

                using SqliteCommand merge = _connection.CreateCommand();
                merge.Transaction = transaction;
                merge.CommandText =
                    $"INSERT OR IGNORE INTO main.sample ({list}) SELECT {list} FROM history.sample WHERE ticks >= $cutoff;";
                merge.Parameters.AddWithValue("$cutoff", cutoff);
                int added = merge.ExecuteNonQuery();

                transaction.Commit();

                return new HistoryImportResult(added, candidates - added);
            }
            finally
            {
                Execute("DETACH DATABASE history;");
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
