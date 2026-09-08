using System.ComponentModel;
using System.Globalization;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
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
    private double? _powerOnHours;
    private IReadOnlyList<string> _parseWarnings = [];
    private bool _isReading;
    private string? _fault;

    /// <summary>Whether <see cref="Fault"/> is a capability gap rather than something going wrong (#435).</summary>
    private bool _faultIsUnsupported;

    /// <summary>Creates a view model over the shared session.</summary>
    public DiagnosticsViewModel(DeviceSessionService session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// What the parser could not make sense of in the latest status screen (§11.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by the page from the store, because parse warnings belong to a screen rather than to a
    /// query — the same reason the identity on Overview is set from the session.
    /// </para>
    /// <para>
    /// <b>§11.2 has always said these are "surfaced in Diagnostics" and they were not</b> (#320).
    /// They reached the application log and nowhere else, and at Debug level — below the
    /// Information floor the application ships at — so in practice they reached nobody at all.
    /// §11.1's rule is that an unreadable field becomes null and renders as a dash, which is right
    /// for the reading and useless as a report: "it shows dashes" is not something anyone can act
    /// on, and "unrecognised health item 'Xtal Pwr'" is.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> ParseWarnings
    {
        get => _parseWarnings;
        set
        {
            IReadOnlyList<string> next = value ?? [];
            if (!_parseWarnings.SequenceEqual(next))
            {
                _parseWarnings = next;
                RaiseAll();
            }
        }
    }

    /// <summary>
    /// What the card says when the parser met nothing it could not read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated rather than left blank: an empty card reads as "not implemented", and the useful fact
    /// here is the negative one — the screen parsed completely.
    /// </para>
    /// <para>
    /// <b>Unless there is no screen (#435).</b> A broadcast talker sends a cycle of sentences and
    /// has no status screen at all, so "the last status screen parsed completely" reported a
    /// success at something that never happened — the audit found it saying exactly that with a
    /// VK-162 connected.
    /// </para>
    /// </remarks>
    public string ParseWarningSummary => !_session.Driver.Reports(ReceiverReading.StatusScreen)
        ? $"This receiver does not send a status screen. The {_session.Driver.Family} protocol broadcasts sentences instead, and any that could not be read are counted above."
        : ParseWarnings.Count switch
    {
        0 => "The last status screen parsed completely.",
        1 => "1 field in the last status screen could not be read.",
        int many => $"{many} fields in the last status screen could not be read.",
    };

    /// <summary>Whether a read is in flight.</summary>
    public bool IsReading => _isReading;

    /// <summary>Whether the page can ask the receiver anything.</summary>
    public bool CanRead => !_isReading && _session.Status == ConnectionStatus.Connected;

    /// <summary>What went wrong, if anything did.</summary>
    public string? Fault => _fault;

    /// <summary>
    /// Whether <see cref="Fault"/> describes a family that never had the command, rather than a
    /// read that failed (#435).
    /// </summary>
    /// <remarks>
    /// The page shows an error bar for a fault and an informational one for this. A receiver doing
    /// exactly what it was built to do must not be presented with an error icon, which is what a
    /// talker got on this page and on §10.10's.
    /// </remarks>
    public bool FaultIsUnsupported => _faultIsUnsupported;

    /// <summary>The result of the last self-test the receiver ran.</summary>
    public string SelfTestResultText => string.IsNullOrWhiteSpace(_selfTestResult)
        ? "Not read"
        : _selfTestResult;

    /// <summary>
    /// How long the receiver has been running in total, from <c>:DIAG:LIF:COUN?</c> (§10.9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manual calls this <c>:DIAGnostic:LIFetime:COUNt?</c> and the catalog describes it as the
    /// accumulated running time. §10.9 called the card a "power-on <i>count</i>" and #316 recorded
    /// that no such query existed at all — both wrong, and the second nearly had the requirement
    /// struck. It is hours (#320).
    /// </para>
    /// <para>
    /// Worth having on an instrument whose oscillator ages with running time: the EFC trend on
    /// Overview shows the drift, and this is the figure that says how much life produced it.
    /// </para>
    /// </remarks>
    public string PowerOnHoursText => _powerOnHours is double hours
        ? string.Create(CultureInfo.CurrentCulture, $"{hours:N0} h")
        : ReadoutFormatter.NoValue;

    /// <summary>
    /// The caption under the Lifetime card.
    /// </summary>
    /// <remarks>
    /// <b>It explained an OCXO to a receiver that has none (#435).</b> The sentence was a literal in
    /// the XAML, so it was shown whatever was connected: with a talker on the port the card offered
    /// an em dash above prose about an oven-controlled oscillator ageing with running time, which
    /// invites the reading that the figure is obtainable and merely missing. A family that has no
    /// oscillator has no hours behind one either.
    /// </remarks>
    public string PowerOnHoursCaption => _session.Driver.Reports(ReceiverReading.PowerOnHours)
        ? "How long the receiver has run in total. An oven-controlled oscillator ages with running time, so this is the figure behind the drift the Overview page's EFC trend shows."
        : NotReported("how long it has been powered");

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
    /// <remarks>
    /// <b>"Has not been read yet" is a promise, and for some families it is one that cannot be kept
    /// (#435).</b> §9.11's empty state says what <i>will</i> appear there, which is right when
    /// something will; a receiver with no log of its own leaves that sentence describing a read that
    /// is never going to happen. The filter message stays ahead of both, because a filter that
    /// matches nothing is about the filter whatever the receiver is.
    /// </remarks>
    public string LogEmptyText => _log.Count > 0
        ? $"No entry matches “{_filter}”."
        : _session.Driver.Reports(ReceiverReading.DiagnosticLog)
            ? "The log has not been read yet, or the receiver has nothing in it."
            : NotReported("a diagnostic log of its own");

    /// <summary>The error queue, oldest first, as read.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>What the error card says.</summary>
    /// <remarks>
    /// <b>"No errors." is a positive claim about a queue that need not exist (#435).</b> It is the
    /// useful answer for a receiver that keeps one — the negative fact is worth stating rather than
    /// leaving the card blank — and it is a clean bill of health invented out of nothing for a
    /// family that has no error queue at all. The audit found it saying exactly that with a talker
    /// connected, beside a Read button that would have reported a fault if pressed.
    /// </remarks>
    public string ErrorSummaryText => !_session.Driver.Reports(ReceiverReading.ErrorQueue)
        ? NotReported("an error queue")
        : _errors.Count switch
    {
        0 => "No errors.",
        1 => "1 error read from the queue.",
        _ => $"{_errors.Count} errors read from the queue.",
    };

    /// <summary>
    /// The sentence a card shows for a reading this receiver's family can never supply (#435).
    /// </summary>
    /// <remarks>
    /// Local rather than <c>Views.Capability.NotReported</c>, which says the same words: a view
    /// model reaching into the view layer for a string would invert the dependency this project
    /// keeps, and this class already wrote the sentence out once by hand for the status screen. Same
    /// wording either way, so the two surfaces read alike.
    /// </remarks>
    private string NotReported(string what) =>
        $"This receiver does not report {what}. The {_session.Driver.Family} protocol does not carry it.";

    /// <summary>Reads the self-test result, the log count and the log.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRead)
        {
            return;
        }

        _isReading = true;
        _fault = null;
        _faultIsUnsupported = false;
        RaiseAll();

        try
        {
            _selfTestResult = await ReadTextAsync(":DIAG:TEST:RES?", cancellationToken).ConfigureAwait(true);

            string? count = await ReadTextAsync(":DIAG:LOG:COUN?", cancellationToken).ConfigureAwait(true);
            _logCount = int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : null;

            // §10.9's Lifetime card. Read here rather than polled: it changes by one an hour, so
            // asking for it on every sweep would spend wire time on a figure that cannot move
            // between sweeps.
            string? hours = await ReadTextAsync(":DIAG:LIF:COUN?", cancellationToken).ConfigureAwait(true);
            _powerOnHours = double.TryParse(hours, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedHours)
                ? parsedHours
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
        _faultIsUnsupported = false;
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
        if (_session.Driver.Find(mnemonic) is not ScpiCommand command)
        {
            // §8.1 makes the catalog an allowlist, so a family that never had this command has done
            // nothing wrong. Naming the mnemonic put raw SCPI in front of a user and an error icon on
            // a page that was working correctly (#435).
            _fault ??= $"This receiver does not support this reading. The {_session.Driver.Family} driver has no command for it.";
            _faultIsUnsupported = true;
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
