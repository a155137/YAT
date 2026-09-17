namespace YAT.app.Graphs.Export;

// Where an image sits on a slide, in EMU (English Metric Units, 914400 to the inch - the unit PowerPoint positions
// everything in).
public readonly record struct SlideImagePlacement(long X, long Y, long Width, long Height);

// Fits a graph image onto a slide: as large as the margins allow, in its own proportions, in the middle.
public static class SlideImageLayout
{
    // 16:9 widescreen, the shape PowerPoint has used by default since 2013: 13.333 x 7.5 inches.
    public const long SlideWidth = 12_192_000;

    public const long SlideHeight = 6_858_000;

    // Half an inch of slide left clear on every side.
    public const long Margin = 457_200;

    public static SlideImagePlacement Fit(long slideWidth, long slideHeight, long margin, int imageWidth, int imageHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slideWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(slideHeight, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageHeight, 1);

        var availableWidth = slideWidth - (2 * margin);
        var availableHeight = slideHeight - (2 * margin);
        if (availableWidth < 1 || availableHeight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(margin), margin, "The margins leave no room on the slide.");
        }

        // The image keeps its proportions: it is scaled by whichever of the two fits, never stretched to fill both.
        var scale = Math.Min((double)availableWidth / imageWidth, (double)availableHeight / imageHeight);
        var width = Math.Max(1, (long)Math.Round(imageWidth * scale));
        var height = Math.Max(1, (long)Math.Round(imageHeight * scale));

        return new SlideImagePlacement((slideWidth - width) / 2, (slideHeight - height) / 2, width, height);
    }
}
