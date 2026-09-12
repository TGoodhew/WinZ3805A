using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinZ3805A.Device.Drivers;
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

    /// <summary>The ordered ring as last built, handed to every reader until the next sample (#403).</summary>
    private IReadOnlyList<double?> _recentTimeInterval = [];

    /// <summary>Which receiver everything held here came from, or null before the first (#492).</summary>
    private string? _deviceKey;

    /// <summary>
    /// What the connected driver's sweep answers, as the last update said (#479).
    /// </summary>
    /// <remarks>
    /// <see cref="FastFields.All"/> until a driver says otherwise, which is the SmartClock's shape
    /// and the one every surface assumed before a split plan existed. An optimistic default is the
    /// right one here: it reproduces the old behaviour exactly for a driver that carries everything,
    /// so nothing moves for the family this was measured on.
    /// </remarks>
    private FastFields _fastTierCarries = FastFields.All;

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

    /// <summary>
    /// Which receiver the readings currently held came from, or <see langword="null"/> before any
    /// (#492). Diagnostic — the poller sets it through <see cref="BeginDevice"/>.
    /// </summary>
    public string? DeviceKey => _deviceKey;

    /// <summary>
    /// The key that decides whether two connections are the same receiver (#492).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The port is in it because the identity is not enough.</b> Every NMEA talker answers to the
    /// same <see cref="DeviceIdentity"/> — the family name, with nothing in it to tell two pucks
    /// apart — so a key built from the identity alone calls a VK-162 and a forM8N the same receiver
    /// and carries one's readings into the other's display. That is the reported bug, exactly.
    /// </para>
    /// <para>
    /// <b>The identity is in it because the port is not enough either.</b> A different receiver on
    /// the same port is a different receiver, and where the hardware says so — a SmartClock's serial
    /// number — the key notices.
    /// </para>
    /// <para>
    /// Two receivers of the same family swapped on one port remain indistinguishable, because
    /// nothing on the wire distinguishes them. The readings are kept rather than guessed at.
    /// </para>
    /// </remarks>
    /// <param name="portName">The port the session is on, or null before it has one.</param>
    /// <param name="identity">What the receiver said it is, or null when it has not said.</param>
    public static string KeyFor(string? portName, DeviceIdentity? identity)
    {
        string port = string.IsNullOrWhiteSpace(portName) ? "(no port)" : portName;
        string who = identity is null
            ? "(unidentified)"
            : $"{identity.Manufacturer}/{identity.Model}/{identity.SerialNumber}";

        return $"{port}|{who}";
    }

    /// <summary>
    /// Says which receiver the readings that follow come from, blanking everything if it is a
    /// different one (#492).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>§9.11's "stale data is kept, not blanked" is about one receiver going quiet</b>, and it is
    /// right about that: an old reading with an honest timestamp beats an empty field, because the
    /// user can see the age and judge it. It is not right about a <i>different</i> receiver. Nothing
    /// distinguishes "this one has not answered yet" from "this number belongs to the instrument you
    /// were looking at a minute ago", and the footer's age is the new connection's, so the borrowed
    /// reading looks fresh.
    /// </para>
    /// <para>
    /// The bug that produced this: a VK-162 on one port and a forM8N on another, switched by
    /// disconnecting and reconnecting rather than restarting, showed the same satellite count for
    /// both. Their true counts differed by four.
    /// </para>
    /// <para>
    /// <b>The key must carry the port, not just the identity.</b> Every NMEA talker answers to the
    /// same <c>DeviceIdentity</c> — the family name, with no serial number to tell two pucks apart —
    /// so an identity-only key would have called those two receivers the same one and changed
    /// nothing. It is the pair that distinguishes them.
    /// </para>
    /// <para>
    /// What this deliberately does <i>not</i> catch: a different receiver of the same family
    /// substituted on the same port. There is nothing on the wire to tell those apart, so it keeps
    /// the readings rather than guessing, and the case is named here rather than left to be
    /// rediscovered.
    /// </para>
    /// </remarks>
    /// <param name="key">
    /// Identifies the receiver — see <c>PollingService</c> for what it is built from. A null key
    /// means "no receiver", and is itself a change if readings are held.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if this was a different receiver and the readings were blanked, so
    /// the caller can say so in the log.
    /// </returns>
    public bool BeginDevice(string? key)
    {
        if (string.Equals(_deviceKey, key, StringComparison.Ordinal))
        {
            return false;
        }

        bool hadReadings = _lastFastPoll is not null || _lastFullPoll is not null;
        _deviceKey = key;
        ClearReadings();

        // A first connection is not a change worth reporting: there was nothing to carry over.
        return hadReadings;
    }

    /// <summary>Blanks every reading, as though nothing had ever been polled.</summary>
    /// <remarks>
    /// The timestamps go too. A reading without one cannot be aged, and §9.11's whole treatment of
    /// staleness is built on the age — leaving <see cref="LastFastPoll"/> behind would date the new
    /// receiver's empty display to the old one's last answer.
    /// </remarks>
    private void ClearReadings()
    {
        Status = null;
        SyncState = null;
        Tfom = null;
        Ffom = null;
        OnePpsTiNanoseconds = null;
        OscillatorControl = null;
        TrackedCount = null;
        LastFastPoll = null;
        LastFullPoll = null;
        OnPropertyChanged(nameof(LastDisplayedPoll));

        // Back to the optimistic default: the next driver's shape arrives with its first update,
        // and until then there is nothing held to mis-age (#479, #492).
        _fastTierCarries = FastFields.All;

        Array.Clear(_timeInterval);
        _timeIntervalNext = 0;
        _timeIntervalCount = 0;

        // One rebuild, not sixty: the snapshot is replaced rather than mutated, for #403's reason.
        SnapshotTimeInterval();
        OnPropertyChanged(nameof(RecentTimeInterval));
    }

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
    /// The poll a page of mixed readings should be aged against: the older of the tiers that
    /// actually contribute to it (#479).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It understates otherwise, which is the unsafe direction.</b> A Trimble UCCM-P sweeps
    /// every second and prints its screen every ten, and TFOM, FFOM and the satellite count are on
    /// the screen — so a TFOM that is nine seconds old was reported as one second old, and §9.11's
    /// whole staleness treatment ran off the wrong number for exactly the readings it matters most
    /// for.
    /// </para>
    /// <para>
    /// <b>Slightly pessimistic for the fast readings sharing the page, and that is the choice.</b>
    /// A one-second-old sync state shown as ten seconds old looks worse than it is; a
    /// nine-second-old TFOM shown as one second old looks better than it is. §9.11 exists to stop a
    /// user acting on something stale, so of the two ways to be wrong only one is safe.
    /// </para>
    /// <para>
    /// A driver whose sweep carries everything is unaffected: the answer is
    /// <see cref="LastFastPoll"/>, exactly as before. So is one whose screen has not arrived yet —
    /// there is nothing screen-borne on the page to age.
    /// </para>
    /// </remarks>
    public DateTimeOffset? LastDisplayedPoll
    {
        get
        {
            if (_fastTierCarries == FastFields.All || _lastFullPoll is null)
            {
                return _lastFastPoll;
            }

            if (_lastFastPoll is null)
            {
                return _lastFullPoll;
            }

            return _lastFullPoll < _lastFastPoll ? _lastFullPoll : _lastFastPoll;
        }
    }

    /// <summary>
    /// The last <see cref="TimeIntervalWindow"/> time-interval samples, oldest first.
    /// </summary>
    /// <remarks>
    /// A fixed ring rather than a growing list: this is written once a second for as long as the
    /// application runs, and §9.10.2's ring only ever draws the last sixty. Persisting a longer
    /// history is P1-2, and a different concern from what the medallion needs to redraw.
    /// </remarks>
    /// <remarks>
    /// <b>Built when a sample arrives, not when one is read (#403).</b> This used to allocate a
    /// fresh array on every read, which looked harmless and was not: the medallion is assigned
    /// <c>Samples</c> on every render, a fresh array is a fresh managed object, and handing a
    /// managed object to WinRT mints a COM callable wrapper the runtime records in storage it never
    /// shrinks. It also defeated the reference comparison guarding that assignment, which could
    /// never match and so never skipped anything - measured at 0.47 MB an hour, unchanged by every
    /// other guard added for #399 and #403 until this one.
    /// <para>
    /// Readers get the same instance until the next sample, so an unchanged ring is recognisable as
    /// unchanged. The snapshot is replaced rather than mutated, so a caller holding the previous one
    /// still sees a coherent ring.
    /// </para>
    /// </remarks>
    public IReadOnlyList<double?> RecentTimeInterval => _recentTimeInterval;

    /// <summary>Rebuilds the ordered snapshot. Called only when the ring changes.</summary>
    private void SnapshotTimeInterval()
    {
        double?[] ordered = new double?[_timeIntervalCount];
        int start = _timeIntervalCount == TimeIntervalWindow ? _timeIntervalNext : 0;
        for (int i = 0; i < _timeIntervalCount; i++)
        {
            ordered[i] = _timeInterval[(start + i) % TimeIntervalWindow];
        }

        _recentTimeInterval = ordered;
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
    /// <para>
    /// <b>A null overwrites, but only for a field the sweep actually asked about.</b> A field the
    /// receiver stopped answering must go to an em dash rather than keep showing the last number
    /// it gave, which would be a fabrication; the timestamp is what tells the user the rest is
    /// old. That reasoning holds only where the question was put. For a reading this family
    /// carries on the full screen instead, the sweep has no opinion, and writing its null over a
    /// good value is not honesty but erasure — ten times between screens, which is how a locked
    /// UCCM-P came to show `TFOM —`, `FFOM —` and no satellite count permanently (#475).
    /// </para>
    /// <para>
    /// So <paramref name="carries"/> says which fields this driver's sweep answers, and the rest
    /// are left for <see cref="UpdateFull"/>. It is not inferred from the values: an all-null
    /// sweep from a receiver that has gone quiet must still blank the display, and a sweep that
    /// happens to read zero satellites is a reading rather than a silence.
    /// </para>
    /// </remarks>
    public void UpdateFast(
        string? syncState,
        int? tfom,
        int? ffom,
        double? onePpsTiNanoseconds,
        double? oscillatorControl,
        int? trackedCount,
        FastFields carries)
    {
        _fastTierCarries = carries;

        if (carries.HasFlag(FastFields.SyncState)) { SyncState = syncState; }
        if (carries.HasFlag(FastFields.Tfom)) { Tfom = tfom; }
        if (carries.HasFlag(FastFields.Ffom)) { Ffom = ffom; }
        if (carries.HasFlag(FastFields.OscillatorControl)) { OscillatorControl = oscillatorControl; }
        if (carries.HasFlag(FastFields.SatellitesTracked)) { TrackedCount = trackedCount; }

        // The trend window is the time interval's own history, so a driver that does not measure
        // one must not push a null into it every second: that would fill the §9.4.4 sparkline with
        // gaps that say "no reading" where the truth is "never asked".
        if (carries.HasFlag(FastFields.TimeInterval))
        {
            OnePpsTiNanoseconds = onePpsTiNanoseconds;

            _timeInterval[_timeIntervalNext] = onePpsTiNanoseconds;
            _timeIntervalNext = (_timeIntervalNext + 1) % TimeIntervalWindow;
            _timeIntervalCount = Math.Min(_timeIntervalCount + 1, TimeIntervalWindow);
            SnapshotTimeInterval();
            OnPropertyChanged(nameof(RecentTimeInterval));
        }

        LastFastPoll = _timeProvider.GetUtcNow();
        OnPropertyChanged(nameof(LastDisplayedPoll));
    }

    /// <summary>Records one full status screen.</summary>
    /// <remarks>
    /// <para>
    /// <b>The screen supplies the readings the fast tier does not.</b> Where a family has no
    /// scalar query for TFOM, FFOM, the time interval or the satellite count, the status screen is
    /// the only place they exist, and the scalar properties on this store are what the primary
    /// window binds to — not <see cref="Status"/>. Filling them here is what makes those readings
    /// reachable at all (#475).
    /// </para>
    /// <para>
    /// <b>Only the fields the sweep does not carry.</b> Where the sweep does ask, it is both
    /// fresher and authoritative, and a ten-second-old screen must not overwrite a one-second-old
    /// answer — nor quietly restore a value the sweep has just blanked on purpose.
    /// </para>
    /// <para>
    /// <b>One thing this does not fix.</b> A reading taken from here ages against
    /// <see cref="LastFullPoll"/>, while the primary window shows a single page age taken from
    /// <see cref="LastFastPoll"/>. For a driver whose readings are split across the tiers that age
    /// understates the screen-borne ones by up to a full cadence. Per-reading staleness is a
    /// §9.11 question and is deliberately not answered here.
    /// </para>
    /// </remarks>
    public void UpdateFull(ReceiverStatus status, FastFields carries)
    {
        ArgumentNullException.ThrowIfNull(status);

        // Set here as well as in UpdateFast, because on a fresh connection the full screen can
        // arrive first and LastDisplayedPoll must not judge the page by a stale shape (#479).
        _fastTierCarries = carries;

        Status = status;

        if (!carries.HasFlag(FastFields.Tfom)) { Tfom = status.Tfom; }
        if (!carries.HasFlag(FastFields.Ffom)) { Ffom = status.Ffom; }
        if (!carries.HasFlag(FastFields.TimeInterval)) { OnePpsTiNanoseconds = status.OnePpsTiNanoseconds; }

        // Tracked.Count rather than a count field, because the model has none: the table is the
        // count. A screen whose satellite table did not parse and a receiver tracking nothing both
        // read 0 here, which is the one place this cannot tell absence from zero — §11.1's
        // warnings on the status are where that shows up instead.
        if (!carries.HasFlag(FastFields.SatellitesTracked)) { TrackedCount = status.Tracked.Count; }

        LastFullPoll = _timeProvider.GetUtcNow();
        OnPropertyChanged(nameof(LastDisplayedPoll));
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
