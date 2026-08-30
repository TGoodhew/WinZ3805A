using WinZ3805A.Controls;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// P1-10's tray icon: the shapes, and the words that carry the same meaning without them.
/// </summary>
public sealed class TrayIconTests
{
    private static Rgb White { get; } = new(0xFF, 0xFF, 0xFF);

    private static byte AlphaAt(uint[] pixels, int size, int x, int y) =>
        (byte)(pixels[(y * size) + x] >> 24);

    /// <summary>How many pixels the shape covers at all, as a crude area.</summary>
    private static int Coverage(Severity severity, int size = 32) =>
        TrayIconRaster.Render(severity, size, White).Count(p => (p >> 24) > 8);

    // ------------------------------------------------------------------------ the shapes differ

    /// <summary>
    /// <b>No two severities produce the same picture.</b>
    /// </summary>
    /// <remarks>
    /// This is P1-10's real acceptance criterion. The icon is 16 pixels on a strip the user is not
    /// looking at, and if two states rasterise alike then the tray is reporting by colour alone —
    /// which is what §9.4.3 exists to prevent, and which no screenshot review would catch because
    /// the two icons are never on screen together.
    /// </remarks>
    [Fact]
    public void EverySeverityRastersToADifferentShape()
    {
        Severity[] all = Enum.GetValues<Severity>();

        foreach (Severity first in all)
        {
            foreach (Severity second in all)
            {
                if (first >= second)
                {
                    continue;
                }

                uint[] a = TrayIconRaster.Render(first, 32, White);
                uint[] b = TrayIconRaster.Render(second, 32, White);

                int differing = a.Zip(b).Count(p => p.First != p.Second);

                Assert.True(
                    differing > 32,
                    $"{first} and {second} differ in only {differing} of 1024 pixels - "
                    + "at 16 px these would be the same icon");
            }
        }
    }

    /// <summary>The filled shapes are filled at the centre, and the ring is not.</summary>
    /// <remarks>
    /// Distinctness alone would be satisfied by two shapes that differ only at their edges. This
    /// pins the one difference a user actually perceives at a glance: solid versus hollow.
    /// </remarks>
    [Theory]
    [InlineData(Severity.Success, true)]
    [InlineData(Severity.Caution, true)]
    [InlineData(Severity.Critical, true)]
    [InlineData(Severity.Neutral, false)]
    [InlineData(Severity.Info, false)]
    public void TheCentreIsFilledOnlyForTheSolidShapes(Severity severity, bool expectFilled)
    {
        const int Size = 32;
        uint[] pixels = TrayIconRaster.Render(severity, Size, White);

        // Sampled a little above centre: Info's bar passes through the exact middle, and the
        // question here is whether the shape is solid, not whether a glyph stroke happens to land.
        byte alpha = AlphaAt(pixels, Size, Size / 2, (Size / 2) - 6);

        Assert.Equal(expectFilled, alpha > 200);
    }

    /// <summary>Nothing is drawn in the corners, so the icon does not read as a filled square.</summary>
    [Theory]
    [InlineData(Severity.Neutral)]
    [InlineData(Severity.Success)]
    [InlineData(Severity.Caution)]
    [InlineData(Severity.Critical)]
    [InlineData(Severity.Info)]
    public void TheCornersAreTransparent(Severity severity)
    {
        const int Size = 32;
        uint[] pixels = TrayIconRaster.Render(severity, Size, White);

        Assert.Equal(0u, pixels[0] >> 24);
        Assert.Equal(0u, pixels[Size - 1] >> 24);
        Assert.Equal(0u, pixels[Size * (Size - 1)] >> 24);
        Assert.Equal(0u, pixels[^1] >> 24);
    }

