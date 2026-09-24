using System.Globalization;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Robustness;

// #042: the histogram's normal fit over the whole named corpus, on every Y scale and every kind of bins, alone and with
// several variables drawn together. Each case is built with the fit off and on through the harness's own checks - every
// invariant a histogram holds, the fits' own (GraphRobustnessInvariants.HistogramNormalFits) - and drawn in both themes.
// On top of those: the fit adds its curves and nothing else. Bins, counts, heights, series, legend and X axis are the
// same with it off and on; the Y axis differs only where a curve rises above every bar; and every fit is of the mean and
// sample standard deviation the statistics panel shows for its series.
public sealed class GraphRobustnessNormalFitTests
{
    public static TheoryData<string> Cases => GraphRobustnessNamedTests.UnivariateCases;

    private static readonly HistogramYScale[] Scales = [HistogramYScale.Frequency, HistogramYScale.Percent, HistogramYScale.Density];

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheNormalFitAddsItsCurvesAndNothingElse(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        var data = (UnivariateGraphData)RobustnessGraphs.DataFor(RobustnessGraph.Histogram, robustnessCase);
        var context = robustnessCase.Describe("Histogram normal fit");
        Check(context, data);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TogetherEverySeriesIsFittedOnItsOwn(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        var reg1 = (UnivariateGraphData)RobustnessGraphs.DataFor(RobustnessGraph.Histogram, robustnessCase);
        var negated = new UnivariateGraphData(reg1.GraphType, reg1.WorksheetId,
            new GraphColumnInfo(Guid.NewGuid(), "Negated", reg1.Variable.DataType),
            (double[])[.. reg1.Values.ToArray().Select(value => -value)], reg1.Group);
        var together = GraphVariablesTogether.Combine(
            new MultiVariableGraphData(reg1.GraphType, reg1.WorksheetId, [reg1, negated]), TestContext.Current.CancellationToken);

        Check(robustnessCase.Describe("Histogram normal fit together"), together);
    }

    private static void Check(string context, UnivariateGraphData data)
    {
        var panel = GraphStatisticsPanelBuilder.Build(data, TestContext.Current.CancellationToken);
        var theme = 0;
        foreach (var scale in Scales)
        {
            foreach (var bins in Bins(data, scale))
            {
                var where = $"{context} {bins}";
                var off = Build(where, data, bins);
                var on = Build(where, data, bins with { ShowNormalFit = true });
                if (off is null || on is null)
                {
                    // A width-and-start grid that does not suit the data is refused the same way with the fit or without.
                    GraphRobustnessInvariants.That((off is null) == (on is null), where, "the fit changed whether the histogram can be drawn");
                    continue;
                }

                // Every invariant, the fits' own included, and a drawing in the light and the dark theme in turn.
                GraphRobustnessInvariants.Exercise(where, RobustnessGraph.Histogram, data, theme++ % 2 == 0 ? GraphThemes.Light : GraphThemes.Dark,
                    repeat: false, histogramOptions: bins with { ShowNormalFit = true });

                if (off.Model is not HistogramRenderModel without || on.Model is not HistogramRenderModel with)
                {
                    GraphRobustnessInvariants.That(off.Model is null && on.Model is null, where, "the fit changed whether there is a histogram");
                    continue;
                }

                GraphRobustnessInvariants.That(WithoutFits(without) == WithoutFits(with), where,
                    $"the fit changed more than its curves:\n    off: {WithoutFits(without)}\n    on:  {WithoutFits(with)}");

                var highest = with.Series.Max(series => series.NormalFit?.MaximumHeight ?? 0);
                if (highest <= with.MaximumHeight)
                {
                    GraphRobustnessInvariants.That(Axis(without.Frame.YAxis) == Axis(with.Frame.YAxis), where,
                        $"no curve rises above the bars, yet the Y axis changed: {Axis(without.Frame.YAxis)} -> {Axis(with.Frame.YAxis)}");
                }

                for (var index = 0; index < with.Series.Count; index++)
                {
                    if (with.Series[index].NormalFit is { } fit)
                    {
                        var row = panel!.Rows[index];
                        GraphRobustnessInvariants.That(row.Label == (data.Group is null ? string.Empty : with.Series[index].Label)
                            && row.Mean == fit.Mean && row.StandardDeviation == fit.StandardDeviation, where,
                            $"series '{with.Series[index].Label}' is fitted with {fit.Mean:R} / {fit.StandardDeviation:R}, " +
                            $"the statistics panel shows {row.Mean:R} / {row.StandardDeviation:R}");
                    }
                }
            }
        }
    }

    private static BuiltGraph? Build(string context, UnivariateGraphData data, HistogramOptions options)
    {
        try
        {
            return RobustnessGraphs.Build(RobustnessGraph.Histogram, data, cancellationToken: TestContext.Current.CancellationToken,
                histogramOptions: options);
        }
        catch (GraphPreparationException)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Assert.Fail($"{context}\n  valid finite input made the builder throw: {exception}");
            throw;
        }
    }

    // Automatic bins, a counted number of them, and a fixed grid of a tenth of the data's range from its smallest value.
    private static IEnumerable<HistogramOptions> Bins(UnivariateGraphData data, HistogramYScale scale)
    {
        yield return new HistogramOptions(scale);
        yield return new HistogramOptions(scale, HistogramBinningMode.Count, BinCount: 7);

        var values = data.Values.ToArray().Where(double.IsFinite).ToArray();
        if (values.Length == 0)
        {
            yield break;
        }

        var minimum = values.Min();
        var width = (values.Max() - minimum) / 10;
        if (!double.IsFinite(width) || width <= 0)
        {
            width = 1;
        }

        yield return new HistogramOptions(scale, HistogramBinningMode.WidthAndStart, BinWidth: width, BinStart: minimum);
    }

    private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Axis(GraphAxisModel axis) =>
        $"{R(axis.Range.Minimum)}..{R(axis.Range.Maximum)} [{string.Join(",", axis.Ticks.Select(tick => $"{R(tick.Value)}={tick.Label}"))}] {axis.Title}";

    // Everything the fit must leave as it was: the whole model but its fits, and the whole frame but its Y axis.
    private static string WithoutFits(HistogramRenderModel model) =>
        $"{model.Frame.Title}|x={Axis(model.Frame.XAxis)}|y={model.Frame.YAxis.Title}|{model.YScale}|n={model.SourceObservationCount}" +
        $"|max={model.MaximumCount}/{R(model.MaximumHeight)}|bins={string.Join(",", model.Bins.Select(bin => $"{R(bin.LowerEdge)}~{R(bin.UpperEdge)}"))}" +
        $"|series={string.Join(";", model.Series.Select(series => $"{series.Label}#{series.SeriesIndex} n={series.ObservationCount} " +
            $"{string.Join(",", series.Counts)} {string.Join(",", series.Heights.Select(R))}"))}" +
        $"|legend={(model.Frame.Legend is { } legend ? legend.Title + ":" + string.Join(",", legend.Entries.Select(entry => $"{entry.Label}#{entry.SeriesIndex}")) : "none")}";
}
