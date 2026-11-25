using System.Globalization;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Ingestion;
using YAT.Application.Queries;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

public class DuckDbWorksheetColumnWindowTests
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

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    public async Task QueriesPartialColumnWindowsAcrossWideWorksheets_AndMatchesLegacyGetPage(int columnCount)
    {
        using var composition = new Composition();
        await composition.Worksheets.AddAsync(composition.Worksheet, Token);

        const int rowCount = 520;
        var headers = string.Join('\t', Enumerable.Range(0, columnCount).Select(c => c % 4 == 3 ? $"文字_{c}" : $"Num_{c}"));
        var lines = new string[rowCount + 1];
        lines[0] = headers;
        for (var r = 0; r < rowCount; r++)
        {
            var rowCells = new string[columnCount];
            for (var c = 0; c < columnCount; c++)
            {
                if ((r + c) % 13 == 0)
                {
                    rowCells[c] = string.Empty;
                }
                else if (c % 4 == 3)
                {
                    rowCells[c] = $"晶圓_{r}_{c}";
                }
                else
                {
                    rowCells[c] = ((r * 1000) + c + 0.25).ToString(CultureInfo.InvariantCulture);
                }
            }

            lines[r + 1] = string.Join('\t', rowCells);
        }

        await composition.PasteAsync(string.Join('\n', lines), startColumnIndex: 0);

        var meta = await composition.Query.GetPageMetadataAsync(
            composition.Worksheet.Id, 500, WorksheetDataQueryService.MaxPageRowCount, Token);

        Assert.Equal(rowCount, meta.TotalRowCount);
        Assert.Equal(500, meta.RowOffset);
        Assert.Equal(20, meta.RowCount);
        Assert.Equal(columnCount, meta.Columns.Count);

        var windowStart = columnCount - WorksheetDataQueryService.MaxWindowColumnCount;
        var requestedIds = meta.Columns.Skip(windowStart).Take(WorksheetDataQueryService.MaxWindowColumnCount).Select(c => c.Id).ToArray();
        var window = await composition.Query.GetColumnWindowAsync(
            composition.Worksheet.Id, 500, WorksheetDataQueryService.MaxPageRowCount, requestedIds, Token);

        var legacyPage = await composition.Query.GetPageAsync(
            composition.Worksheet.Id, 500, WorksheetDataQueryService.MaxPageRowCount, Token);

        Assert.Equal(20, window.RowCount);
        Assert.Equal(requestedIds.Length, window.Columns.Count);

        for (var i = 0; i < requestedIds.Length; i++)
        {
            var legacyCol = legacyPage.Columns[windowStart + i];
            var windowCol = window.Columns[i];
            Assert.Equal(legacyCol.Column.Id, windowCol.Column.Id);
            Assert.Equal(legacyCol.Column.DataType, windowCol.Column.DataType);

            if (legacyCol.Values is NumericRawDataColumn expectedNum)
            {
                var actualNum = Assert.IsType<NumericRawDataColumn>(windowCol.Values);
                Assert.Equal(expectedNum.Values, actualNum.Values);
            }
            else
            {
                var expectedStr = Assert.IsType<StringRawDataColumn>(legacyCol.Values);
                var actualStr = Assert.IsType<StringRawDataColumn>(windowCol.Values);
                Assert.Equal(expectedStr.Values, actualStr.Values);
            }
        }
    }

    [Fact]
    public async Task HandlesShortEmptyAndMetadataOnlyColumnsAndOffsetBoundaries()
    {
        using var composition = new Composition();
        await composition.Worksheets.AddAsync(composition.Worksheet, Token);

        var longRows = string.Join('\n', Enumerable.Range(0, 510).Select(r => r.ToString(CultureInfo.InvariantCulture)));
        await composition.PasteAsync("Long\n" + longRows, startColumnIndex: 0);
        await composition.PasteAsync("ShortNum\tShortStr\n10.5\tHi\n\t\n", startColumnIndex: 1);

        var metaOnly = new WorksheetColumn
        {
            Id = Guid.NewGuid(),
            WorksheetId = composition.Worksheet.Id,
            Index = 3,
            Name = "MetaOnly",
            DataType = WorksheetDataType.String
        };
        await composition.Columns.AddAsync(metaOnly, Token);

        var meta = await composition.Query.GetPageMetadataAsync(composition.Worksheet.Id, 500, WorksheetDataQueryService.MaxPageRowCount, Token);
        Assert.Equal(510, meta.TotalRowCount);
        Assert.Equal(10, meta.RowCount);
        Assert.Equal(4, meta.Columns.Count);

        var window = await composition.Query.GetColumnWindowAsync(
            composition.Worksheet.Id,
            500,
            WorksheetDataQueryService.MaxPageRowCount,
            [meta.Columns[1].Id, meta.Columns[3].Id, meta.Columns[0].Id],
            Token);

        Assert.Equal(10, window.RowCount);
        Assert.All(Assert.IsType<NumericRawDataColumn>(window.Columns[0].Values).Values, v => Assert.Null(v));
        Assert.All(Assert.IsType<StringRawDataColumn>(window.Columns[1].Values).Values, v => Assert.Null(v));
        Assert.Equal([500d, 501d, 502d], Assert.IsType<NumericRawDataColumn>(window.Columns[2].Values).Values.Take(3));

        var atEnd = await composition.Query.GetColumnWindowAsync(
            composition.Worksheet.Id,
            510,
            WorksheetDataQueryService.MaxPageRowCount,
            [meta.Columns[0].Id],
            Token);
        Assert.Equal(0, atEnd.RowCount);
        Assert.Empty(Assert.IsType<NumericRawDataColumn>(atEnd.Columns[0].Values).Values);
    }

    [Fact]
    public async Task PageBoundaryAndDataMutationUpdateMetadataAndWindowRowCounts()
    {
        using var composition = new Composition();
        await composition.Worksheets.AddAsync(composition.Worksheet, Token);

        // Exact page boundary: 500 rows.
        var rows500 = string.Join('\n', Enumerable.Range(0, 500).Select(r => $"{r}\t{r * 2}"));
        await composition.PasteAsync("A\tB\n" + rows500, startColumnIndex: 0);

        var metaAtZero = await composition.Query.GetPageMetadataAsync(composition.Worksheet.Id, 0, WorksheetDataQueryService.MaxPageRowCount, Token);
        var metaAtBoundary = await composition.Query.GetPageMetadataAsync(composition.Worksheet.Id, 500, WorksheetDataQueryService.MaxPageRowCount, Token);
        Assert.Equal(500, metaAtZero.TotalRowCount);
        Assert.Equal(500, metaAtZero.RowCount);
        Assert.Equal(500, metaAtBoundary.TotalRowCount);
        Assert.Equal(0, metaAtBoundary.RowCount);

        // Extend worksheet to 502 rows via paste into column B.
        var rows502 = string.Join('\n', Enumerable.Range(0, 502).Select(r => (r * 3).ToString(CultureInfo.InvariantCulture)));
        await composition.PasteAsync("B\n" + rows502, startColumnIndex: 1);

        var updatedMeta = await composition.Query.GetPageMetadataAsync(composition.Worksheet.Id, 500, WorksheetDataQueryService.MaxPageRowCount, Token);
        Assert.Equal(502, updatedMeta.TotalRowCount);
        Assert.Equal(2, updatedMeta.RowCount);

        var updatedWindow = await composition.Query.GetColumnWindowAsync(
            composition.Worksheet.Id, 500, WorksheetDataQueryService.MaxPageRowCount, [updatedMeta.Columns[0].Id, updatedMeta.Columns[1].Id], Token);
        Assert.Equal(2, updatedWindow.RowCount);
        Assert.Equal([null, null], Assert.IsType<NumericRawDataColumn>(updatedWindow.Columns[0].Values).Values);
        Assert.Equal([1500d, 1503d], Assert.IsType<NumericRawDataColumn>(updatedWindow.Columns[1].Values).Values);

        // Delete longest column B -> row count shrinks back to 500.
        await composition.RawStore.DeleteColumnsAsync(composition.Worksheet.Id, [updatedMeta.Columns[1].Id], Token);
        await composition.Columns.DeleteAsync(updatedMeta.Columns[1].Id, Token);

        var afterDelete = await composition.Query.GetPageMetadataAsync(composition.Worksheet.Id, 500, WorksheetDataQueryService.MaxPageRowCount, Token);
        Assert.Equal(500, afterDelete.TotalRowCount);
        Assert.Equal(0, afterDelete.RowCount);
        Assert.Single(afterDelete.Columns);
    }
}
