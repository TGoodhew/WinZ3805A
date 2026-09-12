using System.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Services;

/// <summary>
/// Follows the receiver's lock state on its front-panel Active lamp (#462).
/// </summary>
/// <remarks>
/// <para>
/// <b>The second of the panel's two user-definable indicators.</b> z3801.pdf, <i>Front Panel at a
/// Glance</i>, item 2: "User-definable indicators labeled Enabled and Active. These can be turned on
/// through the RS-422 port." The panel has six lamps and exactly two belong to the host software;
/// the other four are the receiver's own. Until #462 both of ours said the same thing, which wasted
/// one of them.
/// </para>
/// <para>
/// So <see cref="ActivityLamp"/> holds <b>Enabled</b> for the application — steady while connected —
/// and this holds <b>Active</b> for the receiver: lit when it is locked to GPS, out when it is not.
/// In front of a rack that separates "software is attached to this one" from "this one is locked",
/// which is the pair of questions a person is actually asking, and which one lamp cannot answer.
/// </para>
/// <para>
/// <b>Written only when the state changes, which is what makes it affordable.</b> §8 worked out that
/// a flash per sweep is 194 % of the whole poll budget, and that arithmetic is untouched — a
/// <c>:LED:</c> write still costs about a second, because the receiver services the node on its own
/// 1 Hz tick (measured again on 9 Sep 2026 at 810 ms and 903 ms, against ~30 ms for a query on the
/// same connection). What makes this different from the per-sweep idea #440 rejected is the
/// <i>cadence of the thing being shown</i>: a locked receiver stays locked for hours, so this costs
/// one write per transition rather than one per second.
/// </para>
/// <para>
/// <b>#440's rules bind here exactly as they bind the other lamp</b>: read the baseline before
/// writing, put it back verbatim on the way out, retain nothing between sessions. The one wrinkle
/// #462 adds is that this lamp's value now depends on the receiver, so the restore must return
/// <i>what was read at connect</i> rather than simply extinguishing it — a user who left Active on
/// gets it back on, even though this class spent the session turning it off and on.
/// </para>
/// <para>
/// An unexpected disconnect leaves it wherever it was, for the reason <see cref="ActivityLamp"/>
/// gives: there is no wire left to restore over, nothing is retained, and §10.9's manual control is
/// how a person puts it right.
/// </para>
/// </remarks>
public sealed class LockLamp : IDisposable
{
    /// <summary>Reads the lamp. Present on the SmartClock family and no other.</summary>
    private const string Read = ":LED:ACT?";

    /// <summary>Sets the lamp. Tier S: a front-panel indicator changes no receiver behaviour.</summary>
    private const string Write = ":LED:ACTive";

    private readonly DeviceSessionService _session;
    private readonly ReceiverStateStore _store;
    private readonly ILogger<LockLamp> _logger;

    /// <summary>What the lamp read before this session took it, held only while taken.</summary>
    private bool? _borrowedFrom;

    /// <summary>What this class last wrote, so an unchanged state costs no wire time.</summary>
    private bool? _shown;

    /// <summary>True while a write is in flight, so a burst of changes does not queue writes.</summary>
    private int _writing;

    /// <summary>Creates a lock lamp over one session and its readings.</summary>
    public LockLamp(DeviceSessionService session, ReceiverStateStore store, ILogger<LockLamp>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);

