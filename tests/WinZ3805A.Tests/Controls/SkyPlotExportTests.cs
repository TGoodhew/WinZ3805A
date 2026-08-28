using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// The parts of §10.5's image export (#47) that can be wrong without anyone noticing.
/// </summary>
/// <remarks>
/// The picture itself is checked by eye, and by <c>docs/manual-qa.md</c> in three themes. What is
/// checked here is everything a reader of a filed calibration record would rely on and could not
/// spot as wrong from the image alone: the timestamp's zone, the counts, and the mask the plot was
/// drawn under.
/// </remarks>
public class SkyPlotExportTests
{
    private static readonly DateTimeOffset Captured =
        new(2026, 8, 28, 14, 3, 22, TimeSpan.Zero);

    [Fact]
    public void Caption_states_the_time_in_utc()
    {
        string caption = SkyPlotExport.Caption("WinZ3805A", Captured, 9, 2, 10);

        Assert.Contains("2026-08-28 14:03:22 UTC", caption);
    }

    [Fact]
    public void Caption_converts_a_local_capture_time_to_utc()
    {
        // The record is compared against another site's a year later, by someone who does not know
        // which zone the exporting machine was in. Writing the local time and labelling it UTC would
        // be the worst possible failure here, because it is unfalsifiable from the file.
        DateTimeOffset local = new(2026, 8, 28, 15, 3, 22, TimeSpan.FromHours(1));

        Assert.Contains("2026-08-28 14:03:22 UTC", SkyPlotExport.Caption("WinZ3805A", local, 9, 2, 10));
    }

    [Fact]
    public void Caption_carries_the_elevation_mask()
    {
        // The same sky under a 10 degree mask and a 25 degree mask makes two legitimate and
        // different pictures. A record that does not say which cannot be compared with anything.
        Assert.Contains("elevation mask 10°", SkyPlotExport.Caption("WinZ3805A", Captured, 9, 2, 10));
    }

    [Fact]
    public void Caption_omits_the_mask_when_it_is_unknown()
    {
        string caption = SkyPlotExport.Caption("WinZ3805A", Captured, 9, 2, elevationMaskDegrees: null);

        Assert.DoesNotContain("elevation mask", caption);
        Assert.DoesNotContain("—", caption);
    }

    [Fact]
    public void Caption_counts_both_populations()
    {
        string caption = SkyPlotExport.Caption("WinZ3805A", Captured, 9, 2, 10);

        Assert.Contains("9 satellites tracked", caption);
        Assert.Contains("2 more predicted in view", caption);
    }

    [Fact]
    public void Caption_drops_the_predicted_clause_when_there_are_none()
    {
        Assert.DoesNotContain("predicted", SkyPlotExport.Caption("WinZ3805A", Captured, 9, 0, 10));
    }

    [Fact]
    public void Caption_does_not_say_one_satellites()
    {
        string caption = SkyPlotExport.Caption("WinZ3805A", Captured, 1, 0, 10);

        Assert.Contains("1 satellite tracked", caption);
        Assert.DoesNotContain("1 satellites", caption);
    }

    [Fact]
    public void Caption_uses_the_display_name_it_is_given()
    {
        // §6.3: the product name is read from the package at run time and never hard-coded, which
        // includes not hard-coding it here. The test passes an unrelated name on purpose.
        Assert.StartsWith("Some Other Name ·", SkyPlotExport.Caption("Some Other Name", Captured, 9, 2, 10));
    }

    [Fact]
    public void File_name_is_sortable_and_marked_utc()
    {
        Assert.Equal(
            "WinZ3805A sky plot 2026-08-28 1403Z",
            SkyPlotExport.SuggestedFileName("WinZ3805A", Captured));
    }

