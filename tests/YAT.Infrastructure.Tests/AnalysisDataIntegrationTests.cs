using System.Diagnostics;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Analyses;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

// The analysis data pipeline over real DuckDB raw storage: aligned multi-column reads, the worksheet's own logical row
// count as the basis for what is missing, and a large dataset read in chunks.
public class AnalysisDataIntegrationTests
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

        public AnalysisDataQueryService Service => new(Worksheets, Columns, RawStore);

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

        public AnalysisConfiguration Configuration(IEnumerable<WorksheetColumn> variables, WorksheetColumn? group = null) =>
            new(Worksheet.Id, [.. variables.Select(column => column.Id)], group?.Id);

        public void Dispose() => RawStore.Dispose();
    }

    [Fact]
    public async Task VariablesAndTheGroupAreReadRowAlignedFromStorage()
    {
        using var composition = new Composition();
        var reg1 = await composition.AddNumericAsync("Reg1", 0, [1, null, 3]);
        var reg2 = await composition.AddNumericAsync("Reg2", 1, [10, 20, null]);
        var site = await composition.AddStringAsync("SITE", 2, ["A", null, "B"]);

        var data = await composition.Service.LoadAsync(composition.Configuration([reg1, reg2], site), Token);

        Assert.Equal(3, data.RowCount);
        Assert.Equal([1, null, 3], data.Variables[0].Values.ToArray());
        Assert.Equal([10, 20, null], data.Variables[1].Values.ToArray());
        Assert.Equal(["A", null, "B"], ((StringAnalysisGroupData)data.Group!).Values.ToArray());
    }

    // The worksheet's logical row count is what a variable is reported over, whatever it was selected with.
    [Fact]
    public async Task AShortColumnIsPaddedToTheWorksheetsLogicalRowCount()
    {
        using var composition = new Composition();
        await composition.AddNumericAsync("Reg1", 0, [1, 2, 3, 4, 5]);
        var reg2 = await composition.AddNumericAsync("Reg2", 1, [10, 20]);

        var alone = await composition.Service.LoadAsync(composition.Configuration([reg2]), Token);

        Assert.Equal(5, await composition.RawStore.GetWorksheetRowCountAsync(composition.Worksheet.Id, Token));
        Assert.Equal(5, alone.RowCount);
        Assert.Equal([10, 20, null, null, null], alone.Variables[0].Values.ToArray());
    }

    [Fact]
    public async Task AColumnWithoutStoredValuesReadsAsEntirelyMissing()
    {
        using var composition = new Composition();
        await composition.AddNumericAsync("Reg1", 0, [1, 2, 3]);
        var reg2 = await composition.AddColumnAsync("Reg2", 1, WorksheetDataType.Numeric);

        var data = await composition.Service.LoadAsync(composition.Configuration([reg2]), Token);

        Assert.Equal(3, data.RowCount);
        Assert.Equal([null, null, null], data.Variables[0].Values.ToArray());
    }

    [Fact]
    public async Task ADatasetLongerThanOneReadWindowIsExtractedInOrder()
    {
        using var composition = new Composition();
        var rowCount = (AnalysisDataQueryService.ReadChunkRowCount * 2) + 123;
        var values = new double?[rowCount];
        for (var row = 0; row < rowCount; row++)
        {
            values[row] = row % 7 == 0 ? null : row;
        }

        var reg1 = await composition.AddNumericAsync("Reg1", 0, values);

        var data = await composition.Service.LoadAsync(composition.Configuration([reg1]), Token);

        Assert.Equal(rowCount, data.RowCount);
        Assert.Equal(values, data.Variables[0].Values.ToArray());
    }

    // A realistic dataset: 200,000 rows of three variables and a String group, with nulls in each. Timing is reported,
    // never asserted.
    [Fact]
    public async Task ALargeAnalysisDatasetIsExtractedWithAlignedRows()
    {
        using var composition = new Composition();
        const int RowCount = 200_000;
        var reg1Values = new double?[RowCount];
        var reg2Values = new double?[RowCount];
        var reg3Values = new double?[RowCount];
        var groupValues = new string?[RowCount];

        for (var row = 0; row < RowCount; row++)
        {
            reg1Values[row] = row % 11 == 0 ? null : row * 0.5;
            reg2Values[row] = row % 13 == 0 ? null : row * 2.0;
            reg3Values[row] = row * 0.25;
            groupValues[row] = row % 5 == 0 ? null : $"SITE{row % 4}";
        }

        var reg1 = await composition.AddNumericAsync("Reg1", 0, reg1Values);
        var reg2 = await composition.AddNumericAsync("Reg2", 1, reg2Values);
        var reg3 = await composition.AddNumericAsync("Reg3", 2, reg3Values);
        var site = await composition.AddStringAsync("SITE", 3, groupValues);

        var watch = Stopwatch.StartNew();
        var data = await composition.Service.LoadAsync(composition.Configuration([reg1, reg2, reg3], site), Token);
        watch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{RowCount:N0} rows x 3 variables + group extracted in {watch.ElapsedMilliseconds:N0} ms");

        Assert.Equal(RowCount, data.RowCount);

        var loadedReg1 = data.Variables[0].Values.Span;
        var loadedReg2 = data.Variables[1].Values.Span;
        var loadedGroups = ((StringAnalysisGroupData)data.Group!).Values.Span;
        for (var row = 0; row < RowCount; row++)
        {
            // Every row keeps the values and the group of one worksheet row.
            if (loadedReg1[row] != reg1Values[row] || loadedReg2[row] != reg2Values[row] || loadedGroups[row] != groupValues[row])
            {
                Assert.Fail($"Row {row} is not aligned with the worksheet.");
            }
        }
    }
}
