using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>
/// Everything currently known about the receiver, in one place that view models bind to (§12).
/// </summary>
/// <remarks>
/// <para>
/// §12 is specific about the shape: <c>PollingService</c> writes here and view models bind here,
/// never to the poller. That keeps one copy of the truth and means a view cannot accidentally
/// depend on when a poll happens, only on what it found.
/// </para>
/// <para>
/// <b>Stale data is kept, not blanked</b> (§9.11). When polling stops or the link drops, the last
/// reading stays on screen with the time it was taken; the UI dims it and shows the age. An old
/// reading with an honest timestamp is more useful than an empty field, which tells the user
/// nothing about whether the value or the connection is the problem.
/// </para>
/// <para>
/// Plain <see cref="INotifyPropertyChanged"/> rather than the MVVM toolkit's generator, so this
/// file has no dependency beyond the Device library and can be compiled into the headless test
/// project by link.
/// </para>
/// </remarks>
public sealed class ReceiverStateStore : INotifyPropertyChanged
{
    /// <summary>How many time-interval samples the §9.10.2 medallion ring draws.</summary>
    public const int TimeIntervalWindow = 60;

    private readonly TimeProvider _timeProvider;
    private readonly double?[] _timeInterval = new double?[TimeIntervalWindow];
    private int _timeIntervalNext;
    private int _timeIntervalCount;

    private ReceiverStatus? _status;
    private string? _syncState;
    private int? _tfom;
    private int? _ffom;
    private double? _onePpsTiNanoseconds;
    private double? _oscillatorControl;
    private int? _trackedCount;
    private DateTimeOffset? _lastFastPoll;
    private DateTimeOffset? _lastFullPoll;

    /// <summary>Creates a store.</summary>
    /// <param name="timeProvider">
    /// Supplies the timestamps every reading is stamped with, and the "now" that
    /// <see cref="AgeOf"/> measures against. Injected per §12 so staleness is testable without
    /// waiting for it.
    /// </param>
    public ReceiverStateStore(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The most recent full status screen, or <see langword="null"/> before the first one.</summary>
    /// <remarks>
    /// The satellite table, position, and health sections come only from here: §7.3 notes the
    /// elevation/azimuth table has no scalar equivalent, which is why the full tier exists at all.
    /// </remarks>
    public ReceiverStatus? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>The disciplining state from <c>:SYNC:STAT?</c>, such as <c>LOCK</c>.</summary>
    public string? SyncState
    {
        get => _syncState;
        private set => Set(ref _syncState, value);
    }

    /// <summary>Time figure of merit; lower is better.</summary>
    public int? Tfom
    {
        get => _tfom;
        private set => Set(ref _tfom, value);
    }

    /// <summary>Frequency figure of merit; lower is better.</summary>
    public int? Ffom
    {
        get => _ffom;
        private set => Set(ref _ffom, value);
    }

    /// <summary>The 1 PPS time interval against GPS, in nanoseconds.</summary>
    public double? OnePpsTiNanoseconds
    {
        get => _onePpsTiNanoseconds;
        private set => Set(ref _onePpsTiNanoseconds, value);
    }

    /// <summary>The oscillator's electronic frequency control, as a relative figure.</summary>
    public double? OscillatorControl
    {
        get => _oscillatorControl;
        private set => Set(ref _oscillatorControl, value);
    }

    /// <summary>How many satellites are being tracked.</summary>
    public int? TrackedCount
    {
        get => _trackedCount;
        private set => Set(ref _trackedCount, value);
    }

    /// <summary>When the fast tier last completed, or <see langword="null"/> if it never has.</summary>
    public DateTimeOffset? LastFastPoll
    {
        get => _lastFastPoll;
        private set => Set(ref _lastFastPoll, value);
    }

    /// <summary>When the full screen last arrived.</summary>
    public DateTimeOffset? LastFullPoll
    {
        get => _lastFullPoll;
        private set => Set(ref _lastFullPoll, value);
    }

    /// <summary>
    /// The last <see cref="TimeIntervalWindow"/> time-interval samples, oldest first.
    /// </summary>
    /// <remarks>
    /// A fixed ring rather than a growing list: this is written once a second for as long as the
    /// application runs, and §9.10.2's ring only ever draws the last sixty. Persisting a longer
    /// history is P1-2, and a different concern from what the medallion needs to redraw.
    /// </remarks>
    public IReadOnlyList<double?> RecentTimeInterval
    {
        get
        {
            double?[] ordered = new double?[_timeIntervalCount];
            int start = _timeIntervalCount == TimeIntervalWindow ? _timeIntervalNext : 0;
            for (int i = 0; i < _timeIntervalCount; i++)
            {
                ordered[i] = _timeInterval[(start + i) % TimeIntervalWindow];
            }

            return ordered;
        }
    }

    /// <summary>
    /// How old a reading is, or <see langword="null"/> if there has never been one.
    /// </summary>
    /// <remarks>
    /// The UI dims a reading and shows its age past the §9.11 threshold rather than clearing it.
    /// This returns the age rather than a bool so the caller can decide the threshold — the fast
    /// tier and the full screen go stale at very different rates.
    /// </remarks>
    public TimeSpan? AgeOf(DateTimeOffset? timestamp) =>
        timestamp is DateTimeOffset taken ? _timeProvider.GetUtcNow() - taken : null;

    /// <summary>Records one completed fast-tier sweep.</summary>
    /// <remarks>
    /// Every value is nullable and a null overwrites: a field the receiver stopped answering must
    /// go to an em dash rather than keep showing the last number it gave, which would be a
    /// fabrication. The timestamp is what tells the user the rest is old.
    /// </remarks>
    public void UpdateFast(
        string? syncState,
        int? tfom,
        int? ffom,
        double? onePpsTiNanoseconds,
        double? oscillatorControl,
        int? trackedCount)
    {
        SyncState = syncState;
        Tfom = tfom;
        Ffom = ffom;
        OnePpsTiNanoseconds = onePpsTiNanoseconds;
        OscillatorControl = oscillatorControl;
        TrackedCount = trackedCount;

        _timeInterval[_timeIntervalNext] = onePpsTiNanoseconds;
        _timeIntervalNext = (_timeIntervalNext + 1) % TimeIntervalWindow;
        _timeIntervalCount = Math.Min(_timeIntervalCount + 1, TimeIntervalWindow);
        OnPropertyChanged(nameof(RecentTimeInterval));

        LastFastPoll = _timeProvider.GetUtcNow();
    }

    /// <summary>Records one full status screen.</summary>
    public void UpdateFull(ReceiverStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Status = status;
        LastFullPoll = _timeProvider.GetUtcNow();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
