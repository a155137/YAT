using YAT.Application.Abstractions.Persistence;
using YAT.Application.Analyses;
using YAT.Application.Exceptions;
using YAT.Application.Filtering;
using YAT.Application.Graphs;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// One row filter, read by both data queries (Task #053): the graph query and the analysis query keep exactly the same
// worksheet rows - every condition met, before anything is built from them - the filter's columns read once in the
// same aligned windows as the graph's or analysis's own. An analysis's rows are then only the rows kept: its RowCount,
// and every count over it, Missing included, are about them.
public class RowFilterQueryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Fixture
    {
        public Fixture()
        {
            Worksheets.Seed(new Worksheet { Id = WorksheetId, Name = "Sheet1" });
            Graphs = new GraphDataQueryService(Worksheets, Columns, RawData);
            Analyses = new AnalysisDataQueryService(Worksheets, Columns, RawData);
        }

        public Guid WorksheetId { get; } = Guid.NewGuid();

        public FakeWorksheetRepository Worksheets { get; } = new();

        public FakeWorksheetColumnRepository Columns { get; } = new();

        public FakeWorksheetRawDataStore RawData { get; } = new();

        public GraphDataQueryService Graphs { get; }

        public AnalysisDataQueryService Analyses { get; }

        public WorksheetColumn Column(string name, WorksheetDataType dataType, int index)
        {
            var column = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = WorksheetId, Index = index, Name = name, DataType = dataType };
            Columns.Seed(column);
            return column;
        }

        public WorksheetColumn Numeric(string name, int index, params double?[] values)
        {
            var column = Column(name, WorksheetDataType.Numeric, index);
            RawData.Seed(WorksheetId, new NumericRawDataColumn(column.Id, values));
            return column;
        }

        public WorksheetColumn Text(string name, int index, params string?[] values)
        {
            var column = Column(name, WorksheetDataType.String, index);
            RawData.Seed(WorksheetId, new StringRawDataColumn(column.Id, values));
            return column;
        }

        public Task<AnalysisData> AnalyseAsync(RowFilter? filter, IEnumerable<WorksheetColumn> variables, WorksheetColumn? group = null) =>
            Analyses.LoadAsync(new AnalysisConfiguration(WorksheetId, [.. variables.Select(column => column.Id)], group?.Id) { Filter = filter }, Token);

        public async Task<UnivariateGraphData> HistogramAsync(RowFilter? filter, WorksheetColumn variable, WorksheetColumn? group = null)
        {
            List<GraphColumnAssignment> roles = [new(GraphVariableRole.Variable, variable.Id)];
            if (group is not null)
            {
                roles.Add(new GraphColumnAssignment(GraphVariableRole.Group, group.Id));
            }

            var data = await Graphs.LoadAsync(new GraphConfiguration(GraphType.Histogram, WorksheetId, roles) { Filter = filter }, Token);
            return Assert.IsType<UnivariateGraphData>(Assert.IsType<MultiVariableGraphData>(data).Variables.Single());
        }
    }

    // Site, Current, Bin and Tester of ten rows: row 3 has no Current, row 6 no Site, row 8 no Bin; Tester is two rows
    // shorter than the rest.
    private static (Fixture Fixture, WorksheetColumn Site, WorksheetColumn Current, WorksheetColumn Bin, WorksheetColumn Tester) Worksheet()
    {
        var fixture = new Fixture();
        var site = fixture.Numeric("Site", 0, 1, 2, 3, 5, 7, 1, null, 3, 5, 7);
        var current = fixture.Numeric("Current", 1, 14.4, 14.5, 15.0, null, 15.5, 15.6, 15.0, 14.9, 15.2, 14.6);
        var bin = fixture.Numeric("Bin", 2, 1, 1, 3, 1, 2, 1, 1, null, 3, 1);
        var tester = fixture.Text("Tester", 3, "T1", "T2", "T1", "T2", "T1", "T2", "T1", "T2");
        return (fixture, site, current, bin, tester);
    }

    private static RowFilter Engineering(WorksheetColumn site, WorksheetColumn current, WorksheetColumn bin) => new(
    [
        new NumericValueSetCondition(site.Id, [1, 3, 5, 7]),
        new NumericComparisonCondition(current.Id, NumericComparison.GreaterOrEqual, 14.5),
        new NumericComparisonCondition(current.Id, NumericComparison.LessOrEqual, 15.5),
        new NumericValueSetCondition(bin.Id, [3], exclude: true)
    ]);

    [Fact]
    public async Task GraphsAndAnalysesKeepTheSameRows()
    {
        var (fixture, site, current, bin, _) = Worksheet();
        var filter = Engineering(site, current, bin);

        var graph = await fixture.HistogramAsync(filter, current);
        var analysis = await fixture.AnalyseAsync(filter, [current]);

        // Rows kept: 4 (15.5, site 7, bin 2), 7 (14.9, site 3, bin Missing - not 3) and 9 (14.6, site 7, bin 1).
        Assert.Equal([15.5, 14.9, 14.6], graph.Values.ToArray());
        Assert.Equal(3, graph.FilteredRowCount);
        Assert.Equal(3, analysis.RowCount);
        Assert.Equal([15.5, 14.9, 14.6], analysis.Variables[0].Values.ToArray());
    }

    [Fact]
    public async Task AnAnalysisCountsOnlyTheKeptRowsMissingIncluded()
    {
        var (fixture, site, current, _, _) = Worksheet();
        var filter = new RowFilter(new NumericValueSetCondition(site.Id, [1, 2]));

        var all = await fixture.AnalyseAsync(null, [current]);
        var filtered = await fixture.AnalyseAsync(filter, [current]);

        Assert.Equal(10, all.RowCount);
        Assert.Equal(1, all.Variables[0].Values.ToArray().Count(value => value is null));
        Assert.Equal(3, filtered.RowCount);
        Assert.Equal([14.4, 14.5, 15.6], filtered.Variables[0].Values.ToArray());

        var missingCurrent = await fixture.AnalyseAsync(new RowFilter(new NumericValueSetCondition(site.Id, [5])), [current]);
        Assert.Equal([null, 15.2], missingCurrent.Variables[0].Values.ToArray());
    }

    [Fact]
    public async Task TheGroupStaysWithItsRow()
    {
        var (fixture, site, current, _, tester) = Worksheet();
        var filter = new RowFilter(new NumericComparisonCondition(current.Id, NumericComparison.Greater, 15));

        var analysis = await fixture.AnalyseAsync(filter, [current, site], tester);

        Assert.Equal([15.5, 15.6, 15.2], analysis.Variables[0].Values.ToArray());
        Assert.Equal([7, 1, 5], analysis.Variables[1].Values.ToArray());
        Assert.Equal(["T1", "T2", null], Assert.IsType<StringAnalysisGroupData>(analysis.Group).Values.ToArray());
    }

    [Fact]
    public async Task RowsBeyondEveryColumnReadAreKeptAsTheFilterTreatsRowsWithoutValues()
    {
        // Six worksheet rows (an unrelated column is six long); Reg has three values, Lot four: rows 5 and 6 are beyond both.
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1, 2, 3);
        var lot = fixture.Text("Lot", 1, "A", "B", "A", "B");
        fixture.Numeric("Other", 3, 1, 1, 1, 1, 1, 1);

        var keepsMissing = await fixture.AnalyseAsync(new RowFilter(new TextValueSetCondition(lot.Id, ["B"], exclude: true)), [reg]);
        var dropsMissing = await fixture.AnalyseAsync(new RowFilter(new TextComparisonCondition(lot.Id, "B", negated: true)), [reg]);
        var stored = fixture.Column("Empty", WorksheetDataType.Numeric, 2);
        var unstored = await fixture.AnalyseAsync(new RowFilter(new NumericValueSetCondition(stored.Id, [], includeMissing: true)), [stored]);

        Assert.Equal([1, 3, null, null], keepsMissing.Variables[0].Values.ToArray());
        Assert.Equal([1, 3], dropsMissing.Variables[0].Values.ToArray());
        Assert.Equal(6, unstored.RowCount);
        Assert.Equal(0, (await fixture.AnalyseAsync(new RowFilter(new NumericComparisonCondition(stored.Id, NumericComparison.NotEqual, 1)), [stored])).RowCount);
    }

    [Fact]
    public async Task NoRowMatchingIsZeroRows()
    {
        var (fixture, _, current, _, _) = Worksheet();
        var contradictory = new RowFilter(
        [
            new NumericComparisonCondition(current.Id, NumericComparison.Greater, 15),
            new NumericComparisonCondition(current.Id, NumericComparison.Less, 15)
        ]);

        Assert.Equal(0, (await fixture.AnalyseAsync(contradictory, [current])).RowCount);
        var graph = await fixture.HistogramAsync(contradictory, current);
        Assert.Equal((0, 0L), (graph.Count, graph.FilteredRowCount!.Value));
    }

    [Fact]
    public async Task WithoutAFilterNothingChanges()
    {
        var (fixture, site, current, bin, tester) = Worksheet();

        var analysis = await fixture.AnalyseAsync(null, [current, site], tester);
        var graph = await fixture.HistogramAsync(null, current);

        Assert.Equal(10, analysis.RowCount);
        Assert.Null(graph.FilteredRowCount);
        Assert.Equal(9, graph.Count);
        Assert.All(fixture.RawData.Reads, read => Assert.DoesNotContain(bin.Id, read.ColumnIds));
    }

    [Fact]
    public async Task EachFilterColumnIsReadOnceInTheSameWindows()
    {
        var (fixture, site, current, bin, _) = Worksheet();

        await fixture.AnalyseAsync(Engineering(site, current, bin), [current], site);

        Assert.All(fixture.RawData.Reads, read => Assert.Equal(read.ColumnIds.Distinct().Count(), read.ColumnIds.Count));
        Assert.Equal([current.Id, site.Id, bin.Id], fixture.RawData.Reads[0].ColumnIds);
    }

    [Fact]
    public async Task AFilterTheRulesRefuseIsRefusedByBothQueries()
    {
        var (fixture, site, current, _, _) = Worksheet();
        var tooMany = new RowFilter(Enumerable.Range(0, 21).Select(index => new NumericComparisonCondition(site.Id, NumericComparison.Greater, -index)));
        var gone = new RowFilter(new NumericComparisonCondition(Guid.NewGuid(), NumericComparison.Equal, 1));

        var analysisTooMany = await Assert.ThrowsAsync<AnalysisDataException>(() => fixture.AnalyseAsync(tooMany, [current]));
        var analysisGone = await Assert.ThrowsAsync<AnalysisDataException>(() => fixture.AnalyseAsync(gone, [current]));
        var graphGone = await Assert.ThrowsAsync<GraphDataException>(() => fixture.HistogramAsync(gone, current));

        Assert.Equal(AnalysisDataError.InvalidConfiguration, analysisTooMany.Error);
        Assert.Equal(AnalysisDataError.ColumnUnavailable, analysisGone.Error);
        Assert.Equal(GraphDataError.ColumnUnavailable, graphGone.Error);
    }

    [Fact]
    public async Task TheAnalysisValidatorAppliesTheFilterRules()
    {
        var (fixture, site, current, _, _) = Worksheet();
        var columns = await fixture.Columns.GetByWorksheetIdAsync(fixture.WorksheetId, Token);
        var configuration = new AnalysisConfiguration(fixture.WorksheetId, [current.Id], null) { Filter = new RowFilter(new NumericValueSetCondition(site.Id, [])) };

        var result = new AnalysisConfigurationValidator().Validate(configuration, columns);

        Assert.Equal(AnalysisValidationReason.FilterSelectionEmpty, Assert.Single(result.Errors).Reason);
    }

    [Fact]
    public void CapabilityReadsWithItsFilter()
    {
        var filter = new RowFilter(new NumericComparisonCondition(Guid.NewGuid(), NumericComparison.Less, 1));
        var capability = new CapabilityAnalysisConfiguration(Guid.NewGuid(), [new CapabilityVariable(Guid.NewGuid(), 1, 2)], null, CapabilityAnalysisConfiguration.DefaultDisplayStatistics)
        {
            Filter = filter
        };

        Assert.Same(filter, capability.ToAnalysisConfiguration().Filter);
    }
}
