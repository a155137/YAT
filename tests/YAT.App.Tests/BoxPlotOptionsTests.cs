using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// A box plot's own options (Task #047): the box width, and whether the means and the outliers are marked. The options,
// their rule and the editor both dialogs share; the width a box is drawn at; and what hiding the means or the outliers
// changes in the drawn graph - their markers, and nothing else: not a box, not a whisker, not the frame or its axes.
public class BoxPlotOptionsTests
{
    private const int Width = 640;
    private const int Height = 480;

    private const string WidthMessage = "Box width must be a whole number from 20 to 90.";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    // A skewed sample - its mean well away from its median - with outliers on both sides.
    private static readonly double[] Skewed = [.. Enumerable.Range(0, 100).Select(i => i * i / 100.0), 260, 275, -120];

    private static MultiVariableGraphData Data(int variables = 1) =>
        new(GraphType.BoxPlot, Guid.NewGuid(),
        [
            .. Enumerable.Range(0, variables).Select(index => new UnivariateGraphData(
                GraphType.BoxPlot, Guid.NewGuid(), Column($"Reg{index + 1}"), Skewed.Select(value => value + (index * 7)).ToArray(), null))
        ]);

    private static BoxPlotRenderModel Model(BoxPlotOptions? options = null, int variables = 1)
    {
        var data = Data(variables);
        var labels = new BoxPlotLabels([.. data.Variables.Select(variable => variable.Variable.Name)]);
        return options is null
            ? new BoxPlotRenderModelBuilder().Build(data, labels, Token)!
            : new BoxPlotRenderModelBuilder().Build(data, labels, options, Token)!;
    }

    // The light theme without its grid, so a row of the plot is only the plot background and the boxes.
    private static readonly GraphTheme Plain =
        GraphAppearance.Resolve(GraphThemes.Light, new GraphAppearanceOptions(GridMode: GraphGridMode.Hide));

    private sealed class Capturing(IGraphPlotRenderer inner) : IGraphPlotRenderer
    {
        public GraphCoordinateTransform? Transform { get; private set; }

        public void RenderPlot(SKCanvas canvas, GraphCoordinateTransform transform, GraphTheme theme)
        {
            Transform = transform;
            inner.RenderPlot(canvas, transform, theme);
        }
    }

