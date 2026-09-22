using YAT.Application.Graphs;
using YAT.App.Tests.TestDoubles;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The File menu of a graph window: where the file goes, what is written, and what happens when the user changes their
// mind or the file cannot be written.
public class GraphExportControllerTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            Controller = new GraphExportController(Dialogs, new GraphExportService(), PowerPoint, Clipboard);
        }

        public TemporaryDirectory Directory { get; } = new();

        public FakeGraphExportDialogs Dialogs { get; } = new();

        public FakePowerPointGraphExporter PowerPoint { get; } = new();

        public FakeGraphImageClipboard Clipboard { get; } = new();

        public GraphExportController Controller { get; }

        public void Dispose() => Directory.Dispose();
    }

    private static GraphExportSnapshot Snapshot(GraphTheme? theme = null)
    {
        var model = new HistogramRenderModelBuilder().Build(
            new UnivariateGraphData(
                GraphType.Histogram,
                Guid.NewGuid(),
                new GraphColumnInfo(Guid.NewGuid(), "Reg1", WorksheetDataType.Numeric),
                Enumerable.Range(0, 100).Select(value => (double)(value % 20)).ToArray(),
                null),
            new HistogramPlotLabels("Reg1"),
            TestContext.Current.CancellationToken)!;

        return new GraphExportSnapshot(model.Frame, new HistogramRenderer(model), theme ?? GraphThemes.Light);
    }

    // 1
    [Fact]
    public async Task ExportingAPngWritesTheGraphWhereTheUserChose()
    {
        using var runtime = new Runtime();
        var path = runtime.Directory.File("graph.png");
        runtime.Dialogs.PngPath = path;

        await runtime.Controller.ExportPngAsync(Snapshot(), Token);

        Assert.True(File.Exists(path));
        Assert.Equal(PngSignature, File.ReadAllBytes(path).Take(PngSignature.Length));
        Assert.Equal(path, runtime.Controller.LastExportedPath);
        Assert.Empty(runtime.Dialogs.Errors);
    }

    // 2
    [Fact]
    public async Task ThePngIsOfferedUnderTheGraphsOwnName()
    {
        using var runtime = new Runtime();
        runtime.Dialogs.PngPath = null;

        await runtime.Controller.ExportPngAsync(Snapshot(), Token);

        Assert.Equal(["Histogram of Reg1.png"], runtime.Dialogs.SuggestedNames);
    }

    // 3
    [Fact]
    public async Task CancellingThePngDialogDoesNothing()
    {
        using var runtime = new Runtime();
        runtime.Dialogs.PngPath = null;

        await runtime.Controller.ExportPngAsync(Snapshot(), Token);

        Assert.Empty(System.IO.Directory.GetFiles(runtime.Directory.DirectoryPath));
        Assert.Null(runtime.Controller.LastExportedPath);
        Assert.Empty(runtime.Dialogs.Errors);
    }

    // 4
    [Fact]
    public async Task ExportingAPresentationEmbedsTheRenderedImage()
    {
        using var runtime = new Runtime();
        var path = runtime.Directory.File("graph.pptx");
        runtime.Dialogs.PowerPointPath = path;

        await runtime.Controller.ExportPowerPointAsync(Snapshot(), Token);

        var (saved, image) = runtime.PowerPoint.Last;
        Assert.Equal(path, saved);
        Assert.Equal(PngSignature, image.Png.Take(PngSignature.Length));
        Assert.Equal(GraphExportService.ExportWidth, image.Width);
        Assert.Equal(GraphExportService.ExportHeight, image.Height);
        Assert.Equal(path, runtime.Controller.LastExportedPath);
    }

    // 5
    [Fact]
    public async Task ThePresentationIsOfferedUnderTheGraphsOwnName()
    {
        using var runtime = new Runtime();
        runtime.Dialogs.PowerPointPath = null;

        await runtime.Controller.ExportPowerPointAsync(Snapshot(), Token);

        Assert.Equal(["Histogram of Reg1.pptx"], runtime.Dialogs.SuggestedNames);
    }

    // 6
    [Fact]
    public async Task CancellingThePresentationDialogDoesNothing()
    {
        using var runtime = new Runtime();
        runtime.Dialogs.PowerPointPath = null;

        await runtime.Controller.ExportPowerPointAsync(Snapshot(), Token);

        Assert.Empty(runtime.PowerPoint.Saved);
        Assert.Null(runtime.Controller.LastExportedPath);
        Assert.Empty(runtime.Dialogs.Errors);
    }

    // 7
    [Fact]
    public async Task TheSlideBackgroundFollowsTheThemeOfTheSnapshot()
    {
        using var runtime = new Runtime();
        runtime.Dialogs.PowerPointPath = runtime.Directory.File("dark.pptx");

        await runtime.Controller.ExportPowerPointAsync(Snapshot(GraphThemes.Dark), Token);

        var background = GraphThemes.Dark.Background;
        Assert.Equal($"{background.Red:X2}{background.Green:X2}{background.Blue:X2}", runtime.PowerPoint.Last.Image.BackgroundHex);
    }

    // 8
    [Fact]
    public async Task AFileThatCannotBeWrittenIsReportedAndNothingElse()
    {
        using var runtime = new Runtime();
        runtime.Dialogs.PngPath = Path.Combine(runtime.Directory.DirectoryPath, "no-such-folder", "graph.png");

        await runtime.Controller.ExportPngAsync(Snapshot(), Token);

        var error = Assert.Single(runtime.Dialogs.Errors);
        Assert.StartsWith("Unable to export the graph.", error, StringComparison.Ordinal);
        Assert.Null(runtime.Controller.LastExportedPath);
    }

    // 9
    [Fact]
    public async Task APresentationThatCannotBeWrittenIsReportedAndNothingElse()
    {
        using var runtime = new Runtime();
        runtime.Dialogs.PowerPointPath = runtime.Directory.File("graph.pptx");
        runtime.PowerPoint.Failure = new IOException("the file is open in another program");

        await runtime.Controller.ExportPowerPointAsync(Snapshot(), Token);

        var error = Assert.Single(runtime.Dialogs.Errors);
        Assert.Contains("the file is open in another program", error, StringComparison.Ordinal);
        Assert.Null(runtime.Controller.LastExportedPath);
    }

    // 10
    [Fact]
    public async Task ACancelledExportWritesNothingAndSaysNothing()
    {
        using var runtime = new Runtime();
        runtime.Dialogs.PngPath = runtime.Directory.File("graph.png");

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await runtime.Controller.ExportPngAsync(Snapshot(), cancellation.Token);

        Assert.Empty(runtime.Dialogs.Errors);
        Assert.Null(runtime.Controller.LastExportedPath);
    }

    // ---- Copy Image (#038) ----

    // A probability plot prepared the way the graph preparation prepares one, with its presentation applied.
    private static GraphExportSnapshot ProbabilitySnapshot(
        bool statistics = true,
        bool fittedLine = true,
        YAT.Application.Specifications.Specification? specification = null,
        GraphTheme? theme = null)
    {
        var data = new UnivariateGraphData(
            GraphType.ProbabilityPlot,
            Guid.NewGuid(),
            new GraphColumnInfo(Guid.NewGuid(), "Reg1", WorksheetDataType.Numeric),
            Enumerable.Range(0, 60).Select(index => 15 + (Math.Sin(index * 0.7) * 0.2)).ToArray(),
            new StringGroupData(new GraphColumnInfo(Guid.NewGuid(), "Lot", WorksheetDataType.String), Enumerable.Range(0, 60).Select(index => (string?)$"Lot {index % 2}").ToArray()));
        var configuration = new GraphConfiguration(GraphType.ProbabilityPlot, Guid.NewGuid(), [])
        {
            PresentationOptions = new GraphPresentationOptions(statistics),
            ProbabilityPlotOptions = new ProbabilityPlotOptions(fittedLine),
            Specification = specification ?? YAT.Application.Specifications.Specification.None
        };
        var model = new ProbabilityPlotRenderModelBuilder().Build(data, new ProbabilityPlotLabels("Reg1", "Lot"), configuration.ProbabilityPlotOptions, Token)!;
        var frame = GraphPresentation.Apply(model.Frame, data, configuration, Token);
        return new GraphExportSnapshot(frame, new ProbabilityPlotRenderer(model), theme ?? GraphThemes.Light);
    }

    [Fact]
    public async Task CopyImagePutsExactlyThePngAnExportWritesOnTheClipboard()
    {
        using var runtime = new Runtime();
        var snapshot = Snapshot();
        var path = runtime.Directory.File("graph.png");
        runtime.Dialogs.PngPath = path;

        await runtime.Controller.CopyImageAsync(snapshot, Token);
        await runtime.Controller.ExportPngAsync(snapshot, Token);

        var copied = Assert.Single(runtime.Clipboard.Copied);
        Assert.NotEmpty(copied);
        Assert.Equal(PngSignature, copied.Take(PngSignature.Length));
        Assert.Equal(File.ReadAllBytes(path), copied);
        Assert.Equal(new GraphExportService().RenderPng(snapshot), copied);
    }

    [Fact]
    public async Task TheCopiedImageHasTheExportSize()
    {
        using var runtime = new Runtime();

        await runtime.Controller.CopyImageAsync(Snapshot(), Token);

        using var bitmap = SkiaSharp.SKBitmap.Decode(Assert.Single(runtime.Clipboard.Copied));
        Assert.Equal(GraphExportService.ExportWidth, bitmap.Width);
        Assert.Equal(GraphExportService.ExportHeight, bitmap.Height);
    }

    [Fact]
    public async Task CopyImageIsSilentAndAsksNothing()
    {
        using var runtime = new Runtime();

        await runtime.Controller.CopyImageAsync(Snapshot(), Token);

        Assert.Empty(runtime.Dialogs.Errors);
        Assert.Empty(runtime.Dialogs.SuggestedNames);
        Assert.Null(runtime.Controller.LastExportedPath);
        Assert.Empty(runtime.PowerPoint.Saved);
    }

    // Statistics, fitted line, specification and theme are all whatever the graph shows: the copied image is the
    // graph's own export image in every combination, and the combinations are different images.
    [Fact]
    public async Task TheCopiedImageFollowsTheGraphsOwnState()
    {
        using var runtime = new Runtime();
        var specification = new YAT.Application.Specifications.Specification(14.5, 15, 15.5);
        GraphExportSnapshot[] snapshots =
        [
            ProbabilitySnapshot(),
            ProbabilitySnapshot(statistics: false),
            ProbabilitySnapshot(fittedLine: false),
            ProbabilitySnapshot(specification: specification),
            ProbabilitySnapshot(statistics: false, fittedLine: false, specification: specification),
            ProbabilitySnapshot(theme: GraphThemes.Dark)
        ];

        foreach (var snapshot in snapshots)
        {
            await runtime.Controller.CopyImageAsync(snapshot, Token);
        }

        Assert.Equal(snapshots.Length, runtime.Clipboard.Copied.Count);
        for (var index = 0; index < snapshots.Length; index++)
        {
            Assert.Equal(new GraphExportService().RenderPng(snapshots[index]), runtime.Clipboard.Copied[index]);
        }

        Assert.Equal(snapshots.Length, runtime.Clipboard.Copied.Select(Convert.ToBase64String).Distinct().Count());
        Assert.Null(snapshots[1].Frame.StatisticsPanel);
        Assert.All(((ProbabilityPlotRenderer)snapshots[2].Plot!).Model.Series, series => Assert.Null(series.FittedLine));
        Assert.Equal(3, snapshots[3].Frame.ReferenceLines.Count);
    }

    [Fact]
    public async Task CopyingLeavesTheGraphAsItWas()
    {
        using var runtime = new Runtime();
        var snapshot = ProbabilitySnapshot(specification: new YAT.Application.Specifications.Specification(14, 15, 16));
        var before = new GraphExportService().RenderPng(snapshot);
        var frame = snapshot.Frame;
        var lines = frame.ReferenceLines;
        var panel = frame.StatisticsPanel;
        var axis = frame.XAxis;

        await runtime.Controller.CopyImageAsync(snapshot, Token);
        await runtime.Controller.CopyImageAsync(snapshot, Token);

        Assert.Same(frame, snapshot.Frame);
        Assert.Same(lines, frame.ReferenceLines);
        Assert.Same(panel, frame.StatisticsPanel);
        Assert.Same(axis, frame.XAxis);
        Assert.Equal(before, new GraphExportService().RenderPng(snapshot));
        Assert.Equal(runtime.Clipboard.Copied[0], runtime.Clipboard.Copied[1]);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(System.Runtime.InteropServices.COMException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task AClipboardThatFailsIsReportedOnceAndNothingThrows(Type failure)
    {
        using var runtime = new Runtime();
        runtime.Clipboard.FailWith = (Exception)Activator.CreateInstance(failure, "The clipboard is busy.")!;

        await runtime.Controller.CopyImageAsync(Snapshot(), Token);

        Assert.Equal([GraphExportController.CopyFailureMessage], runtime.Dialogs.Errors);
        Assert.Equal("The graph could not be copied to the clipboard.", GraphExportController.CopyFailureMessage);
        Assert.Empty(runtime.Clipboard.Copied);
    }

    [Fact]
    public async Task ACancelledCopyIsSilent()
    {
        using var runtime = new Runtime();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await runtime.Controller.CopyImageAsync(Snapshot(), cancellation.Token);

        Assert.Empty(runtime.Dialogs.Errors);
        Assert.Empty(runtime.Clipboard.Copied);
    }

    [Fact]
    public async Task CopyImageDoesNotChangeWhatAnExportWrites()
    {
        using var runtime = new Runtime();
        var snapshot = Snapshot();
        var first = runtime.Directory.File("first.png");
        var second = runtime.Directory.File("second.png");

        runtime.Dialogs.PngPath = first;
        await runtime.Controller.ExportPngAsync(snapshot, Token);
        await runtime.Controller.CopyImageAsync(snapshot, Token);
        runtime.Dialogs.PngPath = second;
        await runtime.Controller.ExportPngAsync(snapshot, Token);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    // 11
    [Fact]
    public void AnExportControllerNeedsItsDialogsServiceExporterAndClipboard()
    {
        var clipboard = new FakeGraphImageClipboard();
        Assert.Throws<ArgumentNullException>(() => new GraphExportController(null!, new GraphExportService(), new FakePowerPointGraphExporter(), clipboard));
        Assert.Throws<ArgumentNullException>(() => new GraphExportController(new FakeGraphExportDialogs(), null!, new FakePowerPointGraphExporter(), clipboard));
        Assert.Throws<ArgumentNullException>(() => new GraphExportController(new FakeGraphExportDialogs(), new GraphExportService(), null!, clipboard));
        Assert.Throws<ArgumentNullException>(() => new GraphExportController(new FakeGraphExportDialogs(), new GraphExportService(), new FakePowerPointGraphExporter(), null!));
    }
}
