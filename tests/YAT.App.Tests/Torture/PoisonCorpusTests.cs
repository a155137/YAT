using YAT.Application.Graphs;

namespace YAT.App.Tests.Torture;

// Task #063, 1: the numerical poison corpus through every graph type, alone, grouped, in panels, grouped in panels and
// under a long CJK and emoji group column, with all eight statistics shown - laid out and drawn at every size, and
// exported. A refusal the user can act on is written to the test output; anything unexpected fails with the column,
// the request and the dataset.
public sealed class PoisonCorpusTests
{
    private static readonly GraphType[] Univariate = [GraphType.Histogram, GraphType.BoxPlot, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf];

    public static TheoryData<string, GraphType> UnivariateCases
    {
        get
        {
            var data = new TheoryData<string, GraphType>();
            foreach (var column in TortureCorpus.NumericNames)
            {
                foreach (var type in Univariate)
                {
                    data.Add(column, type);
                }
            }

            return data;
        }
    }

    public static TheoryData<string> Columns => [.. TortureCorpus.NumericNames];

    public static TheoryData<int, GraphType> SmallCases
    {
        get
        {
            var data = new TheoryData<int, GraphType>();
            foreach (var rows in new[] { 0, 1, 2, 3 })
            {
                foreach (var type in Univariate.Append(GraphType.ScatterPlot))
                {
                    data.Add(rows, type);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnivariateCases))]
    public async Task EveryPoisonColumnDrawsOrIsRefusedInEveryArrangement(string column, GraphType type)
    {
        using var session = await TortureSession.StartAsync(TortureCorpus.Dataset);

        foreach (var request in Arrangements(new TortureRequest(type, [column]) { Statistics = TortureStatistics.All }))
        {
            await DrawAndVerifyAsync(session, TortureCorpus.Dataset, request);
        }
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public async Task EveryPoisonColumnDrawsOrIsRefusedAsAScatterPlotAxis(string column)
    {
        using var session = await TortureSession.StartAsync(TortureCorpus.Dataset);

        TortureRequest[] requests =
        [
            new(GraphType.ScatterPlot, [column]) { Y = TortureCorpus.Reference },
            new(GraphType.ScatterPlot, [TortureCorpus.Reference]) { Y = column },
            new(GraphType.ScatterPlot, [column]) { Y = TortureCorpus.Reference, Group = TortureCorpus.Group, Panel = TortureCorpus.Panel }
        ];
        foreach (var request in requests)
        {
            await DrawAndVerifyAsync(session, TortureCorpus.Dataset, request);
        }
    }

    // All poison columns together in one graph, grouped in panels: the worst of everything in one frame.
    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.BoxPlot)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task EveryPoisonColumnTogetherDrawsOrIsRefused(GraphType type)
    {
        using var session = await TortureSession.StartAsync(TortureCorpus.Dataset);
        var all = TortureCorpus.NumericNames.Where(name => name != "AllMissing").ToArray();

        foreach (var request in Arrangements(new TortureRequest(type, all) { Statistics = TortureStatistics.All }))
        {
            await DrawAndVerifyAsync(session, TortureCorpus.Dataset, request);
        }
    }

    [Theory]
    [MemberData(nameof(SmallCases))]
    public async Task ATinyWorksheetDrawsOrIsRefusedInEveryArrangement(int rows, GraphType type)
    {
        var dataset = TortureCorpus.Small(rows);
        using var session = await TortureSession.StartAsync(dataset);

        var request = type == GraphType.ScatterPlot
            ? new TortureRequest(type, [TortureCorpus.Reference]) { Y = "Y", Statistics = TortureStatistics.All }
            : new TortureRequest(type, [TortureCorpus.Reference]) { Statistics = TortureStatistics.All };
        foreach (var arranged in Arrangements(request))
        {
            await DrawAndVerifyAsync(session, dataset, arranged);
        }
    }

    private static IEnumerable<TortureRequest> Arrangements(TortureRequest request)
    {
        yield return request;
        yield return request with { Group = TortureCorpus.Group };
        yield return request with { Group = TortureCorpus.LongGroup };
        if (request.Type != GraphType.BoxPlot)
        {
            yield return request with { Panel = TortureCorpus.Panel };
            yield return request with { Group = TortureCorpus.Group, Panel = TortureCorpus.Panel };
        }

        if (request.Variables.Count > 1)
        {
            yield return request with { Group = TortureCorpus.Group, Layout = GraphVariableLayout.Separate };
        }
    }

    internal static async Task DrawAndVerifyAsync(TortureSession session, TortureDataset dataset, TortureRequest request)
    {
        var context = $"seed=0x{TortureCorpus.Seed:X} named case\n  request: {request}\n  {dataset.Describe()}";
        var outcome = await session.DrawAsync(request);
        if (outcome is null)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"NOT OFFERED by the setup: {request}");
            return;
        }

        foreach (var error in outcome.Errors)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"REFUSED: {request}\n  {error.Replace("\n", "\n  ", StringComparison.Ordinal)}");
        }

        TortureInvariants.Verify(context, outcome, TortureSizes.Normal);
    }
}
