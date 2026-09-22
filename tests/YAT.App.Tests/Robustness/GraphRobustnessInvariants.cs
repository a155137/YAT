using System.Globalization;
using System.Text;
using YAT.Analytics.Statistics;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Robustness;

// What must hold for every valid dataset a graph is given, checked on the render model the graph's own builder made.
//
// Only two kinds of rule belong here:
//   * mathematical invariants - true of the statistic whatever the implementation (Q1 <= median <= Q3, an empirical
//     CDF ends at 100 %);
//   * implementation invariants - promises the current graph code makes on purpose (the histogram never samples, so
//     its counts add up).
// Visual expectations (what a graph should look like) do not belong here. Task #034.1 is why this matters: "the
// whiskers enclose the box" looked like an invariant, was written into the model as one, and is false for
// interpolated quartiles. Every rule below says which kind it is.
internal static class GraphRobustnessInvariants
{
    // The one rule sampling has to keep: statistics are computed from every observation. The sampled build only gets
    // as many points as there are series, so that every series still draws one - DisplaySampling deliberately stops
    // representing every group when the budget is smaller than that, which is presentation policy, not statistics.
    public static int SamplingBudget(RobustnessGraph graph, GraphData data) => graph switch
    {
        RobustnessGraph.BoxPlot => 1,
        RobustnessGraph.Scatter => Math.Max(1, ExpectedSeries(((ScatterGraphData)data).Group, ((ScatterGraphData)data).XValues.Span).Count),
        _ => Math.Max(1, ExpectedSeries(((UnivariateGraphData)data).Group, ((UnivariateGraphData)data).Values.Span).Count)
    };

    // Runs one case through one graph at builder level (and, if asked, through the renderer): build, check every
    // invariant, build again and compare, build with a spent display budget and compare the statistics.
    // specification: prepared with the graph, as a user who typed it would (#036). Every build of the case gets the same.
    public static BuiltGraph Exercise(
        RobustnessCase robustnessCase,
        RobustnessGraph graph,
        GraphTheme? renderTheme,
        bool repeat = true,
        Specification? specification = null,
        ProbabilityPlotOptions? probabilityPlotOptions = null,
        HistogramOptions? histogramOptions = null)
    {
        var data = RobustnessGraphs.DataFor(graph, robustnessCase);
        return Exercise(robustnessCase.Describe(RobustnessGraphs.Name(graph)), graph, data, renderTheme, repeat, specification, probabilityPlotOptions, histogramOptions);
    }

    public static BuiltGraph Exercise(
        string context,
        RobustnessGraph graph,
        GraphData data,
        GraphTheme? renderTheme,
        bool repeat = true,
        Specification? specification = null,
        ProbabilityPlotOptions? probabilityPlotOptions = null,
        HistogramOptions? histogramOptions = null)
    {
        if (specification is { IsEmpty: false })
        {
            context += $" with specification {Describe(specification)}";
        }

        if (probabilityPlotOptions is { ShowFittedLine: false } && graph == RobustnessGraph.ProbabilityPlot)
        {
            context += " without fitted lines";
        }

        if (histogramOptions is not null && histogramOptions != HistogramOptions.Default && graph == RobustnessGraph.Histogram)
        {
            context += $" with histogram options {histogramOptions}";
        }

        var built = BuildOrFail(context, graph, data, specification: specification, probabilityPlotOptions: probabilityPlotOptions, histogramOptions: histogramOptions);
        Verify(context, built);

        if (repeat && built.Model is not null)
        {
            // Implementation invariant: the same data builds the same graph, points and all.
            var again = BuildOrFail(context, graph, data, specification: specification, probabilityPlotOptions: probabilityPlotOptions, histogramOptions: histogramOptions);
            That(Fingerprint(again, statisticsOnly: false) == Fingerprint(built, statisticsOnly: false), context,
                "building the same data twice must give the same model");

            if (graph != RobustnessGraph.Histogram)
            {
                // Sampling must not change statistics (the histogram never samples).
                var sampled = BuildOrFail(context, graph, data, SamplingBudget(graph, data), specification, probabilityPlotOptions, histogramOptions);
                Verify(context + " (sampled)", sampled);
                var expected = Fingerprint(built, statisticsOnly: true);
                var actual = Fingerprint(sampled, statisticsOnly: true);
                That(actual == expected, context, $"sampling changed the statistics:\n    default: {expected}\n    sampled: {actual}");
            }
        }

        if (renderTheme is not null && built.Model is not null)
        {
            try
            {
                RobustnessGraphs.Render(built, renderTheme);
            }
            catch (Exception exception)
            {
                Assert.Fail($"{context}\n  rendering threw: {exception}");
            }
        }

        return built;
    }

