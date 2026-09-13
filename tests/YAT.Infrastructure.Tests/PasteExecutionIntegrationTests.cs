using YAT.Application.Abstractions.Persistence;
using YAT.Application.Ingestion;
using YAT.Domain.Entities;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

// Paste execution against the real in-memory metadata repositories and a private in-memory DuckDB raw store.
public class PasteExecutionIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Composition : IDisposable
    {
        public InMemoryWorksheetRepository Worksheets { get; } = new();

        public InMemoryWorksheetColumnRepository Columns { get; } = new();

        public DuckDbWorksheetRawDataStore RawStore { get; } =
            new(new DuckDbRawDataStoreSettings(":memory:", memoryLimitBytes: 256L * 1024 * 1024, threads: 1));

        public Worksheet Worksheet { get; } = new() { Id = Guid.NewGuid(), Name = "WAT_Lot_A" };

        public async Task<PasteExecutionResult> PasteAsync(string text, int startColumnIndex)
        {
            var data = new TabularTextParser().Parse(text);
            var existing = await Columns.GetByWorksheetIdAsync(Worksheet.Id, Token);
            var plan = new WorksheetPastePlanner(new ColumnDataTypeDetector()).Plan(Worksheet.Id, existing, startColumnIndex, data);

            return await new PasteExecutionService(Worksheets, Columns, RawStore).ExecuteAsync(plan, data, Token);
        }

        public void Dispose() => RawStore.Dispose();
    }

    [Fact]
    public async Task CanonicalPasteIsStoredRowAligned()
    {
        using var composition = new Composition();
        await composition.Worksheets.AddAsync(composition.Worksheet, Token);

        await composition.PasteAsync(
            "No\tBin\tSITE\tReg1\tReg2\tReg3\n" +
            "1\t1\t1\t5\t.132\t500\n" +
            "2\t2\t2\t7\t.157\t2350\n" +
            "3\t1\t3\t2\t.122\t450\n",
            startColumnIndex: 0);

        var columns = await composition.Columns.GetByWorksheetIdAsync(composition.Worksheet.Id, Token);
        var block = await composition.RawStore.ReadColumnsAsync(
            composition.Worksheet.Id, columns.Select(column => column.Id).ToArray(), 0, int.MaxValue, Token);

        Assert.Equal(["No", "Bin", "SITE", "Reg1", "Reg2", "Reg3"], columns.Select(column => column.Name));
        Assert.Equal(3, block.RowCount);
        double?[][] expected = [[1, 2, 3], [1, 2, 1], [1, 2, 3], [5, 7, 2], [0.132, 0.157, 0.122], [500, 2350, 450]];
        Assert.Equal(expected, block.Columns.Select(column => Assert.IsType<NumericRawDataColumn>(column).Values.ToArray()));
    }

    [Fact]
    public async Task OverwriteWithDifferentRowCountKeepsIdAndLeavesUntouchedColumns()
    {
        using var composition = new Composition();
        await composition.Worksheets.AddAsync(composition.Worksheet, Token);
        var first = await composition.PasteAsync("No\tBin\tSITE\n1\t1\t1\n2\t2\t2\n3\t1\t3\n", startColumnIndex: 0);

        var second = await composition.PasteAsync("Lot\nN1\n \nN3\nN4\nN5\n", startColumnIndex: 1);

        var binId = first.Columns[1].ColumnId;
        Assert.Equal(binId, Assert.Single(second.Columns).ColumnId);

        var ids = first.Columns.Select(column => column.ColumnId).ToArray();
        var block = await composition.RawStore.ReadColumnsAsync(composition.Worksheet.Id, ids, 0, int.MaxValue, Token);

        Assert.Equal(5, block.RowCount);
        Assert.Equal([1, 2, 3, null, null], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal(["N1", null, "N3", "N4", "N5"], Assert.IsType<StringRawDataColumn>(block.Columns[1]).Values);
        Assert.Equal([1, 2, 3, null, null], Assert.IsType<NumericRawDataColumn>(block.Columns[2]).Values);
        Assert.Equal(["No", "Lot", "SITE"], (await composition.Columns.GetByWorksheetIdAsync(composition.Worksheet.Id, Token)).Select(column => column.Name));
    }
}
