using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// The values a graph's filter is chosen from (Task #049): one column's distinct values through the raw store, limited
// to GraphValueFilter.MaximumDistinctValues, with the column's own type, Missing and "more" - and failures said in the
// graph pipeline's own terms.
public class GraphFilterValuesQueryServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly Guid _worksheetId = Guid.NewGuid();
    private readonly FakeWorksheetColumnRepository _columns = new();
    private readonly FakeWorksheetRawDataStore _rawData = new();

    private GraphFilterValuesQueryService Service => new(_columns, _rawData);

    private WorksheetColumn Column(string name, WorksheetDataType dataType, int index)
    {
        var column = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = _worksheetId, Index = index, Name = name, DataType = dataType };
        _columns.Seed(column);
        return column;
    }

    private WorksheetColumn Numeric(string name, int index, params double?[] values)
    {
        var column = Column(name, WorksheetDataType.Numeric, index);
        _rawData.Seed(_worksheetId, new NumericRawDataColumn(column.Id, values));
        return column;
    }

    private WorksheetColumn Text(string name, int index, params string?[] values)
    {
        var column = Column(name, WorksheetDataType.String, index);
        _rawData.Seed(_worksheetId, new StringRawDataColumn(column.Id, values));
        return column;
    }

    [Fact]
    public async Task ANumericColumnsValuesAreListedInFirstOccurrenceOrder()
    {
        var site = Numeric("Site", 0, 3, 1, null, 3, 2);

        var values = await Service.LoadAsync(_worksheetId, site.Id, Token);

        Assert.Equal(site.Id, values.ColumnId);
        Assert.Equal(WorksheetDataType.Numeric, values.DataType);
        Assert.Equal([3, 1, 2], values.Numbers);
        Assert.Empty(values.Texts);
        Assert.Equal(3, values.Count);
        Assert.True(values.HasMissing);
        Assert.False(values.HasMore);
    }

    [Fact]
    public async Task ATextColumnsValuesAreListedAsText()
    {
        var lot = Text("Lot", 0, "B", "A", "B");

        var values = await Service.LoadAsync(_worksheetId, lot.Id, Token);

        Assert.Equal(WorksheetDataType.String, values.DataType);
        Assert.Equal(["B", "A"], values.Texts);
        Assert.Empty(values.Numbers);
        Assert.False(values.HasMissing);
    }

    [Fact]
    public async Task AtMostTheMostThatCanBeOfferedAreAskedFor()
    {
        var site = Numeric("Site", 0, [.. Enumerable.Range(0, GraphValueFilter.MaximumDistinctValues + 1).Select(index => (double?)index)]);

        var values = await Service.LoadAsync(_worksheetId, site.Id, Token);

        Assert.Equal(GraphValueFilter.MaximumDistinctValues, Assert.Single(_rawData.DistinctReads).Limit);
        Assert.Equal(GraphValueFilter.MaximumDistinctValues, values.Count);
        Assert.True(values.HasMore);
    }

    [Fact]
    public async Task AColumnWithoutStoredValuesHasOnlyMissingRows()
    {
        Numeric("Reg", 0, 1, 2);
        var site = Column("Site", WorksheetDataType.Numeric, 1);

        var values = await Service.LoadAsync(_worksheetId, site.Id, Token);

        Assert.Equal(0, values.Count);
        Assert.True(values.HasMissing);
        Assert.False(values.HasMore);
        Assert.Empty(_rawData.DistinctReads);
    }

    [Fact]
    public async Task AColumnWithoutStoredValuesInAnEmptyWorksheetHasNothing()
    {
        var lot = Column("Lot", WorksheetDataType.String, 0);

        var values = await Service.LoadAsync(_worksheetId, lot.Id, Token);

        Assert.Equal(WorksheetDataType.String, values.DataType);
        Assert.Equal(0, values.Count);
        Assert.False(values.HasMissing);
    }

    [Fact]
    public async Task AColumnThatIsGoneIsUnavailable()
    {
        var exception = await Assert.ThrowsAsync<GraphDataException>(() => Service.LoadAsync(_worksheetId, Guid.NewGuid(), Token));

        Assert.Equal(GraphDataError.ColumnUnavailable, exception.Error);
    }

    [Fact]
    public async Task AColumnThatCannotFilterIsRefused()
    {
        var tested = Column("Tested", WorksheetDataType.DateTime, 0);

        var exception = await Assert.ThrowsAsync<GraphDataException>(() => Service.LoadAsync(_worksheetId, tested.Id, Token));

        Assert.Equal(GraphDataError.InvalidConfiguration, exception.Error);
    }

    [Fact]
    public async Task AStorageFailureIsAReadFailure()
    {
        var site = Numeric("Site", 0, 1);
        _rawData.ReadFailure = new RawDataStorageException("disk");

        var exception = await Assert.ThrowsAsync<GraphDataException>(() => Service.LoadAsync(_worksheetId, site.Id, Token));

        Assert.Equal(GraphDataError.DataReadFailed, exception.Error);
        Assert.IsType<RawDataStorageException>(exception.InnerException);
    }

    [Fact]
    public async Task AnAlreadyCancelledRequestReadsNothing()
    {
        var site = Numeric("Site", 0, 1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.LoadAsync(_worksheetId, site.Id, cancellation.Token));
        Assert.Empty(_rawData.DistinctReads);
    }

    [Fact]
    public async Task TheValuesCanonicalizeAFilterThatSelectsThemAll()
    {
        var site = Numeric("Site", 0, 1, null, 2);
        var values = await Service.LoadAsync(_worksheetId, site.Id, Token);

        Assert.Null(GraphValueFilter.Canonicalize(new NumericValueFilter(site.Id, [2, 1], includeMissing: true), values));

        var partial = new NumericValueFilter(site.Id, [1, 2]);
        Assert.Same(partial, GraphValueFilter.Canonicalize(partial, values));
    }
}
