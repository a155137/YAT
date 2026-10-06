using System.Diagnostics;
using System.Globalization;
using System.Text;
using SkiaSharp;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Filtering;
using YAT.Application.Graphs;
using YAT.app.Composition;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;

namespace YAT.App.Tests.Torture;

// Task #063: the stability torture harness. A dataset is pasted into a real project the way a user pastes it, a graph is
// asked for through the Graph menu's own command with a scripted setup, and everything the application would open is
// checked: the frames it presents, the layout of every frame at several sizes, the drawing of every panel, and a PNG
// export. Nothing here draws or computes on its own - it only drives and inspects what the application does.
//
// What counts as a failure (TortureInvariants): an unexpected exception (traced by the graph setup as "could not be
// drawn"), a non-finite or reversed axis, a non-finite tick or statistic, statistics out of order, layout geometry that
// is not finite or leaves the canvas, and a PNG that is empty, of the wrong size or blank. A refusal the user can act on
// (too many panels, no rows, nothing to plot) is designed behaviour and is only recorded.

// One worksheet column: its name and its cells as text, an empty string being an empty cell.
internal sealed record TortureColumn(string Name, string[] Cells);

// A dataset to paste: columns of equal length. Describe() is what a failure reports about it.
internal sealed record TortureDataset(string Name, IReadOnlyList<TortureColumn> Columns)
{
    public int RowCount => Columns.Count == 0 ? 0 : Columns[0].Cells.Length;

    public string ToTsv()
    {
        var text = new StringBuilder();
        text.Append(string.Join('\t', Columns.Select(column => column.Name))).Append('\n');
        for (var row = 0; row < RowCount; row++)
        {
            text.Append(string.Join('\t', Columns.Select(column => column.Cells[row]))).Append('\n');
        }

        return text.ToString();
    }

    public string Describe()
    {
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"dataset={Name} rows={RowCount} columns={Columns.Count}");
        foreach (var column in Columns.Take(8))
        {
            var filled = column.Cells.Count(cell => cell.Length > 0);
            var sample = string.Join(", ", column.Cells.Where(cell => cell.Length > 0).Distinct().Take(4));
            text.Append(CultureInfo.InvariantCulture, $"\n  [{Shorten(column.Name)}] filled={filled}/{column.Cells.Length} e.g. {sample}");
        }

        if (Columns.Count > 8)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n  ... {Columns.Count - 8} more columns");
        }

        return text.ToString();
    }

    private static string Shorten(string name) => name.Length <= 40 ? name : $"{name[..37]}...";

    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static TortureColumn Numeric(string name, IEnumerable<double?> values) =>
        new(name, [.. values.Select(value => value is { } number ? Number(number) : string.Empty)]);

    public static TortureColumn Text(string name, IEnumerable<string?> values) =>
        new(name, [.. values.Select(value => value ?? string.Empty)]);
}

// The statistics a request asks for (Task #062): the eight items, in panel order.
internal sealed record TortureStatistics(bool Mean, bool StDev, bool N, bool Min, bool Q1, bool Median, bool Q3, bool Max)
{
    public static TortureStatistics Default { get; } = new(true, true, true, false, false, false, false, false);

    public static TortureStatistics All { get; } = new(true, true, true, true, true, true, true, true);

    public void ApplyTo(GraphStatisticsEditorViewModel statistics)
    {
        statistics.ShowMean = Mean;
        statistics.ShowStandardDeviation = StDev;
        statistics.ShowCount = N;
        statistics.ShowMinimum = Min;
        statistics.ShowFirstQuartile = Q1;
        statistics.ShowMedian = Median;
        statistics.ShowThirdQuartile = Q3;
        statistics.ShowMaximum = Max;
    }

    public override string ToString() =>
        string.Concat(new[] { Mean, StDev, N, Min, Q1, Median, Q3, Max }.Select(on => on ? '1' : '0'));
}

// What a scripted user asks for in the setup.
internal sealed record TortureRequest(GraphType Type, IReadOnlyList<string> Variables)
{
    public string? Group { get; init; }

    public string? Panel { get; init; }

    // For a scatter plot: X is Variables[0], Y is this.
    public string? Y { get; init; }

    public GraphVariableLayout Layout { get; init; } = GraphVariableLayout.Together;

    public TortureStatistics Statistics { get; init; } = TortureStatistics.Default;

