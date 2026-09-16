using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Maps data values onto the plot rectangle. Screen Y grows downward while data Y grows upward, so the Y axis is
// flipped here once and nowhere else.
//
// Boundary behaviour is deliberate and part of the contract:
//   * Minimum maps to the left (X) or bottom (Y) edge, Maximum to the right or top edge, and the midpoint to the centre.
//   * Values outside the range are extrapolated linearly and are not clamped: clipping is the renderer's decision.
//   * A non-finite value maps to NaN, never to a coordinate that looks usable. Callers skip NaN.
public sealed class GraphCoordinateTransform
{
    private readonly SKRect _plotArea;
    private readonly double _xScale;
    private readonly double _yScale;

    public GraphCoordinateTransform(GraphAxisRange xRange, GraphAxisRange yRange, SKRect plotArea)
    {
        if (!xRange.IsValid)
        {
            throw new ArgumentException("The X axis range must be finite and non-empty.", nameof(xRange));
        }

        if (!yRange.IsValid)
        {
            throw new ArgumentException("The Y axis range must be finite and non-empty.", nameof(yRange));
        }

        if (!IsFinite(plotArea) || plotArea.Width <= 0 || plotArea.Height <= 0)
        {
            throw new ArgumentException("The plot area must be a finite rectangle with a positive width and height.", nameof(plotArea));
        }

        XRange = xRange;
        YRange = yRange;
        _plotArea = plotArea;
        _xScale = plotArea.Width / xRange.Span;
        _yScale = plotArea.Height / yRange.Span;
    }

    public GraphAxisRange XRange { get; }

    public GraphAxisRange YRange { get; }

    public SKRect PlotArea => _plotArea;

    // Minimum -> PlotArea.Left, Maximum -> PlotArea.Right.
    public double ToScreenX(double x) =>
        double.IsFinite(x) ? _plotArea.Left + ((x - XRange.Minimum) * _xScale) : double.NaN;

    // Minimum -> PlotArea.Bottom, Maximum -> PlotArea.Top.
    public double ToScreenY(double y) =>
        double.IsFinite(y) ? _plotArea.Bottom - ((y - YRange.Minimum) * _yScale) : double.NaN;

    // NaN coordinates are preserved: an SKPoint with NaN is skipped by Skia and by the renderer's own checks.
    public SKPoint ToScreenPoint(double x, double y) => new((float)ToScreenX(x), (float)ToScreenY(y));

    private static bool IsFinite(SKRect rect) =>
        float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);
}
