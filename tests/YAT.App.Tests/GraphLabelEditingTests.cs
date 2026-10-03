using System.Reflection;
using SkiaSharp;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;
using YAT.app.Views;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Editing the labels of a drawn graph (Task #040.2): the graph keeps its frame from before the labels, so other labels
// go onto it without the data; the titles are picked where they are drawn and nowhere else; the dialog opens on the
// picked title and a cancelled edit changes nothing.
public class GraphLabelEditingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 400;

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)) + ((i % 7) * 0.003))];
    private static readonly double[] Reg2 = [.. Enumerable.Range(0, Count).Select(i => 14.9 + (0.15 * Math.Cos(i * 0.23)))];

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static StringGroupData Lots() => new(Column("Lot", WorksheetDataType.String), Enumerable.Range(0, Count).Select(i => (string?)$"Lot {i % 3}").ToArray());

    private static GraphLabelOptions Labels(GraphLabelOption? title = null, GraphLabelOption? x = null, GraphLabelOption? y = null) =>
        new(title ?? GraphLabelOption.Auto, x ?? GraphLabelOption.Auto, y ?? GraphLabelOption.Auto);

    private static readonly GraphLabelOptions AllHidden = Labels(GraphLabelOption.Hidden, GraphLabelOption.Hidden, GraphLabelOption.Hidden);

    private static readonly GraphLabelOptions AllCustom =
        Labels(GraphLabelOption.Custom("Wafer thickness"), GraphLabelOption.Custom("Thickness (um)"), GraphLabelOption.Custom("Wafers"));

    // A graph of one type presented as the graph preparation presents it: with its statistics panel, a specification
    // where it has one, and the given labels.
    internal sealed record Presented(GraphPresentationState Graph, GraphData Data, GraphRenderModel BuilderFrame, IGraphPlotRenderer Plot);

    internal static Presented Present(GraphType type, GraphLabelOptions? labels = null, HistogramOptions? histogram = null, bool grouped = true)
    {
        GraphData data;
        GraphRenderModel frame;
        IGraphPlotRenderer plot;
        switch (type)
        {
            case GraphType.ScatterPlot:
            {
                var scatter = new ScatterGraphData(Guid.Empty, Column("Reg1"), Column("Reg2"), Reg1, Reg2, grouped ? Lots() : null);
                var model = new ScatterRenderModelBuilder().Build(scatter, new ScatterPlotLabels("Reg1", "Reg2", scatter.Group?.Column.Name), Token)!;
                (data, frame, plot) = (scatter, model.Frame, new ScatterRenderer(model));
                break;
            }

            case GraphType.BoxPlot:
            {
                var parts = new[]
                {
                    new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, grouped ? Lots() : null),
                    new UnivariateGraphData(type, Guid.Empty, Column("Reg2"), Reg2, grouped ? Lots() : null)
                };
                var multi = new MultiVariableGraphData(type, Guid.Empty, parts);
                var model = new BoxPlotRenderModelBuilder().Build(multi, new BoxPlotLabels(["Reg1", "Reg2"], grouped ? "Lot" : null), Token)!;
                (data, frame, plot) = (multi, model.Frame, new BoxPlotRenderer(model));
                break;
            }

            case GraphType.Histogram:
            {
                var univariate = new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, grouped ? Lots() : null);
                var model = new HistogramRenderModelBuilder().Build(
                    univariate, new HistogramPlotLabels("Reg1", univariate.Group?.Column.Name), histogram ?? HistogramOptions.Default, Token)!;
                (data, frame, plot) = (univariate, model.Frame, new HistogramRenderer(model));
                break;
            }

            case GraphType.ProbabilityPlot:
            {
                var univariate = new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, grouped ? Lots() : null);
                var model = new ProbabilityPlotRenderModelBuilder().Build(univariate, new ProbabilityPlotLabels("Reg1", univariate.Group?.Column.Name), Token)!;
                (data, frame, plot) = (univariate, model.Frame, new ProbabilityPlotRenderer(model));
                break;
            }

            default:
            {
                var univariate = new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, grouped ? Lots() : null);
                var model = new EmpiricalCdfRenderModelBuilder().Build(univariate, new EmpiricalCdfLabels("Reg1", univariate.Group?.Column.Name), Token)!;
                (data, frame, plot) = (univariate, model.Frame, new EmpiricalCdfRenderer(model));
                break;
            }
        }

        var configuration = new GraphConfiguration(type, Guid.Empty, [])
        {
            Specification = GraphTypeDefinitions.For(type).Supports(GraphCapability.SpecificationLines)
                ? new Specification(14.8, 15, 15.3)
                : Specification.None,
            HistogramOptions = histogram ?? HistogramOptions.Default,
            LabelOptions = labels ?? GraphLabelOptions.Default
        };

        return new Presented(GraphPresentation.Present(frame, data, configuration, Token), data, frame, plot);
    }

    public static TheoryData<GraphType> GraphTypes => [.. Enum.GetValues<GraphType>()];

    private static (string? Title, string? X, string? Y) Titles(GraphRenderModel frame) => (frame.Title, frame.XAxis.Title, frame.YAxis.Title);

    // ---- The presentation a window keeps ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void PresentGivesTheFrameApplyGives(GraphType type)
    {
        foreach (var labels in new[] { GraphLabelOptions.Default, AllCustom, AllHidden })
        {
            var presented = Present(type, labels);
            var configuration = new GraphConfiguration(type, Guid.Empty, [])
            {
                Specification = GraphTypeDefinitions.For(type).Supports(GraphCapability.SpecificationLines) ? new Specification(14.8, 15, 15.3) : Specification.None,
                LabelOptions = labels
            };
            var applied = GraphPresentation.Apply(presented.BuilderFrame, presented.Data, configuration, Token);

            Assert.Equal(Titles(applied), Titles(presented.Graph.Frame));
            Assert.Equal(applied.XAxis.Range, presented.Graph.Frame.XAxis.Range);
            Assert.Equal(applied.ReferenceLines, presented.Graph.Frame.ReferenceLines);
            Assert.Same(labels, presented.Graph.LabelOptions);
            Assert.Equal(GraphTypeDefinitions.For(type), presented.Graph.Definition);
        }
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void UnderAutoLabelsTheShownFrameIsTheFrameBeforeTheLabels(GraphType type)
    {
        var graph = Present(type).Graph;

        Assert.Same(graph.UnlabelledFrame, graph.Frame);
        Assert.Equal(Titles(Present(type).BuilderFrame), Titles(graph.UnlabelledFrame));
    }

    // Auto -> Custom -> Hidden -> Auto: every change goes onto the same frame from before the labels, and Auto finds
    // the graph type's own titles there again.
    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void LabelsRoundTripWithoutTouchingAnythingElse(GraphType type)
    {
        var start = Present(type).Graph;
        var unlabelled = start.UnlabelledFrame;

        var custom = start.WithLabels(AllCustom);
        Assert.Equal(("Wafer thickness", "Thickness (um)", "Wafers"), Titles(custom.Frame));

        var hidden = custom.WithLabels(AllHidden);
        Assert.Equal(((string?)null, (string?)null, (string?)null), Titles(hidden.Frame));

        var auto = hidden.WithLabels(GraphLabelOptions.Default);
        Assert.Same(unlabelled, auto.Frame);
        Assert.Equal(Titles(start.Frame), Titles(auto.Frame));

        foreach (var state in new[] { custom, hidden, auto })
        {
            Assert.Same(unlabelled, state.UnlabelledFrame);
            Assert.Same(start.Definition, state.Definition);
            Assert.Same(unlabelled.StatisticsPanel, state.Frame.StatisticsPanel);
            Assert.Same(unlabelled.ReferenceLines, state.Frame.ReferenceLines);
            Assert.Same(unlabelled.Legend, state.Frame.Legend);
            Assert.Same(unlabelled.XAxis.Ticks, state.Frame.XAxis.Ticks);
            Assert.Equal(unlabelled.XAxis.Range, state.Frame.XAxis.Range);
            Assert.Same(unlabelled.YAxis.Ticks, state.Frame.YAxis.Ticks);
        }

        // The state it came from is left as it was.
        Assert.Same(unlabelled, start.Frame);
    }

    [Theory]
    [InlineData(HistogramYScale.Frequency, "Frequency")]
    [InlineData(HistogramYScale.Percent, "Percent")]
    [InlineData(HistogramYScale.Density, "Density")]
    public void AutoFindsTheHistogramsOwnYTitleAgainAfterACustomOne(HistogramYScale scale, string automatic)
    {
        var graph = Present(GraphType.Histogram, Labels(y: GraphLabelOption.Custom("Wafers")), new HistogramOptions(scale)).Graph;
        Assert.Equal("Wafers", graph.Frame.YAxis.Title);

        Assert.Equal(automatic, graph.WithLabels(GraphLabelOptions.Default).Frame.YAxis.Title);
    }

    [Fact]
    public void ABoxPlotsEmptyTitlesStayEmptyUnderAutoAfterCustomOnes()
    {
        var graph = Present(GraphType.BoxPlot, AllCustom, grouped: false).Graph;
        Assert.Equal("Wafers", graph.Frame.YAxis.Title);

        var auto = graph.WithLabels(GraphLabelOptions.Default).Frame;
        Assert.Null(auto.XAxis.Title);
        Assert.Null(auto.YAxis.Title);
    }

    [Fact]
    public void LabelsTheRulesRefuseLeaveTheGraphAsItWas()
    {
        var graph = Present(GraphType.Histogram).Graph;

        Assert.Throws<ArgumentException>(() => graph.WithLabels(Labels(GraphLabelOption.Custom("   "))));
        Assert.Same(graph.UnlabelledFrame, graph.Frame);
    }

    [Fact]
    public void ThePresentationHoldsNoDataOrConfiguration()
    {
        var kept = typeof(GraphPresentationState)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToList();

        Assert.DoesNotContain(kept, type => typeof(GraphData).IsAssignableFrom(type));
        Assert.DoesNotContain(typeof(GraphConfiguration), kept);

        // The frame before the statistics, legend, axis ranges and labels, the frame before the labels and the frame after
        // them, the graph type, and the label, axis range (#043), legend (#044), statistics (#045), appearance (#046) and
        // axis tick (#054) options: nothing else.
        Assert.Equal(10, kept.Count);
        Assert.Equal(3, kept.Count(type => type == typeof(GraphRenderModel)));
        Assert.Contains(typeof(GraphTypeDefinition), kept);
        Assert.Contains(typeof(GraphLabelOptions), kept);
        Assert.Contains(typeof(GraphAxisRangeOptions), kept);
        Assert.Contains(typeof(GraphLegendOptions), kept);
        Assert.Contains(typeof(GraphStatisticsOptions), kept);
        Assert.Contains(typeof(GraphAppearanceOptions), kept);
        Assert.Contains(typeof(GraphAxisTickOptions), kept);
    }

    // ---- Where the titles are ----

    private static readonly SKRect Canvas = new(0, 0, 760, 488);

    private static SKBitmap Render(GraphRenderModel frame, IGraphPlotRenderer plot, GraphTheme theme)
    {
        var bitmap = new SKBitmap((int)Canvas.Width, (int)Canvas.Height);
        using var canvas = new SKCanvas(bitmap);
        new SkiaGraphRenderer().Render(canvas, frame, Canvas, theme, plot);
        return bitmap;
    }

    // Every pixel a title puts down lies in its text box: drawn again with the title colour turned into the background
    // colour, the only pixels of the title band and the axis areas that change are the titles', and each title's lie
    // inside its own box - the turned Y axis title included.
    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void EachTitleIsDrawnInsideItsTextBox(GraphType type)
    {
        foreach (var (labels, theme) in new[] { (AllCustom, GraphThemes.Light), (GraphLabelOptions.Default, GraphThemes.Dark) })
        {
            var presented = Present(type, labels);
            var frame = presented.Graph.Frame;
            var geometry = SkiaGraphRenderer.LabelGeometry(frame, Canvas, theme);
            using var drawn = Render(frame, presented.Plot, theme);
            using var titlesOff = Render(frame, presented.Plot, theme with { Text = theme.Background });

            foreach (var label in geometry)
            {
                var ink = 0;
                for (var y = (int)label.Area.Top; y < (int)Math.Ceiling(label.Area.Bottom); y++)
                {
                    for (var x = (int)label.Area.Left; x < (int)Math.Ceiling(label.Area.Right); x++)
                    {
                        if (x < 0 || y < 0 || x >= drawn.Width || y >= drawn.Height || drawn.GetPixel(x, y) == titlesOff.GetPixel(x, y))
                        {
                            continue;
                        }

                        ink++;
                        var box = label.Text;
                        box.Inflate(1, 1);
                        Assert.True(box.Contains(x + 0.5f, y + 0.5f), $"{type} {label.Field}: ink at ({x},{y}) outside {label.Text}");
                    }
                }

                Assert.True(ink > 0, $"{type} {label.Field}: no ink in {label.Text}");
            }
        }
    }

    [Fact]
    public void TheTurnedYTitleStandsOnEnd()
    {
        var frame = Present(GraphType.Histogram).Graph.Frame;
        var y = Assert.Single(SkiaGraphRenderer.LabelGeometry(frame, Canvas, GraphThemes.Light), label => label.Field == GraphLabelField.YAxisTitle);
        var x = Assert.Single(SkiaGraphRenderer.LabelGeometry(frame, Canvas, GraphThemes.Light), label => label.Field == GraphLabelField.XAxisTitle);

        Assert.True(y.Text.Height > y.Text.Width, "the Y title's box is taller than it is wide");
        Assert.Equal(x.Text.Height, y.Text.Width, 3);
        Assert.True(y.Area.Contains(y.Text), "the Y title's box lies in the Y axis area");
    }

    [Fact]
    public void TitlesTheGraphDoesNotShowHaveNoGeometry()
    {
        var hidden = Present(GraphType.Histogram, AllHidden).Graph.Frame;
        Assert.Empty(SkiaGraphRenderer.LabelGeometry(hidden, Canvas, GraphThemes.Light));

        // An ungrouped box plot has no axis titles of its own.
        var box = Present(GraphType.BoxPlot, grouped: false).Graph.Frame;
        Assert.Equal([GraphLabelField.Title], SkiaGraphRenderer.LabelGeometry(box, Canvas, GraphThemes.Light).Select(label => label.Field));

        // Too small for a plot: nothing is drawn, so nothing can be picked.
        var shown = Present(GraphType.Histogram).Graph.Frame;
        Assert.Empty(SkiaGraphRenderer.LabelGeometry(shown, new SKRect(0, 0, 60, 40), GraphThemes.Light));
        Assert.Empty(SkiaGraphRenderer.LabelGeometry(shown, SKRect.Empty, GraphThemes.Light));
    }

    [Fact]
    public void ATitlesBoxIsAsWideAsItsTextIsDrawn()
    {
        foreach (var text in new[] { "Wafer thickness", "晶圓厚度 直方圖 / Lot 12" })
        {
            var frame = Present(GraphType.Histogram, Labels(GraphLabelOption.Custom(text))).Graph.Frame;
            var title = Assert.Single(SkiaGraphRenderer.LabelGeometry(frame, Canvas, GraphThemes.Light), label => label.Field == GraphLabelField.Title);
            using var font = new SKFont { Size = GraphThemes.Light.TitleFontSize, Edging = SKFontEdging.Antialias, Subpixel = true };

            Assert.Equal(GraphTextFallback.MeasureText(font, text), title.Text.Width, 3);
        }
    }

    // ---- Picking a title ----

    private static SKPoint Centre(SKRect rect) => new(rect.MidX, rect.MidY);

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void EachShownTitleIsPickedAtItsText(GraphType type)
    {
        var frame = Present(type, AllCustom).Graph.Frame;
        var geometry = SkiaGraphRenderer.LabelGeometry(frame, Canvas, GraphThemes.Light);

        Assert.Equal([GraphLabelField.Title, GraphLabelField.XAxisTitle, GraphLabelField.YAxisTitle], geometry.Select(label => label.Field));
        foreach (var label in geometry)
        {
            Assert.Equal(label.Field, GraphLabelHitTest.Find(geometry, Centre(label.Text)));
            Assert.Equal(label.Field, GraphLabelHitTest.Find(geometry, new SKPoint(label.Text.Left + 1, label.Text.Top + 1)));
        }
    }

    // Nothing else of the graph opens the labels: not the plot, a tick label, the legend, the statistics panel or the
    // labels of the specification lines.
    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheRestOfTheGraphPicksNothing(GraphType type)
    {
        var frame = Present(type, AllCustom).Graph.Frame;
        var geometry = SkiaGraphRenderer.LabelGeometry(frame, Canvas, GraphThemes.Light);
        var layout = SkiaGraphRenderer.Layout(frame, Canvas, GraphThemes.Light);

        var elsewhere = new List<(string What, SKPoint Point)>
        {
            ("plot centre", Centre(layout.PlotArea)),
            ("plot corner", new SKPoint(layout.PlotArea.Left + 2, layout.PlotArea.Top + 2)),
            ("X tick label", new SKPoint(layout.PlotArea.MidX, layout.PlotArea.Bottom + 12)),
            ("Y tick label", new SKPoint(layout.PlotArea.Left - 12, layout.PlotArea.MidY)),
            ("canvas corner", new SKPoint(2, 2))
        };
        if (!layout.LegendArea.IsEmpty)
        {
            elsewhere.Add(("legend", Centre(layout.LegendArea)));
        }

        if (!layout.StatisticsPanelArea.IsEmpty)
        {
            elsewhere.Add(("statistics", Centre(layout.StatisticsPanelArea)));
        }

        if (!layout.ReferenceLabelArea.IsEmpty)
        {
            elsewhere.Add(("specification labels", Centre(layout.ReferenceLabelArea)));
        }

        foreach (var (what, point) in elsewhere)
        {
            Assert.True(GraphLabelHitTest.Find(geometry, point) is null, $"{type}: the {what} at {point} picked a title");
        }
    }

    [Fact]
    public void ATitleIsPickedWithinThreePixelsOfItsTextButNotBeyondItsArea()
    {
        var frame = Present(GraphType.Histogram, AllCustom).Graph.Frame;
        var geometry = SkiaGraphRenderer.LabelGeometry(frame, Canvas, GraphThemes.Light);
        var x = geometry.Single(label => label.Field == GraphLabelField.XAxisTitle);
        var y = geometry.Single(label => label.Field == GraphLabelField.YAxisTitle);

        Assert.Equal(3f, GraphLabelHitTest.Padding);
        Assert.Equal(GraphLabelField.XAxisTitle, GraphLabelHitTest.Find(geometry, new SKPoint(x.Text.Right + 2.5f, x.Text.MidY)));
        Assert.Null(GraphLabelHitTest.Find(geometry, new SKPoint(x.Text.Right + 3.5f, x.Text.MidY)));
        Assert.Equal(GraphLabelField.YAxisTitle, GraphLabelHitTest.Find(geometry, new SKPoint(y.Text.MidX, y.Text.Top - 2.5f)));
        Assert.Null(GraphLabelHitTest.Find(geometry, new SKPoint(y.Text.MidX, y.Text.Top - 3.5f)));

        // The padding never reaches past the title's own area: below the X title is the canvas edge, left of the Y title
        // the canvas margin.
        Assert.Equal(x.Area.Bottom, GraphLabelHitTest.HitArea(x).Bottom);
        Assert.Null(GraphLabelHitTest.Find(geometry, new SKPoint(x.Text.MidX, x.Area.Bottom + 1)));
        Assert.Equal(y.Area.Left, GraphLabelHitTest.HitArea(y).Left);
        Assert.Null(GraphLabelHitTest.Find(geometry, new SKPoint(y.Area.Left - 1, y.Text.MidY)));
    }

    [Fact]
    public void AHiddenTitleCannotBePickedWhereItWas()
    {
        var shown = Present(GraphType.Histogram).Graph;
        var where = SkiaGraphRenderer.LabelGeometry(shown.Frame, Canvas, GraphThemes.Light).Single(label => label.Field == GraphLabelField.XAxisTitle);

        var hidden = shown.WithLabels(Labels(x: GraphLabelOption.Hidden)).Frame;
        var geometry = SkiaGraphRenderer.LabelGeometry(hidden, Canvas, GraphThemes.Light);

        Assert.DoesNotContain(geometry, label => label.Field == GraphLabelField.XAxisTitle);
        Assert.NotEqual(GraphLabelField.XAxisTitle, GraphLabelHitTest.Find(geometry, Centre(where.Text)));
    }

    // ---- Editing ----

    private sealed class FakeLabelsDialog : IGraphLabelsDialog
    {
        public List<(GraphLabelOptions Current, GraphLabelField? Focus, string? Shown)> Calls { get; } = [];

        public Func<GraphLabelOptions, GraphLabelOptions?> Answer { get; set; } = _ => null;

        public TaskCompletionSource<GraphLabelOptions?>? Pending { get; set; }

        public Task<GraphLabelOptions?> EditAsync(GraphTypeDefinition definition, GraphLabelOptions current, GraphLabelField? focus, string? shownText)
        {
            Calls.Add((current, focus, shownText));
            return Pending?.Task ?? Task.FromResult(Answer(current));
        }
    }

    [Fact]
    public async Task ConfirmedLabelsBecomeTheGraph()
    {
        var dialog = new FakeLabelsDialog { Answer = _ => AllCustom };
        var controller = new GraphLabelEditController(Present(GraphType.Histogram).Graph, dialog);
        var changes = 0;
        controller.GraphChanged += (_, _) => changes++;

        Assert.True(await controller.EditAsync(focus: null));

        Assert.Equal(1, changes);
        Assert.Same(AllCustom, controller.Graph.LabelOptions);
        Assert.Equal(("Wafer thickness", "Thickness (um)", "Wafers"), Titles(controller.Graph.Frame));
    }

    [Fact]
    public async Task ACancelledEditChangesNothing()
    {
        var start = Present(GraphType.Histogram).Graph;
        var dialog = new FakeLabelsDialog { Answer = _ => null };
        var controller = new GraphLabelEditController(start, dialog);
        var changes = 0;
        controller.GraphChanged += (_, _) => changes++;

        Assert.False(await controller.EditAsync(GraphLabelField.Title));

        Assert.Equal(0, changes);
        Assert.Same(start, controller.Graph);
        Assert.Same(GraphLabelOptions.Default, controller.Graph.LabelOptions);
    }

    [Fact]
    public async Task ThePickedTitleAndTheTextItShowsReachTheDialog()
    {
        var dialog = new FakeLabelsDialog();
        var graph = Present(GraphType.Histogram, Labels(y: GraphLabelOption.Custom("Wafers"))).Graph;
        var controller = new GraphLabelEditController(graph, dialog);

        await controller.EditAsync(GraphLabelField.Title);
        await controller.EditAsync(GraphLabelField.XAxisTitle);
        await controller.EditAsync(GraphLabelField.YAxisTitle);
        await controller.EditAsync(focus: null);

        Assert.Equal(
            [
                (graph.LabelOptions, (GraphLabelField?)GraphLabelField.Title, (string?)"Histogram of Reg1"),
                (graph.LabelOptions, GraphLabelField.XAxisTitle, "Reg1"),
                (graph.LabelOptions, GraphLabelField.YAxisTitle, "Wafers"),
                (graph.LabelOptions, null, null)
            ],
            dialog.Calls);
    }

    [Fact]
    public async Task AHiddenTitleIsBroughtBackFromTheMenu()
    {
        var dialog = new FakeLabelsDialog { Answer = current => current with { XAxisTitle = GraphLabelOption.Auto } };
        var controller = new GraphLabelEditController(Present(GraphType.Histogram, Labels(x: GraphLabelOption.Hidden)).Graph, dialog);
        Assert.Null(controller.Graph.Frame.XAxis.Title);

        Assert.True(await controller.EditAsync(focus: null));

        Assert.Equal("Reg1", controller.Graph.Frame.XAxis.Title);
    }

    [Fact]
    public async Task LabelsTheRulesRefuseAreNeverApplied()
    {
        var start = Present(GraphType.Histogram).Graph;
        var dialog = new FakeLabelsDialog { Answer = _ => Labels(GraphLabelOption.Custom(" ")) };
        var controller = new GraphLabelEditController(start, dialog);

        Assert.False(await controller.EditAsync(focus: null));
        Assert.Same(start, controller.Graph);
    }

    [Fact]
    public async Task OneEditAtATime()
    {
        var dialog = new FakeLabelsDialog { Pending = new TaskCompletionSource<GraphLabelOptions?>() };
        var controller = new GraphLabelEditController(Present(GraphType.Histogram).Graph, dialog);

        var first = controller.EditAsync(GraphLabelField.Title);
        Assert.False(await controller.EditAsync(GraphLabelField.XAxisTitle));
        dialog.Pending.SetResult(AllHidden);

        Assert.True(await first);
        Assert.Single(dialog.Calls);
        Assert.Same(AllHidden, controller.Graph.LabelOptions);
    }

    // ---- The editor, as the dialog opens it ----

    [Fact]
    public void TheEditorShowsTheLabelsAsTheyAre()
    {
        var editor = new GraphLabelsEditorViewModel(Labels(GraphLabelOption.Custom("  Wafer\r\nthickness "), GraphLabelOption.Hidden));

        Assert.Equal(GraphLabelMode.Custom, editor.SelectedGraphTitleMode.Value);
        Assert.Equal("Wafer thickness", editor.GraphTitleText);
        Assert.Equal(GraphLabelMode.Hidden, editor.SelectedXAxisTitleMode.Value);
        Assert.Equal(string.Empty, editor.XAxisTitleText);
        Assert.Equal(GraphLabelMode.Auto, editor.SelectedYAxisTitleMode.Value);
        Assert.True(editor.IsValid);
        Assert.Equal(Labels(GraphLabelOption.Custom("Wafer thickness"), GraphLabelOption.Hidden), editor.Options);
        Assert.Equal(GraphLabelOptions.Default, new GraphLabelsEditorViewModel().Options);
    }

    // A picked title on Auto opens as Custom with the text the graph shows; a Custom one stays as it is.
    [Fact]
    public void BeginningToEditAPickedTitle()
    {
        var editor = new GraphLabelsEditorViewModel(Labels(y: GraphLabelOption.Custom("Wafers")));

        editor.BeginEditing(GraphLabelField.Title, "Histogram of Reg1");
        Assert.Equal(GraphLabelMode.Custom, editor.SelectedGraphTitleMode.Value);
        Assert.Equal("Histogram of Reg1", editor.GraphTitleText);
        Assert.True(editor.IsGraphTitleTextEnabled);

        editor.BeginEditing(GraphLabelField.YAxisTitle, "something else shown");
        Assert.Equal(GraphLabelMode.Custom, editor.SelectedYAxisTitleMode.Value);
        Assert.Equal("Wafers", editor.YAxisTitleText);

        editor.BeginEditing(GraphLabelField.XAxisTitle, "Reg1");
        Assert.Equal(("Histogram of Reg1", "Reg1", "Wafers"), (editor.Options.Title.Text, editor.Options.XAxisTitle.Text, editor.Options.YAxisTitle.Text));

        var hidden = new GraphLabelsEditorViewModel(AllHidden);
        hidden.BeginEditing(GraphLabelField.Title, null);
        Assert.Equal(GraphLabelMode.Hidden, hidden.SelectedGraphTitleMode.Value);
    }

    [Fact]
    public void TheEditorRefusesWhatTheSetupRefusesInTheSameWords()
    {
        var editor = new GraphLabelsEditorViewModel();
        editor.SelectedXAxisTitleMode = editor.LabelModeChoices.Single(choice => choice.Value == GraphLabelMode.Custom);
        editor.XAxisTitleText = "  \t ";

        Assert.False(editor.IsValid);
        var error = Assert.Single(editor.Errors);
        Assert.Equal(GraphConfigurationValidator.LabelErrors(editor.Options), editor.Errors);
        Assert.Equal("Enter an X-axis title, or choose Auto or Hidden.", GraphValidationMessages.For(error, GraphTypeDefinitions.For(GraphType.Histogram)));

        editor.XAxisTitleText = "Thickness";
        Assert.True(editor.IsValid);
    }

    [Fact]
    public void TheEditorSaysWhenItsLabelsChange()
    {
        var editor = new GraphLabelsEditorViewModel();
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.GraphTitleText = "typed";

        Assert.Contains(nameof(GraphLabelsEditorViewModel.Options), changed);
        Assert.Contains(nameof(GraphLabelsEditorViewModel.IsValid), changed);
    }

    // ---- The window's menu and title ----

    [Fact]
    public void EditLabelsIsOfferedOnTheRightClickMenu()
    {
        var command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        var item = GraphWindow.EditLabelsItem(command);

        Assert.Equal("_Edit Labels...", item.Header);
        Assert.Same(command, item.Command);
    }

    [Fact]
    public void TheWindowIsNamedAfterTheTitleItShows()
    {
        var graph = Present(GraphType.Histogram).Graph;

        Assert.Equal("Histogram of Reg1", GraphWindow.WindowTitle(graph.Frame));
        Assert.Equal("Wafer thickness", GraphWindow.WindowTitle(graph.WithLabels(AllCustom).Frame));
        Assert.Equal("Graph", GraphWindow.WindowTitle(graph.WithLabels(AllHidden).Frame));
    }
}
