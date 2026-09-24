using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// The histogram's normal fit from the Graph menu (Task #042), over a real project: several variables drawn together
// fit every "Variable / Group" series on its own, drawn separately every window fits its own variable, and without the
// option nothing has a fit.
public partial class GraphCommandTests
{
    private static HistogramRenderModel HistogramOf(Runtime runtime, int shown) =>
        Assert.IsType<HistogramRenderer>(runtime.GraphWindows.Shown[shown].Plot).Model;

    [Fact]
    public async Task TogetherEverySeriesHasItsOwnFitAgreeingWithItsStatistics()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frame = Assert.Single(await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot",
            setup => setup.ShowNormalFit = true));
        var model = HistogramOf(runtime, 0);

        // Reg1 / (Missing) holds one observation, which has no fit; every other series has its own.
        Assert.Equal(frame.Legend!.Entries.Select(entry => entry.Label), model.Series.Select(series => series.Label));
        var panel = frame.StatisticsPanel!;
        for (var index = 0; index < model.Series.Count; index++)
        {
            var series = model.Series[index];
            var row = panel.Rows[index];
            Assert.Equal(row.Label, series.Label);
            if (row.StandardDeviation is not > 0)
            {
                Assert.Null(series.NormalFit);
                continue;
            }

            Assert.Equal(row.Mean, series.NormalFit!.Mean);
            Assert.Equal(row.StandardDeviation, series.NormalFit.StandardDeviation);
        }

        Assert.Contains(model.Series, series => series.NormalFit is null);
        Assert.Contains(model.Series, series => series.NormalFit is not null);
        Assert.True(frame.YAxis.Range.Maximum >= model.Series.Max(series => series.NormalFit?.MaximumHeight ?? 0));
    }

    [Fact]
    public async Task SeparatelyEveryWindowFitsItsOwnVariableAsSettingItUpAloneWould()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frames = await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Separate, ["Reg1", "Reg2"], "Lot",
            setup => setup.ShowNormalFit = true);
        Assert.Equal(2, frames.Count);
        var separate = new[] { HistogramOf(runtime, 0), HistogramOf(runtime, 1) };

        for (var index = 0; index < 2; index++)
        {
            var variable = index == 0 ? "Reg1" : "Reg2";
            var alone = Assert.Single(await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, [variable], "Lot",
                setup => setup.ShowNormalFit = true));
            var aloneModel = HistogramOf(runtime, runtime.GraphWindows.Shown.Count - 1);

            Assert.Equal(Describe(alone), Describe(frames[index]));
            Assert.Equal(
                aloneModel.Series.Select(series => series.NormalFit?.Points),
                separate[index].Series.Select(series => series.NormalFit?.Points));
            Assert.Contains(separate[index].Series, series => series.NormalFit is not null);
        }
    }

    [Fact]
    public async Task WithoutTheOptionNoSeriesHasAFit()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot");
        await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Separate, ["Reg1", "Reg2"], "Lot");

        Assert.Equal(3, runtime.GraphWindows.Shown.Count);
        for (var index = 0; index < 3; index++)
        {
            Assert.All(HistogramOf(runtime, index).Series, series => Assert.Null(series.NormalFit));
        }
    }
}