    public static BuiltGraph BuildOrFail(
        string context,
        RobustnessGraph graph,
        GraphData data,
        int maximumRenderedPoints = DisplaySampling.DefaultMaximumRenderedPoints,
        Specification? specification = null,
        ProbabilityPlotOptions? probabilityPlotOptions = null,
        HistogramOptions? histogramOptions = null)
    {
        try
        {
            return RobustnessGraphs.Build(graph, data, maximumRenderedPoints, TestContext.Current.CancellationToken, specification, probabilityPlotOptions, histogramOptions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Assert.Fail($"{context}\n  valid finite input made the builder throw: {exception}");
            throw;
        }
    }

    // ---- Shared invariants ----

    public static void Verify(string context, BuiltGraph built)
    {
        // Implementation invariant: a builder has nothing to draw exactly when it was given no observations.
        var effective = EffectiveCount(built.Data);
        That((built.Model is null) == (effective == 0), context,
            $"builder returned {(built.Model is null ? "no model" : "a model")} for {effective} effective observations");

        if (built.Model is null)
        {
            return;
        }

        Frame(context, built.Frame!);
        StatisticsPanel(context, built);
        SpecificationLines(context, built);

        switch (built.Model)
        {
            case HistogramRenderModel histogram:
                Histogram(context, (UnivariateGraphData)built.Data, histogram, built.HistogramOptions);
                break;
            case BoxPlotRenderModel boxPlot:
                BoxPlot(context, (MultiVariableGraphData)built.Data, boxPlot);
                break;
            case ProbabilityPlotRenderModel probability:
                ProbabilityPlot(context, (UnivariateGraphData)built.Data, probability, built.ProbabilityPlotOptions);
                break;
            case EmpiricalCdfRenderModel empirical:
                EmpiricalCdf(context, (UnivariateGraphData)built.Data, empirical);
                break;
            case ScatterRenderModel scatter:
                Scatter(context, (ScatterGraphData)built.Data, scatter);
                break;
        }
    }

    public static int EffectiveCount(GraphData data) => data.Count;

    // Implementation invariant (also enforced by the model constructors, checked again here): both axes are finite,
    // non-empty ranges with finite ticks.
    private static void Frame(string context, GraphRenderModel frame)
    {
        foreach (var (name, axis) in (ReadOnlySpan<(string, GraphAxisModel)>)[("X", frame.XAxis), ("Y", frame.YAxis)])
        {
            var range = axis.Range;
            That(range.IsValid && double.IsFinite(range.Minimum) && double.IsFinite(range.Maximum) && range.Minimum < range.Maximum,
                context, $"{name} axis range {range.Minimum:R}..{range.Maximum:R} must be finite and non-empty");
            That(axis.Ticks.All(tick => double.IsFinite(tick.Value) && tick.Label is not null), context, $"{name} axis ticks must be finite");
        }
    }

    // ---- Statistics panel ----

    // Implementation invariant: a graph type that offers the panel has one (statistics are shown by default); one that
    // does not never has one. Then, for the panel: one row per series of the graph - same labels, same order, same
    // colours, same N - adding up to every observation (implementation invariants), and statistics that are what they
    // must be whatever the implementation (mathematical invariants): a finite mean within the observations, a finite
    // non-negative standard deviation that exists exactly for N >= 2, and exactly 0 for exactly constant data.
    private static void StatisticsPanel(string context, BuiltGraph built)
    {
        var panel = built.Frame!.StatisticsPanel;
        var offered = GraphTypeDefinitions.For(RobustnessGraphs.TypeOf(built.Graph)).Supports(GraphCapability.StatisticsPanel);
        That((panel is not null) == offered, context,
            offered ? "the graph type offers a statistics panel but the graph has none" : "a graph type without a statistics panel has one");

        if (panel is null)
        {
            return;
        }

        var data = (UnivariateGraphData)built.Data;
        var expected = ExpectedSeries(data.Group, data.Values.Span);

        That(panel.IsGrouped == (data.Group is not null), context, "the panel is grouped exactly when the graph is");
        That(panel.Rows.Count == expected.Count, context, $"the panel has {panel.Rows.Count} rows for {expected.Count} series");
        That(panel.Rows.Sum(row => row.Count) == EffectiveCount(data), context,
            $"the panel counts {panel.Rows.Sum(row => row.Count)} of {EffectiveCount(data)} observations");

        for (var index = 0; index < Math.Min(panel.Rows.Count, expected.Count); index++)
        {
            var row = panel.Rows[index];
            var (label, values) = expected[index];
            var where = $"statistics row {index} ({row.Label})";

            That(row.Label == label, context, $"{where} is labelled '{row.Label}' where the graph's series is '{label}'");
            That(row.SeriesIndex == (data.Group is null ? null : index), context, $"{where} has series index {row.SeriesIndex}");
            That(row.Count == values.Length, context, $"{where} counts {row.Count} of {values.Length} observations");
            That(row.CountText == row.Count.ToString(CultureInfo.InvariantCulture), context, $"{where} shows N as '{row.CountText}'");

            var minimum = values.Min();
            var maximum = values.Max();
            That(double.IsFinite(row.Mean) && WithinTolerance(row.Mean, minimum, maximum, values.Length), context,
                $"{where} mean {row.Mean:R} lies outside {minimum:R}..{maximum:R}");
            That(row.Mean == Descriptives.Mean(values), context, $"{where} mean {row.Mean:R} is not the Analytics mean");

            if (values.Length < 2)
            {
                That(row.StandardDeviation is null, context, $"{where} has a standard deviation for a single observation");
            }
            else
            {
                That(row.StandardDeviation is { } spread && double.IsFinite(spread) && spread >= 0, context,
                    $"{where} standard deviation {row.StandardDeviation:R} must be finite and non-negative for N >= 2");
                That(!(minimum == maximum) || row.StandardDeviation == 0, context,
                    $"{where} of exactly constant data has standard deviation {row.StandardDeviation:R}, not 0");
            }
        }
    }

    // ---- Specification lines (#036) ----

    // Implementation invariants: a graph type without the capability, or a graph without a specification, shows the
    // builder's own X axis and no line at all. Otherwise there is one X line per finite value - LSL, Target, USL, in
    // that order, labelled "<name> <G8 value>" - the Y axis is the builder's, and the displayed X axis is the builder's
    // reached out by GraphAxisRanges.Including: it covers the builder's axis, its ticks are the nice ticks of the new
    // range, and its title is kept.
    //
    // Mathematical invariant: whenever the values and the axis are of a magnitude the padding can separate, every value
    // lies strictly inside the displayed axis, so no line is ever drawn on the frame or lost outside it. Beyond that
    // (values near the limits of double) the axis may stay as the builder made it, and must then be exactly that.
    private static void SpecificationLines(string context, BuiltGraph built)
    {
        var frame = built.Frame!;
        var builder = RobustnessGraphs.BuilderFrame(built.Model!);
        var specification = built.Specification;
        var offered = GraphTypeDefinitions.For(RobustnessGraphs.TypeOf(built.Graph)).Supports(GraphCapability.SpecificationLines);

        That(ReferenceEquals(frame.YAxis, builder.YAxis), context, "the specification changed the Y axis");

        var expected = new List<(string Name, double Value, GraphReferenceLineKind Kind)>();
        if (offered)
        {
            AddExpected(expected, GraphSpecificationLinesBuilder.LowerLimitLabel, specification.LowerLimit, GraphReferenceLineKind.SpecificationLimit);
            AddExpected(expected, GraphSpecificationLinesBuilder.TargetLabel, specification.Target, GraphReferenceLineKind.Target);
            AddExpected(expected, GraphSpecificationLinesBuilder.UpperLimitLabel, specification.UpperLimit, GraphReferenceLineKind.SpecificationLimit);
        }

        That(frame.ReferenceLines.Count == expected.Count, context,
            $"the frame has {frame.ReferenceLines.Count} reference lines for {expected.Count} specification values");
        if (expected.Count == 0)
        {
            That(ReferenceEquals(frame.XAxis, builder.XAxis), context, "without specification lines the X axis must be the builder's");
            return;
        }

        for (var index = 0; index < Math.Min(frame.ReferenceLines.Count, expected.Count); index++)
        {
            var line = frame.ReferenceLines[index];
            var (name, value, kind) = expected[index];
            That(line.Axis == GraphReferenceAxis.X && line.Value == value && line.Kind == kind && double.IsFinite(line.Value), context,
                $"reference line {index} is {line.Axis} {line.Value:R} {line.Kind}, not X {value:R} {kind}");
            That(line.Label == GraphSpecificationLinesBuilder.Label(name, value), context, $"reference line {index} is labelled '{line.Label}'");
        }

        var values = expected.Select(item => item.Value).ToArray();
        var axis = frame.XAxis;
        var original = builder.XAxis.Range;

        That(axis.Range == GraphAxisRanges.Including(original, values), context,
            $"the X axis {axis.Range.Minimum:R}..{axis.Range.Maximum:R} is not the builder's axis reached out to the specification");
        That(axis.Range.Minimum <= original.Minimum && axis.Range.Maximum >= original.Maximum, context,
            "the specification narrowed the X axis");
        That(axis.Title == builder.XAxis.Title, context, "the specification changed the X axis title");

        if (axis.Range == original)
        {
            That(ReferenceEquals(axis, builder.XAxis), context, "an unchanged X range must keep the builder's axis");
        }
        else
        {
            That(axis.Ticks.Select(tick => tick.Value).SequenceEqual(GraphAxisTicks.Nice(axis.Range).Select(tick => tick.Value)), context,
                "a widened X axis must have the nice ticks of its new range");
            // GraphAxisModel allows ticks outside the range (they are not drawn), and a nice tick is k * step, which can
            // round a last-bit past an edge; what must not happen is a tick meaningfully outside the new range.
            var slack = axis.Range.Span * 1e-9;
            That(axis.Ticks.All(tick => tick.Value >= axis.Range.Minimum - slack && tick.Value <= axis.Range.Maximum + slack), context,
                Invariant($"a widened X axis has a tick outside its range {axis.Range.Minimum:R}..{axis.Range.Maximum:R}: {string.Join(", ", axis.Ticks.Select(tick => tick.Value.ToString("R", CultureInfo.InvariantCulture)))}"));
        }

        var lowest = Math.Min(values.Min(), original.Minimum);
        var highest = Math.Max(values.Max(), original.Maximum);
        var magnitude = Math.Max(Math.Abs(lowest), Math.Abs(highest));
        var representable = magnitude <= 1e300 && highest - lowest > 1e-9 * magnitude;
        var inside = values.All(value => value > axis.Range.Minimum && value < axis.Range.Maximum);
        That(inside || (!representable && axis.Range == original), context,
            $"a specification value lies outside or on the X axis {axis.Range.Minimum:R}..{axis.Range.Maximum:R}");
    }

    private static void AddExpected(
        List<(string Name, double Value, GraphReferenceLineKind Kind)> expected,
        string name,
        double? value,
        GraphReferenceLineKind kind)
    {
        if (value is { } number && double.IsFinite(number))
        {
            expected.Add((name, number, kind));
        }
    }

    public static string Describe(Specification specification) =>
        Invariant($"LSL={specification.LowerLimit:R} Target={specification.Target:R} USL={specification.UpperLimit:R}");

    // ---- Histogram ----

    private static void Histogram(string context, UnivariateGraphData data, HistogramRenderModel model, HistogramOptions options)
    {
        var expected = ExpectedSeries(data.Group, data.Values.Span);
        var fixedGrid = options.BinningMode == HistogramBinningMode.WidthAndStart;
        That(model.SourceObservationCount == data.Count, context, $"histogram counted {model.SourceObservationCount} of {data.Count} observations");
        That(model.Series.Select(series => series.Label).SequenceEqual(expected.Select(series => series.Label)), context,
            "histogram series must be the groups in first-observed order");

        // Implementation invariants: the bin count stays within the limits HistogramBinCount keeps to - automatic,
        // counted and fixed bins alike (#039) - and the bins are equal-width intervals built from one list of edges.
        That(model.Bins.Count is >= HistogramBinCount.MinimumBinCount and <= HistogramBinCount.MaximumBinCount, context,
            $"histogram has {model.Bins.Count} bins");
        for (var index = 1; index < model.Bins.Count; index++)
        {
            That(model.Bins[index - 1].UpperEdge == model.Bins[index].LowerEdge, context, $"bins {index - 1} and {index} are not contiguous");
        }

        var first = model.Bins[0].LowerEdge;
        var last = model.Bins[^1].UpperEdge;
        foreach (var value in data.Values.Span)
        {
            // A fixed grid always reaches past its largest value (#039); bins over the data's range end on it.
            That(value >= first && (fixedGrid ? value < last : value <= last), context,
                $"observation {value:R} lies outside the bins {first:R}..{last:R}");
        }

        That(Within(model.Frame.XAxis.Range, first) && Within(model.Frame.XAxis.Range, last), context, "the X axis must cover the bins");

        // Implementation invariant: the histogram never samples, so every observation is counted exactly once.
        var maximum = 0;
        var tallest = 0d;
        for (var index = 0; index < model.Series.Count; index++)
        {
            var series = model.Series[index];
            var sum = series.Counts.Sum();
            That(sum == series.ObservationCount, context, $"series '{series.Label}' counts add up to {sum}, not {series.ObservationCount}");
            That(series.ObservationCount == expected[index].Values.Length, context,
                $"series '{series.Label}' has {series.ObservationCount} observations, the data {expected[index].Values.Length}");
            maximum = Math.Max(maximum, series.Counts.Count == 0 ? 0 : series.Counts.Max());
            tallest = Math.Max(tallest, series.Heights.Count == 0 ? 0 : series.Heights.Max());

            // Implementation invariant: counted again here, independently, every value lands in the bin its edges put
            // it in - [lower, upper), the last bin also holding its upper edge except on a fixed grid (#039).
            var recount = new int[model.Bins.Count];
            foreach (var value in expected[index].Values)
            {
                var bin = model.Bins.Count - 1;
                for (var candidate = 0; candidate < model.Bins.Count; candidate++)
                {
                    if (value < model.Bins[candidate].UpperEdge)
                    {
                        bin = candidate;
                        break;
                    }
                }

                recount[bin]++;
            }

            That(recount.SequenceEqual(series.Counts), context,
                $"series '{series.Label}' counts {string.Join(",", series.Counts)} are not the recount {string.Join(",", recount)}");

            HistogramHeights(context, model, series, options.YScale);
        }

        That(model.MaximumCount == maximum, context, $"maximum count {model.MaximumCount} is not the largest bin count {maximum}");
        That(model.MaximumHeight == tallest, context, $"maximum height {model.MaximumHeight:R} is not the tallest bar {tallest:R}");
        That(model.YScale == options.YScale, context, $"the histogram is on the {model.YScale} scale, not {options.YScale}");

        // Implementation invariant: the Y axis starts at zero and reaches the tallest bar on its scale (the tallest count
        // on the frequency scale).
        That(Within(model.Frame.YAxis.Range, 0) && Within(model.Frame.YAxis.Range, model.MaximumHeight), context,
            $"the Y axis must cover 0..{model.MaximumHeight:R}");

        if (fixedGrid)
        {
            FixedHistogramGrid(context, model, options);
        }
    }

    // Mathematical invariants of the Y scales (#039): heights are finite and non-negative; frequency is the count;
    // percent is each series' own share, adding up to 100; density has unit area per series, over each bin's width.
    private static void HistogramHeights(string context, HistogramRenderModel model, HistogramSeriesRenderModel series, HistogramYScale scale)
    {
        var where = $"series '{series.Label}' on the {scale} scale";
        That(series.Heights.Count == series.Counts.Count, context, $"{where} has {series.Heights.Count} heights for {series.Counts.Count} bins");
        That(series.Heights.All(height => double.IsFinite(height) && height >= 0), context, $"{where} has a height that is not finite and non-negative");

        switch (scale)
        {
            case HistogramYScale.Frequency:
                That(series.Heights.Select((height, index) => height == series.Counts[index]).All(same => same), context,
                    $"{where}: a height is not its count");
                break;

            case HistogramYScale.Percent:
                That(Math.Abs(series.Heights.Sum() - 100) <= 1e-9 * 100, context, $"{where}: percents add up to {series.Heights.Sum():R}, not 100");
                break;

            case HistogramYScale.Density:
                var area = series.Heights.Select((height, index) => height * model.Bins[index].Width).Sum();
                That(Math.Abs(area - 1) <= 1e-9, context, $"{where}: the bars' area is {area:R}, not 1");
                break;
        }
    }

    // Implementation invariants of a fixed width-and-start grid (#039): every edge is exactly start + k x width for
    // consecutive whole numbers k (computed, never accumulated, so exact equality is the contract), the width is the
    // user's, and the first and last bins hold data - the grid reaches only as far as the data needs.
    private static void FixedHistogramGrid(string context, HistogramRenderModel model, HistogramOptions options)
    {
        var width = options.BinWidth!.Value;
        var start = options.BinStart!.Value;
        var k = Math.Round((model.Bins[0].LowerEdge - start) / width);
        for (var index = 0; index < model.Bins.Count; index++)
        {
            That(model.Bins[index].LowerEdge == start + ((k + index) * width) && model.Bins[index].UpperEdge == start + ((k + index + 1) * width), context,
                $"bin {index} {model.Bins[index].LowerEdge:R}..{model.Bins[index].UpperEdge:R} is not start + k x width for k = {k + index:R}");
        }

        That(model.Series.Sum(series => series.Counts[0]) > 0 && model.Series.Sum(series => series.Counts[^1]) > 0, context,
            "a fixed grid must begin and end with bins that hold data");
    }

    // ---- Box Plot ----

    private static void BoxPlot(string context, MultiVariableGraphData data, BoxPlotRenderModel model)
    {
        That(model.SourceObservationCount == data.Count, context, $"box plot used {model.SourceObservationCount} of {data.Count} observations");

        // The categories the data defines: every variable in order, split into its groups in first-observed order; a
        // variable without observations keeps a slot of its own and draws no box.
        var grouped = data.Variables.Any(variable => variable.Group is not null);
        var categories = new List<(string Label, double[] Values)>();
        foreach (var variable in data.Variables)
        {
            var groups = ExpectedSeries(variable.Group, variable.Values.Span);
            if (groups.Count == 0)
            {
                categories.Add((variable.Variable.Name, []));
                continue;
            }

            categories.AddRange(groups.Select(group => (grouped ? $"{variable.Variable.Name} / {group.Label}" : variable.Variable.Name, group.Values)));
        }

        That(model.Categories.SequenceEqual(categories.Select(category => category.Label)), context,
            $"categories [{string.Join(" | ", model.Categories)}] are not the variables and first-observed groups " +
            $"[{string.Join(" | ", categories.Select(category => category.Label))}]");

        var seriesByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var outliers = 0;
        for (var index = 0; index < categories.Count; index++)
        {
            var boxes = model.Boxes.Where(box => box.CategoryIndex == index).ToArray();
            var observations = categories[index].Values;
            if (observations.Length == 0)
            {
                That(boxes.Length == 0, context, $"category '{categories[index].Label}' has no observations but draws a box");
                continue;
            }

            That(boxes.Length == 1, context, $"category '{categories[index].Label}' must draw exactly one box");
            var box = boxes[0];
            var boxContext = $"{context}\n  box '{box.Label}'";
            outliers += box.OutlierCount;
            That(box.ObservationCount == observations.Length, boxContext, $"box has {box.ObservationCount} observations, the data {observations.Length}");

            Box(boxContext, box, observations, model.Frame.YAxis.Range);

            // Implementation invariant (Task #034 §34): a group keeps one colour across variables.
            if (grouped)
            {
                var group = box.Label[(box.Label.IndexOf(" / ", StringComparison.Ordinal) + 3)..];
                if (seriesByGroup.TryGetValue(group, out var series))
                {
                    That(series == box.SeriesIndex, boxContext, $"group '{group}' changes colour between variables");
                }
                else
                {
                    seriesByGroup.Add(group, box.SeriesIndex);
                }
            }
        }

        That(model.OutlierCount == outliers, context, $"model reports {model.OutlierCount} outliers, its boxes {outliers}");
    }

    private static void Box(string context, BoxPlotBoxRenderModel box, double[] observations, GraphAxisRange vertical)
    {
        var sorted = observations.Order().ToArray();

        // Mathematical invariants. The whiskers are NOT required to enclose the box: with interpolated quartiles a
        // whisker can end inside it (Task #034.1).
        That(box.FirstQuartile <= box.Median && box.Median <= box.ThirdQuartile, context,
            $"Q1 {box.FirstQuartile:R} <= median {box.Median:R} <= Q3 {box.ThirdQuartile:R} must hold");
        That(box.LowerWhisker <= box.UpperWhisker, context, $"lower whisker {box.LowerWhisker:R} must not exceed upper whisker {box.UpperWhisker:R}");
        That(WithinTolerance(box.Mean, sorted[0], sorted[^1], sorted.Length), context, $"mean {box.Mean:R} lies outside the data {sorted[0]:R}..{sorted[^1]:R}");

        // Tukey fences, derived here from the box's own quartiles - outliers are defined by the fences, never by where
        // the whiskers happen to be.
        var iqr = box.ThirdQuartile - box.FirstQuartile;
        var lowerFence = box.FirstQuartile - (BoxPlotSummary.FenceMultiplier * iqr);
        var upperFence = box.ThirdQuartile + (BoxPlotSummary.FenceMultiplier * iqr);

        // A whisker is an actual observation of this box, inside its fence, and the most extreme one there.
        That(Array.BinarySearch(sorted, box.LowerWhisker) >= 0, context, $"lower whisker {box.LowerWhisker:R} is not an observation of this box");
        That(Array.BinarySearch(sorted, box.UpperWhisker) >= 0, context, $"upper whisker {box.UpperWhisker:R} is not an observation of this box");
        That(box.LowerWhisker >= lowerFence, context, $"lower whisker {box.LowerWhisker:R} lies below the lower fence {lowerFence:R}");
        That(box.UpperWhisker <= upperFence, context, $"upper whisker {box.UpperWhisker:R} lies above the upper fence {upperFence:R}");
        That(box.LowerWhisker == sorted.First(value => value >= lowerFence), context, "the lower whisker must be the smallest observation inside the lower fence");
        That(box.UpperWhisker == sorted.Last(value => value <= upperFence), context, "the upper whisker must be the largest observation inside the upper fence");

        var outside = sorted.Count(value => value < lowerFence || value > upperFence);
        That(box.OutlierCount == outside, context, $"box reports {box.OutlierCount} outliers, the fences {outside}");
        That(box.Outliers.Length <= box.OutlierCount, context, "a box cannot draw more outliers than it has");
        foreach (var outlier in box.Outliers.Span)
        {
            That(outlier < lowerFence || outlier > upperFence, context, $"drawn outlier {outlier:R} lies inside the fences {lowerFence:R}..{upperFence:R}");
            That(Array.BinarySearch(sorted, outlier) >= 0, context, $"drawn outlier {outlier:R} is not an observation of this box");
        }

        // Implementation invariant (Task #034.1 E3): the axis describes the whole model, including every outlier that
        // was not drawn - which, with the whiskers, means every observation.
        foreach (var (name, value) in (ReadOnlySpan<(string, double)>)
                 [("Q1", box.FirstQuartile), ("median", box.Median), ("Q3", box.ThirdQuartile), ("mean", box.Mean),
                  ("lower whisker", box.LowerWhisker), ("upper whisker", box.UpperWhisker),
                  ("smallest observation", sorted[0]), ("largest observation", sorted[^1])])
        {
            That(Within(vertical, value), context, $"{name} {value:R} lies outside the Y axis {vertical.Minimum:R}..{vertical.Maximum:R}");
        }
    }

    // ---- Probability Plot ----

    private static void ProbabilityPlot(string context, UnivariateGraphData data, ProbabilityPlotRenderModel model, ProbabilityPlotOptions options)
    {
        var expected = ExpectedSeries(data.Group, data.Values.Span);
        That(model.SourceObservationCount == data.Count, context, $"probability plot used {model.SourceObservationCount} of {data.Count} observations");
        That(model.Series.Select(series => series.Label).SequenceEqual(expected.Select(series => series.Label)), context,
            "probability plot series must be the groups in first-observed order");

        for (var index = 0; index < model.Series.Count; index++)
        {
            var series = model.Series[index];
            var observations = expected[index].Values.Order().ToArray();
            var seriesContext = $"{context}\n  series '{series.Label}'";
            That(series.ObservationCount == observations.Length, seriesContext, $"series has {series.ObservationCount} observations, the data {observations.Length}");

            var points = series.Points.Span;
            That(points.Length >= 1 && points.Length <= observations.Length, seriesContext, $"series draws {points.Length} of {observations.Length} points");

            for (var point = 0; point < points.Length; point++)
            {
                // Mathematical: each point is an observation at its own plotting position; positions rise with rank,
                // ties included, so values never fall and scores strictly rise.
                That(double.IsFinite(points[point].Score), seriesContext, $"point {point} has a non-finite score");
                That(Array.BinarySearch(observations, points[point].Value) >= 0, seriesContext, $"point value {points[point].Value:R} is not an observation");
                if (point > 0)
                {
                    That(points[point].Value >= points[point - 1].Value, seriesContext, $"point {point} value falls");
                    That(points[point].Score > points[point - 1].Score, seriesContext, $"point {point} score does not rise");
                }

                // Implementation: both axes are built from every point (the probability axis pads the extreme scores).
                That(Within(model.Frame.XAxis.Range, points[point].Value), seriesContext, $"value {points[point].Value:R} lies outside the X axis");
                That(Within(model.Frame.YAxis.Range, points[point].Score), seriesContext, $"score {points[point].Score:R} lies outside the Y axis");
            }

            // IMPLEMENTATION-SPECIFIC, not a lasting robustness contract: a fitted line is drawn exactly when the fitted
            // line is shown (ProbabilityPlotOptions, Task #037) and the series has a spread to fit. Nothing else in the
            // harness depends on the line being there.
            var hasSpread = observations.Length >= 2 && observations[0] != observations[^1];
            var expectsLine = options.ShowFittedLine && hasSpread;
            That((series.FittedLine is not null) == expectsLine, seriesContext,
                $"fitted line is {(series.FittedLine is null ? "missing" : "present")} for a series {(hasSpread ? "with" : "without")} spread " +
                $"with the fitted line {(options.ShowFittedLine ? "shown" : "hidden")}");
        }

        // Implementation (Task #037): without fitted lines, nothing but the points decides the horizontal axis - the
        // builder's axis is exactly the one the observations ask for. (The frame the application shows may still be
        // reached out by a specification afterwards; that is checked with the specification lines.)
        if (!options.ShowFittedLine)
        {
            var all = expected.SelectMany(series => series.Values).ToArray();
            var fromPoints = GraphAxisRanges.FromValues(all.Min(), all.Max());
            That(model.Frame.XAxis.Range == fromPoints, context,
                $"without fitted lines the X axis {model.Frame.XAxis.Range.Minimum:R}..{model.Frame.XAxis.Range.Maximum:R} is not the points' own {fromPoints.Minimum:R}..{fromPoints.Maximum:R}");
        }
    }

    // What a probability plot says apart from its fitted lines and the horizontal axis they reach into (#037): the
    // title, the vertical axis, the legend, the statistics panel, the reference lines, and every series with its label,
    // index, counts and points. Showing or hiding the fitted line must leave all of it exactly as it is.
    public static string WithoutFittedLines(BuiltGraph built)
    {
        var model = (ProbabilityPlotRenderModel)built.Model!;
        var frame = built.Frame!;
        var text = new StringBuilder(frame.Title);
        Axis(text, "y", frame.YAxis);
        text.Append(" legend=").Append(frame.Legend is { } legend
            ? string.Join(",", legend.Entries.Select(entry => $"{entry.Label}#{entry.SeriesIndex}"))
            : "none");
        text.Append(" statistics=").Append(frame.StatisticsPanel is { } panel
            ? string.Join(";", panel.Rows.Select(row => Invariant($"{row.Label}#{row.SeriesIndex} n={row.Count} mean={row.Mean:R} sd={row.StandardDeviation:R} {row.MeanText}/{row.StandardDeviationText}/{row.CountText}")))
            : "none");
        text.Append(" lines=").AppendJoin(",", frame.ReferenceLines.Select(line => Invariant($"{line.Axis}:{line.Value:R}:{line.Kind}:{line.Label}")));
        text.Append(Invariant($" n={model.SourceObservationCount} drawn={model.RenderedPointCount}"));
        foreach (var series in model.Series)
        {
            text.Append(Invariant($" [{series.Label}#{series.SeriesIndex} n={series.ObservationCount} points="));
            text.AppendJoin(",", series.Points.ToArray().Select(point => Invariant($"{point.Value:R}:{point.Score:R}"))).Append(']');
        }

        return text.ToString();
    }

    // ---- Empirical CDF ----

    private static void EmpiricalCdf(string context, UnivariateGraphData data, EmpiricalCdfRenderModel model)
    {
        var expected = ExpectedSeries(data.Group, data.Values.Span);
        That(model.SourceObservationCount == data.Count, context, $"empirical CDF used {model.SourceObservationCount} of {data.Count} observations");
        That(model.Series.Select(series => series.Label).SequenceEqual(expected.Select(series => series.Label)), context,
            "empirical CDF series must be the groups in first-observed order");

        for (var index = 0; index < model.Series.Count; index++)
        {
            var series = model.Series[index];
            var observations = expected[index].Values.Order().ToArray();
            var seriesContext = $"{context}\n  series '{series.Label}'";
            That(series.ObservationCount == observations.Length, seriesContext, $"series has {series.ObservationCount} observations, the data {observations.Length}");
            That(series.UniquePointCount == observations.Distinct().Count(), seriesContext, "one step per distinct value");

            var points = series.Points.Span;
            for (var point = 0; point < points.Length; point++)
            {
                var (value, percent) = (points[point].Value, points[point].CumulativePercent);

                // Mathematical: a step sits on an observed value, at the share of the series at or below it.
                var atOrBelow = UpperBound(observations, value);
                That(atOrBelow > 0 && observations[atOrBelow - 1] == value, seriesContext, $"step value {value:R} is not an observation");
                That(percent == atOrBelow * 100d / observations.Length, seriesContext,
                    $"step at {value:R} is {percent:R} %, the data says {atOrBelow * 100d / observations.Length:R} %");

                if (point > 0)
                {
                    That(value > points[point - 1].Value && percent > points[point - 1].CumulativePercent, seriesContext, $"step {point} does not rise");
                }

                That(Within(model.Frame.XAxis.Range, value), seriesContext, $"value {value:R} lies outside the X axis");
                That(Within(model.Frame.YAxis.Range, percent), seriesContext, $"{percent:R} % lies outside the Y axis");
            }

            // Mathematical, and kept by sampling (the last step is always drawn): the distribution ends at 100 %.
            That(points.Length > 0 && points[^1].CumulativePercent == 100, seriesContext, "the last step must be at 100 %");
        }
    }

    // ---- Scatter Plot ----

    private static void Scatter(string context, ScatterGraphData data, ScatterRenderModel model)
    {
        var expected = ExpectedSeries(data.Group, data.XValues.Span);
        That(model.SourcePointCount == data.Count, context, $"scatter plot used {model.SourcePointCount} of {data.Count} points");
        That(model.RenderedPointCount <= model.SourcePointCount, context, "a scatter plot cannot draw more points than it has");
        That(model.Series.Sum(series => series.Points.Length) == model.RenderedPointCount, context, "rendered count must match the drawn points");
        That(model.Series.Select(series => series.Label).SequenceEqual(expected.Select(series => series.Label)), context,
            "scatter series must be the groups in first-observed order");

        // Implementation invariant: the axes are built from every point, drawn or not.
        var x = model.Frame.XAxis.Range;
        var y = model.Frame.YAxis.Range;
        foreach (var value in data.XValues.Span)
        {
            That(Within(x, value), context, $"x {value:R} lies outside the X axis {x.Minimum:R}..{x.Maximum:R}");
        }

        foreach (var value in data.YValues.Span)
        {
            That(Within(y, value), context, $"y {value:R} lies outside the Y axis {y.Minimum:R}..{y.Maximum:R}");
        }

        foreach (var series in model.Series)
        {
            That(series.Points.Length >= 1, context, $"series '{series.Label}' draws no point");
            foreach (var point in series.Points.Span)
            {
                That(double.IsFinite(point.X) && double.IsFinite(point.Y) && Within(x, point.X) && Within(y, point.Y), context,
                    $"drawn point ({point.X:R}, {point.Y:R}) is not finite or not on the axes");
            }
        }
    }

    // ---- Groups, as the graphs define them ----

    // The observations of each group in first-observed order, labelled the way the graphs label them: text as it is,
    // numbers with 0.#### in the invariant culture, an empty group cell as "(Missing)". Groups are told apart by their
    // value, not by their label. Without a group column there is one unnamed series.
    public static List<(string Label, double[] Values)> ExpectedSeries(GraphGroupData? group, ReadOnlySpan<double> values)
    {
        var series = new List<(string Label, List<double> Values)>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var row = 0; row < values.Length; row++)
        {
            string key;
            string label;
            if (group is null)
            {
                key = label = string.Empty;
            }
            else if (group.IsMissing(row))
            {
                key = "missing";
                label = "(Missing)";
            }
            else if (group is StringGroupData text)
            {
                label = text.Values.Span[row]!;
                key = "text:" + label;
            }
            else
            {
                var number = ((NumericGroupData)group).Values.Span[row]!.Value;
                key = "number:" + number.ToString("R", CultureInfo.InvariantCulture);
                label = number.ToString("0.####", CultureInfo.InvariantCulture);
            }

            if (!byKey.TryGetValue(key, out var index))
            {
                index = series.Count;
                byKey.Add(key, index);
                series.Add((label, []));
            }

            series[index].Values.Add(values[row]);
        }

        return [.. series.Select(item => (item.Label, item.Values.ToArray()))];
    }

