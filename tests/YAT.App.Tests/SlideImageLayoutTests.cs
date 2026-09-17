using YAT.app.Graphs.Export;

namespace YAT.App.Tests;

// Where the graph image sits on a slide: as large as the margins allow, in its own proportions, in the middle.
public class SlideImageLayoutTests
{
    private const double Tolerance = 0.001;

    private static SlideImagePlacement Fit(int imageWidth, int imageHeight) =>
        SlideImageLayout.Fit(SlideImageLayout.SlideWidth, SlideImageLayout.SlideHeight, SlideImageLayout.Margin, imageWidth, imageHeight);

    // 1
    [Fact]
    public void TheSlideIsSixteenByNine()
    {
        Assert.Equal(12_192_000, SlideImageLayout.SlideWidth);
        Assert.Equal(6_858_000, SlideImageLayout.SlideHeight);
        Assert.Equal(16d / 9d, (double)SlideImageLayout.SlideWidth / SlideImageLayout.SlideHeight, Tolerance);
    }

    // 2
    [Fact]
    public void TheExportedGraphKeepsItsProportions()
    {
        var placement = Fit(GraphExportService.ExportWidth, GraphExportService.ExportHeight);

        Assert.Equal(
            (double)GraphExportService.ExportWidth / GraphExportService.ExportHeight,
            (double)placement.Width / placement.Height,
            Tolerance);
    }

    // 3
    [Fact]
    public void TheImageIsCentred()
    {
        var placement = Fit(1600, 1000);

        Assert.Equal(SlideImageLayout.SlideWidth - placement.Width - placement.X, placement.X, 1d);
        Assert.Equal(SlideImageLayout.SlideHeight - placement.Height - placement.Y, placement.Y, 1d);
    }

    // 4
    [Theory]
    [InlineData(1600, 1000)]
    [InlineData(1600, 900)]
    [InlineData(4000, 400)]
    [InlineData(400, 4000)]
    [InlineData(1, 1)]
    [InlineData(10_000, 10_000)]
    public void EveryImageFitsInsideTheMargins(int imageWidth, int imageHeight)
    {
        var placement = Fit(imageWidth, imageHeight);

        Assert.True(placement.Width > 0 && placement.Height > 0);
        Assert.True(placement.X >= SlideImageLayout.Margin - 1, $"x = {placement.X}");
        Assert.True(placement.Y >= SlideImageLayout.Margin - 1, $"y = {placement.Y}");
        Assert.True(placement.X + placement.Width <= SlideImageLayout.SlideWidth - SlideImageLayout.Margin + 1);
        Assert.True(placement.Y + placement.Height <= SlideImageLayout.SlideHeight - SlideImageLayout.Margin + 1);
        Assert.Equal((double)imageWidth / imageHeight, (double)placement.Width / placement.Height, 0.01);
    }

    // 5
    [Fact]
    public void AWideImageIsLimitedByTheWidthAndATallOneByTheHeight()
    {
        var wide = Fit(4000, 400);
        var tall = Fit(400, 4000);

        Assert.Equal(SlideImageLayout.SlideWidth - (2 * SlideImageLayout.Margin), wide.Width);
        Assert.Equal(SlideImageLayout.SlideHeight - (2 * SlideImageLayout.Margin), tall.Height);
    }

    // 6
    [Fact]
    public void TheImageIsNeverStretched()
    {
        // A slide is wider than the export canvas, so fitting must not fill the width at the cost of the proportions.
        var placement = Fit(GraphExportService.ExportWidth, GraphExportService.ExportHeight);

        Assert.True(placement.Width < SlideImageLayout.SlideWidth - (2 * SlideImageLayout.Margin));
        Assert.Equal(SlideImageLayout.SlideHeight - (2 * SlideImageLayout.Margin), placement.Height);
    }

    // 7
    [Fact]
    public void ASlideAndAnImageBothNeedASize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SlideImageLayout.Fit(0, 100, 0, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => SlideImageLayout.Fit(100, 0, 0, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => SlideImageLayout.Fit(100, 100, -1, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => SlideImageLayout.Fit(100, 100, 0, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => SlideImageLayout.Fit(100, 100, 0, 10, 0));
    }

    // 8
    [Fact]
    public void MarginsThatLeaveNoRoomAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SlideImageLayout.Fit(1_000, 1_000, 500, 10, 10));
    }
}
