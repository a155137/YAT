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
            Controller = new GraphExportController(Dialogs, new GraphExportService(), PowerPoint);
        }

        public TemporaryDirectory Directory { get; } = new();

        public FakeGraphExportDialogs Dialogs { get; } = new();

        public FakePowerPointGraphExporter PowerPoint { get; } = new();

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

    // 11
    [Fact]
    public void AnExportControllerNeedsItsDialogsServiceAndExporter()
    {
        Assert.Throws<ArgumentNullException>(() => new GraphExportController(null!, new GraphExportService(), new FakePowerPointGraphExporter()));
        Assert.Throws<ArgumentNullException>(() => new GraphExportController(new FakeGraphExportDialogs(), null!, new FakePowerPointGraphExporter()));
        Assert.Throws<ArgumentNullException>(() => new GraphExportController(new FakeGraphExportDialogs(), new GraphExportService(), null!));
    }
}