    // A text column and the values the row filter keeps (Task #053), or none.
    public (string Column, string[] Values)? Filter { get; init; }

    public override string ToString()
    {
        var variables = Variables.Count <= 4 ? string.Join(",", Variables) : $"{Variables[0]}..{Variables[^1]} ({Variables.Count})";
        return $"type={Type} variables=[{variables}]{(Y is null ? "" : $" y={Y}")} group={Group ?? "-"} panel={Panel ?? "-"} layout={Layout} statistics={Statistics}"
            + (Filter is { } filter ? $" filter={filter.Column} in [{string.Join(",", filter.Values)}]" : string.Empty);
    }
}

// One window the request would open.
internal sealed record TortureGraph(GraphPresentationState State, IGraphPlotRenderer? Plot, IReadOnlyList<GraphPanel>? Panels);

internal sealed record TortureOutcome(IReadOnlyList<TortureGraph> Graphs, IReadOnlyList<string> Errors, IReadOnlyList<string> Traces);

// Captures what the graph setup traces when a graph fails unexpectedly (Trace.TraceError in GraphSetupController), for
// the request being drawn on this async flow only, so tests running in parallel never see each other's traces.
internal sealed class TortureTraceListener : TraceListener
{
    private static readonly AsyncLocal<List<string>?> Sink = new();
    private static readonly object Gate = new();
    private static bool _installed;

    public static List<string> Begin()
    {
        lock (Gate)
        {
            if (!_installed)
            {
                Trace.Listeners.Add(new TortureTraceListener());
                _installed = true;
            }
        }

        var sink = new List<string>();
        Sink.Value = sink;
        return sink;
    }

    public override void Write(string? message) => Record(message);

    public override void WriteLine(string? message) => Record(message);

    private static void Record(string? message)
    {
        if (Sink.Value is { } sink && message is not null)
        {
            lock (sink)
            {
                sink.Add(message);
            }
        }
    }
}