        _session = session;
        _store = store;
        _logger = logger ?? NullLogger<LockLamp>.Instance;
    }

    /// <summary>Whether this session has taken the lamp.</summary>
    public bool IsHeld => _borrowedFrom is not null;

    /// <summary>Whether the connected receiver has this lamp at all.</summary>
    /// <remarks>
    /// Answered by the driver's own allowlist rather than here (#304). A talker has no lamp and no
    /// command channel to drive one.
    /// </remarks>
    public bool IsSupported =>
        _session.Driver.Find(Read) is not null && _session.Driver.Find(Write) is not null;

    /// <summary>
    /// Reads the baseline, then begins following the lock state.
    /// </summary>
    /// <remarks>
    /// Subscribes only after the baseline is safely in hand. Subscribing first would let a state
    /// change write the lamp before there was anything to give back, which is the one way this class
    /// could take something it cannot return.
    /// </remarks>
    public async Task<bool> ArmAsync(CancellationToken cancellationToken = default)
    {
        if (_borrowedFrom is not null || !IsSupported || _session.Status != ConnectionStatus.Connected)
        {
            return false;
        }

        if (await ReadAsync(cancellationToken).ConfigureAwait(false) is not bool baseline)
        {
            _logger.LogInformation("The Active lamp could not be read, so it was left alone.");
            return false;
        }

        _borrowedFrom = baseline;
        _shown = null;
        _store.PropertyChanged += OnStoreChanged;

        Follow();
        return true;
    }

    /// <summary>Stops following and puts the lamp back as it was found.</summary>
    /// <remarks>
    /// <b>Must run before the transport is torn down.</b> By the time <c>Disconnected</c> is raised
    /// there is no wire left to write over, which is why the session invokes this rather than a
    /// status subscriber — the same reason <see cref="ActivityLamp.RestoreAsync"/> gives.
    /// </remarks>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        _store.PropertyChanged -= OnStoreChanged;

        if (_borrowedFrom is not bool baseline)
        {
            return;
        }

        _borrowedFrom = null;
        _shown = null;

        if (!IsSupported || _session.Status != ConnectionStatus.Connected)
        {
            return;
        }

        // What was READ at connect, not "off". A user who left Active on gets it back on, even
        // though this session spent its life turning it off and on (#462).
        if (!await SetAsync(baseline, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "The Active lamp could not be put back to {State}; it is left as the application set it.",
                baseline ? "on" : "off");
        }
    }

    /// <summary>Hands the lamp to the user, so no restore is owed (§10.9).</summary>
    /// <remarks>
    /// Identical in spirit to <see cref="ActivityLamp.SetManuallyAsync"/>: once a person has set it
    /// themselves, theirs is the value. Following stops too — otherwise the next lock transition
    /// would undo what they just asked for, which would read as the control not working.
    /// </remarks>
    public async Task<bool> SetManuallyAsync(bool on, CancellationToken cancellationToken = default)
    {
        if (!IsSupported || _session.Status != ConnectionStatus.Connected)
        {
            return false;
        }

        _store.PropertyChanged -= OnStoreChanged;

        if (!await SetAsync(on, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _borrowedFrom = null;
        _shown = on;
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _store.PropertyChanged -= OnStoreChanged;

    /// <summary>Named rather than a lambda, so it can be removed (#388).</summary>
    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e) => Follow();

    /// <summary>Writes the lamp if the lock state has changed since the last write.</summary>
    /// <remarks>
    /// Fire-and-forget, and guarded so a burst of notifications cannot queue writes behind each
    /// other: at about a second each, a queue would still be draining long after the state it
    /// described had gone.
    /// </remarks>
    private void Follow()
    {
        if (_borrowedFrom is null)
        {
            return;
        }

        bool locked = ShellMode.For(_session.Driver, _store, _session.Status) == ReceiverMode.Locked;
        if (_shown == locked || Interlocked.Exchange(ref _writing, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (await SetAsync(locked, CancellationToken.None).ConfigureAwait(false))
                {
                    _shown = locked;
                }
            }
            catch (Exception exception)
            {
                // A lamp must never be able to take the application down (§11.1's reasoning applied
                // to the write side).
                _logger.LogInformation(exception, "Following the lock state on the Active lamp failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _writing, 0);

                // The state may have moved again while that write was in flight.
                Follow();
            }
        });
    }

    /// <summary>Reads the lamp, or null when the read failed or made no sense.</summary>
    public async Task<bool?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_session.Driver.Find(Read) is not ScpiCommand command)
        {
            return null;
        }

        Transaction transaction = await _session
            .ExecuteAsync(command, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!transaction.Succeeded || transaction.Lines.Count == 0)
        {
            return null;
        }

        return transaction.Lines[0].Trim().TrimStart('+') switch
        {
            "1" => true,
            "0" => false,
            _ => null,
        };
    }

    private async Task<bool> SetAsync(bool on, CancellationToken cancellationToken)
    {
        if (_session.Driver.Find(Write) is not ScpiCommand command)
        {
            return false;
        }

        Transaction transaction = await _session
            .ExecuteAsync(command, on ? "ON" : "OFF", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return transaction.Succeeded;
    }
}
