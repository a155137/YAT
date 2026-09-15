using YAT.Application.Exceptions;
using YAT.Application.Features.Worksheets.RenameWorksheet;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;

namespace YAT.Application.Tests;

public class RenameWorksheetHandlerTests
{
    private static readonly DateTimeOffset Created = new(2026, 4, 2, 8, 15, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = new(2026, 4, 3, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RenameWorksheetHandler CreateHandler(FakeWorksheetRepository worksheets)
        => new(worksheets, new FixedTimeProvider(Now));

    private static Worksheet SeedWorksheet(FakeWorksheetRepository worksheets, Guid projectId, string name)
    {
        var worksheet = new Worksheet
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            RowCount = 1200,
            ColumnCount = 3,
            CreatedAt = Created,
            UpdatedAt = Created
        };
        worksheets.Seed(worksheet);
        return worksheet;
    }

    [Fact]
    public async Task RenamesTheWorksheetAndStoresIt()
    {
        var worksheets = new FakeWorksheetRepository();
        var sheet1 = SeedWorksheet(worksheets, Guid.NewGuid(), "Sheet1");

        var renamed = await CreateHandler(worksheets).HandleAsync(new RenameWorksheetCommand(sheet1.Id, "WAT_Lot_A"), Token);

        Assert.Equal(sheet1.Id, renamed.Id);
        Assert.Equal("WAT_Lot_A", renamed.Name);
        Assert.Same(renamed, Assert.Single(worksheets.Updated));
        Assert.Same(renamed, await worksheets.GetByIdAsync(sheet1.Id, Token));
    }

    [Fact]
    public async Task StoresARenamedCopyWithoutModifyingTheLoadedInstance()
    {
        var worksheets = new FakeWorksheetRepository();
        var sheet1 = SeedWorksheet(worksheets, Guid.NewGuid(), "Sheet1");

        var renamed = await CreateHandler(worksheets).HandleAsync(new RenameWorksheetCommand(sheet1.Id, "WAT_Lot_A"), Token);

        Assert.NotSame(sheet1, renamed);
        Assert.Equal("Sheet1", sheet1.Name);
        Assert.Equal(Created, sheet1.UpdatedAt);
    }

    [Fact]
    public async Task KeepsOtherFieldsAndStampsUpdatedAt()
    {
        var worksheets = new FakeWorksheetRepository();
        var projectId = Guid.NewGuid();
        var sheet1 = SeedWorksheet(worksheets, projectId, "Sheet1");

        var renamed = await CreateHandler(worksheets).HandleAsync(new RenameWorksheetCommand(sheet1.Id, "WAT_Lot_A"), Token);

        Assert.Equal(projectId, renamed.ProjectId);
        Assert.Equal(1200, renamed.RowCount);
        Assert.Equal(3, renamed.ColumnCount);
        Assert.Equal(Created, renamed.CreatedAt);
        Assert.Equal(Now, renamed.UpdatedAt);
    }

    [Fact]
    public async Task TrimsName()
    {
        var worksheets = new FakeWorksheetRepository();
        var sheet1 = SeedWorksheet(worksheets, Guid.NewGuid(), "Sheet1");

        var renamed = await CreateHandler(worksheets).HandleAsync(new RenameWorksheetCommand(sheet1.Id, "  WAT_Lot_A  "), Token);

        Assert.Equal("WAT_Lot_A", renamed.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsBlankName(string? name)
    {
        var worksheets = new FakeWorksheetRepository();
        var sheet1 = SeedWorksheet(worksheets, Guid.NewGuid(), "Sheet1");

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateHandler(worksheets).HandleAsync(new RenameWorksheetCommand(sheet1.Id, name!), Token));

        Assert.Empty(worksheets.Updated);
    }

    [Fact]
    public async Task RejectsUnknownWorksheet()
    {
        var worksheets = new FakeWorksheetRepository();
        var missingWorksheetId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => CreateHandler(worksheets).HandleAsync(new RenameWorksheetCommand(missingWorksheetId, "WAT_Lot_A"), Token));

        Assert.Equal(missingWorksheetId, exception.Id);
        Assert.Empty(worksheets.Updated);
    }

    [Theory]
    [InlineData("Sheet2")]
    [InlineData("sheet2")]
    [InlineData("SHEET2")]
    [InlineData("  sHeEt2  ")]
    public async Task RejectsANameUsedByAnotherWorksheetOfTheProjectIgnoringCase(string name)
    {
        var worksheets = new FakeWorksheetRepository();
        var projectId = Guid.NewGuid();
        var sheet1 = SeedWorksheet(worksheets, projectId, "Sheet1");
        SeedWorksheet(worksheets, projectId, "Sheet2");

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateHandler(worksheets).HandleAsync(new RenameWorksheetCommand(sheet1.Id, name), Token));

        Assert.Empty(worksheets.Updated);
        Assert.Equal("Sheet1", (await worksheets.GetByIdAsync(sheet1.Id, Token))!.Name);
    }

    [Theory]
    [InlineData("sheet1")]
    [InlineData("SHEET1")]
    [InlineData("Sheet1")]
    public async Task AllowsKeepingTheOwnNameWithDifferentCasing(string name)
    {
        var worksheets = new FakeWorksheetRepository();
        var sheet1 = SeedWorksheet(worksheets, Guid.NewGuid(), "Sheet1");

        var renamed = await CreateHandler(worksheets).HandleAsync(new RenameWorksheetCommand(sheet1.Id, name), Token);

        Assert.Equal(name, renamed.Name);
        Assert.Same(renamed, Assert.Single(worksheets.Updated));
    }

    [Fact]
    public async Task AllowsANameUsedInAnotherProject()
    {
        var worksheets = new FakeWorksheetRepository();
        var sheet1 = SeedWorksheet(worksheets, Guid.NewGuid(), "Sheet1");
        SeedWorksheet(worksheets, Guid.NewGuid(), "WAT_Lot_A");

        var renamed = await CreateHandler(worksheets).HandleAsync(new RenameWorksheetCommand(sheet1.Id, "wat_lot_a"), Token);

        Assert.Equal("wat_lot_a", renamed.Name);
    }
}