    // ---- Fingerprints: what two builds are compared by ----

    // Everything a model says, as text. statisticsOnly leaves out what sampling may change - drawn points and
    // outliers, rendered counts, whether the model was sampled - and keeps observation counts, statistics, axes,
    // categories, series order and legend.
    // What the graph type's builder made, whatever was applied to its frame afterwards: the builder's own axes and
    // legend, the statistics panel, and the plot model. A specification may widen the displayed X axis and add lines;
    // this must be the same with or without one (#036).
    public static string PlotFingerprint(BuiltGraph built) =>
        built.Model is null
            ? "no model"
            : Fingerprint(
                built with { Frame = RobustnessGraphs.BuilderFrame(built.Model).WithStatisticsPanel(built.Frame!.StatisticsPanel) },
                statisticsOnly: false);

    public static string Fingerprint(BuiltGraph built, bool statisticsOnly)
    {
        if (built.Model is null)
        {
            return "no model";
        }

        var text = new StringBuilder();
        Axis(text, "x", built.Frame!.XAxis);
        Axis(text, "y", built.Frame.YAxis);
        text.Append(" legend=").Append(built.Frame.Legend is { } legend
            ? string.Join(",", legend.Entries.Select(entry => $"{entry.Label}#{entry.SeriesIndex}"))
            : "none");

        // The statistics panel is statistics: sampling must leave it exactly as it is.
        text.Append(" statistics=");
        if (built.Frame.StatisticsPanel is { } panel)
        {
            text.Append(panel.GroupHeader ?? "ungrouped");
            foreach (var row in panel.Rows)
            {
                text.Append(Invariant(
                    $" [{row.Label}#{row.SeriesIndex} n={row.Count} mean={row.Mean:R} sd={row.StandardDeviation:R} {row.MeanText}/{row.StandardDeviationText}/{row.CountText}]"));
            }
        }
        else
        {
            text.Append("none");
        }

        // Reference lines are part of the frame: sampling must not move them either.
        text.Append(" lines=");
        text.AppendJoin(",", built.Frame.ReferenceLines.Select(line => Invariant($"{line.Axis}:{line.Value:R}:{line.Kind}:{line.Label}")));

        switch (built.Model)
        {
            case HistogramRenderModel histogram:
                text.Append(Invariant($" n={histogram.SourceObservationCount} max={histogram.MaximumCount} scale={histogram.YScale} tallest={histogram.MaximumHeight:R} bins="));
                text.AppendJoin(",", histogram.Bins.Select(bin => Invariant($"{bin.LowerEdge:R}..{bin.UpperEdge:R}")));
                foreach (var series in histogram.Series)
                {
                    text.Append(Invariant($" [{series.Label}#{series.SeriesIndex} n={series.ObservationCount} counts={string.Join(",", series.Counts)} heights="));
                    text.AppendJoin(",", series.Heights.Select(height => height.ToString("R", CultureInfo.InvariantCulture))).Append(']');
                }

                break;

            case BoxPlotRenderModel boxPlot:
                text.Append(Invariant($" n={boxPlot.SourceObservationCount} outliers={boxPlot.OutlierCount} categories={string.Join("|", boxPlot.Categories)}"));
                foreach (var box in boxPlot.Boxes)
                {
                    text.Append(Invariant(
                        $" [{box.Label}@{box.CategoryIndex}#{box.SeriesIndex} n={box.ObservationCount} {box.LowerWhisker:R}/{box.FirstQuartile:R}/{box.Median:R}/{box.ThirdQuartile:R}/{box.UpperWhisker:R} mean={box.Mean:R} outliers={box.OutlierCount}"));
                    if (!statisticsOnly)
                    {
                        text.Append(" drawn=").AppendJoin(",", box.Outliers.ToArray().Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
                    }

                    text.Append(']');
                }

                break;

            case ProbabilityPlotRenderModel probability:
                text.Append(Invariant($" n={probability.SourceObservationCount}"));
                foreach (var series in probability.Series)
                {
                    var line = series.FittedLine is { } fitted
                        ? Invariant($"{fitted.Mean:R}/{fitted.StandardDeviation:R}/{fitted.FromScore:R}..{fitted.ToScore:R}/{fitted.FromValue:R}..{fitted.ToValue:R}")
                        : "none";
                    text.Append(Invariant($" [{series.Label}#{series.SeriesIndex} n={series.ObservationCount} line={line}"));
                    if (!statisticsOnly)
                    {
                        text.Append(" points=").AppendJoin(",", series.Points.ToArray().Select(point => Invariant($"{point.Value:R}:{point.Score:R}")));
                    }

                    text.Append(']');
                }

                break;

            case EmpiricalCdfRenderModel empirical:
                text.Append(Invariant($" n={empirical.SourceObservationCount} unique={empirical.UniquePointCount}"));
                foreach (var series in empirical.Series)
                {
                    text.Append(Invariant($" [{series.Label}#{series.SeriesIndex} n={series.ObservationCount} unique={series.UniquePointCount}"));
                    if (!statisticsOnly)
                    {
                        text.Append(" steps=").AppendJoin(",", series.Points.ToArray().Select(point => Invariant($"{point.Value:R}:{point.CumulativePercent:R}")));
                    }

                    text.Append(']');
                }

                break;

            case ScatterRenderModel scatter:
                text.Append(Invariant($" n={scatter.SourcePointCount}"));
                foreach (var series in scatter.Series)
                {
                    text.Append(Invariant($" [{series.Label}#{series.SeriesIndex}"));
                    if (!statisticsOnly)
                    {
                        text.Append(" points=").AppendJoin(",", series.Points.ToArray().Select(point => Invariant($"{point.X:R}:{point.Y:R}")));
                    }

                    text.Append(']');
                }

                break;
        }

        return text.ToString();
    }

    private static void Axis(StringBuilder text, string name, GraphAxisModel axis)
    {
        text.Append(Invariant($" {name}={axis.Range.Minimum:R}..{axis.Range.Maximum:R} ticks="));
        text.AppendJoin(",", axis.Ticks.Select(tick => Invariant($"{tick.Value:R}:{tick.Label}")));
        text.Append(" title=").Append(axis.Title);
    }

    // ---- Helpers ----

    public static void That(bool condition, string context, string violated)
    {
        if (!condition)
        {
            Assert.Fail($"{context}\n  violated: {violated}");
        }
    }

    private static bool Within(GraphAxisRange range, double value) => value >= range.Minimum && value <= range.Maximum;

    // A computed mean may differ from the exact one by the rounding of its sum: at most about n machine epsilons of the
    // largest magnitude. "Between the smallest and the largest observation" allows for that and nothing more - the
    // slack scales with the data, so a tiny range is not swallowed by an absolute tolerance.
    private static bool WithinTolerance(double value, double minimum, double maximum, int count)
    {
        const double MachineEpsilon = 2.220446049250313e-16;
        var slack = 2 * count * MachineEpsilon * Math.Max(Math.Abs(minimum), Math.Abs(maximum));
        return value >= minimum - slack && value <= maximum + slack;
    }

    // The number of sorted values at or below value.
    private static int UpperBound(double[] sorted, double value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] <= value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
