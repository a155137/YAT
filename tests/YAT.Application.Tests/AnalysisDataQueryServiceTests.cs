using YAT.Application.Abstractions.Persistence;
using YAT.Application.Analyses;
using YAT.Application.Exceptions;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// Turning an analysis configuration into worksheet rows: one aligned read, nulls kept in place, and every variable
// reported over the worksheet's own logical rows.
public class AnalysisDataQueryServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Fixture
    {
        public Fixture()
        {
            Worksheets.Seed(new Worksheet { Id = WorksheetId, Name = "Sheet1" });
            Service = new AnalysisDataQueryService(Worksheets, Columns, RawData);
        }

        public Guid WorksheetId { get; } = Guid.NewGuid();

        public FakeWorksheetRepository Worksheets { get; } = new();

        public FakeWorksheetColumnRepository Columns { get; } = new();

        public FakeWorksheetRawDataStore RawData { get; } = new();

        public AnalysisDataQueryService Service { get; }

        // A worksheet column with metadata; values are stored separately (or not at all).
        public WorksheetColumn Column(string name, WorksheetDataType dataType, int index)
        {
            var column = new WorksheetColumn
            {
                Id = Guid.NewGuid(),
                WorksheetId = WorksheetId,
                Index = index,
                Name = name,
                DataType = dataType
            };
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

        public AnalysisConfiguration Configuration(IEnumerable<WorksheetColumn> variables, WorksheetColumn? group = null) =>
            new(WorksheetId, [.. variables.Select(column => column.Id)], group?.Id);

        public Task<AnalysisData> LoadAsync(AnalysisConfiguration configuration) => Service.LoadAsync(configuration, Token);
    }

    private static double?[] Values(AnalysisData data, int variable) => [.. data.Variables[variable].Values.ToArray()];

    // 0
    [Fact]
    public async Task EveryVariableKeepsItsWorksheetRowsIncludingTheEmptyOnes()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, null, 3);
        var reg2 = fixture.Numeric("Reg2", 1, null, 20, 30);

        var data = await fixture.LoadAsync(fixture.Configuration([reg1, reg2]));

        Assert.Equal(3, data.RowCount);
        Assert.Equal([1, null, 3], Values(data, 0));
        Assert.Equal([null, 20, 30], Values(data, 1));
        Assert.Equal(["Reg1", "Reg2"], data.Variables.Select(variable => variable.Column.Name));
        Assert.Equal(reg1.Id, data.Variables[0].Column.ColumnId);
        Assert.Null(data.Group);
    }

    // 1
    [Fact]
    public async Task TheVariablesAndTheGroupAreReadTogetherSoRowsStayAligned()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2, 3);
        var reg2 = fixture.Numeric("Reg2", 1, 10, 20, 30);
        var site = fixture.Text("SITE", 2, "A", null, "B");

        var data = await fixture.LoadAsync(fixture.Configuration([reg1, reg2], site));

        // One request, all three columns in it: the block the store returns is rectangular, so row i is row i.
        var read = Assert.Single(fixture.RawData.Reads);
        Assert.Equal([reg1.Id, reg2.Id, site.Id], read.ColumnIds);
        Assert.Equal(["A", null, "B"], ((StringAnalysisGroupData)data.Group!).Values.ToArray());
        Assert.Equal(site.Id, data.Group!.Column.ColumnId);
        Assert.True(data.Group.IsMissing(1));
    }

    // 2
    [Fact]
    public async Task ANumericGroupColumnStaysNumeric()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2);
        var site = fixture.Numeric("SITE", 1, 3, null);

        var data = await fixture.LoadAsync(fixture.Configuration([reg1], site));

        Assert.Equal([3, null], ((NumericAnalysisGroupData)data.Group!).Values.ToArray());
    }

    // 3
    [Fact]
    public async Task AVariableIsReportedOverTheWorksheetsRowsNotOverTheColumnsItWasSelectedWith()
    {
        // Reg2 holds 2 of the worksheet's 5 rows. What it is missing must not depend on which other columns are
        // selected with it, so the data always spans the worksheet's logical rows.
        var fixture = new Fixture();
        fixture.Numeric("Reg1", 0, 1, 2, 3, 4, 5);
        var reg2 = fixture.Numeric("Reg2", 1, 10, 20);

        var alone = await fixture.LoadAsync(fixture.Configuration([reg2]));

        Assert.Equal(5, alone.RowCount);
        Assert.Equal([10, 20, null, null, null], Values(alone, 0));
    }

    // 4
    [Fact]
    public async Task SelectingAnotherVariableChangesNothingAboutTheFirstOne()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2, 3, 4, 5);
        var reg2 = fixture.Numeric("Reg2", 1, 10, 20);

        var alone = await fixture.LoadAsync(fixture.Configuration([reg2]));
        var together = await fixture.LoadAsync(fixture.Configuration([reg1, reg2]));

        Assert.Equal(alone.RowCount, together.RowCount);
        Assert.Equal(Values(alone, 0), Values(together, 1));
    }

    // 5
    [Fact]
    public async Task AColumnWithoutStoredValuesIsEntirelyMissingRatherThanAFailure()
    {
        var fixture = new Fixture();
        fixture.Numeric("Reg1", 0, 1, 2, 3);
        var empty = fixture.Column("Reg2", WorksheetDataType.Numeric, 1);

        var data = await fixture.LoadAsync(fixture.Configuration([empty]));

        Assert.Equal(3, data.RowCount);
        Assert.Equal([null, null, null], Values(data, 0));
        Assert.Empty(fixture.RawData.Reads);
    }

    // 6
    [Fact]
    public async Task AWorksheetWithoutAnyStoredValuesHasNoRows()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Column("Reg1", WorksheetDataType.Numeric, 0);

        var data = await fixture.LoadAsync(fixture.Configuration([reg1]));

        Assert.Equal(0, data.RowCount);
        Assert.Empty(Values(data, 0));
    }

    // 7
    [Fact]
    public async Task AColumnUsedAsAVariableAndAsTheGroupIsRequestedOnlyOnce()
    {
        var fixture = new Fixture();
        var site = fixture.Numeric("SITE", 0, 1, 2, 1);

        var data = await fixture.LoadAsync(fixture.Configuration([site], site));

        var read = Assert.Single(fixture.RawData.Reads);
        Assert.Equal([site.Id], read.ColumnIds);
        Assert.Equal([1, 2, 1], Values(data, 0));
        Assert.Equal([1, 2, 1], ((NumericAnalysisGroupData)data.Group!).Values.ToArray());
    }

    // 8
    [Fact]
    public async Task LongWorksheetsAreReadInBoundedWindowsThatStayAligned()
    {
        const int RowCount = (AnalysisDataQueryService.ReadChunkRowCount * 2) + 123;
        var values = new double?[RowCount];
        var groups = new string?[RowCount];
        for (var row = 0; row < RowCount; row++)
        {
            values[row] = row % 3 == 0 ? null : row;
            groups[row] = $"G{row % 4}";
        }

        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, values);
        var site = fixture.Text("SITE", 1, groups);

        var data = await fixture.LoadAsync(fixture.Configuration([reg1], site));

        Assert.Equal(3, fixture.RawData.Reads.Count);
        Assert.Equal(RowCount, data.RowCount);
        Assert.All(fixture.RawData.Reads, read => Assert.True(read.RowCount <= AnalysisDataQueryService.ReadChunkRowCount));

        var loaded = Values(data, 0);
        var loadedGroups = ((StringAnalysisGroupData)data.Group!).Values.ToArray();
        for (var row = 0; row < RowCount; row++)
        {
            Assert.Equal(values[row], loaded[row]);
            Assert.Equal(groups[row], loadedGroups[row]);
        }
    }

    // 9
    [Fact]
    public async Task AnUnknownWorksheetIsReportedAsUnavailable()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1);

        var exception = await Assert.ThrowsAsync<AnalysisDataException>(
            () => fixture.Service.LoadAsync(new AnalysisConfiguration(Guid.NewGuid(), [reg1.Id], null), Token));

        Assert.Equal(AnalysisDataError.WorksheetUnavailable, exception.Error);
    }

    // 10
    [Fact]
    public async Task AConfigurationWithoutVariablesIsRefusedBeforeAnythingIsRead()
    {
        var fixture = new Fixture();
        fixture.Numeric("Reg1", 0, 1);

        var exception = await Assert.ThrowsAsync<AnalysisDataException>(
            () => fixture.LoadAsync(new AnalysisConfiguration(fixture.WorksheetId, [], null)));

        Assert.Equal(AnalysisDataError.InvalidConfiguration, exception.Error);
        Assert.Empty(fixture.RawData.Reads);
    }

    // 11
    [Fact]
    public async Task AColumnThatIsNoLongerPartOfTheWorksheetIsReportedAsUnavailable()
    {
        var fixture = new Fixture();
        fixture.Numeric("Reg1", 0, 1);

        var exception = await Assert.ThrowsAsync<AnalysisDataException>(
            () => fixture.LoadAsync(new AnalysisConfiguration(fixture.WorksheetId, [Guid.NewGuid()], null)));

        Assert.Equal(AnalysisDataError.ColumnUnavailable, exception.Error);
    }

    // 12
    [Fact]
    public async Task AFailedReadIsReportedWithTheStorageErrorInside()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2);
        var failure = new RawDataStorageException("The database is unavailable.");
        fixture.RawData.ReadFailure = failure;

        var exception = await Assert.ThrowsAsync<AnalysisDataException>(() => fixture.LoadAsync(fixture.Configuration([reg1])));

        Assert.Equal(AnalysisDataError.DataReadFailed, exception.Error);
        Assert.Same(failure, exception.InnerException);
    }

    // 13
    [Fact]
    public async Task ACancelledRequestStopsTheRead()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Service.LoadAsync(fixture.Configuration([reg1]), cancellation.Token));

        Assert.Empty(fixture.RawData.Reads);
    }
}
