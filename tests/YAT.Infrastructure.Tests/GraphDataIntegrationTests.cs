using System.Diagnostics;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

// The graph data pipeline over real DuckDB raw storage: aligned multi-column reads, null padding of shorter columns,
// and a large dataset read in chunks.
public class GraphDataIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Composition : IDisposable
    {
        public Composition()
        {
            Worksheets.AddAsync(Worksheet, CancellationToken.None).GetAwaiter().GetResult();
        }

        public InMemoryWorksheetRepository Worksheets { get; } = new();

        public InMemoryWorksheetColumnRepository Columns { get; } = new();

        public DuckDbWorksheetRawDataStore RawStore { get; } =
            new(new DuckDbRawDataStoreSettings(":memory:", memoryLimitBytes: 512L * 1024 * 1024, threads: 2));

        public Worksheet Worksheet { get; } = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

        public GraphDataQueryService Service => new(Worksheets, Columns, RawStore);

        // Adds column metadata and stores the column's raw values (one block per call keeps their lengths independent).
        public async Task<WorksheetColumn> AddNumericAsync(string name, int index, IReadOnlyList<double?> values)
        {
            var column = await AddColumnAsync(name, index, WorksheetDataType.Numeric);
            await RawStore.WriteColumnsAsync(Worksheet.Id, new RawDataBlock([new NumericRawDataColumn(column.Id, values)]), Token);
            return column;
        }

        public async Task<WorksheetColumn> AddStringAsync(string name, int index, IReadOnlyList<string?> values)
        {
            var column = await AddColumnAsync(name, index, WorksheetDataType.String);
            await RawStore.WriteColumnsAsync(Worksheet.Id, new RawDataBlock([new StringRawDataColumn(column.Id, values)]), Token);
            return column;
        }

        public async Task<WorksheetColumn> AddColumnAsync(string name, int index, WorksheetDataType dataType)
        {
            var column = new WorksheetColumn
            {
                Id = Guid.NewGuid(),
                WorksheetId = Worksheet.Id,
                Index = index,
                Name = name,
                DataType = dataType
            };
            await Columns.AddAsync(column, Token);
            return column;
        }

        public GraphConfiguration Configuration(GraphType graphType, params (GraphVariableRole Role, WorksheetColumn Column)[] assignments) =>
            new(graphType, Worksheet.Id, [.. assignments.Select(item => new GraphColumnAssignment(item.Role, item.Column.Id))]);

        public void Dispose() => RawStore.Dispose();
    }

    [Fact]
    public async Task ColumnsOfDifferentStoredLengthsAlignByRowAndPadWithNull()
    {
        using var composition = new Composition();
        var x = await composition.AddNumericAsync("X", 0, [1, 2, 3, 4, 5]);
        var y = await composition.AddNumericAsync("Y", 1, [10, null, 30]);
        var group = await composition.AddStringAsync("Lot", 2, ["A", "B", null, "D"]);

        var data = Assert.IsType<ScatterGraphData>(await composition.Service.LoadAsync(composition.Configuration(
            GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, group)), Token));

        // Row 1 has no Y, rows 3 and 4 are beyond Y's stored rows; row 2 keeps its value with a missing group.
        Assert.Equal([1, 3], data.XValues.ToArray());
        Assert.Equal([10, 30], data.YValues.ToArray());
        Assert.Equal(["A", null], ((StringGroupData)data.Group!).Values.ToArray());
    }

    [Fact]
    public async Task AColumnWithMetadataButNoStoredValuesYieldsNoObservations()
    {
        using var composition = new Composition();
        var x = await composition.AddColumnAsync("X", 0, WorksheetDataType.Numeric);
        var y = await composition.AddNumericAsync("Y", 1, [10, 20]);

        var data = await composition.Service.LoadAsync(
            composition.Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y)), Token);

        Assert.Equal(0, data.Count);
    }

    [Fact]
    public async Task DeletedRawColumnsAreReportedAsUnavailable()
    {
        using var composition = new Composition();
        var variable = await composition.AddNumericAsync("Reg1", 0, [1, 2, 3]);
        var configuration = composition.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, variable));
        await composition.RawStore.DeleteColumnsAsync(composition.Worksheet.Id, [variable.Id], Token);

        // The column metadata still exists, but its values are gone: the graph has no observations.
        var data = await composition.Service.LoadAsync(configuration, Token);

        Assert.Equal(0, data.Count);
    }

    [Fact]
    public async Task ObservationsKeepWorksheetOrderAcrossReadChunksAndBlocks()
    {
        using var composition = new Composition();
        var rowCount = (GraphDataQueryService.ReadChunkRowCount * 2) + 123;
        var xValues = new double?[rowCount];
        var yValues = new double?[rowCount];
        for (var row = 0; row < rowCount; row++)
        {
            xValues[row] = row;
            yValues[row] = row % 7 == 0 ? null : rowCount - row;
        }

        var x = await composition.AddNumericAsync("X", 0, xValues);
        var y = await composition.AddNumericAsync("Y", 1, yValues);

        var data = Assert.IsType<ScatterGraphData>(await composition.Service.LoadAsync(
            composition.Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y)), Token));

        var expected = Enumerable.Range(0, rowCount).Where(row => row % 7 != 0).ToArray();
        Assert.Equal(expected.Length, data.Count);
        Assert.Equal(expected.Select(row => (double)row), data.XValues.ToArray());
        Assert.Equal(expected.Select(row => (double)(rowCount - row)), data.YValues.ToArray());
    }

    // A realistic dataset: 200,000 rows of X, Y and a String group, with nulls in each. Timing is reported, never asserted.
    [Fact]
    public async Task ALargeScatterDatasetIsExtractedWithAlignedObservations()
    {
        using var composition = new Composition();
        const int RowCount = 200_000;
        var xValues = new double?[RowCount];
        var yValues = new double?[RowCount];
        var groupValues = new string?[RowCount];
        var expected = new List<(double X, double Y, string? Group)>();

        for (var row = 0; row < RowCount; row++)
        {
            xValues[row] = row % 11 == 0 ? null : row * 0.5;
            yValues[row] = row % 13 == 0 ? null : row * 2.0;
            groupValues[row] = row % 5 == 0 ? null : $"Lot{row % 4}";

            if (xValues[row] is { } x && yValues[row] is { } y)
            {
                expected.Add((x, y, groupValues[row]));
            }
        }

        var xColumn = await composition.AddNumericAsync("X", 0, xValues);
        var yColumn = await composition.AddNumericAsync("Y", 1, yValues);
        var groupColumn = await composition.AddStringAsync("Lot", 2, groupValues);

        var watch = Stopwatch.StartNew();
        var data = Assert.IsType<ScatterGraphData>(await composition.Service.LoadAsync(composition.Configuration(
            GraphType.ScatterPlot,
            (GraphVariableRole.X, xColumn),
            (GraphVariableRole.Y, yColumn),
            (GraphVariableRole.Group, groupColumn)), Token));
        watch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{RowCount:N0} rows -> {data.Count:N0} observations in {watch.ElapsedMilliseconds:N0} ms");

        Assert.Equal(expected.Count, data.Count);
        Assert.Equal(data.Count, data.Group!.Count);

        var groups = ((StringGroupData)data.Group).Values;
        for (var index = 0; index < expected.Count; index++)
        {
            // Every observation keeps the X, Y and group of one worksheet row.
            if (data.XValues.Span[index] != expected[index].X
                || data.YValues.Span[index] != expected[index].Y
                || groups.Span[index] != expected[index].Group)
            {
                Assert.Fail($"Observation {index} does not match its worksheet row.");
            }
        }
    }
}
