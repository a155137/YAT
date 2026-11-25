using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Queries;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

public class WorksheetColumnWindowQueryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static (
        FakeWorksheetRepository Worksheets,
        FakeWorksheetColumnRepository Columns,
        FakeWorksheetRawDataStore RawStore,
        WorksheetDataQueryService Query,
        Worksheet Worksheet) CreateRuntime()
    {
        var worksheets = new FakeWorksheetRepository();
        var columns = new FakeWorksheetColumnRepository();
        var rawStore = new FakeWorksheetRawDataStore();
        var query = new WorksheetDataQueryService(worksheets, columns, rawStore);
        var worksheet = new Worksheet { Id = Guid.NewGuid(), Name = "Sheet1" };
        return (worksheets, columns, rawStore, query, worksheet);
    }

    [Fact]
    public async Task GetPageMetadataAsync_DoesNotReadRawCellValues_AndPreservesColumnOrder()
    {
        var (worksheets, columns, rawStore, query, worksheet) = CreateRuntime();
        await worksheets.AddAsync(worksheet, Token);

        var col0 = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = worksheet.Id, Index = 0, Name = "C0", DataType = WorksheetDataType.Numeric };
        var col1 = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = worksheet.Id, Index = 1, Name = "C1", DataType = WorksheetDataType.String };
        await columns.AddAsync(col1, Token);
        await columns.AddAsync(col0, Token);

        rawStore.Seed(worksheet.Id, new NumericRawDataColumn(col0.Id, Enumerable.Range(0, 620).Select(i => (double?)i).ToArray()));
        rawStore.Seed(worksheet.Id, new StringRawDataColumn(col1.Id, ["a", null]));

        var meta = await query.GetPageMetadataAsync(worksheet.Id, 500, WorksheetDataQueryService.MaxPageRowCount, Token);

        Assert.Empty(rawStore.Reads);
        Assert.Equal(worksheet.Id, meta.WorksheetId);
        Assert.Equal(620, meta.TotalRowCount);
        Assert.Equal(500, meta.RowOffset);
        Assert.Equal(120, meta.RowCount);
        Assert.Equal([col0.Id, col1.Id], meta.Columns.Select(c => c.Id));
    }

    [Fact]
    public async Task GetColumnWindowAsync_QueriesOnlyRequestedColumns_AndPreservesOrderAndMissingValues()
    {
        var (worksheets, columns, rawStore, query, worksheet) = CreateRuntime();
        await worksheets.AddAsync(worksheet, Token);

        var colIds = new Guid[10];
        for (var i = 0; i < 10; i++)
        {
            var col = new WorksheetColumn
            {
                Id = Guid.NewGuid(),
                WorksheetId = worksheet.Id,
                Index = i,
                Name = $"Col_{i}",
                DataType = i % 2 == 0 ? WorksheetDataType.Numeric : WorksheetDataType.String
            };
            colIds[i] = col.Id;
            await columns.AddAsync(col, Token);

            if (i < 8)
            {
                if (col.DataType == WorksheetDataType.Numeric)
                {
                    rawStore.Seed(worksheet.Id, new NumericRawDataColumn(col.Id, [i, null, i + 10.5]));
                }
                else
                {
                    rawStore.Seed(worksheet.Id, new StringRawDataColumn(col.Id, [$"s{i}", null]));
                }
            }
            // i == 8, 9 are metadata-only (no stored raw values).
        }

        // Request out of index order, mixing stored and metadata-only columns.
        Guid[] requested = [colIds[5], colIds[8], colIds[2]];
        var window = await query.GetColumnWindowAsync(worksheet.Id, 0, 10, requested, Token);

        var read = Assert.Single(rawStore.Reads);
        Assert.Equal([colIds[5], colIds[2]], read.ColumnIds);
        Assert.Equal(3, window.RowCount);
        Assert.Equal(requested, window.Columns.Select(c => c.Column.Id));

        Assert.Equal(["s5", null, null], Assert.IsType<StringRawDataColumn>(window.Columns[0].Values).Values);
        Assert.Equal([null, null, null], Assert.IsType<NumericRawDataColumn>(window.Columns[1].Values).Values);
        Assert.Equal([2d, null, 12.5d], Assert.IsType<NumericRawDataColumn>(window.Columns[2].Values).Values);
    }

    [Fact]
    public async Task GetColumnWindowAsync_ValidatesBoundsDuplicatesAndCancellation()
    {
        var (worksheets, columns, _, query, worksheet) = CreateRuntime();
        await worksheets.AddAsync(worksheet, Token);

        var id = Guid.NewGuid();
        await columns.AddAsync(new WorksheetColumn { Id = id, WorksheetId = worksheet.Id, Index = 0, Name = "A", DataType = WorksheetDataType.Numeric }, Token);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            query.GetColumnWindowAsync(worksheet.Id, 0, WorksheetDataQueryService.MaxPageRowCount, new Guid[WorksheetDataQueryService.MaxWindowColumnCount + 1], Token));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            query.GetColumnWindowAsync(worksheet.Id, 0, 10, [id, id], Token));

        await Assert.ThrowsAsync<EntityNotFoundException>(() =>
            query.GetColumnWindowAsync(worksheet.Id, 0, 10, [Guid.NewGuid()], Token));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            query.GetColumnWindowAsync(worksheet.Id, 0, 10, [id], cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            query.GetPageMetadataAsync(worksheet.Id, 0, 10, cts.Token));
    }

    [Fact]
    public async Task GetColumnWindowAsync_HandlesEmptyAndPastEndRowOffsets()
    {
        var (worksheets, columns, rawStore, query, worksheet) = CreateRuntime();
        await worksheets.AddAsync(worksheet, Token);

        var col = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = worksheet.Id, Index = 0, Name = "A", DataType = WorksheetDataType.Numeric };
        await columns.AddAsync(col, Token);
        rawStore.Seed(worksheet.Id, new NumericRawDataColumn(col.Id, [1d, 2d, 3d]));

        var pastEnd = await query.GetColumnWindowAsync(worksheet.Id, 3, 10, [col.Id], Token);
        Assert.Equal(0, pastEnd.RowCount);
        Assert.Empty(Assert.IsType<NumericRawDataColumn>(pastEnd.Columns[0].Values).Values);
        Assert.Empty(rawStore.Reads);

        var emptyCols = await query.GetColumnWindowAsync(worksheet.Id, 0, 10, [], Token);
        Assert.Equal(3, emptyCols.RowCount);
        Assert.Empty(emptyCols.Columns);
    }
}
