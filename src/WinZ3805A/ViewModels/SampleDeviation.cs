namespace WinZ3805A.ViewModels;

/// <summary>
/// The spread of a set of readings, and how much of a window they actually cover (§10.7).
/// </summary>
/// <remarks>
/// <para>
/// §10.7's wireframe asks for <c>σ (1 h)</c>. When the Timing page was built nothing kept an hour —
/// the only history was <c>ReceiverStateStore</c>'s 60-sample ring — so the page computed the
/// deviation of whatever that held and said so, promising the hour "arrives with P1 persistence".
/// </para>
/// <para>
/// <b>P1 persistence arrived, and the sentence did not change.</b> The same page reads twelve
/// thousand rows out of <c>trend.db</c> to draw its charts while printing a deviation over the last
/// thirteen seconds and blaming the absence of a trend store. That was only ever going to be caught
/// by looking at the page, which is what found it.
/// </para>
/// <para>
/// Separated from the page so the arithmetic can be held against known answers — a deviation is
/// exactly the kind of figure that looks plausible while being wrong by a factor of the sample
/// count.
/// </para>
/// </remarks>
public static class SampleDeviation
{
    /// <summary>Below this many readings a deviation is arithmetic without meaning.</summary>
    /// <remarks>Two points define a line, so three is the first count that describes a spread.</remarks>
    public const int MinimumSamples = 3;

    /// <summary>
    /// The sample standard deviation of <paramref name="values"/>, or null when there are too few.
    /// </summary>
    /// <remarks>
    /// <b>Sample, not population</b> — divided by n−1. These are a sample of the receiver's
    /// behaviour rather than the whole of it, and at the small counts this runs on the difference is
    /// not academic: over ten readings the two answers differ by about five per cent.
    /// </remarks>
    public static double? Of(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < MinimumSamples)
        {
            return null;
        }

        double mean = 0;
        foreach (double value in values)
        {
            mean += value;
        }

        mean /= values.Count;

        double sumOfSquares = 0;
        foreach (double value in values)
        {
            double difference = value - mean;
            sumOfSquares += difference * difference;
        }

        return Math.Sqrt(sumOfSquares / (values.Count - 1));
    }

    /// <summary>
    /// How to describe the window a deviation was taken over, given what was actually found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Named from the readings, not from the request.</b> Asking the trend store for an hour and
    /// then saying "σ over the last hour" would be a claim about the receiver rather than about the
    /// data: the application is not always running, and an hour of wall clock routinely holds four
    /// minutes of readings. §9.11's honesty rule applies to a caption as much as to a readout.
    /// </para>
    /// <para>
    /// The count is given as well as the span because the two answer different questions — a
    /// deviation over 3,000 readings spread across an hour and one over 12 is not the same figure,
    /// and the span alone does not distinguish them.
    /// </para>
    /// </remarks>
    /// <param name="count">How many readings the deviation used.</param>
    /// <param name="span">The time from the first of them to the last.</param>
    public static string Describe(int count, TimeSpan span)
    {
        if (count < MinimumSamples)
        {
            return count switch
            {
                0 => "no readings yet",
                1 => "one reading so far",
                _ => $"{count} readings so far",
            };
        }

        string period = span switch
        {
            { TotalMinutes: >= 90 } => $"{Math.Floor(span.TotalHours * 10) / 10:0.0} hours",
            { TotalMinutes: >= 2 } => $"{Math.Floor(span.TotalMinutes)} minutes",
            _ => $"{Math.Floor(span.TotalSeconds)} seconds",
        };

        return $"{count:N0} readings over {period}";
    }
}
