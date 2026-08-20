using System.Globalization;

namespace WinZ3805A.ViewModels;

/// <summary>
/// Which satellites the receiver is including and excluding, as it reports them.
/// </summary>
/// <param name="Included">The PRNs on the inclusion list.</param>
/// <param name="Excluded">The PRNs on the exclusion list.</param>
/// <remarks>
/// Two independent lists, not one with two states. The receiver keeps an inclusion list and an
/// exclusion list separately and a PRN can be absent from both, so a dialog that modelled them as a
/// single include/exclude flag per satellite would have to invent a state for that case.
/// </remarks>
public readonly record struct SatelliteTrackingState(
    IReadOnlySet<int> Included,
    IReadOnlySet<int> Excluded)
{
    /// <summary>The lowest PRN the constellation uses.</summary>
    public const int FirstPrn = 1;

    /// <summary>The highest.</summary>
    public const int LastPrn = 32;

    /// <summary>Nothing read yet — which is not the same as "nothing included".</summary>
    public static SatelliteTrackingState Unknown { get; } =
        new(new HashSet<int>(), new HashSet<int>());

    /// <summary>Every PRN the dialog lists.</summary>
    public static IEnumerable<int> AllPrns => Enumerable.Range(FirstPrn, LastPrn - FirstPrn + 1);
}

/// <summary>
/// Reads the receiver's answers to the tracking list queries.
/// </summary>
/// <remarks>
/// <para>
/// <b>The formats came off the receiver, not the manual.</b> Asked through the Advanced Console on
/// 20 Aug 2026, this Z3805A answered:
/// </para>
/// <code>
/// > :GPS:SAT:TRAC:IGN?
/// &lt; +0
/// > :GPS:SAT:TRAC:INCL?
/// &lt;
/// &lt; +1,+2,+3,+4,+5,+6,+7,+8,+9,+10, ... ,+32
/// </code>
/// <para>
/// Two things there that no reasonable guess would have produced. An <b>empty list answers
/// <c>+0</c></b> rather than an empty response — and since PRN 0 does not exist, that is
/// unambiguous. And a <b>non-empty list arrives on the second line</b>, the first being blank; the
/// value is not in <c>Lines[0]</c> where every other query in this application puts it.
/// </para>
/// </remarks>
public static class SatelliteTrackingParser
{
    /// <summary>
    /// Reads a PRN list from a query's response lines.
    /// </summary>
    /// <param name="lines">The response, echo already removed.</param>
    /// <remarks>
    /// Scans every line rather than assuming which one carries the list, because this receiver puts
    /// it on the second and a sibling model may not. §11.1: anything unreadable is dropped rather
    /// than guessed at, and a response with nothing readable in it is an empty list — which is what
    /// <c>+0</c> means anyway.
    /// </remarks>
    public static IReadOnlySet<int> ParsePrnList(IReadOnlyList<string>? lines)
    {
        HashSet<int> prns = [];

        if (lines is null)
        {
            return prns;
        }

        foreach (string line in lines)
        {
            foreach (string token in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int prn))
                {
                    continue;
                }

                // Zero is how this receiver says "the list is empty", and there is no PRN 0 to
                // confuse it with. Anything outside the constellation is dropped for the same
                // reason: it cannot be a satellite, so it is not one.
                if (prn is >= SatelliteTrackingState.FirstPrn and <= SatelliteTrackingState.LastPrn)
                {
                    prns.Add(prn);
                }
            }
        }

        return prns;
    }

    /// <summary>Builds the argument for a set command from a chosen list of PRNs.</summary>
    /// <remarks>
    /// Ascending and comma-separated, formatted from parsed integers rather than assembled from
    /// anything a user typed — the same rule the Advanced Console's PRN field follows, and for the
    /// same reason: nothing that could carry a command separator reaches the wire.
    /// </remarks>
    public static string FormatPrnList(IEnumerable<int> prns)
    {
        ArgumentNullException.ThrowIfNull(prns);

        return string.Join(
            ",",
            prns.Where(prn => prn is >= SatelliteTrackingState.FirstPrn and <= SatelliteTrackingState.LastPrn)
                .Distinct()
                .Order()
                .Select(prn => prn.ToString(CultureInfo.InvariantCulture)));
    }
}
