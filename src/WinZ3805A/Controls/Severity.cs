namespace WinZ3805A.Controls;

/// <summary>
/// The five severity levels §9.4.3 defines, and the only vocabulary in which the application
/// expresses "how bad is this".
/// </summary>
/// <remarks>
/// <para>
/// There are five. Do not add a sixth: each one is a fixed triple of colour token,
/// <c>Path</c> shape, and glyph in the §9.4.3 table, and a value without a shape of its own
/// silently degrades to colour-only meaning — which is the thing the whole scheme exists to
/// prevent.
/// </para>
/// <para>
/// <c>SeverityPill</c> takes this enum and never a brush. That is what makes the
/// colour-blindness guarantee structural rather than something each page has to remember: a
/// caller cannot pass "red" because there is no way to say it. The control is named in plain text
/// rather than with a cref, because this file is also compiled into the headless test assembly
/// where it does not exist.
/// </para>
/// </remarks>
public enum Severity
{
    /// <summary>Unknown, powering up, or not applicable. Ring outline.</summary>
    Neutral = 0,

    /// <summary>Locked, valid, test passed. Filled circle.</summary>
    Success,

    /// <summary>Recovering, waiting, reduced accuracy, or stale data. Triangle.</summary>
    Caution,

    /// <summary>Holdover, hardware failure, or disconnected with an error. Hexagon.</summary>
    Critical,

    /// <summary>A neutral advisory such as the week-rollover notice. Circled i.</summary>
    Info,
}

/// <summary>The word a severity is reported by outside the pixels (#728).</summary>
public static class SeverityStatus
{
    /// <summary>
    /// What a pill reports as its UI Automation item status: the severity's own name, lower case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>So that a pill's meaning can be read without its colour.</b> Its name is its label
    /// ("FFOM 3"), which says nothing about whether that is good: until this, neither a screen
    /// reader nor the QA pass could tell a red hexagon from a green circle without the pixels. The
    /// QA pass's judging gate had only the pixels, and a pill changing from red to green moved 0.33 %
    /// of a photograph, under its 0.5 % threshold, so #724's whole change was filed as unchanged.
    /// </para>
    /// <para>
    /// The enum's names, not prose, because the harness compares these exactly and a reworded
    /// string would read as every pill changing at once. They are also short, plain words a screen
    /// reader can say after the label.
    /// </para>
    /// </remarks>
    public static string Word(Severity severity) => severity switch
    {
        Severity.Neutral => "neutral",
        Severity.Success => "success",
        Severity.Caution => "caution",
        Severity.Critical => "critical",
        Severity.Info => "info",
        _ => "neutral",
    };
}
