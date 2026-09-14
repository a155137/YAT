using YAT.Application.Exceptions;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

public class InMemoryWorksheetColumnRepositoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WorksheetColumn Column(Guid id, int index) => Column(id, Guid.NewGuid(), index, "Vth");

    private static WorksheetColumn Column(Guid id, Guid worksheetId, int index, string name) => new()
    {
        Id = id,
        WorksheetId = worksheetId,
        Index = index,
        Name = name,
        DataType = WorksheetDataType.Numeric
    };

    [Fact]
    public async Task AcceptsColumnsWithDistinctIds()
    {
        var repository = new InMemoryWorksheetColumnRepository();

        await repository.AddAsync(Column(Guid.NewGuid(), 0), Token);
        await repository.AddAsync(Column(Guid.NewGuid(), 1), Token);
    }

    [Fact]
    public async Task AcceptsRepeatedAddOfTheSameId()
    {
        var repository = new InMemoryWorksheetColumnRepository();
        var id = Guid.NewGuid();

        await repository.AddAsync(Column(id, 0), Token);
        await repository.AddAsync(Column(id, 1), Token);
    }

    [Fact]
    public async Task ReturnsOnlyTheWorksheetsColumnsOrderedByIndex()
    {
        var repository = new InMemoryWorksheetColumnRepository();
        var worksheetId = Guid.NewGuid();
        var otherWorksheetId = Guid.NewGuid();

        await repository.AddAsync(Column(Guid.NewGuid(), worksheetId, 2, "SITE"), Token);
        await repository.AddAsync(Column(Guid.NewGuid(), otherWorksheetId, 0, "Lot"), Token);
        await repository.AddAsync(Column(Guid.NewGuid(), worksheetId, 0, "No"), Token);
        await repository.AddAsync(Column(Guid.NewGuid(), worksheetId, 1, "Bin"), Token);

        var columns = await repository.GetByWorksheetIdAsync(worksheetId, Token);

        Assert.Equal(["No", "Bin", "SITE"], columns.Select(column => column.Name));
        Assert.All(columns, column => Assert.Equal(worksheetId, column.WorksheetId));
    }

    [Fact]
    public async Task ReturnsEmptyListForWorksheetWithoutColumns()
    {
        var repository = new InMemoryWorksheetColumnRepository();
        await repository.AddAsync(Column(Guid.NewGuid(), 0), Token);

        var columns = await repository.GetByWorksheetIdAsync(Guid.NewGuid(), Token);

        Assert.Empty(columns);
    }

    [Fact]
    public async Task ReturnsTheSameInstanceThatWasAdded()
    {
        var repository = new InMemoryWorksheetColumnRepository();
        var column = Column(Guid.NewGuid(), Guid.NewGuid(), 0, "Vth");
        await repository.AddAsync(column, Token);

        var columns = await repository.GetByWorksheetIdAsync(column.WorksheetId, Token);

        Assert.Same(column, Assert.Single(columns));
    }

    [Fact]
    public async Task RepeatedAddOfTheSameIdIsReadBackOnce()
    {
        var repository = new InMemoryWorksheetColumnRepository();
        var id = Guid.NewGuid();
        var worksheetId = Guid.NewGuid();

        await repository.AddAsync(Column(id, worksheetId, 0, "Vth"), Token);
        await repository.AddAsync(Column(id, worksheetId, 3, "Ioff"), Token);

        var column = Assert.Single(await repository.GetByWorksheetIdAsync(worksheetId, Token));
        Assert.Equal("Ioff", column.Name);
        Assert.Equal(3, column.Index);
    }

    [Fact]
    public async Task UpdateReplacesTheColumnWithTheSameId()
    {
        var repository = new InMemoryWorksheetColumnRepository();
        var id = Guid.NewGuid();
        var worksheetId = Guid.NewGuid();
        await repository.AddAsync(Column(id, worksheetId, 2, "Vth"), Token);
        var replacement = Column(id, worksheetId, 2, "Lot");
        replacement.DataType = WorksheetDataType.String;

        await repository.UpdateAsync(replacement, Token);

        Assert.Same(replacement, Assert.Single(await repository.GetByWorksheetIdAsync(worksheetId, Token)));
    }

    [Fact]
    public async Task UpdateOfUnknownColumnThrowsAndStoresNothing()
    {
        var repository = new InMemoryWorksheetColumnRepository();
        var column = Column(Guid.NewGuid(), Guid.NewGuid(), 0, "Vth");

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(() => repository.UpdateAsync(column, Token));

        Assert.Equal(column.Id, exception.Id);
        Assert.Empty(await repository.GetByWorksheetIdAsync(column.WorksheetId, Token));
    }

    [Fact]
    public async Task DeleteRemovesOnlyThatColumnWithoutReindexing()
    {
        var repository = new InMemoryWorksheetColumnRepository();
        var worksheetId = Guid.NewGuid();
        var no = Column(Guid.NewGuid(), worksheetId, 0, "No");
        var site = Column(Guid.NewGuid(), worksheetId, 1, "SITE");
        var reg1 = Column(Guid.NewGuid(), worksheetId, 2, "Reg1");
        await repository.AddAsync(no, Token);
        await repository.AddAsync(site, Token);
        await repository.AddAsync(reg1, Token);

        await repository.DeleteAsync(site.Id, Token);

        var remaining = await repository.GetByWorksheetIdAsync(worksheetId, Token);
        Assert.Equal([no, reg1], remaining);
        Assert.Equal([0, 2], remaining.Select(column => column.Index));
    }

    [Fact]
    public async Task DeleteOfUnknownColumnThrows()
    {
        var repository = new InMemoryWorksheetColumnRepository();
        var id = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(() => repository.DeleteAsync(id, Token));

        Assert.Equal(id, exception.Id);
    }

    [Fact]
    public async Task DeletedColumnCannotBeUpdatedOrDeletedAgain()
    {
        var repository = new InMemoryWorksheetColumnRepository();
        var column = Column(Guid.NewGuid(), 0);
        await repository.AddAsync(column, Token);
        await repository.DeleteAsync(column.Id, Token);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => repository.UpdateAsync(column, Token));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => repository.DeleteAsync(column.Id, Token));
    }

    [Fact]
    public async Task ColumnsAddedThroughTheHandlerAreReadableWithoutUiState()
    {
        var worksheets = new InMemoryWorksheetRepository();
        var columns = new InMemoryWorksheetColumnRepository();
        var worksheet = new Worksheet { Id = Guid.NewGuid(), Name = "WAT_Lot_A" };
        await worksheets.AddAsync(worksheet, Token);
        var handler = new AddWorksheetColumnHandler(worksheets, columns);

        var bin = await handler.HandleAsync(
            new AddWorksheetColumnCommand(worksheet.Id, 1, "Bin", WorksheetDataType.Numeric, null, null), Token);
        var no = await handler.HandleAsync(
            new AddWorksheetColumnCommand(worksheet.Id, 0, "No", WorksheetDataType.Numeric, null, null), Token);

        var stored = await columns.GetByWorksheetIdAsync(worksheet.Id, Token);

        Assert.Equal([no.Id, bin.Id], stored.Select(column => column.Id));
    }
}