    private static (SKBitmap Bitmap, GraphCoordinateTransform Transform) Render(BoxPlotRenderModel model, GraphTheme? theme = null)
    {
        var bitmap = new SKBitmap(Width, Height);
        var plot = new Capturing(new BoxPlotRenderer(model));
        using (var canvas = new SKCanvas(bitmap))
        {
            new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, Width, Height), theme ?? GraphThemes.Light, plot);
        }

        return (bitmap, plot.Transform!);
    }

    private static List<SKPointI> Changed(SKBitmap before, SKBitmap after)
    {
        var changed = new List<SKPointI>();
        for (var y = 0; y < before.Height; y++)
        {
            for (var x = 0; x < before.Width; x++)
            {
                if (before.GetPixel(x, y) != after.GetPixel(x, y))
                {
                    changed.Add(new SKPointI(x, y));
                }
            }
        }

        return changed;
    }

    // ---- The options and their rule ----

    [Fact]
    public void TheDefaultIsTheBoxPlotAsItWasAlwaysDrawn()
    {
        Assert.Equal(new BoxPlotOptions(60, ShowMean: true, ShowOutliers: true), BoxPlotOptions.Default);
        Assert.True(BoxPlotOptions.Default.IsValid);
        Assert.Equal(BoxPlotOptions.Default, new GraphConfiguration(GraphType.BoxPlot, Guid.Empty, []).BoxPlotOptions);
        Assert.Equal(BoxPlotOptions.Default, Model().Options);
        Assert.Equal(BoxPlotRenderer.BoxWidthFraction, BoxPlotOptions.DefaultBoxWidthPercent / 100.0);
    }

    [Theory]
    [InlineData(int.MinValue, false)]
    [InlineData(0, false)]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(60, true)]
    [InlineData(90, true)]
    [InlineData(91, false)]
    [InlineData(int.MaxValue, false)]
    public void ABoxWidthIsTwentyToNinetyPercent(int percent, bool valid)
    {
        var options = new BoxPlotOptions(percent);
        var configuration = new GraphConfiguration(GraphType.BoxPlot, Guid.Empty, []) { BoxPlotOptions = options };
        var errors = new GraphConfigurationValidator().Validate(configuration, []).Errors;

        Assert.Equal(valid, options.IsValid);
        Assert.Equal(valid, GraphConfigurationValidator.BoxPlotErrors(options).Count == 0);
        Assert.Equal(valid, errors.All(error => error.Reason != GraphValidationReason.BoxPlotWidthInvalid));
    }

    [Fact]
    public void AnInvalidBoxWidthIsRefusedWithItsMessage()
    {
        var error = Assert.Single(GraphConfigurationValidator.BoxPlotErrors(new BoxPlotOptions(19)));

        Assert.Equal(GraphValidationReason.BoxPlotWidthInvalid, error.Reason);
        Assert.Equal(WidthMessage, GraphValidationMessages.For(error, GraphTypeDefinitions.For(GraphType.BoxPlot)));
    }

    // Only a box plot reads its options, so only a box plot is held to them.
    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void OtherGraphTypesIgnoreTheBoxPlotOptions(GraphType graphType)
    {
        var configuration = new GraphConfiguration(graphType, Guid.Empty, []) { BoxPlotOptions = new BoxPlotOptions(5) };

        Assert.False(GraphTypeDefinitions.For(graphType).Supports(GraphCapability.BoxPlotControls));
        Assert.DoesNotContain(
            new GraphConfigurationValidator().Validate(configuration, []).Errors,
            error => error.Reason == GraphValidationReason.BoxPlotWidthInvalid);
    }

    [Fact]
    public void OnlyTheBoxPlotHasBoxPlotControls() =>
        Assert.Equal(
            [GraphType.BoxPlot],
            GraphTypeDefinitions.All.Where(definition => definition.Supports(GraphCapability.BoxPlotControls)).Select(definition => definition.GraphType));

    // ---- The editor both dialogs share ----

    [Fact]
    public void TheEditorStartsFromTheOptionsItIsGiven()
    {
        var fresh = new GraphBoxPlotEditorViewModel();
        var given = new GraphBoxPlotEditorViewModel(new BoxPlotOptions(35, ShowMean: false, ShowOutliers: true));

        Assert.Equal("60", fresh.BoxWidthText);
        Assert.Equal(BoxPlotOptions.Default, fresh.Options);
        Assert.True(fresh.IsValid);
        Assert.Null(fresh.ValidationMessage);
        Assert.Equal("35", given.BoxWidthText);
        Assert.False(given.ShowMean);
        Assert.True(given.ShowOutliers);
        Assert.Equal(new BoxPlotOptions(35, false, true), given.Options);
    }

    [Theory]
    [InlineData("20", 20)]
    [InlineData("90", 90)]
    [InlineData(" 45 ", 45)]
    [InlineData("075", 75)]
    [InlineData("19", null)]
    [InlineData("91", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("60.0", null)]
    [InlineData("60,5", null)]
    [InlineData("+60", null)]
    [InlineData("-60", null)]
    [InlineData("1e2", null)]
    [InlineData("sixty", null)]
    [InlineData("６０", null)]
    public void TheEditorTakesAWholeNumberFromTwentyToNinety(string text, int? percent)
    {
        var editor = new GraphBoxPlotEditorViewModel { ShowOutliers = false };
        editor.BoxWidthText = text;

        if (percent is { } expected)
        {
            Assert.Equal(new BoxPlotOptions(expected, true, false), editor.Options);
            Assert.True(editor.IsValid);
            Assert.Null(editor.ValidationMessage);
        }
        else
        {
            Assert.Null(editor.Options);
            Assert.False(editor.IsValid);
            Assert.Equal(WidthMessage, editor.ValidationMessage);
        }
    }

    [Fact]
    public void TheEditorSaysWhenWhatItDescribesChanges()
    {
        var editor = new GraphBoxPlotEditorViewModel();
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.BoxWidthText = "5";
        Assert.Contains(nameof(GraphBoxPlotEditorViewModel.ValidationMessage), changed);
        Assert.Contains(nameof(GraphBoxPlotEditorViewModel.Options), changed);

        changed.Clear();
        editor.ShowMean = false;
        Assert.Contains(nameof(GraphBoxPlotEditorViewModel.Options), changed);
    }

    // ---- The width a box is drawn at ----

    // The default width is the width boxes were drawn at before there were options: to the bit, for every slot.
    [Fact]
    public void TheDefaultWidthIsTheOldWidthForEverySlot()
    {
        for (var slot = 0.25f; slot < 2500f; slot += 0.37f)
        {
            var old = (float)Math.Clamp(slot * 0.6, 5f, 72f);
            Assert.Equal(old, BoxPlotRenderer.BoxWidth(slot, 60));
        }
    }

    // A wide slot - a handful of boxes - holds every width at its upper limit, which scales with the width chosen.
    [Theory]
    [InlineData(20, 24f)]
    [InlineData(60, 72f)]
    [InlineData(90, 108f)]
    public void TheUpperLimitScalesWithTheBoxWidth(int percent, float limit)
    {
        Assert.Equal(limit, BoxPlotRenderer.BoxWidth(600f, percent), 3);
        Assert.Equal(limit, BoxPlotRenderer.BoxWidth(10_000f, percent), 3);
    }

    // Between the limits, a box is its share of the slot.
    [Theory]
    [InlineData(20, 20f)]
    [InlineData(45, 45f)]
    [InlineData(60, 60f)]
    [InlineData(90, 90f)]
    public void BetweenTheLimitsABoxIsItsShareOfTheSlot(int percent, float width) =>
        Assert.Equal(width, BoxPlotRenderer.BoxWidth(100f, percent), 3);

    // However narrow the slot - many categories on a small window - a box is never narrower than five pixels.
    [Theory]
    [InlineData(20, 1f)]
    [InlineData(20, 10f)]
    [InlineData(60, 3.7f)]
    [InlineData(90, 5f)]
    public void ABoxIsNeverNarrowerThanFivePixels(int percent, float slot) =>
        Assert.Equal(BoxPlotRenderer.MinimumBoxWidth, BoxPlotRenderer.BoxWidth(slot, percent));

    // Drawn: one box on a wide plot, at its limit - 24, 72 and 108 pixels, outline included.
    [Theory]
    [InlineData(20, 24f)]
    [InlineData(60, 72f)]
    [InlineData(90, 108f)]
    public void OneBoxIsDrawnAtTheLimitOfItsWidth(int percent, float expected)
    {
        var model = Model(new BoxPlotOptions(percent));
        var (bitmap, transform) = Render(model, Plain);
        using var rendered = bitmap;
        var box = model.Boxes[0];
        var centre = (int)Math.Round(transform.ToScreenX(BoxPlotRenderModelBuilder.CategoryPosition(0)));

        // A row inside the box between Q1 and the median, away from the mean's cross (the sample is skewed upward).
        var q1 = transform.ToScreenY(box.FirstQuartile);
        var median = transform.ToScreenY(box.Median);
        var row = (int)Math.Round((q1 + median) / 2);
        Assert.True(Math.Abs(row - transform.ToScreenY(box.Mean)) > BoxPlotRenderer.MeanMarkerSize);

        var inked = Enumerable.Range(centre - 80, 161).Where(x => bitmap.GetPixel(x, row) != Plain.PlotBackground).ToList();
        var measured = inked.Max() - inked.Min() + 1;

        Assert.InRange(measured, expected - 1, expected + 3);
    }

    // ---- Hiding the means and the outliers ----

    [Fact]
    public void HidingTheMeanChangesOnlyTheMeansCrosses()
    {
        var shown = Model(variables: 3);
        var hidden = shown with { Options = new BoxPlotOptions(ShowMean: false) };
        var (before, transform) = Render(shown);
        var (after, _) = Render(hidden);
        using var first = before;
        using var second = after;

        var changed = Changed(before, after);
        var reach = (BoxPlotRenderer.MeanMarkerSize / 2) + 2;
        var crosses = shown.Boxes.Select(box => (
            X: transform.ToScreenX(BoxPlotRenderModelBuilder.CategoryPosition(box.CategoryIndex)),
            Y: transform.ToScreenY(box.Mean))).ToList();

        Assert.NotEmpty(changed);
        Assert.All(changed, pixel => Assert.Contains(crosses, cross => Math.Abs(pixel.X - cross.X) <= reach && Math.Abs(pixel.Y - cross.Y) <= reach));

        // Every cross is gone: no pixel changed near a mean is left changed back to ink the default did not have.
        Assert.All(crosses, cross => Assert.Contains(changed, pixel => Math.Abs(pixel.X - cross.X) <= reach && Math.Abs(pixel.Y - cross.Y) <= reach));
    }

    [Fact]
    public void HidingTheOutliersChangesOnlyTheOutlierMarkers()
    {
        var shown = Model(variables: 3);
        var hidden = shown with { Options = new BoxPlotOptions(ShowOutliers: false) };
        var (before, transform) = Render(shown);
        var (after, _) = Render(hidden);
        using var first = before;
        using var second = after;

        var changed = Changed(before, after);
        var markers = shown.Boxes.SelectMany(box => box.Outliers.ToArray().Select(value => (
            X: transform.ToScreenX(BoxPlotRenderModelBuilder.CategoryPosition(box.CategoryIndex)),
            Y: transform.ToScreenY(value)))).ToList();
        var reach = (BoxPlotRenderer.OutlierDiameter / 2) + 1.5;

        Assert.Equal(9, markers.Count);
        Assert.NotEmpty(changed);
        Assert.All(changed, pixel => Assert.Contains(markers, marker => Math.Abs(pixel.X - marker.X) <= reach && Math.Abs(pixel.Y - marker.Y) <= reach));
        Assert.All(markers, marker => Assert.Contains(changed, pixel => Math.Abs(pixel.X - marker.X) <= reach && Math.Abs(pixel.Y - marker.Y) <= reach));
    }

    // Both hidden: exactly the pixels of one and of the other.
    [Fact]
    public void HidingBothChangesTheCrossesAndTheMarkersTogether()
    {
        var shown = Model(variables: 2);
        var (all, _) = Render(shown);
        var (noMean, _) = Render(shown with { Options = new BoxPlotOptions(ShowMean: false) });
        var (noOutliers, _) = Render(shown with { Options = new BoxPlotOptions(ShowOutliers: false) });
        var (neither, _) = Render(shown with { Options = new BoxPlotOptions(ShowMean: false, ShowOutliers: false) });
        using var a = all;
        using var b = noMean;
        using var c = noOutliers;
        using var d = neither;

        var expected = Changed(all, noMean).Union(Changed(all, noOutliers)).OrderBy(p => (p.Y, p.X)).ToList();
        Assert.Equal(expected, Changed(all, neither).OrderBy(p => (p.Y, p.X)).ToList());
    }

    // Hidden outliers still count: the whiskers stay at the most extreme observations inside the fences, and the Y axis
    // still reaches every outlier.
    [Fact]
    public void TheOptionsChangeNoBoxWhiskerOrAxis()
    {
        var plain = Model(variables: 2);
        foreach (var options in new[]
                 {
                     new BoxPlotOptions(20), new BoxPlotOptions(90), new BoxPlotOptions(ShowMean: false),
                     new BoxPlotOptions(ShowOutliers: false), new BoxPlotOptions(33, false, false)
                 })
        {
            var built = Model(options, variables: 2);

            Assert.Equal(options, built.Options);
            Assert.Equal(plain.Categories, built.Categories);
            Assert.Equal(plain.Frame.XAxis.Range, built.Frame.XAxis.Range);
            Assert.Equal(plain.Frame.YAxis.Range, built.Frame.YAxis.Range);
            Assert.Equal(plain.Frame.YAxis.Ticks, built.Frame.YAxis.Ticks);
            Assert.Equal(plain.Frame.XAxis.Ticks, built.Frame.XAxis.Ticks);
            Assert.Equal(plain.Frame.Title, built.Frame.Title);
            Assert.Equal(plain.OutlierCount, built.OutlierCount);
            Assert.Equal(plain.RenderedOutlierCount, built.RenderedOutlierCount);
            Assert.Equal(
                plain.Boxes.Select(box => (box.LowerWhisker, box.FirstQuartile, box.Median, box.ThirdQuartile, box.UpperWhisker, box.Mean, box.OutlierCount, string.Join(",", box.Outliers.ToArray()))),
                built.Boxes.Select(box => (box.LowerWhisker, box.FirstQuartile, box.Median, box.ThirdQuartile, box.UpperWhisker, box.Mean, box.OutlierCount, string.Join(",", box.Outliers.ToArray()))));
        }

        // The Y axis reaches the outliers beyond the whiskers, shown or not.
        var box = plain.Boxes[0];
        Assert.True(plain.Frame.YAxis.Range.Maximum >= 275 && plain.Frame.YAxis.Range.Minimum <= -120);
        Assert.True(box.UpperWhisker < 260 && box.LowerWhisker > -120);
    }

    // Other options for a drawn box plot are the same model - the very boxes and frame - with other Options.
    [Fact]
    public void OtherOptionsKeepTheVeryBoxesAndFrame()
    {
        var model = Model(variables: 2);
        var other = model with { Options = new BoxPlotOptions(25, false, false) };

        Assert.Same(model.Boxes, other.Boxes);
        Assert.Same(model.Frame, other.Frame);
        Assert.Same(model.Categories, other.Categories);
        Assert.Equal(BoxPlotOptions.Default, model.Options);
    }

    [Fact]
    public void InvalidOptionsAreRefusedByTheBuilderAndTheModel()
    {
        var model = Model();

        Assert.Throws<ArgumentOutOfRangeException>(() => Model(new BoxPlotOptions(19)));
        Assert.Throws<ArgumentOutOfRangeException>(() => model with { Options = new BoxPlotOptions(91) });
        Assert.Throws<ArgumentNullException>(() => model with { Options = null! });
    }

    // ---- The setup ----

    private static readonly Worksheet Sheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

    private static GraphSetupViewModel Setup(GraphType graphType)
    {
        var columns = new List<WorksheetColumn>
        {
            new() { Id = Guid.NewGuid(), WorksheetId = Sheet.Id, Index = 0, Name = "Reg1", DataType = WorksheetDataType.Numeric },
            new() { Id = Guid.NewGuid(), WorksheetId = Sheet.Id, Index = 1, Name = "Reg2", DataType = WorksheetDataType.Numeric }
        };
        var setup = new GraphSetupViewModel(GraphTypeDefinitions.For(graphType), Sheet, columns);
        foreach (var role in setup.Roles.Where(role => role.IsRequired))
        {
            var option = role.Options.First(option => option.Name == (role.Role == GraphVariableRole.Y ? "Reg2" : "Reg1"));
            if (role.AllowsMultiple)
            {
                role.SelectedOptions.Add(option);
            }
            else
            {
                role.SelectedOption = option;
            }
        }

        return setup;
    }

    [Fact]
    public void TheSetupsBoxPlotOptionsAreTheConfigurations()
    {
        var setup = Setup(GraphType.BoxPlot);
        Assert.True(setup.SupportsBoxPlotControls);
        Assert.Equal(BoxPlotOptions.Default, setup.BoxPlot);
        Assert.Equal(BoxPlotOptions.Default, setup.Confirm()!.BoxPlotOptions);

        var chosen = new BoxPlotOptions(35, ShowMean: false, ShowOutliers: false);
        setup.BoxPlot = chosen;

        Assert.True(setup.CanConfirm);
        Assert.Equal(chosen, setup.Confirm()!.BoxPlotOptions);
        Assert.Equal(chosen, setup.ConfirmRequest()!.Configuration.BoxPlotOptions);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void OtherSetupsOfferNoBoxPlotOptionsAndConfigureTheDefault(GraphType graphType)
    {
        var setup = Setup(graphType);
        setup.BoxPlot = new BoxPlotOptions(30, false, false);

        Assert.False(setup.SupportsBoxPlotControls);
        Assert.Same(BoxPlotOptions.Default, setup.Confirm()!.BoxPlotOptions);
    }

    [Fact]
    public void InvalidBoxPlotOptionsKeepTheSetupFromBeingConfirmed()
    {
        var setup = Setup(GraphType.BoxPlot);
        setup.BoxPlot = new BoxPlotOptions(10);

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal(WidthMessage, setup.ValidationMessage);

        setup.BoxPlot = new BoxPlotOptions(20);
        Assert.True(setup.CanConfirm);
    }

    // Drawn separately, every variable's graph is configured with the setup's options.
    [Fact]
    public void EveryVariableDrawnSeparatelyKeepsTheOptions()
    {
        var chosen = new BoxPlotOptions(80, ShowMean: false);
        var configuration = new GraphConfiguration(GraphType.BoxPlot, Guid.Empty,
        [
            new GraphColumnAssignment(GraphVariableRole.Variable, Guid.NewGuid()),
            new GraphColumnAssignment(GraphVariableRole.Variable, Guid.NewGuid())
        ]) { BoxPlotOptions = chosen };

        Assert.All(
            configuration.Assignments,
            assignment => Assert.Equal(chosen, GraphSetupRequest.ForVariable(configuration, assignment.WorksheetColumnId).BoxPlotOptions));
    }
}
