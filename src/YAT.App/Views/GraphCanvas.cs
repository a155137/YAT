using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Views;

// The control a graph is drawn on: it owns no graph state beyond the render model it is given, and hands the drawing to
// SkiaGraphRenderer through Avalonia's Skia canvas.
//
// Sizing and scaling are Avalonia's: the draw operation receives the control's coordinate space with the window's
// scaling already applied, so the graph is laid out in layout units and comes out sharp at any DPI without this control
// assuming that one layout unit is one physical pixel.
//
// Redrawing happens when Avalonia asks for it (a new size, an uncovered window), when the model changes, when the
// theme changes and when the graph's appearance changes. There is no timer and no continuous rendering.
//
// The theme a graph is drawn in is the application's light or dark graph theme with the graph's appearance put on it
// (Task #046, GraphAppearance.Resolve) - on screen and in every copy and export alike, which take it from here.
internal sealed class GraphCanvas : Control
{
    public static readonly StyledProperty<GraphRenderModel?> ModelProperty =
        AvaloniaProperty.Register<GraphCanvas, GraphRenderModel?>(nameof(Model));

    public static readonly StyledProperty<IGraphPlotRenderer?> PlotProperty =
        AvaloniaProperty.Register<GraphCanvas, IGraphPlotRenderer?>(nameof(Plot));

    public static readonly StyledProperty<GraphAppearanceOptions> AppearanceProperty =
        AvaloniaProperty.Register<GraphCanvas, GraphAppearanceOptions>(
            nameof(Appearance), GraphAppearanceOptions.Default);

    private readonly SkiaGraphRenderer _renderer = new();

    // The theme last resolved, and what it was resolved from: the same theme for the same variant and appearance, so a
    // redraw of an unchanged graph is recognised as one.
    private (ThemeVariant? Variant, GraphAppearanceOptions? Appearance, GraphTheme? Theme) _resolved;

    static GraphCanvas()
    {
        AffectsRender<GraphCanvas>(ModelProperty, PlotProperty, AppearanceProperty);
    }

    public GraphCanvas()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public GraphRenderModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    // What the graph type draws inside the plot area (the scatter markers, later the bars and curves of other graphs).
    public IGraphPlotRenderer? Plot
    {
        get => GetValue(PlotProperty);
        set => SetValue(PlotProperty, value);
    }

    // How the graph looks: its colours and grid, put on the theme it is drawn in. The default changes nothing.
    public GraphAppearanceOptions Appearance
    {
        get => GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    // The theme this graph is being drawn in right now, its appearance put on it. An export takes it once, so that a
    // theme or appearance change while a file is being written cannot change what was exported.
    public GraphTheme CurrentTheme
    {
        get
        {
            var variant = ActualThemeVariant;
            var appearance = Appearance;
            if (_resolved.Theme is not { } theme
                || _resolved.Variant != variant
                || !Equals(_resolved.Appearance, appearance))
            {
                theme = GraphAppearance.Resolve(ThemeFor(variant), appearance);
                _resolved = (variant, appearance, theme);
            }

            return theme;
        }
    }

    // The title of the graph at a point of this control, or null: only a title the graph shows, and only near its text,
    // laid out exactly as it is drawn at this control's size in its theme (see GraphLabelHitTest).
    internal GraphLabelField? LabelAt(Point point)
    {
        if (Model is not { } model)
        {
            return null;
        }

        var bounds = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
        var labels = SkiaGraphRenderer.LabelGeometry(model, bounds, CurrentTheme);
        return GraphLabelHitTest.Find(labels, new SKPoint((float)point.X, (float)point.Y));
    }

    // The axis of the graph at a point of this control, or null (Task #052): laid out exactly as it is drawn at this
    // control's size in its theme (see GraphAxisHitTest). Whether that axis can be edited is the graph type's business.
    internal GraphAxisField? AxisAt(Point point)
    {
        if (Model is not { } model)
        {
            return null;
        }

        var bounds = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
        return GraphAxisHitTest.Find(SkiaGraphRenderer.AxisGeometry(model, bounds, CurrentTheme), new SKPoint((float)point.X, (float)point.Y));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Model is not { } model)
        {
            return;
        }

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        context.Custom(new GraphDrawOperation(bounds, model, Plot, CurrentTheme, _renderer));
    }

    private static GraphTheme ThemeFor(ThemeVariant variant) =>
        variant == ThemeVariant.Dark ? GraphThemes.Dark : GraphThemes.Light;

    // An immutable snapshot of what to draw. The operation runs on the render thread, so it holds nothing that the UI
    // thread can change underneath it, and it compares equal only to a snapshot of the same size, model and theme -
    // otherwise a resized or re-themed graph could keep showing the previous frame.
    private sealed class GraphDrawOperation : ICustomDrawOperation
    {
        private readonly GraphRenderModel _model;
        private readonly IGraphPlotRenderer? _plot;
        private readonly GraphTheme _theme;
        private readonly SkiaGraphRenderer _renderer;

        public GraphDrawOperation(
            Rect bounds,
            GraphRenderModel model,
            IGraphPlotRenderer? plot,
            GraphTheme theme,
            SkiaGraphRenderer renderer)
        {
            Bounds = bounds;
            _model = model;
            _plot = plot;
            _theme = theme;
            _renderer = renderer;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            // Every platform YAT runs on draws through Skia; if some other backend is ever in use, the graph area stays
            // blank instead of failing the frame.
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                return;
            }

            using var lease = feature.Lease();
            _renderer.Render(lease.SkCanvas, _model, new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height), _theme, _plot);
        }

        public bool Equals(ICustomDrawOperation? other) =>
            other is GraphDrawOperation operation
            && operation.Bounds == Bounds
            && ReferenceEquals(operation._model, _model)
            && ReferenceEquals(operation._plot, _plot)
            && operation._theme == _theme;

        public void Dispose()
        {
        }
    }
}
