using System.Globalization;
using System.Text.RegularExpressions;

namespace WinZ3805A.Services;

/// <summary>How an install run that wrote a record ended.</summary>
public enum InstallOutcome
{
    /// <summary>The installer started the application and it got going: it wrote its own log and was
    /// still running fifteen seconds later (#599).</summary>
    Started,

    /// <summary>The installer started the application and it did not get going.</summary>
    DidNotStart,

    /// <summary>The installer finished but did not start the application - most often because .NET
    /// was not installed yet, which the online zip leaves to the person.</summary>
    StartNotChecked,

    /// <summary>The installer stopped on an error, recorded with the reason.</summary>
    Failed,

    /// <summary>The record ends without a verdict: the window was closed part-way, or the machine
    /// went down.</summary>
    Unfinished,
}

/// <summary>What one installer run recorded about itself (#592), read back for Diagnostics (#602).</summary>
/// <param name="FileName">The record's file name, which carries its date and time.</param>
/// <param name="StartedAt">When the run started, from the file name; null if it does not follow the pattern.</param>
/// <param name="Version">The version the run installed, from the package it was given; null if not recorded.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Failure">The installer's own words for the error, when <paramref name="Outcome"/> is <see cref="InstallOutcome.Failed"/>.</param>
public sealed record InstallRecord(
    string FileName,
    DateTime? StartedAt,
    string? Version,
    InstallOutcome Outcome,
    string? Failure);

/// <summary>
/// Finds and reads the installer's records (#592) - the files <c>install.ps1</c> writes to
/// <c>%LOCALAPPDATA%\WinZ3805A Installer\logs</c>, one per run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Readable from inside the package.</b> This application is packaged, and its writes under
/// AppData are redirected into the package; the installer is not, so its records are in the real
/// folder. Reads see both, which was checked rather than assumed: a process started inside this
/// package's container on 30 Sep 2026 listed and read a file placed in the real folder.
/// </para>
/// <para>
/// <b>Read for the lines the installer writes for this purpose, and nothing else.</b> The record is
/// written for a person, and its format is install.ps1's; the three lines read here are the ones
/// whose wording that script owns for exactly this - the <c>download</c> line naming the package,
/// the <c>finished</c> line carrying the start check's verdict, and a <c>FAILED:</c> line. Anything
/// unrecognised leaves the record <see cref="InstallOutcome.Unfinished"/> rather than guessing, and
/// nothing here throws (§11.1's rule, applied to a file this application did not write).
/// </para>
/// </remarks>
public static partial class InstallRecords
{
    /// <summary>The folder install.ps1 writes its records to.</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinZ3805A Installer", "logs");

    /// <summary>The newest record in <paramref name="folder"/>, read; null when there is none or it
    /// cannot be read.</summary>
    public static InstallRecord? ReadNewest(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return null;
            }

            // The names sort by time: install-yyyyMMdd-HHmmss.log.
            string? newest = Directory.EnumerateFiles(folder, "install-*.log")
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .FirstOrDefault();

            if (newest is null)
            {
                return null;
            }

            // Shared for writing, because an installer still running may have it open.
            using FileStream stream = new(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            List<string> lines = [];
            while (reader.ReadLine() is string line)
            {
                lines.Add(line);
            }

            return Parse(Path.GetFileName(newest), lines);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads one record from its file name and lines.</summary>
    public static InstallRecord Parse(string fileName, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(lines);

        string? version = null;
        string? failure = null;
        InstallOutcome? verdict = null;

        foreach (string line in lines)
        {
            if (version is null && DownloadLine().Match(line) is { Success: true } download)
            {
                version = download.Groups["version"].Value;
            }

            // The unelevated half's failure is the one the person saw; the elevated half reports to
            // it by exit code, so an "[elevated] FAILED" is followed by the one that matters.
            if (FailedLine().Match(line) is { Success: true } failed)
            {
                failure = failed.Groups["message"].Value.Trim();
            }

            if (FinishedLine().Match(line) is { Success: true } finished)
            {
                verdict = finished.Groups["verdict"].Value switch
                {
                    "True" => InstallOutcome.Started,
                    "False" => InstallOutcome.DidNotStart,
                    _ => InstallOutcome.StartNotChecked,
                };
            }
        }

        InstallOutcome outcome = failure is not null ? InstallOutcome.Failed : verdict ?? InstallOutcome.Unfinished;

        return new InstallRecord(fileName, StartedAtFrom(fileName), version, outcome, failure);
    }

    /// <summary>One sentence for the Diagnostics page.</summary>
    public static string Describe(InstallRecord record, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(culture);

        string what = record.Version is null ? "The last install" : $"Version {record.Version}";
        string when = record.StartedAt is DateTime at
            ? $", installed {at.ToString("d MMMM yyyy", culture)} at {at.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : string.Empty;

        string ending = record.Outcome switch
        {
            InstallOutcome.Started => "The installer started WinZ3805A and it got going.",
            InstallOutcome.DidNotStart => "The installer started WinZ3805A and it did not get going - the record says what Windows reported.",
            InstallOutcome.StartNotChecked => "The installer could not start WinZ3805A to check it, usually because .NET 10 was not installed yet.",
            InstallOutcome.Failed => $"The installer stopped: {record.Failure}",
            _ => "The record stops without a result - the installer's window was probably closed part-way.",
        };

        return $"{what}{when}. {ending}";
    }

    private static DateTime? StartedAtFrom(string fileName) =>
        FileNameStamp().Match(fileName) is { Success: true } stamp
        && DateTime.TryParseExact(stamp.Groups["stamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime at)
            ? at
            : null;

    [GeneratedRegex(@"^install-(?<stamp>\d{8}-\d{6})\.log$")]
    private static partial Regex FileNameStamp();

    [GeneratedRegex(@"\bdownload\s+WinZ3805A_(?<version>\d+\.\d+\.\d+\.\d+)_x64\.msixbundle")]
    private static partial Regex DownloadLine();

    [GeneratedRegex(@"^\S+\s+(?!\[elevated\])FAILED:\s*(?<message>.*)$")]
    private static partial Regex FailedLine();

    [GeneratedRegex(@"\bfinished\s+started ok:\s*(?<verdict>True|False|not checked)\s*$")]
    private static partial Regex FinishedLine();
}