// A real project behind the Graph menu, with scripted dialogs and a window presenter that records instead of opening.
internal sealed class TortureSession : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public TortureSession()
    {
        Composition = new CompositionRoot(new FixedTimeProvider(Now), Directory.File("temp"));
        Workspace = Composition.CreateProjectWorkspace();
        Lifecycle = Composition.CreateProjectLifecycle(Workspace, Clipboard, Clipboard, new FakeProjectLifecycleDialogs());
        Graphs = Composition.CreateGraphSetup(GraphDialogs, GraphWindows);
        Shell = Composition.CreateMainWindowShellViewModel(
            Lifecycle,
            Graphs,
            Composition.CreateDescriptiveStatistics(new FakeAnalysisSetupDialogs(), new FakeAnalysisResultPresenter()),
            Composition.CreateCapabilityAnalysis(new FakeCapabilityAnalysisSetupDialogs(), new FakeAnalysisResultPresenter()));
    }

    public TemporaryDirectory Directory { get; } = new();

    public CompositionRoot Composition { get; }

    public ProjectWorkspace Workspace { get; }

    public FakeClipboard Clipboard { get; } = new();

    public FakeGraphSetupDialogs GraphDialogs { get; } = new();

    public FakeGraphWindowPresenter GraphWindows { get; } = new();

    public ProjectLifecycleController Lifecycle { get; }

    public GraphSetupController Graphs { get; }

    public MainWindowShellViewModel Shell { get; }

    public MainWindowViewModel Project => Lifecycle.Project!;

    public static async Task<TortureSession> StartAsync(TortureDataset dataset)
    {
        var session = new TortureSession();
        try
        {
            Assert.True(await session.Shell.StartAsync());
            await session.Project.GridLoadTask;
            session.Clipboard.Text = dataset.ToTsv();
            await session.Project.PasteCommand.ExecuteAsync(null);
            await session.Project.GridLoadTask;
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // Asks for the graph through the Graph menu's command, choosing in the setup what the request says. Returns null when
    // the setup cannot express the request (a column the role does not offer) - recorded by the caller, never a failure.
    public async Task<TortureOutcome?> DrawAsync(TortureRequest request)
    {
        var shown = GraphWindows.Shown.Count;
        var errors = GraphDialogs.Errors.Count;
        var expressible = true;
        GraphDialogs.Layout = request.Layout;
        GraphDialogs.AnswerAsync = null;
        GraphDialogs.Answer = setup =>
        {
            expressible = Choose(setup, request);
            if (!expressible)
            {
                var wanted = request.Variables.Concat(new[] { request.Y, request.Group, request.Panel }).OfType<string>().ToHashSet();
                LastNotOffered = string.Join(", ", setup.AvailableColumns.Where(option => wanted.Contains(option.Name)).Select(option => $"{option.Name}:{option.DataTypeName}"));
            }

            return expressible ? setup.Confirm() : null;
        };

        var traces = TortureTraceListener.Begin();
        await Command(request.Type).ExecuteAsync(null);
        if (!expressible)
        {
            return null;
        }

        var graphs = new List<TortureGraph>();
        for (var index = shown; index < GraphWindows.Shown.Count; index++)
        {
            graphs.Add(new TortureGraph(GraphWindows.Graphs[index], GraphWindows.Shown[index].Plot, GraphWindows.Panels[index]));
        }

        List<string> traced;
        lock (traces)
        {
            traced = [.. traces];
        }

        return new TortureOutcome(graphs, [.. GraphDialogs.Errors.Skip(errors)], traced);
    }

    internal static string? LastNotOffered;

    private static bool Choose(GraphSetupViewModel setup, TortureRequest request)
    {
        GraphColumnOption? Find(GraphRoleViewModel role, string name) =>
            role.Options.FirstOrDefault(option => !option.IsNone && option.Name == name);

        if (request.Type == GraphType.ScatterPlot)
        {
            var x = setup.Roles.Single(role => role.Role == GraphVariableRole.X);
            var y = setup.Roles.Single(role => role.Role == GraphVariableRole.Y);
            if (Find(x, request.Variables[0]) is not { } xOption || Find(y, request.Y!) is not { } yOption)
            {
                return false;
            }

            x.SelectedOption = xOption;
            y.SelectedOption = yOption;
        }
        else
        {
            var variables = setup.Roles.Single(role => role.AllowsMultiple);
            foreach (var name in request.Variables)
            {
                if (Find(variables, name) is not { } option)
                {
                    return false;
                }

                variables.SelectedOptions.Add(option);
            }
        }

        foreach (var (role, name) in new[] { (GraphVariableRole.Group, request.Group), (GraphVariableRole.Panel, request.Panel) })
        {
            if (name is null)
            {
                continue;
            }

            if (setup.Roles.SingleOrDefault(candidate => candidate.Role == role) is not { } target || Find(target, name) is not { } option)
            {
                return false;
            }

            target.SelectedOption = option;
        }

        if (setup.SupportsStatisticsPanel)
        {
            request.Statistics.ApplyTo(setup.Statistics);
        }

        if (request.Filter is { } filter)
        {
            if (setup.AvailableColumns.FirstOrDefault(option => option.Name == filter.Column)?.WorksheetColumnId is not { } column)
            {
                return false;
            }

            setup.Filter = new RowFilter(new TextValueSetCondition(column, filter.Values));
        }

        return true;
    }

    private CommunityToolkit.Mvvm.Input.IAsyncRelayCommand Command(GraphType type) => type switch
    {
        GraphType.ScatterPlot => Shell.ScatterPlotCommand,
        GraphType.Histogram => Shell.HistogramCommand,
        GraphType.BoxPlot => Shell.BoxPlotCommand,
        GraphType.ProbabilityPlot => Shell.ProbabilityPlotCommand,
        _ => Shell.EmpiricalCdfCommand
    };

    public void Dispose()
    {
        Workspace.Dispose();
        Directory.Dispose();
    }
}

// The sizes a graph is laid out and drawn at: a window shrunk to almost nothing, ordinary windows, and a very large one.
internal static class TortureSizes
{
    public static readonly IReadOnlyList<SKSize> All =
    [
        new(120, 90), new(260, 180), new(640, 480), new(1280, 800), new(4000, 2400)
    ];

    // The sizes of the normal suite: a narrow one, the window default and a large one.
    public static readonly IReadOnlyList<SKSize> Normal = [new(260, 180), new(640, 480), new(2400, 1500)];
}

internal static class TortureInvariants
{
    // The geometry tolerance: layouts are computed in floats.
    private const float Slack = 0.5f;

    // Checks everything a request opened, at every size; returns how many windows were checked. context is everything a
    // failure needs to be replayed: seed, case, configuration, dataset.
    public static int Verify(string context, TortureOutcome? outcome, IReadOnlyList<SKSize> sizes, bool export = true)
    {
        if (outcome is null)
        {
            return 0;
        }

        That(outcome.Traces.Count == 0, context, $"an unexpected exception was traced:\n{string.Join("\n", outcome.Traces)}");
        foreach (var error in outcome.Errors)
        {
            That(!error.Contains(GraphSetupController.PreparationFailedMessage, StringComparison.Ordinal), context, $"a graph failed unexpectedly: {error}");
        }

        foreach (var (graph, index) in outcome.Graphs.Select((graph, index) => (graph, index)))
        {
            var where = $"{context}\n  window {index + 1}/{outcome.Graphs.Count} \"{graph.State.Frame.Title}\"";
            Frame($"{where} frame", graph.State.Frame);
            Frame($"{where} base frame", graph.State.BaseFrame);
            foreach (var size in sizes)
            {
                Drawing($"{where} at {size.Width}x{size.Height}", graph, new SKRect(0, 0, size.Width, size.Height), GraphThemes.Light);
            }

            if (export)
            {
                Png($"{where} export", Export(graph, GraphThemes.Light), GraphExportService.ExportWidth, GraphExportService.ExportHeight);
            }
        }

        return outcome.Graphs.Count;
    }

    public static byte[] Export(TortureGraph graph, GraphTheme theme, int width = GraphExportService.ExportWidth, int height = GraphExportService.ExportHeight) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(graph.State.Frame, graph.Plot, theme) { Panels = graph.Panels }, width, height);

    // ---- The frame ----

    public static void Frame(string context, GraphRenderModel frame)
    {
        Axis($"{context} X axis", frame.XAxis);
        Axis($"{context} Y axis", frame.YAxis);
        foreach (var entry in frame.Legend?.Entries ?? [])
        {
            That(entry.Label is not null && entry.SeriesIndex >= 0, context, $"legend entry {entry.Label} has series index {entry.SeriesIndex}");
        }

        foreach (var line in frame.ReferenceLines)
        {
            That(double.IsFinite(line.Value), context, $"reference line {line.Label} at {line.Value}");
        }

        if (frame.StatisticsPanel is { } panel)
        {
            foreach (var row in panel.Rows)
            {
                Statistics($"{context} statistics row \"{row.Label}\"", row);
            }
        }
    }

    public static void Axis(string context, GraphAxisModel axis)
    {
        var (minimum, maximum) = (axis.Range.Minimum, axis.Range.Maximum);
        That(double.IsFinite(minimum) && double.IsFinite(maximum), context, $"range [{minimum}, {maximum}] is not finite");
        That(minimum < maximum, context, $"range [{minimum}, {maximum}] is empty or reversed");
        That(double.IsFinite(maximum - minimum), context, $"range [{minimum}, {maximum}] has a span that is not finite");
        // Ticks outside the range are allowed and not drawn (GraphAxisModel), so only their values and labels are checked.
        foreach (var tick in axis.Ticks)
        {
            That(double.IsFinite(tick.Value), context, $"tick {tick.Label} at {tick.Value}");
            Text(context, "tick label", tick.Label);
        }
    }

    public static void Statistics(string context, GraphStatisticsRow row)
    {
        That(row.Count >= 1, context, $"N = {row.Count}");
        That(double.IsFinite(row.Mean), context, $"mean {row.Mean}");
        That(row.StandardDeviation is null || (double.IsFinite(row.StandardDeviation.Value) && row.StandardDeviation >= 0), context, $"standard deviation {row.StandardDeviation}");
        Text(context, "mean", row.MeanText);
        Text(context, "standard deviation", row.StandardDeviationText);
        if (row.FiveNumbers is { } five)
        {
            double[] values = [five.Minimum, five.FirstQuartile, five.Median, five.ThirdQuartile, five.Maximum];
            That(values.All(double.IsFinite), context, $"five numbers [{string.Join(", ", values)}]");
            for (var index = 1; index < values.Length; index++)
            {
                That(values[index - 1] <= values[index], context, $"five numbers out of order [{string.Join(", ", values)}]");
            }

            // The mean lies within the observations; floating point may put it a rounding step outside.
            var slack = Math.Max(Math.Abs(five.Minimum), Math.Abs(five.Maximum)) * 1e-12;
            That(row.Mean >= five.Minimum - slack && row.Mean <= five.Maximum + slack, context, $"mean {row.Mean} outside [{five.Minimum}, {five.Maximum}]");
            foreach (var text in new[] { five.MinimumText, five.FirstQuartileText, five.MedianText, five.ThirdQuartileText, five.MaximumText })
            {
                Text(context, "five-number", text);
            }
        }
    }

    private static void Text(string context, string what, string text) =>
        That(!text.Contains("NaN", StringComparison.Ordinal) && !text.Contains('∞') && !text.Contains("Infinity", StringComparison.Ordinal), context, $"{what} text \"{text}\"");

    // ---- Layout and drawing ----

    public static void Drawing(string context, TortureGraph graph, SKRect bounds, GraphTheme theme)
    {
        var frame = graph.State.Frame;
        if (graph.Panels is { Count: > 0 } panels)
        {
            var outer = GraphPanelLayout.OuterFrame(frame);
            Layout($"{context} outer frame", outer, bounds, theme);
            if (GraphPanelLayout.Area(frame, bounds, theme) is { } area)
            {
                Rect($"{context} panel area", area, bounds);
                var cells = GraphPanelLayout.Cells(area, panels.Count);
                That(cells.Count == panels.Count, context, $"{cells.Count} cells for {panels.Count} panels");
                for (var index = 0; index < cells.Count; index++)
                {
                    Rect($"{context} panel {index + 1} cell", cells[index], area);
                    Layout($"{context} panel {index + 1} \"{panels[index].Title}\"", GraphPanelLayout.PanelFrame(frame, panels[index].Title, cells[index], theme), cells[index], theme);
                }
            }
        }
        else
        {
            Layout(context, frame, bounds, theme);
        }

        using var surface = SKSurface.Create(new SKImageInfo((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height)));
        GraphDrawing.Render(new SkiaGraphRenderer(), surface.Canvas, frame, graph.Plot, graph.Panels, bounds, theme);
    }

    public static void Layout(string context, GraphRenderModel frame, SKRect bounds, GraphTheme theme)
    {
        var layout = SkiaGraphRenderer.Layout(frame, bounds, theme);
        foreach (var (name, rect) in new[]
        {
            ("canvas", layout.Canvas), ("title", layout.TitleArea), ("plot", layout.PlotArea), ("x axis", layout.XAxisArea),
            ("y axis", layout.YAxisArea), ("legend", layout.LegendArea), ("statistics", layout.StatisticsPanelArea),
            ("reference labels", layout.ReferenceLabelArea)
        })
        {
            Rect($"{context} {name} area", rect, bounds);
        }
    }

    private static void Rect(string context, SKRect rect, SKRect bounds)
    {
        That(float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom), context, $"rect {rect} is not finite");
        That(rect.Width >= -Slack && rect.Height >= -Slack, context, $"rect {rect} has a negative size");
        if (rect.Width > 0 && rect.Height > 0)
        {
            That(
                rect.Left >= bounds.Left - Slack && rect.Top >= bounds.Top - Slack && rect.Right <= bounds.Right + Slack && rect.Bottom <= bounds.Bottom + Slack,
                context,
                $"rect {rect} leaves {bounds}");
        }
    }

    // ---- The exported image ----

    public static void Png(string context, byte[] png, int width, int height)
    {
        That(png.Length > 0, context, "the PNG is empty");
        using var bitmap = SKBitmap.Decode(png);
        That(bitmap is not null, context, "the PNG does not decode");
        That(bitmap!.Width == width && bitmap.Height == height, context, $"the PNG is {bitmap.Width}x{bitmap.Height}, not {width}x{height}");
        var first = bitmap.GetPixel(0, 0);
        var blank = true;
        for (var y = 0; y < bitmap.Height && blank; y += 7)
        {
            for (var x = 0; x < bitmap.Width; x += 7)
            {
                if (bitmap.GetPixel(x, y) != first)
                {
                    blank = false;
                    break;
                }
            }
        }

        That(!blank, context, "the PNG is blank");
    }

    public static void That(bool condition, string context, string violated)
    {
        if (!condition)
        {
            throw new TortureFailure($"{violated}\n  at {context}");
        }
    }
}

internal sealed class TortureFailure(string message) : Exception(message);
