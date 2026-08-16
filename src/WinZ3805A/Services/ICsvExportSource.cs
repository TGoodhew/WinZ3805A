namespace WinZ3805A.Services;

/// <summary>
/// A Details page that can answer §9.7.5's <c>Ctrl+E</c>, "Export current view".
/// </summary>
/// <remarks>
/// <para>
/// The title-bar command is on the window and the data is on the page, so something has to carry
/// the question across. An interface the page implements rather than a registry the window owns:
/// a page that gains a table should become exportable by saying so on its own class declaration,
/// not by editing a list somewhere else that nobody thinks to look at.
/// </para>
/// <para>
/// Pages that hold no table simply do not implement it, and the window disables the command. That
/// is the point of <see cref="CanExport"/> existing separately from a null return — a command
/// which is visible, enabled, and silently does nothing is worse than one that is greyed out,
/// because the user cannot tell it from a failure.
/// </para>
/// </remarks>
public interface ICsvExportSource
{
    /// <summary>Raised when <see cref="CanExport"/> may have changed.</summary>
    /// <remarks>
    /// On the interface rather than on the page, because the window subscribes without knowing
    /// which page it is showing. The log arrives asynchronously and the filter box narrows it as
    /// the user types, so evaluating the command's enabled state once on navigation would leave
    /// the title bar greyed out over a page that has just filled with entries.
    /// </remarks>
    event EventHandler? ExportAvailabilityChanged;

    /// <summary>Whether there is anything to export right now.</summary>
    /// <remarks>
    /// Separate from building the document so the command's enabled state can be evaluated on
    /// every render without formatting a few hundred rows each time.
    /// </remarks>
    bool CanExport { get; }

    /// <summary>The file name to offer, without extension.</summary>
    string SuggestedFileName { get; }

    /// <summary>
    /// Builds the document, or <see langword="null"/> if there turned out to be nothing to write.
    /// </summary>
    CsvDocument? BuildCsv();
}
