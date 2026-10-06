using System.Runtime.CompilerServices;
using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;

namespace YAT.App.Tests.Torture;

// Task #063, Phase 2: lifetimes. What a graph is made of - its presentation, frames, plot renderers, panels and the
// graph setup it came from - is released once the window that showed it is gone, while the project that drew it stays
// open: nothing long-lived (the project session, the graph setup controller, the shell, the Graphs list) keeps it. And
// a project replaced by New Project is released with its worksheet, its view model and its session.
//
// Each test makes the objects in a method of its own that hands back only weak references - so no local of the test,
// and no state machine of an async method still running, holds them - then drops what stands in for the window, and
// collects: GC.Collect, GC.WaitForPendingFinalizers, GC.Collect. A real Avalonia window cannot be made here without a
// UI test host; its closing is what removes it from the Graphs list (AvaloniaGraphWindowPresenter), which is tested
// with a stand-in, and the window itself belongs to a runtime probe.
public sealed class LifetimeTests
{
    private static void Collect()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    // Collected only once the test has left the call stack it is on: an await that completes asynchronously runs what
    // follows it inside the code that completed it, whose frames below still reference what that code used (the
    // project replaced, the graph prepared) until they return. Yielding leaves them, as the UI thread leaves its stack
    // between messages.
    private static async Task ReleasedAsync(IEnumerable<(string What, WeakReference Reference)> references)
    {
        await Task.Yield();
        Collect();
        var alive = references.Where(reference => reference.Reference.IsAlive).Select(reference => reference.What).ToList();
        Assert.True(alive.Count == 0, $"still alive after collection: {string.Join(", ", alive)}");
    }

    // The check is not vacuous: something still referenced is reported, by name.
    [Fact]
    public async Task SomethingStillReferencedIsReported()
    {
        var kept = new object();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => ReleasedAsync([("the kept object", new WeakReference(kept))]));

