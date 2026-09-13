using YAT.Application.Exceptions;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

public class AddWorksheetColumnHandlerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Worksheet SeedWorksheet(FakeWorksheetRepository worksheets)
    {
        var worksheet = new Worksheet { Id = Guid.NewGuid(), Name = "WAT_Lot_A" };
        worksheets.Seed(worksheet);
        return worksheet;
    }

    [Fact]
    public async Task CreatesColumnPreservingTheSuppliedDefinition()
    {
        var worksheets = new FakeWorksheetRepository();
        var columns = new FakeWorksheetColumnRepository();
        var worksheet = SeedWorksheet(worksheets);
        var handler = new AddWorksheetColumnHandler(worksheets, columns);

        var column = await handler.HandleAsync(
            new AddWorksheetColumnCommand(
                worksheet.Id,
                Index: 3,
                Name: "  Vth  ",
                DataType: WorksheetDataType.Numeric,
                SemanticType: ColumnSemanticType.TestParameter,
                Unit: "mV"),
            Token);

        Assert.NotEqual(Guid.Empty, column.Id);
        Assert.Equal(worksheet.Id, column.WorksheetId);
        Assert.Equal(3, column.Index);
        Assert.Equal("Vth", column.Name);
        Assert.Equal(WorksheetDataType.Numeric, column.DataType);
        Assert.Equal(ColumnSemanticType.TestParameter, column.SemanticType);
        Assert.Equal("mV", column.Unit);
    }

    [Fact]
    public async Task AllowsUnclassifiedAndUnitlessColumns()
    {
        var worksheets = new FakeWorksheetRepository();
        var columns = new FakeWorksheetColumnRepository();
        var worksheet = SeedWorksheet(worksheets);
        var handler = new AddWorksheetColumnHandler(worksheets, columns);

        var column = await handler.HandleAsync(
            new AddWorksheetColumnCommand(
                worksheet.Id,
                Index: 0,
                Name: "RawValue",
                DataType: WorksheetDataType.String,
                SemanticType: null,
                Unit: null),
            Token);

        Assert.Null(column.SemanticType);
        Assert.Null(column.Unit);
    }

    [Fact]
    public async Task PersistsTheCreatedColumn()
    {
        var worksheets = new FakeWorksheetRepository();
        var columns = new FakeWorksheetColumnRepository();
        var worksheet = SeedWorksheet(worksheets);
        var handler = new AddWorksheetColumnHandler(worksheets, columns);

        var column = await handler.HandleAsync(
            new AddWorksheetColumnCommand(worksheet.Id, 0, "Vth", WorksheetDataType.Numeric, null, null),
            Token);

        Assert.Same(column, Assert.Single(columns.Added));
    }

    [Fact]
    public async Task RejectsUnknownWorksheet()
    {
        var worksheets = new FakeWorksheetRepository();
        var columns = new FakeWorksheetColumnRepository();
        var handler = new AddWorksheetColumnHandler(worksheets, columns);
        var missingWorksheetId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => handler.HandleAsync(
                new AddWorksheetColumnCommand(missingWorksheetId, 0, "Vth", WorksheetDataType.Numeric, null, null),
                Token));

        Assert.Equal(missingWorksheetId, exception.Id);
        Assert.Empty(columns.Added);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task RejectsNegativeIndex(int index)
    {
        var worksheets = new FakeWorksheetRepository();
        var columns = new FakeWorksheetColumnRepository();
        var worksheet = SeedWorksheet(worksheets);
        var handler = new AddWorksheetColumnHandler(worksheets, columns);

        await Assert.ThrowsAsync<ValidationException>(
            () => handler.HandleAsync(
                new AddWorksheetColumnCommand(worksheet.Id, index, "Vth", WorksheetDataType.Numeric, null, null),
                Token));

        Assert.Empty(columns.Added);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsBlankName(string? name)
    {
        var worksheets = new FakeWorksheetRepository();
        var columns = new FakeWorksheetColumnRepository();
        var worksheet = SeedWorksheet(worksheets);
        var handler = new AddWorksheetColumnHandler(worksheets, columns);

        await Assert.ThrowsAsync<ValidationException>(
            () => handler.HandleAsync(
                new AddWorksheetColumnCommand(worksheet.Id, 0, name!, WorksheetDataType.Numeric, null, null),
                Token));

        Assert.Empty(columns.Added);
    }
}
