using YAT.Application.Abstractions.Persistence;
using YAT.Application.Ingestion;
using YAT.Application.Queries;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

// WorksheetDataQueryService over the in-memory metadata repositories and a private in-memory DuckDB raw store.
public class WorksheetDataQueryIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Composition : IDisposable
    {
        public InMemoryWorksheetRepository Worksheets { get; } = new();

        public InMemoryWorksheetColumnRepository Columns { get; } = new();

        public DuckDbWorksheetRawDataStore RawStore { get; } =
            new(new DuckDbRawDataStoreSettings(":memory:", memoryLimitBytes: 256L * 1024 * 1024, threads: 1));

        public Worksheet Worksheet { get; } = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

        public WorksheetDataQueryService Query => new(Worksheets, Columns, RawStore);

        public async Task PasteAsync(string text, int startColumnIndex)
        {
            var data = new TabularTextParser().Parse(text);
            var existing = await Columns.GetByWorksheetIdAsync(Worksheet.Id, Token);
            var plan = new WorksheetPastePlanner(new ColumnDataTypeDetector()).Plan(Worksheet.Id, existing, startColumnIndex, data);
            await new PasteExecutionService(Worksheets, Columns, RawStore).ExecuteAsync(plan, data, Token);
        }

        public void Dispose() => RawStore.Dispose();
    }

    [Fact]
    public async Task ExportsPastedColumnsAsTsvAcrossBlocksAndReadChunks()
    {
        using var composition = new Composition();
        await composition.Worksheets.AddAsync(composition.Worksheet, Token);
        var rowCount = WorksheetColumnsTsvExporter.ReadChunkRowCount + 7;
        var reg = string.Join("\n", Enumerable.Range(0, rowCount).Select(row => (row * 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await composition.PasteAsync("Reg\n" + reg, startColumnIndex: 0);
        await composition.PasteAsync("SITE\tLot\tBin\n1\tN1\t3\n2\t\t4\n", startColumnIndex: 1);
        var columns = await composition.Columns.GetByWorksheetIdAsync(composition.Worksheet.Id, Token);
        var exporter = new WorksheetColumnsTsvExporter(composition.Worksheets, composition.Columns, composition.RawStore);

        // Bin and Reg, requested out of order, across two blocks; Reg spans two read chunks.
        var text = await exporter.ExportAsync(composition.Worksheet.Id, [columns[3].Id, columns[0].Id], Token);

        var lines = text.Split("\r\n");
        Assert.Equal(rowCount + 2, lines.Length);
        Assert.Equal("Reg\tBin", lines[0]);
        Assert.Equal("0\t3", lines[1]);
        Assert.Equal("0.5\t4", lines[2]);
        Assert.Equal("1\t", lines[3]);
        Assert.Equal($"{((rowCount - 1) * 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture)}\t", lines[^2]);

        var shortText = await exporter.ExportAsync(composition.Worksheet.Id, [columns[1].Id, columns[2].Id], Token);
        Assert.Equal("SITE\tLot\r\n1\tN1\r\n2\t\r\n", shortText);
    }

    [Fact]
    public async Task PagesPastedDataWithMetadataOnlyColumnsAndNullPadding()
    {
        using var composition = new Composition();
        await composition.Worksheets.AddAsync(composition.Worksheet, Token);
        var reg = string.Join("\n", Enumerable.Range(0, 1200).Select(row => row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await composition.PasteAsync("Reg\n" + reg, startColumnIndex: 0);
        await composition.PasteAsync("SITE\tLot\n1\tN1\n2\t\n", startColumnIndex: 1);
        await composition.Columns.AddAsync(new WorksheetColumn
        {
            Id = Guid.NewGuid(),
            WorksheetId = composition.Worksheet.Id,
            Index = 3,
            Name = "Vth",
            DataType = WorksheetDataType.Numeric
        }, Token);

        var first = await composition.Query.GetPageAsync(composition.Worksheet.Id, 0, WorksheetDataQueryService.MaxPageRowCount, Token);
        var second = await composition.Query.GetPageAsync(composition.Worksheet.Id, 500, WorksheetDataQueryService.MaxPageRowCount, Token);

        Assert.Equal(1200, first.TotalRowCount);
        Assert.Equal(500, first.RowCount);
        Assert.Equal(["Reg", "SITE", "Lot", "Vth"], first.Columns.Select(column => column.Column.Name));
        Assert.Equal([0, 1, 2], Assert.IsType<NumericRawDataColumn>(first.Columns[0].Values).Values.Take(3));
        Assert.Equal([1, 2, null], Assert.IsType<NumericRawDataColumn>(first.Columns[1].Values).Values.Take(3));
        Assert.Equal(["N1", null, null], Assert.IsType<StringRawDataColumn>(first.Columns[2].Values).Values.Take(3));
        Assert.All(Assert.IsType<NumericRawDataColumn>(first.Columns[3].Values).Values, value => Assert.Null(value));

        Assert.Equal(500, second.RowOffset);
        Assert.Equal(500, Assert.IsType<NumericRawDataColumn>(second.Columns[0].Values).Values[0]);
        Assert.All(Assert.IsType<NumericRawDataColumn>(second.Columns[1].Values).Values, value => Assert.Null(value));
    }
}
