using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #127's rolling log file.
/// </summary>
/// <remarks>
/// The requirement worth asserting is not that a line lands: it is that the file cannot grow
/// without bound on a machine nobody is watching, and that a disk which refuses a write does not
/// take the application with it. §1's usage pattern is a receiver left running for weeks.
/// </remarks>
public sealed class FileLogWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "wz-logtests-" + Guid.NewGuid().ToString("n")[..8]);

    private string Path0 => Path.Combine(_folder, "app.log");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp folder that outlives the run is not a failing test.
        }
    }

    [Fact]
    public void ItCreatesTheFolderAndWritesALine()
    {
        using (FileLogWriter writer = new(Path0))
        {
            writer.Write("hello");
        }

        Assert.True(File.Exists(Path0));
        Assert.Contains("hello", File.ReadAllText(Path0), StringComparison.Ordinal);
    }

    [Fact]
    public void LinesAppendRatherThanReplacingEachOther()
    {
        using (FileLogWriter writer = new(Path0))
        {
            writer.Write("first");
            writer.Write("second");
        }

        string[] lines = File.ReadAllLines(Path0);

        Assert.Equal(["first", "second"], lines);
    }

    /// <summary>Reopening must continue the file, not truncate a session's history away.</summary>
    [Fact]
    public void AReopenedWriterAppendsToWhatWasThereBefore()
    {
        using (FileLogWriter writer = new(Path0))
        {
            writer.Write("before");
        }

        using (FileLogWriter writer = new(Path0))
        {
            writer.Write("after");
        }

        Assert.Equal(["before", "after"], File.ReadAllLines(Path0));
    }

    [Fact]
    public void ThePrimaryFileStaysUnderItsCap()
    {
        const int cap = 2048;

        using (FileLogWriter writer = new(Path0, maximumBytes: cap, keep: 3))
        {
            for (int i = 0; i < 400; i++)
            {
                writer.Write(new string('x', 100));
            }
        }

        // The cap is a ceiling checked before each write, so the file may exceed it by at most the
        // one line that was in flight when it was reached.
        Assert.InRange(new FileInfo(Path0).Length, 0, cap + 200);
    }

    [Fact]
    public void RollingKeepsOnlyAsManyFilesAsAsked()
    {
        const int keep = 3;

        using (FileLogWriter writer = new(Path0, maximumBytes: 1024, keep: keep))
        {
            for (int i = 0; i < 500; i++)
            {
                writer.Write(new string('y', 100));
            }
        }

        string[] files = Directory.GetFiles(_folder);

        // The current file plus exactly `keep` rolled ones, and nothing beyond.
        Assert.Equal(keep + 1, files.Length);
        Assert.True(File.Exists(Path0));
        Assert.False(File.Exists(Path0 + "." + (keep + 1)));
    }

    /// <summary>
    /// The whole point of a bound. Weeks of running must not fill the disk.
    /// </summary>
    [Fact]
    public void TheTotalOnDiskIsBoundedHoweverMuchIsWritten()
    {
        const int cap = 1024;
        const int keep = 2;

        using (FileLogWriter writer = new(Path0, maximumBytes: cap, keep: keep))
        {
            for (int i = 0; i < 5000; i++)
            {
                writer.Write(new string('z', 100));
            }
        }

        long total = Directory.GetFiles(_folder).Sum(file => new FileInfo(file).Length);

        Assert.InRange(total, 0, ((keep + 1) * cap) + 1000);
    }

    /// <summary>The newest entries stay in the current file, where anyone would look first.</summary>
    [Fact]
    public void TheMostRecentLineIsInThePrimaryFile()
    {
        using (FileLogWriter writer = new(Path0, maximumBytes: 1024, keep: 2))
        {
            for (int i = 0; i < 200; i++)
            {
                writer.Write(new string('a', 100));
            }

            writer.Write("THE-LAST-ONE");
        }

        Assert.Contains("THE-LAST-ONE", File.ReadAllText(Path0), StringComparison.Ordinal);
    }

    /// <summary>
    /// The behaviour this type exists for. A path that cannot be written must be a lost log line,
    /// never a lost session — the serial loop calls this and must not learn about the disk.
    /// </summary>
    [Fact]
    public void AnUnwritablePathIsSwallowedRatherThanThrown()
    {
        // A path under a file rather than a folder: creating the directory fails, and so does
        // every write after it.
        string file = Path.Combine(_folder, "a-file");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(file, "not a folder");

        using FileLogWriter writer = new(Path.Combine(file, "nested", "app.log"));

        Exception? thrown = Record.Exception(() =>
        {
            writer.Write("this cannot land anywhere");
            writer.Flush();
        });

        Assert.Null(thrown);
    }

    [Fact]
    public void WritingAfterDisposeIsIgnoredRatherThanThrowing()
    {
        FileLogWriter writer = new(Path0);
        writer.Write("before");
        writer.Dispose();

        Assert.Null(Record.Exception(() => writer.Write("after")));
        Assert.DoesNotContain("after", File.ReadAllText(Path0), StringComparison.Ordinal);
    }

    [Fact]
    public void ANullLineIsIgnored()
    {
        using FileLogWriter writer = new(Path0);

        Assert.Null(Record.Exception(() => writer.Write(null!)));
    }

    /// <summary>
    /// Lines reach the disk as they are written. A buffered logger has nothing to say about the
    /// process that vanished, which is the failure this project has hit three times.
    /// </summary>
    [Fact]
    public void ALineIsReadableWhileTheWriterIsStillOpen()
    {
        using FileLogWriter writer = new(Path0);
        writer.Write("still open");

        using FileStream stream = new(Path0, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);

        Assert.Contains("still open", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentWritersDoNotInterleaveWithinALine()
    {
        using FileLogWriter writer = new(Path0);

        Parallel.For(0, 200, i => writer.Write(new string('c', 50)));
        writer.Flush();

        // Read through a sharing handle: the writer still holds the file, which File.ReadAllLines
        // will not tolerate. That the writer keeps it open with FileShare.ReadWrite is the point -
        // a user tailing the log must not have to close the application first.
        using FileStream stream = new(Path0, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        string[] lines = reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // Every line is whole: a torn write would show up as a line of the wrong length.
        Assert.Equal(200, lines.Length);
        Assert.All(lines, line => Assert.Equal(50, line.Length));
    }
}
