using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #290 — the log must report which ramp was applied, not which was asked for.
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because the failing case cannot be produced by running the application.</b> When
/// the Windows accent reads successfully the preference and the outcome agree, so a log line
/// reporting either is indistinguishable. Only a read that fails separates them, and a read cannot
/// be made to fail on demand — which is exactly why the defect survived review from 21 Aug 2026
/// (#165) until 29 Aug (#290), and why asserting it here is the only way the fix stays fixed.
/// </para>
/// <para>
/// The defect was not a missing log line but a <b>false</b> one: a user who had chosen the Windows
/// accent and hit a failed read was told <c>source Windows</c> while the brand ramp was on screen.
/// A missing line costs a search; a line asserting the read succeeded sends the reader downstream
/// into the half of the code that worked.
/// </para>
/// </remarks>
public class AccentSourceTests
{
    [Fact]
    public void ChoosingTheWindowsAccentAndReadingItReportsWindows() =>
        Assert.Equal(AccentSource.Windows, AccentSources.For(useSystemAccent: true, systemRampWasRead: true));

    /// <summary>The whole of #290, in one assertion.</summary>
    /// <remarks>
    /// The user asked for the Windows accent and the read failed, so the brand ramp is what is on
    /// screen. Reporting anything but <see cref="AccentSource.BuiltIn"/> here is the defect.
    /// </remarks>
    [Fact]
    public void ChoosingTheWindowsAccentAndFailingToReadItReportsBuiltIn() =>
        Assert.Equal(AccentSource.BuiltIn, AccentSources.For(useSystemAccent: true, systemRampWasRead: false));

    [Fact]
    public void NotChoosingTheWindowsAccentReportsBuiltInEvenWhenItCouldBeRead() =>
        Assert.Equal(AccentSource.BuiltIn, AccentSources.For(useSystemAccent: false, systemRampWasRead: true));

    [Fact]
    public void NotChoosingItAndNotReadingItReportsBuiltIn() =>
        Assert.Equal(AccentSource.BuiltIn, AccentSources.For(useSystemAccent: false, systemRampWasRead: false));

    /// <summary>
    /// The outcome never simply restates the preference.
    /// </summary>
    /// <remarks>
    /// Written as a property rather than a fourth example: the defect was a function that happened
    /// to equal <c>UseSystemAccent</c> in every case anyone tried. This asserts that the two are not
    /// the same function, which is the claim the fix actually makes.
    /// </remarks>
    [Fact]
    public void TheSourceIsNotJustThePreferenceRestated()
    {
        AccentSource asked = AccentSources.For(useSystemAccent: true, systemRampWasRead: true);
        AccentSource got = AccentSources.For(useSystemAccent: true, systemRampWasRead: false);

        Assert.NotEqual(asked, got);
    }

    /// <summary>
    /// <see cref="AccentSource.Unchanged"/> is never returned by the rule.
    /// </summary>
    /// <remarks>
    /// It is reserved for the paths that apply nothing at all — high contrast, and a token
    /// dictionary that could not be read — which <c>AccentPalette</c> returns before it ever asks
    /// which ramp won. A rule that produced it would mean a ramp was applied and also not applied.
    /// </remarks>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TheRuleNeverReportsUnchanged(bool useSystemAccent, bool read) =>
        Assert.NotEqual(AccentSource.Unchanged, AccentSources.For(useSystemAccent, read));
}
