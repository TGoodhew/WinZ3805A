namespace WinZ3805A.Device.Transport;

/// <summary>
/// The prompt that ends a transaction, as a grammar rather than a literal (§7.2, #470).
/// </summary>
/// <remarks>
/// <para>
/// §7.2 specifies one prompt because it scopes itself to one family: the SmartClock answers behind
/// <c>scpi &gt; </c>, or <c>E-nnn&gt; </c> while its error queue is not empty. That word was a
/// constant in <see cref="LineProtocol"/> until a second prompted family reached the bench, and a
/// constant is the wrong shape for it — a Trimble UCCM-P prompts <c>UCCM-P &gt;</c>, so every
/// transaction with one ran to its full timeout with the answer already in hand (#470).
/// </para>
/// <para>
/// <b>What varies between families is the word, not the shape.</b> Both prompts are a token, then
/// optional spaces, then <c>&gt;</c>, and both carry no line ending — which is why the matcher below
/// is the SmartClock's, generalised over its vocabulary rather than rewritten. Two details of that
/// shape are worth keeping in view because §7.2 states one of them too strongly:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>The trailing space is optional and always was.</b> §7.2 says the only reliable invariant is
/// that a prompt ends with <c>&gt;</c> followed by a space. On a UCCM-P the byte after <c>&gt;</c>
/// is <c>C5</c>, the first byte of an unsolicited binary time code. The matcher has never required
/// the space, so nothing needed changing here — but do not restore the requirement.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Matching stays narrow rather than "anything ending in <c>&gt;</c>".</b> The tail is also where
/// a half-arrived response line sits, and a status screen line containing a bracket must not end the
/// transaction. That is why this is a vocabulary of words and not a wildcard.
/// </description>
/// </item>
/// </list>
/// </remarks>
public sealed record PromptGrammar
{
    /// <summary>What the prompt shows instead of a <see cref="Words"/> entry while the error queue is not empty (§7.2).</summary>
    private const string ErrorPromptPrefix = "E-";

    /// <summary>
    /// The SmartClock family's grammar (§7.2), and the default for a driver that does not say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both forms were observed on a Z3805A running firmware 1.01.03-A, and the literal
    /// <c>"scpi&gt; "</c> that §7.2 used to give matches neither. The ordinary prompt is
    /// <c>"scpi &gt; "</c>, with a space <i>before</i> the bracket. While the error queue is not
    /// empty the word is replaced entirely — <c>"E-230&gt; "</c> and the like, with no space — so the
    /// prompt doubles as the queue indicator rather than as a verdict on the last command.
    /// </para>
    /// <para>
    /// A command the receiver rejects answers with <i>only</i> that prompt, which is why a protocol
    /// looking for a literal waits out its full timeout on every failed command and then does it
    /// again on the next one.
    /// </para>
    /// </remarks>
    public static PromptGrammar SmartClock { get; } = new()
    {
        Words = ["scpi"],
        AllowsErrorQueuePrompt = true,
    };

    /// <summary>
    /// The words an ordinary prompt may be built from, most likely first.
    /// </summary>
    public required IReadOnlyList<string> Words { get; init; }

    /// <summary>
    /// Whether this family also prompts <c>E-nnn&gt;</c> while its error queue is not empty (§7.2).
    /// </summary>
    /// <remarks>
    /// True for the SmartClock, where it is measured. A family that has not been observed doing it
    /// should leave this false: the form is a claim about the receiver, and a grammar that accepts a
    /// prompt the receiver never sends reports an error status nobody can act on.
    /// </remarks>
    public bool AllowsErrorQueuePrompt { get; init; }

    /// <summary>
    /// One grammar accepting everything these do, in the order given.
    /// </summary>
    /// <remarks>
    /// The connect sequence probes <c>*IDN?</c> before any driver has been selected — selection is
    /// what the answer decides — so the walk has to recognise the prompt of every family it might be
    /// talking to. Registration order is preserved and duplicates are dropped, so adding a driver can
    /// only append words; it cannot change which word an existing family matches on.
    /// </remarks>
    /// <param name="grammars">The grammars to accept, in priority order.</param>
    /// <returns>The union, or <see cref="SmartClock"/> when <paramref name="grammars"/> is empty.</returns>
    public static PromptGrammar Union(IEnumerable<PromptGrammar> grammars)
    {
        ArgumentNullException.ThrowIfNull(grammars);

        List<string> words = [];
        bool errorQueue = false;

        foreach (PromptGrammar grammar in grammars)
        {
            errorQueue |= grammar.AllowsErrorQueuePrompt;

            foreach (string word in grammar.Words)
            {
                if (!words.Contains(word, StringComparer.Ordinal))
                {
                    words.Add(word);
                }
            }
        }

        return words.Count is 0
            ? SmartClock
            : new PromptGrammar { Words = words, AllowsErrorQueuePrompt = errorQueue };
    }

    /// <summary>
    /// Matches a complete prompt at the start of <paramref name="tail"/>.
    /// </summary>
    /// <param name="tail">The remainder of the buffer after the last line ending.</param>
    /// <param name="promptLength">How many characters the prompt occupies.</param>
    /// <param name="status">
    /// The error token when the receiver is reporting one — <c>E-230</c> and the like — or null for
    /// an ordinary prompt.
    /// </param>
    /// <returns>True when <paramref name="tail"/> begins with a complete prompt.</returns>
    public bool TryMatch(ReadOnlySpan<char> tail, out int promptLength, out string? status)
    {
        promptLength = 0;
        status = null;

        int index = 0;
        while (index < tail.Length && tail[index] == ' ')
        {
            index++;
        }

        int tokenStart = index;
        string? matched = null;

        foreach (string word in Words)
        {
            if (tail[index..].StartsWith(word, StringComparison.Ordinal))
            {
                matched = word;
                index += word.Length;
                break;
            }
        }

        if (matched is null)
        {
            if (!AllowsErrorQueuePrompt || !tail[index..].StartsWith(ErrorPromptPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            index += ErrorPromptPrefix.Length;
            int digits = index;
            while (index < tail.Length && char.IsAsciiDigit(tail[index]))
            {
                index++;
            }

            if (index == digits)
            {
                // "E-" with nothing after it yet: either a truncated prompt or not one at all. Both
                // mean wait, so neither needs distinguishing.
                return false;
            }
        }

        int tokenEnd = index;
        while (index < tail.Length && tail[index] == ' ')
        {
            index++;
        }

        if (index >= tail.Length || tail[index] != '>')
        {
            return false;
        }

        index++;
        if (index < tail.Length && tail[index] == ' ')
        {
            index++;
        }

        promptLength = index;
        status = matched is null ? tail[tokenStart..tokenEnd].ToString() : null;
        return true;
    }
}
