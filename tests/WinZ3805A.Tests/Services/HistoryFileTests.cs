using Microsoft.Data.Sqlite;

using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #551 — the history survives anything that replaces the package, by export and import.
/// </summary>
/// <remarks>
/// Real files throughout, as <c>TrendStoreTests</c> uses: the claims here are about what is on disk
/// after an export and what a store holds after an import, and an in-memory database can make
/// neither. This is the user's only copy of data that cannot be gathered again, so the tests are
/// mostly about what must not happen to it.
/// </remarks>
public sealed class HistoryFileTests : IDisposable
{
    private const string Z3805A = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";

    private static readonly long Now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "wz-history-" + Guid.NewGuid().ToString("n")[..8]);

    public HistoryFileTests() => Directory.CreateDirectory(_folder);

    private string In(string name) => Path.Combine(_folder, name);

    private static long Ago(TimeSpan age) => Now - age.Ticks;

    private static TrendRecord Sample(TimeSpan age, double efc = -16.7) =>
        new(Ago(age), efc, -2.0, "LOCK", 7) { OscillatorOffsetPpb = 0.01 };

    private long Cutoff => Now - TimeSpan.FromDays(56).Ticks;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that outlives the run is not a failing test.
        }
    }

    /// <summary>Fills a store with one sample a minute over the given span, ending now.</summary>
    private static TrendStore Filled(string path, TimeSpan span)
    {
        TrendStore store = new(path);

        for (TimeSpan age = span; age >= TimeSpan.Zero; age -= TimeSpan.FromMinutes(1))
        {
            store.Append(Sample(age, efc: -16.7 - (age.TotalMinutes / 1e6)));
        }

        return store;
    }

    // ------------------------------------------------------------------ the round trip

    /// <summary>
    /// <b>The case #551 exists for</b>: export, uninstall, reinstall, import, and the history is
    /// back exactly as it was, sample for sample.
    /// </summary>
    [Fact]
    public void AnExportImportsBackExactly()
    {
        using TrendStore before = Filled(In("trend.db"), TimeSpan.FromDays(3));
        HistoryManifest manifest = before.ExportTo(In("export.sqlite"), Z3805A, "1.2.0.0", new DateTimeOffset(Now, TimeSpan.Zero));

        using TrendStore after = new(In("new-trend.db"));
        HistoryImportResult result = after.Import(In("export.sqlite"), Now);

        IReadOnlyList<TrendRecord> expected = before.Read(long.MinValue, long.MaxValue);
        Assert.Equal(expected.Count, manifest.Rows);
        Assert.Equal(expected.Count, result.Added);
        Assert.Equal(0, result.AlreadyPresent);
        Assert.Equal(expected, after.Read(long.MinValue, long.MaxValue));
        Assert.Equal(expected[^1].OscillatorOffsetPpb, after.Read(long.MinValue, long.MaxValue)[^1].OscillatorOffsetPpb);
    }

    /// <summary>
    /// The newest samples are still in <c>trend.db-wal</c> while the store is open, which is why
    /// the export is not a file copy. Nothing appended before the export may be missing from it.
    /// </summary>
    [Fact]
    public void TheExportIncludesSamplesNotYetCheckpointed()
    {
        using TrendStore store = new(In("trend.db"));
        store.Append(Sample(TimeSpan.FromSeconds(2)));
        store.Append(Sample(TimeSpan.FromSeconds(1)));

        HistoryManifest manifest = store.ExportTo(In("export.sqlite"), null, "1.2.0.0", DateTimeOffset.UnixEpoch);

        Assert.Equal(2, manifest.Rows);
        Assert.Equal(Ago(TimeSpan.FromSeconds(1)), manifest.LastTicks);
    }

    /// <summary>An export is one file, with no journal companions to lose when it is moved.</summary>
    [Fact]
    public void AnExportIsASingleFile()
    {
        using TrendStore store = Filled(In("trend.db"), TimeSpan.FromHours(1));
        store.ExportTo(In("export.sqlite"), null, "1.2.0.0", DateTimeOffset.UnixEpoch);

        Assert.True(HistoryFile.Inspect(In("export.sqlite"), Cutoff).IsValid);
        SqliteConnection.ClearAllPools();

        Assert.False(File.Exists(In("export.sqlite-wal")));
        Assert.False(File.Exists(In("export.sqlite-shm")));
    }

    [Fact]
    public void TheManifestSaysWhatItHolds()
    {
        using TrendStore store = Filled(In("trend.db"), TimeSpan.FromHours(2));
        DateTimeOffset exportedAt = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        store.ExportTo(In("export.sqlite"), Z3805A, "1.2.0.0", exportedAt);

        HistoryInspection read = HistoryFile.Inspect(In("export.sqlite"), Cutoff);

        Assert.True(read.IsValid);
        Assert.NotNull(read.Manifest);
        Assert.Equal(HistoryFile.FormatVersion, read.Manifest.FormatVersion);
        Assert.Equal("1.2.0.0", read.Manifest.AppVersion);
        Assert.Equal(exportedAt, read.Manifest.ExportedAt);
        Assert.Equal(Z3805A, read.Manifest.ReceiverIdentity);
        Assert.Equal(121, read.Rows);
        Assert.Equal(Ago(TimeSpan.FromHours(2)), read.FirstTicks);
        Assert.Equal(Now, read.LastTicks);
        Assert.Empty(read.UnknownColumns);
        Assert.Equal(0, read.RowsOutsideRetention);
    }

    // ------------------------------------------------------------------ what an import must not do

    /// <summary>
    /// <b>Merged, never replaced</b>: what this installation recorded since it started is kept, and a
    /// sample it already has is not overwritten by the file's copy of the same instant.
    /// </summary>
    [Fact]
    public void AnImportNeverOverwritesOrRemovesWhatIsHere()
    {
        using TrendStore exported = new(In("old.db"));
        exported.Append(Sample(TimeSpan.FromHours(2), efc: -1.0));
        exported.Append(Sample(TimeSpan.FromHours(1), efc: -1.0));
        exported.ExportTo(In("export.sqlite"), null, "1.2.0.0", DateTimeOffset.UnixEpoch);

        using TrendStore here = new(In("trend.db"));
        here.Append(Sample(TimeSpan.FromHours(1), efc: -9.0));
        here.Append(Sample(TimeSpan.FromMinutes(5), efc: -9.0));

        HistoryImportResult result = here.Import(In("export.sqlite"), Now);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.AlreadyPresent);

        IReadOnlyList<TrendRecord> merged = here.Read(long.MinValue, long.MaxValue);
        Assert.Equal([-1.0, -9.0, -9.0], merged.Select(r => r.Efc ?? double.NaN));
    }

    [Fact]
    public void ImportingTheSameFileTwiceChangesNothing()
    {
        using TrendStore exported = Filled(In("old.db"), TimeSpan.FromHours(1));
        exported.ExportTo(In("export.sqlite"), null, "1.2.0.0", DateTimeOffset.UnixEpoch);

        using TrendStore here = new(In("trend.db"));
        here.Import(In("export.sqlite"), Now);
        IReadOnlyList<TrendRecord> once = here.Read(long.MinValue, long.MaxValue);

        HistoryImportResult again = here.Import(In("export.sqlite"), Now);

        Assert.Equal(0, again.Added);
        Assert.Equal(once.Count, again.AlreadyPresent);
        Assert.Equal(once, here.Read(long.MinValue, long.MaxValue));
    }

    /// <summary>
    /// Samples older than the retention window are not imported, since the next compaction would
    /// delete them, and the inspection counts them so the user is told first.
    /// </summary>
    [Fact]
    public void SamplesTooOldToKeepAreCountedAndSkipped()
    {
        using TrendStore exported = new(In("old.db"), TimeSpan.FromDays(365));
        exported.Append(Sample(TimeSpan.FromDays(90)));
        exported.Append(Sample(TimeSpan.FromDays(60)));
        exported.Append(Sample(TimeSpan.FromDays(10)));
        exported.ExportTo(In("export.sqlite"), null, "1.2.0.0", DateTimeOffset.UnixEpoch);

        HistoryInspection inspection = HistoryFile.Inspect(In("export.sqlite"), Cutoff);
        Assert.Equal(3, inspection.Rows);
        Assert.Equal(2, inspection.RowsOutsideRetention);
        Assert.Equal(1, inspection.RowsInRetention);

        using TrendStore here = new(In("trend.db"));
        HistoryImportResult result = here.Import(In("export.sqlite"), Now);

        Assert.Equal(1, result.Added);
        Assert.Equal(Ago(TimeSpan.FromDays(10)), Assert.Single(here.Read(long.MinValue, long.MaxValue)).Ticks);
    }

    // ------------------------------------------------------------------ files from other versions

    /// <summary>A file from before #512's <c>osc</c> column imports, with that reading null.</summary>
    [Fact]
    public void AFileFromBeforeANewColumnImportsWithItNull()
    {
        Build(In("older.sqlite"), "ticks INTEGER PRIMARY KEY, efc REAL, tint REAL, sync TEXT, tracked INTEGER", "(%T, -16.7, -2.0, 'LOCK', 7)");

        HistoryInspection inspection = HistoryFile.Inspect(In("older.sqlite"), Cutoff);
        Assert.True(inspection.IsValid);
        Assert.Empty(inspection.UnknownColumns);

        using TrendStore here = new(In("trend.db"));
        Assert.Equal(1, here.Import(In("older.sqlite"), Now).Added);

        TrendRecord read = Assert.Single(here.Read(long.MinValue, long.MaxValue));
        Assert.Equal(-16.7, read.Efc);
        Assert.Null(read.OscillatorOffsetPpb);
    }

    /// <summary>
    /// A file from a newer version imports what this version understands, and names what it does
    /// not, so the user can decline and update instead (Tony, 29 Sep 2026).
    /// </summary>
    [Fact]
    public void AFileFromANewerVersionNamesTheColumnsLeftOut()
    {
        Build(
            In("newer.sqlite"),
            "ticks INTEGER PRIMARY KEY, efc REAL, tint REAL, sync TEXT, tracked INTEGER, osc REAL, antenna REAL",
            "(%T, -16.7, -2.0, 'LOCK', 7, 0.01, 5.1)");

        HistoryInspection inspection = HistoryFile.Inspect(In("newer.sqlite"), Cutoff);
        Assert.True(inspection.IsValid);
        Assert.Equal(["antenna"], inspection.UnknownColumns);

        using TrendStore here = new(In("trend.db"));
        Assert.Equal(1, here.Import(In("newer.sqlite"), Now).Added);
        Assert.Equal(0.01, Assert.Single(here.Read(long.MinValue, long.MaxValue)).OscillatorOffsetPpb);
    }

    /// <summary>The workaround before #551, a <c>trend.db</c> copied by hand, imports as a file with no manifest.</summary>
    [Fact]
    public void AHandCopiedTrendDbImports()
    {
        using (TrendStore original = Filled(In("trend.db"), TimeSpan.FromMinutes(30)))
        {
        }

        SqliteConnection.ClearAllPools();
        File.Copy(In("trend.db"), In("copied.db"));

        HistoryInspection inspection = HistoryFile.Inspect(In("copied.db"), Cutoff);

        Assert.True(inspection.IsValid);
        Assert.Null(inspection.Manifest);
        Assert.Equal(31, inspection.Rows);
    }

    // ------------------------------------------------------------------ files that are refused

    [Fact]
    public void AFileThatIsNotADatabaseIsRefusedInWords()
    {
        File.WriteAllText(In("notes.sqlite"), "This is not a database, and never was.");

        HistoryInspection inspection = HistoryFile.Inspect(In("notes.sqlite"), Cutoff);

        Assert.False(inspection.IsValid);
        Assert.StartsWith("The file is not a history", inspection.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ADatabaseWithNoHistoryIsRefused()
    {
        using (SqliteConnection other = new($"Data Source={In("other.sqlite")};Pooling=False"))
        {
            other.Open();
            using SqliteCommand create = other.CreateCommand();
            create.CommandText = "CREATE TABLE recipes (name TEXT);";
            create.ExecuteNonQuery();
        }

        HistoryInspection inspection = HistoryFile.Inspect(In("other.sqlite"), Cutoff);

        Assert.False(inspection.IsValid);
        Assert.Contains("no receiver history", inspection.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFileIsRefused() =>
        Assert.False(HistoryFile.Inspect(In("gone.sqlite"), Cutoff).IsValid);

    // ------------------------------------------------------------------ which receiver

    [Theory]
    [InlineData(Z3805A, "SYMMETRICOM,Z3805A,3625A02931,1.01.04-A", ReceiverMatch.Same)]      // firmware updated
    [InlineData(Z3805A, "HEWLETT-PACKARD,Z3805A,3625A02931,1.01.03-A", ReceiverMatch.Same)] // badge, not unit
    [InlineData(Z3805A, "SYMMETRICOM,Z3805A,3625A09999,1.01.03-A", ReceiverMatch.Different)]
    [InlineData(Z3805A, "SYMMETRICOM,Z3801A,3625A02931,1.01.03-A", ReceiverMatch.Different)]
    [InlineData(Z3805A, null, ReceiverMatch.Unknown)]                                        // nothing connected now
    [InlineData(null, Z3805A, ReceiverMatch.Unknown)]                                        // nothing connected at export
    [InlineData("u-blox NEO-M8N", "u-blox NEO-M8N", ReceiverMatch.Same)]                     // not four fields: compared whole
    [InlineData("u-blox NEO-M8N", "VK-162", ReceiverMatch.Different)]
    public void AReceiverIsKnownByModelAndSerialNumber(string? exported, string? connected, ReceiverMatch expected) =>
        Assert.Equal(expected, HistoryFile.Compare(exported, connected));

    /// <summary>Writes a history file by hand, with the given columns and one row at <see cref="Now"/>.</summary>
    private static void Build(string path, string columns, string row)
    {
        using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        connection.Open();

        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = $"CREATE TABLE sample ({columns}); INSERT INTO sample VALUES {row.Replace("%T", Now.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)};";
        create.ExecuteNonQuery();
    }
}