    /// <summary>
    /// The triangle is not visibly smaller than the other shapes.
    /// </summary>
    /// <remarks>
    /// A triangle inscribed in the same circle as a hexagon covers about half the area, which at
    /// tray size reads as a smaller, quieter symbol — so "recovering" would look less significant
    /// than "unknown" through size alone. The triangle's circumradius is enlarged to compensate,
    /// and this is what says so.
    /// </remarks>
    [Fact]
    public void TheShapesAreOfComparableWeight()
    {
        int circle = Coverage(Severity.Success);
        int triangle = Coverage(Severity.Caution);
        int hexagon = Coverage(Severity.Critical);

        Assert.True(triangle > circle * 0.35, $"triangle {triangle} against circle {circle}");
        Assert.True(hexagon > circle * 0.70, $"hexagon {hexagon} against circle {circle}");
        Assert.True(triangle < circle, "the triangle should still be the lighter shape");
    }

    /// <summary>Alpha is premultiplied, or the icon gets a pale halo on a dark taskbar.</summary>
    /// <remarks>
    /// The failure this catches is subtle and looks like a rendering fault rather than a format
    /// mistake, so it is asserted rather than eyeballed: no channel may exceed the alpha.
    /// </remarks>
    [Fact]
    public void ColourIsPremultipliedByAlpha()
    {
        foreach (uint pixel in TrayIconRaster.Render(Severity.Caution, 32, White))
        {
            byte alpha = (byte)(pixel >> 24);

            Assert.True((byte)(pixel >> 16) <= alpha, "red exceeds alpha");
            Assert.True((byte)(pixel >> 8) <= alpha, "green exceeds alpha");
            Assert.True((byte)pixel <= alpha, "blue exceeds alpha");
        }
    }

    /// <summary>The requested colour is what gets drawn where the shape is solid.</summary>
    [Fact]
    public void TheFillColourIsUsed()
    {
        const int Size = 32;
        Rgb amber = new(0x8A, 0x53, 0x00);
        uint[] pixels = TrayIconRaster.Render(Severity.Success, Size, amber);

        Assert.Equal(0xFF8A5300, pixels[(Size / 2 * Size) + (Size / 2)]);
    }

    /// <summary>A size too small to draw anything meaningful is rejected rather than returned.</summary>
    [Fact]
    public void AnUnusableSizeIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TrayIconRaster.Render(Severity.Success, 2, White));

    // --------------------------------------------------------------------------- the words

    /// <summary>Every mode gets the shape §9.4.3 assigns it and a tooltip naming it.</summary>
    [Theory]
    [InlineData(ReceiverMode.Locked, Severity.Success, "Locked to GPS")]
    [InlineData(ReceiverMode.Recovering, Severity.Caution, "Recovering")]
    [InlineData(ReceiverMode.Waiting, Severity.Caution, "Waiting to recover")]
    [InlineData(ReceiverMode.Holdover, Severity.Critical, "Holdover")]
    [InlineData(ReceiverMode.PowerUp, Severity.Neutral, "Power-up")]
    [InlineData(ReceiverMode.Disconnected, Severity.Neutral, "Disconnected")]
    public void TheStateCarriesTheShapeAndTheWords(
        ReceiverMode mode,
        Severity expected,
        string words)
    {
        TrayIconState state = TrayIconStates.For(mode, "WinZ3805A");

        Assert.Equal(expected, state.Severity);
        Assert.Contains(words, state.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// A long display name is trimmed, and the state survives the trim.
    /// </summary>
    /// <remarks>
    /// Windows truncates <c>szTip</c> silently at 128 characters. Trimming the name rather than the
    /// tail means a user with a long name still learns the receiver is in holdover — which is the
    /// half of the sentence they did not already know.
    /// </remarks>
    [Fact]
    public void ALongDisplayNameIsTrimmedRatherThanTheState()
    {
        TrayIconState state = TrayIconStates.For(ReceiverMode.Holdover, new string('W', 300));

        Assert.True(state.Tooltip.Length <= TrayIconStates.MaximumTooltipLength);
        Assert.EndsWith("Holdover", state.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>An ordinary name is left exactly as given.</summary>
    [Fact]
    public void AnOrdinaryNameIsNotTrimmed() =>
        Assert.Equal(
            "WinZ3805A — Locked to GPS",
            TrayIconStates.For(ReceiverMode.Locked, "WinZ3805A").Tooltip);
}
