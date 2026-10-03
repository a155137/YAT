using System.Diagnostics;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Analyses;
using YAT.Application.Filtering;
using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

// What a row filter costs at engineering size (Task #053): one million rows in DuckDB, a graph read and an analysis read
// without a filter and with four conditions over three more columns. Explicit - a normal run skips it:
//
//     YAT.Infrastructure.Tests.exe -explicit only -class YAT.Infrastructure.Tests.RowFilterPerformanceTests
//
// Timings are written to the test output for information; nothing here passes or fails on time. What is checked is that
// the filtered reads keep exactly the rows the conditions describe.
public sealed class RowFilterPerformanceTests : IDisposable
{
    private const int Rows = 1_000_000;
    private const int Runs = 3;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly DuckDbWorksheetRawDataStore _store = new(new DuckDbRawDataStoreSettings(":memory:", memoryLimitBytes: 2L * 1024 * 1024 * 1024, threads: 4));
    private readonly InMemoryWorksheetRepository _worksheets = new();
    private readonly InMemoryWorksheetColumnRepository _columns = new();
    private readonly Worksheet _worksheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

    public void Dispose() => _store.Dispose();

    private static void Report(string message)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        Console.WriteLine(message);
    }

    private async Task<WorksheetColumn> AddAsync(string name, int index, RawDataColumn values)
    {
        var column = new WorksheetColumn { Id = values.ColumnId, WorksheetId = _worksheet.Id, Index = index, Name = name, DataType = values.DataType };
        await _columns.AddAsync(column, Token);
        await _store.WriteColumnsAsync(_worksheet.Id, new RawDataBlock([values]), Token);
        return column;
    }

    private static async Task<(double Best, T Result)> Time<T>(Func<Task<T>> run)
    {
        var best = double.MaxValue;
        T result = default!;
        for (var attempt = 0; attempt < Runs; attempt++)
        {
            var watch = Stopwatch.StartNew();
            result = await run();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }

        return (best, result);
    }

    [Fact(Explicit = true)]
    public async Task FourConditionsOverAMillionRows()
    {
        await _worksheets.AddAsync(_worksheet, Token);
        var random = new Random(53);
        var current = new double?[Rows];
        var site = new double?[Rows];
        var bin = new double?[Rows];
        var lot = new string?[Rows];
        for (var row = 0; row < Rows; row++)
        {
            current[row] = row % 97 == 0 ? null : Math.Round(15 + (random.NextDouble() - 0.5) * 2, 4);
            site[row] = row % 8 + 1;
            bin[row] = row % 53 == 0 ? null : random.Next(1, 6);
            lot[row] = $"Lot {row % 8}";
        }

        var currentColumn = await AddAsync("Current", 0, new NumericRawDataColumn(Guid.NewGuid(), current));
        var siteColumn = await AddAsync("Site", 1, new NumericRawDataColumn(Guid.NewGuid(), site));
        var binColumn = await AddAsync("Bin", 2, new NumericRawDataColumn(Guid.NewGuid(), bin));
        var lotColumn = await AddAsync("Lot", 3, new StringRawDataColumn(Guid.NewGuid(), lot));

        var filter = new RowFilter(
        [
            new NumericValueSetCondition(siteColumn.Id, [1, 3, 5, 7]),
            new NumericComparisonCondition(currentColumn.Id, NumericComparison.GreaterOrEqual, 14.5),
            new NumericComparisonCondition(currentColumn.Id, NumericComparison.LessOrEqual, 15.5),
            new NumericValueSetCondition(binColumn.Id, [3], exclude: true)
        ]);
        var expected = Enumerable.Range(0, Rows).Count(row =>
            site[row] is 1 or 3 or 5 or 7 && current[row] is >= 14.5 and <= 15.5 && bin[row] != 3);

        // A histogram of Current grouped by Lot.
        var graphs = new GraphDataQueryService(_worksheets, _columns, _store);
        GraphConfiguration Graph(RowFilter? rows) => new(GraphType.Histogram, _worksheet.Id,
            [new GraphColumnAssignment(GraphVariableRole.Variable, currentColumn.Id), new GraphColumnAssignment(GraphVariableRole.Group, lotColumn.Id)])
        {
            Filter = rows
        };

        var (graphAll, all) = await Time(() => graphs.LoadAsync(Graph(null), Token));
        var (graphFiltered, filtered) = await Time(() => graphs.LoadAsync(Graph(filter), Token));
        Assert.Equal(expected, filtered.FilteredRowCount);
        Assert.Equal(Rows - (Rows / 97 + 1), all.Count);

        // Descriptive Statistics of Current grouped by Lot.
        var analyses = new AnalysisDataQueryService(_worksheets, _columns, _store);
        AnalysisConfiguration Analysis(RowFilter? rows) => new(_worksheet.Id, [currentColumn.Id], lotColumn.Id) { Filter = rows };

        var (analysisAll, allRows) = await Time(() => analyses.LoadAsync(Analysis(null), Token));
        var (analysisFiltered, filteredRows) = await Time(() => analyses.LoadAsync(Analysis(filter), Token));
        Assert.Equal(Rows, allRows.RowCount);
        Assert.Equal(expected, filteredRows.RowCount);

        Report($"Rows: {Rows:N0}; kept by the 4 conditions: {expected:N0} ({100.0 * expected / Rows:F1} %). Best of {Runs} runs.");
        Report($"Graph read (Histogram, Current by Lot):    no filter {graphAll,7:F0} ms; filtered {graphFiltered,7:F0} ms ({100 * (graphFiltered - graphAll) / graphAll:+0;-0} %)");
        Report($"Analysis read (Descriptive, Current by Lot): no filter {analysisAll,7:F0} ms; filtered {analysisFiltered,7:F0} ms ({100 * (analysisFiltered - analysisAll) / analysisAll:+0;-0} %)");
    }
}
