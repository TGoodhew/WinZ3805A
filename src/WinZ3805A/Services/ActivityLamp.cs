using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Services;

/// <summary>
/// Lights the receiver's front-panel Active lamp while the application holds the link (#440).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per session, and the measurement is why.</b> #440 asked for a link-<i>activity</i> indicator
/// driven around what we send, and recommended wrapping each poll sweep. That is not implementable
/// on this receiver. A <c>:LED:</c> write costs <b>999 ms</b> — measured on the bench Z3805A, 10
/// runs, with the delay entirely before the first byte — because the node is serviced on the
/// receiver's own 1 Hz tick. It is not the wire and not the lamp: <c>*CLS</c> is also a write and
/// costs 15 ms, and setting the lamp to the value it already has costs the same 999 ms. So a
/// per-sweep flash of read + on + restore is 1,941 ms against a 1,000 ms cadence — <b>194 % of the
/// entire poll budget</b>, or the wire time of sixty readings, for one flash.
/// </para>
/// <para>
/// Per session costs two writes, about two seconds, once. Connect already spends 2,000 ms per
/// transaction on the §10.12 auto-detect walk, so it disappears into that. What it buys is weaker
/// than what #440 wanted and is the honest version of the same idea: not "the application is
/// talking right now" but <b>"the application is connected to <i>this</i> unit"</b> — which, in
/// front of a rack of four, is the question actually being asked.
/// </para>
/// <para>
/// <b>The receiver owns the lamp; this class borrows it.</b> The baseline is read before the lamp
/// is lit and put back verbatim afterwards, so a user who left it on gets it back on. Nothing is
/// cached between sessions, persisted, or carried across a reconnect — a remembered value is wrong
/// the moment a missed reply, a reconnect or a person changes it, and the front panel is the only
/// truth there is.
/// </para>
/// <para>
/// <b>An unexpected disconnect leaves the lamp lit, and that is an accepted cost.</b> If the cable
/// is pulled or the process dies there is no wire left to restore over, and because nothing is
/// retained the next connect reads the lit lamp and adopts it as the value to restore. The
/// application cannot tell "the user wanted it on" from "we left it on" — which is exactly why the
/// §10.9 Diagnostics page carries a manual control, and why it is not a heuristic.
/// </para>
/// </remarks>
public sealed class ActivityLamp
{
    /// <summary>Reads the lamp. Present on the SmartClock family and no other (#440).</summary>
    private const string Read = ":LED:ACT?";

    /// <summary>Sets the lamp. Tier S: a front-panel indicator changes no receiver behaviour.</summary>
    private const string Write = ":LED:ACTive";

    private readonly DeviceSessionService _session;
    private readonly ILogger<ActivityLamp> _logger;

    /// <summary>
    /// What the lamp read before this session lit it, held only while lit.
    /// </summary>
    /// <remarks>
    /// The <b>only</b> state this class has, and it is not a cache: it is the value owed back to the
    /// receiver, dropped the instant it has been returned. Null means the lamp is not ours.
    /// </remarks>
    private bool? _borrowedFrom;

    /// <summary>Creates a lamp over one session.</summary>
    public ActivityLamp(DeviceSessionService session, ILogger<ActivityLamp>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        _logger = logger ?? NullLogger<ActivityLamp>.Instance;
    }

    /// <summary>Whether the lamp is currently lit by this application.</summary>
    public bool IsLit => _borrowedFrom is not null;

    /// <summary>Whether the connected receiver can drive this lamp at all.</summary>
    /// <remarks>
    /// A capability question answered by the driver's own allowlist rather than by this class
    /// (#304). A talker has no lamp and no command channel to drive one; the UCCM family's
    /// <c>LED:GPSL?</c> is a query in a different dialect and is not this.
    /// </remarks>
    public bool IsSupported =>
        _session.Driver.Find(Read) is not null && _session.Driver.Find(Write) is not null;

    /// <summary>
    /// Reads the lamp, remembers it, and lights it.
    /// </summary>
    /// <returns>True when the lamp is now lit by this application.</returns>
    /// <remarks>
    /// Does nothing and reports false when the receiver has no such lamp, when the link is down, or
    /// when the lamp is already borrowed — arming twice would overwrite the borrowed value with our
    /// own, which is how "the user left it on" becomes "the application decided it is off".
    /// </remarks>
    public async Task<bool> ArmAsync(CancellationToken cancellationToken = default)
    {
        if (_borrowedFrom is not null || !IsSupported || _session.Status != ConnectionStatus.Connected)
        {
            return false;
        }

        bool? found = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (found is not bool baseline)
        {
            // Unreadable. Lighting it anyway would leave nothing to put back, so the lamp is left
            // alone — §11.1's rule seen from the write side: never act on a value we do not have.
            _logger.LogInformation("The Active lamp could not be read, so it was left alone.");
            return false;
        }

        if (!await SetAsync(true, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _borrowedFrom = baseline;
        return true;
    }

    /// <summary>
    /// Puts the lamp back exactly as it was found, and forgets it.
    /// </summary>
    /// <remarks>
    /// <b>Must run before the transport is torn down</b>, which is why the session invokes it rather
    /// than a status-change subscriber: by the time <c>Disconnected</c> is raised there is no wire
    /// left to write over. The borrowed value is dropped whether or not the write lands — a retry
    /// against a link that is going away would hold the disconnect open for a lamp.
    /// </remarks>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_borrowedFrom is not bool baseline)
        {
            return;
        }

        _borrowedFrom = null;

        if (!IsSupported || _session.Status != ConnectionStatus.Connected)
        {
            return;
        }

        if (!await SetAsync(baseline, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "The Active lamp could not be put back to {State}; it is left as the application set it.",
                baseline ? "on" : "off");
        }
    }

    /// <summary>Sets the lamp directly, for §10.9's manual control.</summary>
    /// <remarks>
    /// <b>Hands ownership to the user.</b> Once a person has set the lamp themselves there is no
    /// borrowed value to return — theirs is the value, so the session stops owing one. Without this
    /// a later restore would undo what they just asked for.
    /// </remarks>
    public async Task<bool> SetManuallyAsync(bool on, CancellationToken cancellationToken = default)
    {
        if (!IsSupported || _session.Status != ConnectionStatus.Connected)
        {
            return false;
        }

        if (!await SetAsync(on, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _borrowedFrom = null;
        return true;
    }

    /// <summary>Reads the lamp, or null when the read failed or made no sense.</summary>
    /// <remarks>
    /// The receiver answers <c>+1</c> or <c>+0</c>; it accepts <c>1</c>/<c>0</c> and <c>ON</c>/
    /// <c>OFF</c> on the way in, both confirmed on the bench. Normalising here rather than echoing
    /// the reply back is deliberate: <c>:LED:ACT +1</c> has never been tried, and a restore is not
    /// the place to find out whether a spelling works.
    /// </remarks>
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

        string reply = transaction.Lines[0].Trim().TrimStart('+');

        return reply switch
        {
            "1" => true,
            "0" => false,
            _ => string.Equals(reply, "ON", StringComparison.OrdinalIgnoreCase) ? true
               : string.Equals(reply, "OFF", StringComparison.OrdinalIgnoreCase) ? false
               : null,
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
