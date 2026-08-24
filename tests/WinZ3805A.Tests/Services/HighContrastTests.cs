using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #189: the guard that stops the accent ramp repainting the user's system colours had its polarity
/// backwards - it answered "not high contrast" whenever it could not tell, which is the answer that
/// removes the protection.
/// </summary>
/// <remarks>
/// Only <see cref="HighContrast.Decide"/> is tested. <c>IsEnabled</c> calls <c>user32</c> and would
/// report whatever the machine running the tests is set to, which is not a fact about this code.
/// </remarks>
public sealed class HighContrastTests
{
    private const uint HighContrastOn = 0x00000001;

    [Fact]
    public void Reports_high_contrast_when_the_flag_is_set()
    {
        Assert.True(HighContrast.Decide(queried: true, HighContrastOn));
    }

    [Fact]
    public void Reports_no_high_contrast_when_the_flag_is_clear()
    {
        Assert.False(HighContrast.Decide(queried: true, flags: 0));
    }

    /// <summary>
    /// The real machine value at the time #189 was written: 0x7E, every flag except HCF_HIGHCONTRASTON.
    /// A check that tested the whole word rather than the bit would call this high contrast.
    /// </summary>
    [Fact]
    public void Ignores_the_other_flags_in_the_word()
    {
        Assert.False(HighContrast.Decide(queried: true, flags: 0x7E));
        Assert.True(HighContrast.Decide(queried: true, flags: 0x7F));
    }

    /// <summary>
    /// <b>This is the regression.</b> An unreadable setting must leave the user's colours alone.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(HighContrastOn)]
    [InlineData(0xFFFFFFFFu)]
    public void Assumes_high_contrast_when_the_setting_cannot_be_read(uint flags)
    {
        Assert.True(HighContrast.Decide(queried: false, flags));
    }
}