        Assert.Contains("the kept object", failure.Message, StringComparison.Ordinal);
        GC.KeepAlive(kept);
    }

    // ---- A graph closed while its project stays open ----

    public static TheoryData<string> Graphs => ["histogram in panels", "fifty separately", "box plot", "scatter"];

    private static TortureRequest Request(string graph) => graph switch
    {
        "histogram in panels" => new(GraphType.Histogram, ["Reg1", "Reg2"]) { Group = "Lot", Panel = "Site", Statistics = TortureStatistics.All },
        "fifty separately" => new(GraphType.EmpiricalCdf, [.. Enumerable.Range(1, 50).Select(index => $"Reg{index}")]) { Layout = GraphVariableLayout.Separate },
        "box plot" => new(GraphType.BoxPlot, ["Reg1", "Reg2", "Reg3"]) { Group = "Lot" },
        _ => new(GraphType.ScatterPlot, ["Reg1"]) { Y = "Reg2", Group = "Lot", Panel = "Site" }
    };

    [Theory]
    [MemberData(nameof(Graphs))]
    public async Task AClosedGraphIsReleasedWhileItsProjectStaysOpen(string graph)
    {
        using var session = await TortureSession.StartAsync(MaximumCombinationTests.Dataset(50, rows: 90));

        var references = await DrawAndUseAsync(session, Request(graph));

        // The windows are closed: the presenter that stands in for them forgets them, and the setup dialogs are gone.
        session.GraphWindows.Shown.Clear();
        session.GraphWindows.Graphs.Clear();
        session.GraphWindows.Panels.Clear();
        session.GraphWindows.Cascades.Clear();
        session.GraphDialogs.Shown.Clear();
        session.GraphDialogs.Answer = _ => null;

        await ReleasedAsync(references);
        Assert.NotNull(session.Project.SelectedWorksheet);
    }

    // Draws the graph, then does what a window does with it - draws it, zooms, pans and resets it with a view controller
    // whose change is listened to, exports it - and hands back weak references only.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<(string, WeakReference)>> DrawAndUseAsync(TortureSession session, TortureRequest request)
    {
        var outcome = await session.DrawAsync(request);
        Assert.NotNull(outcome);
        Assert.Empty(outcome.Errors);
        Assert.NotEmpty(outcome.Graphs);
        return Use(outcome, session.GraphDialogs.LastSetup);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<(string, WeakReference)> Use(TortureOutcome outcome, GraphSetupViewModel setup)
    {
        var references = new List<(string, WeakReference)> { ("graph setup", new WeakReference(setup)) };
        foreach (var (graph, index) in outcome.Graphs.Select((graph, index) => (graph, index)))
        {
            var controller = new GraphViewController(graph.State);
            var changes = 0;
            controller.GraphChanged += (_, _) => changes++;
            controller.ZoomAt(null, new SKRect(80, 40, 560, 400), new SKPoint(300, 200), 2);
            controller.Reset();
            if (index < 3)
            {
                TortureInvariants.Export(graph, GraphThemes.Light, 640, 400);
            }

            references.Add(($"window {index + 1} presentation", new WeakReference(graph.State)));
            references.Add(($"window {index + 1} frame", new WeakReference(graph.State.Frame)));
            references.Add(($"window {index + 1} base frame", new WeakReference(graph.State.BaseFrame)));
            references.Add(($"window {index + 1} view controller", new WeakReference(controller)));
            references.Add(($"window {index + 1} zoomed presentation", new WeakReference(controller.Graph)));
            if (graph.Plot is { } plot)
            {
                references.Add(($"window {index + 1} plot", new WeakReference(plot)));
            }

            foreach (var (panel, number) in (graph.Panels ?? []).Select((panel, number) => (panel, number)))
            {
                references.Add(($"window {index + 1} panel {number + 1}", new WeakReference(panel)));
                if (panel.Plot is { } panelPlot)
                {
                    references.Add(($"window {index + 1} panel {number + 1} plot", new WeakReference(panelPlot)));
                }
            }
        }

        return references;
    }

    // ---- An export ----

    // What an export draws on and from is released with it: the snapshot, and the native bitmap the PNG was encoded
    // from (SkiaSharp's managed wrapper of it, which owns the native memory until it is disposed or finalized).
    [Fact]
    public async Task AnExportKeepsNothingOfWhatItDrew()
    {
        using var session = await TortureSession.StartAsync(MaximumCombinationTests.Dataset(3, rows: 90));
        var outcome = await session.DrawAsync(Request("histogram in panels"));
        var graph = Assert.Single(outcome!.Graphs);

        var references = Export(graph);

        await ReleasedAsync(references);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<(string, WeakReference)> Export(TortureGraph graph)
    {
        var snapshot = new GraphExportSnapshot(graph.State.Frame, graph.Plot, GraphThemes.Light) { Panels = graph.Panels };
        var png = new GraphExportService().RenderPng(snapshot, 800, 500);
        var decoded = SKBitmap.Decode(png);
        Assert.Equal(800, decoded.Width);
        return [("export snapshot", new WeakReference(snapshot)), ("decoded bitmap", new WeakReference(decoded)), ("PNG bytes", new WeakReference(png))];
    }

    // ---- The Graphs list ----

    // Fifty windows listed, activated, renamed and closed - in rounds - leave an empty list that keeps none of them: the
    // item's way back to its window (a closure over it) goes with the item.
    [Fact]
    public async Task TheGraphsListKeepsNoWindowItListedOnceItIsClosed()
    {
        var graphs = new OpenGraphsViewModel();

        for (var round = 0; round < 5; round++)
        {
            var references = ListAndClose(graphs, round);

            Assert.Empty(graphs.Items);
            Assert.False(graphs.HasGraphs);
            Assert.Equal("Graphs", graphs.Header);
            Assert.Null(graphs.ActiveItem);
            await ReleasedAsync(references);
        }
    }

    // A stand-in for a graph window: something the list's way back to it holds.
    private sealed class StandInWindow
    {
        public byte[] Weight { get; } = new byte[64 * 1024];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<(string, WeakReference)> ListAndClose(OpenGraphsViewModel graphs, int round)
    {
        var references = new List<(string, WeakReference)>();
        var items = new List<OpenGraphItem>();
        for (var index = 0; index < 50; index++)
        {
            var window = new StandInWindow();
            var item = graphs.Add($"Histogram of Reg{index + 1}", () => GC.KeepAlive(window));
            items.Add(item);
            references.Add(($"round {round} window {index + 1}", new WeakReference(window)));
            references.Add(($"round {round} item {index + 1}", new WeakReference(item)));
        }

        Assert.Equal(50, graphs.Items.Count);
        for (var index = 0; index < items.Count; index += 7)
        {
            graphs.MarkActive(items[index]);
            graphs.Rename(items[index], $"Renamed {index}");
        }

        // Closed in an order of their own, not the order they opened.
        foreach (var item in items.Where((_, index) => index % 2 == 1).Concat(items.Where((_, index) => index % 2 == 0)).Reverse())
        {
            graphs.Remove(item);
        }

        return references;
    }

    // ---- A project replaced ----

    [Fact]
    public async Task AProjectReplacedByNewProjectIsReleased()
    {
        using var session = await TortureSession.StartAsync(MaximumCombinationTests.Dataset(20, rows: 2000));

        var references = await DrawAndReplaceAsync(session);
        session.GraphWindows.Shown.Clear();
        session.GraphWindows.Graphs.Clear();
        session.GraphWindows.Panels.Clear();
        session.GraphDialogs.Shown.Clear();

        await ReleasedAsync(references);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<(string, WeakReference)>> DrawAndReplaceAsync(TortureSession session)
    {
        var outcome = await session.DrawAsync(Request("histogram in panels"));
        Assert.NotNull(outcome);
        var old = session.Project;
        var references = new List<(string, WeakReference)>
        {
            ("old project view model", new WeakReference(old)),
            ("old project session", new WeakReference(session.Workspace.CurrentSession)),
            ("old worksheet", new WeakReference(old.SelectedWorksheet)),
            ("old graph", new WeakReference(outcome.Graphs[0].State))
        };

        session.ProjectDialogs.Choices.Enqueue(SaveChangesChoice.Discard);
        Assert.True(await session.Lifecycle.NewProjectAsync());
        Assert.NotSame(old, session.Project);
        await session.Project.GridLoadTask;
        return references;
    }
}
