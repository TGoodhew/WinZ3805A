using System.Globalization;
using System.Text;

namespace WinZ3805A.Services;

/// <summary>
/// Appends lines to a size-capped file, rolling older ones aside and pruning the oldest.
/// </summary>
/// <remarks>
/// <para>
/// The file half of #127, kept apart from the <c>ILogger</c> plumbing so the part with rules —
/// when a file rolls, what survives, and what happens when the disk says no — is testable without
/// a logger factory.
/// </para>
/// <para>
/// <b>It never throws.</b> Every public member swallows storage faults, which is the opposite of
/// this codebase's usual instinct and is deliberate: this type exists to record what went wrong,
/// and a logger that propagates a full disk into the serial loop would take the application down at
/// exactly the moment its output was worth having. A lost log line is a lost log line; a lost
/// session is a bug report nobody can file.
/// </para>
/// <para>
/// Rotation is by size rather than by day. §1's usage pattern is a receiver left running for weeks,
/// so a per-day file at <c>Debug</c> has no upper bound at all, and "never grows without bound" is
/// the requirement that actually matters on a machine nobody is watching.
/// </para>
/// </remarks>
public sealed class FileLogWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly int _keep;

    private StreamWriter? _writer;
    private long _written;
    private bool _disposed;

    /// <summary>Creates a writer over a file, creating its folder if it is missing.</summary>
    /// <param name="path">The current log file. Rolled files sit beside it.</param>
    /// <param name="maximumBytes">Roll once the file passes this size.</param>
    /// <param name="keep">How many rolled files to keep, beyond the current one.</param>
    public FileLogWriter(string path, long maximumBytes = 1024 * 1024, int keep = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(keep);

        _path = path;
        _maximumBytes = maximumBytes;
        _keep = keep;
    }

    /// <summary>Where the current file lives, so the user can be shown it.</summary>
    public string Path => _path;

    /// <summary>Appends one line, rolling first if the file has grown past its cap.</summary>
    public void Write(string line)
    {
        if (line is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Open();

                if (_writer is null)
                {
                    return;
                }

                // Rolled on the way in rather than after writing, so the cap is a ceiling on the
                // file rather than a ceiling plus one line of overshoot.
                if (_written >= _maximumBytes)
                {
                    Roll();
                    Open();
                }

                _writer?.WriteLine(line);
                _written += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            }
            catch (Exception exception) when (IsStorageFault(exception))
            {
                // Give up on the file rather than on the process. A later call may find the disk
                // writable again, and Open() will start a fresh handle.
                Close();
            }
        }
    }

    /// <summary>Flushes what has been written so far.</summary>
    /// <remarks>
    /// Worth calling before the application closes a window or shows the user the file. A crash
    /// that takes the buffer with it loses the lines that explain the crash.
    /// </remarks>
    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception exception) when (IsStorageFault(exception))
            {
                Close();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Close();
        }
    }

    private void Open()
    {
        if (_writer is not null)
        {
            return;
        }

        string? folder = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        FileStream stream = new(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _written = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <remarks>
    /// <c>AutoFlush</c> is on, which costs a syscall per line and buys the property that matters
    /// here: the log of an application that vanished is only useful if it was on disk before it
    /// vanished. This project has lost three processes to uncatchable failures, and a buffered
    /// logger would have had nothing to say about any of them.
    /// </remarks>
    private void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            // Nothing further to try.
        }

        _writer = null;
        _written = 0;
    }

    /// <summary>Shuffles the numbered files up and drops the oldest.</summary>
    private void Roll()
    {
        Close();

        // Deleted first, or the rename onto it fails on Windows.
        Delete(Numbered(_keep));

        for (int index = _keep - 1; index >= 1; index--)
        {
            Move(Numbered(index), Numbered(index + 1));
        }

        if (_keep >= 1)
        {
            Move(_path, Numbered(1));
        }
        else
        {
            Delete(_path);
        }
    }

    private string Numbered(int index) => $"{_path}.{index.ToString(CultureInfo.InvariantCulture)}";

    private static void Move(string from, string to)
    {
        try
        {
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            // A file someone has open in an editor stays where it is; the next roll tries again.
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            // Same.
        }
    }

    private static bool IsStorageFault(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        NotSupportedException or
        ObjectDisposedException;
}
