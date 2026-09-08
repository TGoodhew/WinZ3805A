using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;

namespace WinZ3805A.Views;

/// <summary>
/// What a page may offer, given the driver the session actually selected (#304).
/// </summary>
/// <remarks>
/// <para>
/// Every Details page resolves its tier C commands through the driver, and until this existed it did
/// so with <c>CommandConfirmation.Require</c>, which <b>throws</b> for a driver that lacks the
/// mnemonic. That was right while one family shipped — a missing entry was a packaging bug and
/// failing loudly found it — and it stopped being right the day a second family arrived. The NMEA
/// driver's catalog is reads only: it has none of the thirteen mnemonics the pages ask for, so
/// navigating to §10.5, §10.7 or §10.8 with a talker connected threw before anything drew.
/// </para>
/// <para>
/// <b>Absent means disabled and explained, never hidden.</b> §9.11 puts it directly — a command that
/// is visible, enabled and silently does nothing is worse than one greyed out, because the user
/// cannot tell it from a failure. Hiding is worse still: a page that quietly loses half its controls
/// looks either broken or, more dangerously, complete. So the control stays where it is, disabled,
/// and one sentence on its card says the receiver has no command for it.
/// </para>
/// <para>
/// <b><c>Require</c> keeps its throw, and becomes an assertion rather than a lookup.</b> Once a
/// control is gated, reaching its handler with the command absent is a programming error — the gate
/// failed — and that is exactly what should fail loudly. The change is that gating happens first.
/// </para>
/// </remarks>
public static class Capability
{
    /// <summary>Whether the connected receiver's driver offers every one of these commands.</summary>
    /// <remarks>
    /// All of them, because a control usually needs a set: §10.5's Manage dialog sends five, and
    /// offering it with four would put the user in front of a button that fails halfway.
    /// </remarks>
    public static bool Offers(IReceiverDriver? driver, params string[] mnemonics)
    {
        ArgumentNullException.ThrowIfNull(mnemonics);

        return driver is not null && Array.TrueForAll(mnemonics, m => driver.Find(m) is not null);
    }

    /// <summary>The parameter spec behind a field, or null when the receiver has no such command.</summary>
    /// <remarks>
    /// Null rather than a throw, so a page can build its validator with no bounds and disable the
    /// field instead of failing to navigate. <c>FirstOrDefault</c> and not <c>[0]</c>: a catalogued
    /// command that lost its parameter would otherwise turn a navigation into an index-out-of-range,
    /// which is the same class of failure one layer down.
    /// </remarks>
    public static ParameterSpec? SpecFor(IReceiverDriver? driver, string mnemonic) =>
        driver?.Find(mnemonic)?.Parameters.FirstOrDefault();

    /// <summary>
    /// The sentence a card shows when the connected receiver cannot do something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Names the family, because "this receiver" alone invites the reading that the application is
    /// broken. <i>The NMEA 0183 driver has no command for it</i> says where the limit is: in what the
    /// receiver speaks, not in what has been built.
    /// </para>
    /// <para>
    /// §9.11's copy rules: no apology, and it says what is true rather than what is missing. A
    /// talker that cannot be told to hold over is not a degraded SmartClock — it is a different
    /// instrument, and the page should read like one.
    /// </para>
    /// </remarks>
    /// <param name="driver">The driver the session selected.</param>
    /// <param name="what">What the control would have done, as a noun phrase — "an elevation mask".</param>
    public static string NotOffered(IReceiverDriver? driver, string what) =>
        driver is null
            ? $"Not connected, so {what} cannot be set."
            : $"This receiver does not support {what}. The {driver.Family} driver has no command for it.";

    // ---- The read side, added by #435 -----------------------------------------------------------

    /// <summary>Whether the connected receiver's family can ever supply this reading.</summary>
    /// <remarks>
    /// True while disconnected, deliberately. "This receiver will never tell you" is a claim about
    /// a family, and with no driver selected there is no family to make it about — so a page falls
    /// back to §11.1's em dash, which is what an unknown value has always looked like.
    /// </remarks>
    public static bool Reports(IReceiverDriver? driver, ReceiverReading reading) =>
        driver is null || driver.Reports(reading);

    /// <summary>
    /// The sentence a card shows for a reading the connected receiver can never supply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not <see cref="NotOffered"/>'s wording.</b> That one ends "the driver has no
    /// command for it", which is right for a control that would have <i>sent</i> something and
    /// wrong for a value: it invites the reading that the number exists and we neglected to ask.
    /// The distinction is the whole point of #435 — the interface could not previously tell
    /// "unread" from "unknowable", and two sentences that differ only in tone would not fix that.
    /// </para>
    /// <para>
    /// §9.11's copy rules apply as they do to the command side: no apology, and it says what is
    /// true rather than what is missing. A talker with no oscillator to discipline is not a
    /// degraded SmartClock; it is a different instrument, and the page should read like one.
    /// </para>
    /// </remarks>
    /// <param name="driver">The driver the session selected.</param>
    /// <param name="what">The reading, as a noun phrase — "a time figure of merit".</param>
    public static string NotReported(IReceiverDriver? driver, string what) =>
        driver is null
            ? $"Not connected, so {what} is unknown."
            : $"This receiver does not report {what}. The {driver.Family} protocol does not carry it.";
}