    [Fact]
    public void File_name_carries_the_minute_so_a_move_produces_distinct_files()
    {
        // Several exports minutes apart, while an antenna is being moved, is the normal case rather
        // than the edge one - it is how #185's obstruction argument was made.
        string first = SkyPlotExport.SuggestedFileName("WinZ3805A", Captured);
        string second = SkyPlotExport.SuggestedFileName("WinZ3805A", Captured.AddMinutes(7));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void File_name_strips_characters_windows_rejects()
    {
        string name = SkyPlotExport.SuggestedFileName("Lab: receiver/2", Captured);

        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('/', name);
        Assert.StartsWith("Lab receiver 2 sky plot", name);
    }

    [Theory]
    [InlineData(360, 520, 3)]
    [InlineData(700, 800, 3)]
    [InlineData(1000, 1000, 2)]
    [InlineData(1400, 900, 1)]
    public void Scale_is_the_largest_whole_multiple_inside_the_budget(
        double width, double height, int expected) =>
        Assert.Equal(expected, SkyPlotExport.ScaleFor(width, height));

    [Fact]
    public void Scale_is_capped_at_the_documented_maximum() =>
        // The 3 the table above uses, asserted once rather than restated in each row.
        Assert.Equal(3, SkyPlotExport.MaximumScale);

    [Fact]
    public void Scale_never_exceeds_the_edge_budget()
    {
        // RenderTargetBitmap returns a silently truncated bitmap rather than throwing when it is
        // asked for more pixels than the hardware allows, so overshooting here produces a cropped
        // export that still opens and still looks plausible.
        for (int edge = 1; edge <= 3000; edge += 7)
        {
            int scale = SkyPlotExport.ScaleFor(edge, edge);

            Assert.True(
                scale == 1 || edge * scale <= SkyPlotExport.MaximumEdgePixels,
                $"{edge} px scaled by {scale} exceeds the budget");
        }
    }

    [Fact]
    public void Scale_falls_back_to_one_for_a_card_already_over_budget()
    {
        // Exported at the size it is, not refused: a very large window is a reason for a plain
        // capture, not for a button that stops working.
        Assert.Equal(1, SkyPlotExport.ScaleFor(4000, 4000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Scale_survives_a_card_that_has_not_been_measured(double edge) =>
        Assert.Equal(1, SkyPlotExport.ScaleFor(edge, edge));
    [Fact]
    public void Flatten_makes_every_pixel_opaque()
    {
        // The defect this exists to stop: a card fill that resolves to a stock Fluent colour is
        // semi-transparent, so the export carried an alpha channel and composited over whatever the
        // viewer used. It looked right in the one the exporter tried and washed out in the next.
        byte[] pixels = [0, 0, 0, 0, 40, 40, 40, 128, 10, 20, 30, 255];

        SkyPlotExport.Flatten(pixels, 0xF3, 0xF3, 0xF3);

        Assert.Equal(255, pixels[3]);
        Assert.Equal(255, pixels[7]);
        Assert.Equal(255, pixels[11]);
    }

    [Fact]
    public void Flatten_leaves_a_fully_transparent_pixel_as_the_background()
    {
        byte[] pixels = [0, 0, 0, 0];

        SkyPlotExport.Flatten(pixels, 0x20, 0x21, 0x22);

        Assert.Equal([0x20, 0x21, 0x22, 255], pixels);
    }

    [Fact]
    public void Flatten_leaves_an_opaque_pixel_untouched()
    {
        // Anything else would tint the plot itself, which is the content of the record.
        byte[] pixels = [10, 20, 30, 255];

        SkyPlotExport.Flatten(pixels, 0xF3, 0xF3, 0xF3);

        Assert.Equal([10, 20, 30, 255], pixels);
    }

    [Fact]
    public void Flatten_treats_the_source_as_premultiplied()
    {
        // RenderTargetBitmap produces premultiplied BGRA, so the composite is src + dst * (1 - a)
        // with no division first. Un-premultiplied maths over the same bytes does not fail loudly -
        // it yields an image that is merely a little washed out, which reads as a rendering quirk.
        // Half-alpha black over white must land on mid grey, not on white.
        byte[] pixels = [0, 0, 0, 128];

        SkyPlotExport.Flatten(pixels, 255, 255, 255);

        Assert.InRange(pixels[0], 126, 128);
        Assert.InRange(pixels[1], 126, 128);
        Assert.InRange(pixels[2], 126, 128);
    }

    [Fact]
    public void Flatten_never_overflows_a_channel()
    {
        // A premultiplied source channel can already be at full scale; adding any background to it
        // must clamp rather than wrap, and a wrap here would show as bright speckle on light areas.
        byte[] pixels = [255, 255, 255, 1];

        SkyPlotExport.Flatten(pixels, 255, 255, 255);

        Assert.Equal([255, 255, 255, 255], pixels);
    }

    [Fact]
    public void Flatten_ignores_a_trailing_partial_pixel()
    {
        byte[] pixels = [0, 0, 0, 0, 9, 9];

        SkyPlotExport.Flatten(pixels, 1, 2, 3);

        Assert.Equal([9, 9], pixels[4..]);
    }
}
