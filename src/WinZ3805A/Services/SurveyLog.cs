using System.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>
/// Writes the survey's own history into the log, so a run can be read after it ends (P0-12, #12).
/// </summary>
/// <remarks>
/// <para>
/// §10.6 shows survey progress on the Position page and nowhere else. A survey takes about two
/// hours, which is long enough that nobody watches it — so a run made with that page closed, or
/// with the application in the background, left <b>no record at all</b> of how it went. Whether it
/// advanced steadily or stalled for forty minutes at the two-thirds mark are very different
/// outcomes and were indistinguishable afterwards.
/// </para>
/// <para>
/// <b>The reason that matters now.</b> #185's figures say a stall is likely rather than
/// hypothetical: the bench receiver held four or more satellites for six per cent of a two-day
/// window, and a survey wants that sustained for two hours. Whether re-siting the antenna fixed it
/// is precisely the question the log of the first survey afterwards answers, and it is not a
/// question anyone can answer from memory of a progress bar.
/// </para>
/// <para>
/// The policy is <see cref="SurveyWatch"/>, which is pure and tested against a replayed two-hour
/// run. This class is the wiring and the wording: subscribe, translate, write. It logs at
/// Information because the application ships at Information (<c>App.xaml.cs</c>), and a line the
/// shipped configuration discards is a line nobody reading <c>app.log</c> at a bench will see —
/// which is the mistake #14 had made in the reconnect path.
/// </para>
/// </remarks>
public sealed class SurveyLog : IDisposable
{
    private readonly ReceiverStateStore _store;
    private readonly ILogger<SurveyLog> _logger;
    private readonly SurveyWatch _watch = new();

    private bool _disposed;

    /// <summary>Subscribes to the store and begins recording.</summary>
    /// <param name="store">§12's one copy of the truth, which the poller writes to.</param>
    /// <param name="logger">Where the history goes. Optional, so tests can leave it out.</param>
    public SurveyLog(ReceiverStateStore store, ILogger<SurveyLog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _logger = logger ?? NullLogger<SurveyLog>.Instance;

        _store.PropertyChanged += OnStoreChanged;
    }

    /// <summary>How many lines have been written, which the tests count.</summary>
    public int Recorded { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.PropertyChanged -= OnStoreChanged;
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ReceiverStateStore.Status) or null))
        {
            return;
        }

        ReceiverStatus? status = _store.Status;

        Record(
            _watch.Observe(status?.SurveyPercentComplete, status?.SurveySuspendedReason ?? SurveySuspendedReason.None),
            status);
    }

    private void Record(SurveyNote note, ReceiverStatus? status)
    {
        if (note == SurveyNote.None)
        {
            return;
        }

        Recorded++;
        double percent = status?.SurveyPercentComplete ?? 100;

        switch (note)
        {
            case SurveyNote.Started:
                _logger.LogInformation("Position survey started at {Percent:F1} %.", percent);
                break;

            case SurveyNote.AlreadyRunning:
                // Deliberately not "started". The percentage is real but this session did not watch
                // it accumulate, and a reader deciding whether the run restarted needs to know which
                // of the two they are looking at.
                _logger.LogInformation("Position survey already in progress at {Percent:F1} %.", percent);
                break;

            case SurveyNote.Progressed:
                _logger.LogInformation("Position survey at {Percent:F1} %.", percent);
                break;

            case SurveyNote.Suspended:
                // The reason is the whole value of the line. §11.3 decodes these to enum values
                // precisely so the application can tell "too few satellites" from "poor geometry",
                // and after a two-hour run those mean different things about the antenna.
                _logger.LogInformation(
                    "Position survey suspended at {Percent:F1} %: {Reason}.",
                    percent,
                    _watch.Reason);
                break;

            case SurveyNote.Resumed:
                _logger.LogInformation("Position survey resumed at {Percent:F1} %.", percent);
                break;

            case SurveyNote.Finished:
                _logger.LogInformation("Position survey finished.");
                break;

            default:
                break;
        }
    }
}
