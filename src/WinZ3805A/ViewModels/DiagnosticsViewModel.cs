using System.ComponentModel;
using System.Globalization;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Parsing;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The §10.9 Diagnostics page.
/// </summary>
/// <remarks>
/// Reads on demand, like the §10.10 registers page and for a related reason: <c>:SYST:ERR?</c>
/// <i>removes</i> the entry it returns, so nothing may read the error queue on a timer. The
/// diagnostic log is safe to re-read — clearing it is a separate command — but it is up to 222
/// entries of text and has no business on a 1 s cadence either.
/// </remarks>
public sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    private readonly DeviceSessionService _session;

    private IReadOnlyList<DiagnosticLogEntry> _log = [];
    private string _filter = string.Empty;
    private string? _selfTestResult;
    private readonly List<string> _errors = [];
    private int? _logCount;
    private bool _isReading;
    private string? _fault;

    /// <summary>Creates a view model over the shared session.</summary>
    public DiagnosticsViewModel(DeviceSessionService session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether a read is in flight.</summary>
    public bool IsReading => _isReading;

    /// <summary>Whether the page can ask the receiver anything.</summary>
    public bool CanRead => !_isReading && _session.Status == ConnectionStatus.Connected;

    /// <summary>What went wrong, if anything did.</summary>
    public string? Fault => _fault;

    /// <summary>The result of the last self-test the receiver ran.</summary>
    public string SelfTestResultText => string.IsNullOrWhiteSpace(_selfTestResult)
        ? "Not read"
        : _selfTestResult;

    /// <summary>
    /// Re-reads <c>:DIAG:TEST:RES?</c> after a test has run (#53).
    /// </summary>
    /// <returns>The raw reply, or null when it could not be read.</returns>
    /// <remarks>
    /// Separate from <see cref="RefreshAsync"/>, which also pulls the whole diagnostic log. After a
    /// self-test the log is not what changed, and re-reading it would add seconds of wire time to
    /// an operation the user is already waiting on.
    /// </remarks>
    public async Task<string?> ReadSelfTestResultAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRead)
        {
            return null;
        }

        _selfTestResult = await ReadTextAsync(":DIAG:TEST:RES?", cancellationToken).ConfigureAwait(true);
        RaiseAll();

        return _selfTestResult;
    }

    /// <summary>
    /// Drops the log this page is holding, after the receiver has been told to clear its own.
    /// </summary>
    /// <remarks>
    /// Rather than re-reading. The log is empty now, and a read that came back with nothing would
    /// be indistinguishable from a read that failed — §9.11's empty state says what <em>will</em>
    /// appear there, which is the honest thing to show and costs no traffic to show it.
    /// </remarks>
    public void ForgetLog()
    {
        _log = [];
        _logCount = 0;
        RaiseAll();
    }

    /// <summary>How many entries the receiver says its log holds.</summary>
    public int? LogCount => _logCount;

    /// <summary>The log header, which names the count the receiver reported rather than the count shown.</summary>
    public string LogHeaderText
    {
        get
        {
            if (_logCount is not int count)
            {
                return "Diagnostic log";
            }

            return Filtered.Count == _log.Count
                ? $"Diagnostic log — {count} entries"
                : $"Diagnostic log — {Filtered.Count} of {_log.Count} shown, {count} in the receiver";
        }
    }

    /// <summary>The filter text.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            string text = value ?? string.Empty;
            if (_filter != text)
            {
                _filter = text;
                RaiseAll();
            }
        }
    }

    /// <summary>
    /// The entries to show, newest first.
    /// </summary>
    /// <remarks>
    /// Reversed because the receiver returns them oldest first and the interesting one is almost
    /// always the last thing that happened. Filtering matches the whole raw line, so a search for a
    /// date works as well as one for a word.
    /// </remarks>
    public IReadOnlyList<DiagnosticLogEntry> Filtered
    {
        get
        {
            IEnumerable<DiagnosticLogEntry> entries = _log;

            if (!string.IsNullOrWhiteSpace(_filter))
            {
                entries = entries.Where(entry =>
                    entry.RawText.Contains(_filter, StringComparison.OrdinalIgnoreCase));
            }

            return [.. entries.Reverse()];
        }
    }

    /// <summary>Whether the log has been read and has nothing in it.</summary>
    public bool IsLogEmpty => _log.Count == 0;

    /// <summary>What the log card says when it has no rows.</summary>
    public string LogEmptyText => _log.Count > 0
        ? $"No entry matches “{_filter}”."
        : "The log has not been read yet, or the receiver has nothing in it.";

    /// <summary>The error queue, oldest first, as read.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>What the error card says.</summary>
    public string ErrorSummaryText => _errors.Count switch
    {
        0 => "No errors.",
        1 => "1 error read from the queue.",
        _ => $"{_errors.Count} errors read from the queue.",
    };

    /// <summary>Reads the self-test result, the log count and the log.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRead)
        {
            return;
        }

        _isReading = true;
        _fault = null;
        RaiseAll();

        try
        {
            _selfTestResult = await ReadTextAsync(":DIAG:TEST:RES?", cancellationToken).ConfigureAwait(true);

            string? count = await ReadTextAsync(":DIAG:LOG:COUN?", cancellationToken).ConfigureAwait(true);
            _logCount = int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : null;

            Transaction transaction = await ExecuteAsync(":DIAG:LOG:READ:ALL?", cancellationToken)
                .ConfigureAwait(true);

            // The whole log arrives as one multi-line block; the entries are comma-separated within
            // it, and a message may itself contain a comma.
            _log = transaction.Succeeded
                ? DiagnosticLogParser.ParseAll(string.Join(' ', transaction.Lines))
                : [];
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-read.
        }
        finally
        {
            _isReading = false;
            RaiseAll();
        }
    }

    /// <summary>
    /// Drains the receiver's error queue.
    /// </summary>
    /// <remarks>
    /// <b>This consumes what it reads.</b> <c>:SYST:ERR?</c> removes the oldest entry and returns
    /// it, so the queue is emptied by looking at it — which is why it is a button rather than
    /// something the page does on arrival, and why what it read is kept here rather than re-read.
    /// The loop stops on the SCPI "no error" answer, and is bounded in case a receiver never gives
    /// it.
    /// </remarks>
    public async Task ReadErrorQueueAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRead)
        {
            return;
        }

        _isReading = true;
        _fault = null;
        _errors.Clear();
        RaiseAll();

        try
        {
            for (int i = 0; i < 64; i++)
            {
                string? entry = await ReadTextAsync(":SYST:ERR?", cancellationToken).ConfigureAwait(true);

                if (string.IsNullOrWhiteSpace(entry) || entry.StartsWith("0,", StringComparison.Ordinal)
                    || entry.Contains("No error", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                _errors.Add(entry);
            }
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-read.
        }
        finally
        {
            _isReading = false;
            RaiseAll();
        }
    }

    private async Task<Transaction> ExecuteAsync(string mnemonic, CancellationToken cancellationToken)
    {
        // §8.1 makes the catalog an allowlist, and ExecuteAsync takes an ScpiCommand so nothing can
        // route around it.
        if (CommandCatalog.Find(mnemonic) is not ScpiCommand command)
        {
            _fault ??= $"{mnemonic} is not in the command catalog.";
            return new Transaction
            {
                Command = mnemonic,
                Outcome = TransactionOutcome.Faulted,
                Lines = [],
                EchoDiscarded = false,
                Elapsed = TimeSpan.Zero,
            };
        }

        Transaction transaction = await _session.ExecuteAsync(command, cancellationToken: cancellationToken)
            .ConfigureAwait(true);

        if (!transaction.Succeeded)
        {
            _fault ??= transaction.PromptStatus is string status
                ? $"The receiver answered {status} to {mnemonic}."
                : $"No answer to {mnemonic}.";
        }

        return transaction;
    }

    private async Task<string?> ReadTextAsync(string mnemonic, CancellationToken cancellationToken)
    {
        Transaction transaction = await ExecuteAsync(mnemonic, cancellationToken).ConfigureAwait(true);

        // Responses carry a leading space (#78).
        return transaction.Succeeded && transaction.Lines.Count > 0
            ? transaction.Lines[0].Trim()
            : null;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for every property.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
