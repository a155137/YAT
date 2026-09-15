using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

public class InMemoryWorksheetRepositoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReturnsTheStoredInstance()
    {
        var repository = new InMemoryWorksheetRepository();
        var worksheet = new Worksheet { Id = Guid.NewGuid(), Name = "WAT_Lot_A" };

        await repository.AddAsync(worksheet, Token);
        var found = await repository.GetByIdAsync(worksheet.Id, Token);

        Assert.Same(worksheet, found);
    }

    [Fact]
    public async Task ReturnsNullForUnknownId()
    {
        var repository = new InMemoryWorksheetRepository();

        var found = await repository.GetByIdAsync(Guid.NewGuid(), Token);

        Assert.Null(found);
    }

    [Fact]
    public async Task OverwritesEntryWithTheSameId()
    {
        var repository = new InMemoryWorksheetRepository();
        var id = Guid.NewGuid();
        var original = new Worksheet { Id = id, Name = "Original" };
        var replacement = new Worksheet { Id = id, Name = "Replacement" };

        await repository.AddAsync(original, Token);
        await repository.AddAsync(replacement, Token);
        var found = await repository.GetByIdAsync(id, Token);

        Assert.Same(replacement, found);
    }

    [Fact]
    public async Task KeepsWorksheetsWithDistinctIdsSeparate()
    {
        var repository = new InMemoryWorksheetRepository();
        var first = new Worksheet { Id = Guid.NewGuid(), Name = "First" };
        var second = new Worksheet { Id = Guid.NewGuid(), Name = "Second" };

        await repository.AddAsync(first, Token);
        await repository.AddAsync(second, Token);

        Assert.Same(first, await repository.GetByIdAsync(first.Id, Token));
        Assert.Same(second, await repository.GetByIdAsync(second.Id, Token));
    }

    [Fact]
    public async Task GetByProjectIdReturnsOnlyThatProjectsWorksheetsInCreationOrder()
    {
        var repository = new InMemoryWorksheetRepository();
        var projectId = Guid.NewGuid();
        var third = new Worksheet { Id = Guid.NewGuid(), ProjectId = projectId, Name = "C" };
        var first = new Worksheet { Id = Guid.NewGuid(), ProjectId = projectId, Name = "A" };
        var other = new Worksheet { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Name = "Other" };
        var second = new Worksheet { Id = Guid.NewGuid(), ProjectId = projectId, Name = "B" };

        await repository.AddAsync(third, Token);
        await repository.AddAsync(first, Token);
        await repository.AddAsync(other, Token);
        await repository.AddAsync(second, Token);

        Assert.Equal([third, first, second], await repository.GetByProjectIdAsync(projectId, Token));
    }

    [Fact]
    public async Task GetByProjectIdReturnsEmptyForAProjectWithoutWorksheets()
    {
        var repository = new InMemoryWorksheetRepository();
        await repository.AddAsync(new Worksheet { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Name = "A" }, Token);

        Assert.Empty(await repository.GetByProjectIdAsync(Guid.NewGuid(), Token));
    }

    [Fact]
    public async Task UpdateReplacesTheStoredWorksheetAndKeepsItsPosition()
    {
        var repository = new InMemoryWorksheetRepository();
        var projectId = Guid.NewGuid();
        var first = new Worksheet { Id = Guid.NewGuid(), ProjectId = projectId, Name = "Sheet1" };
        var second = new Worksheet { Id = Guid.NewGuid(), ProjectId = projectId, Name = "Sheet2" };
        await repository.AddAsync(first, Token);
        await repository.AddAsync(second, Token);
        var renamed = new Worksheet { Id = first.Id, ProjectId = projectId, Name = "WAT_Lot_A" };

        await repository.UpdateAsync(renamed, Token);

        Assert.Same(renamed, await repository.GetByIdAsync(first.Id, Token));
        Assert.Equal([renamed, second], await repository.GetByProjectIdAsync(projectId, Token));
        Assert.Equal("Sheet1", first.Name);
    }

    [Fact]
    public async Task ReAddingAnIdKeepsItsCreationPosition()
    {
        var repository = new InMemoryWorksheetRepository();
        var projectId = Guid.NewGuid();
        var first = new Worksheet { Id = Guid.NewGuid(), ProjectId = projectId, Name = "Sheet1" };
        var second = new Worksheet { Id = Guid.NewGuid(), ProjectId = projectId, Name = "Sheet2" };
        await repository.AddAsync(first, Token);
        await repository.AddAsync(second, Token);
        var replacement = new Worksheet { Id = first.Id, ProjectId = projectId, Name = "Replacement" };

        await repository.AddAsync(replacement, Token);

        Assert.Equal([replacement, second], await repository.GetByProjectIdAsync(projectId, Token));
    }

    [Fact]
    public async Task UpdateOfAnUnknownWorksheetThrowsAndStoresNothing()
    {
        var repository = new InMemoryWorksheetRepository();
        var projectId = Guid.NewGuid();
        var unknown = new Worksheet { Id = Guid.NewGuid(), ProjectId = projectId, Name = "Sheet1" };

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(() => repository.UpdateAsync(unknown, Token));

        Assert.Equal(unknown.Id, exception.Id);
        Assert.Null(await repository.GetByIdAsync(unknown.Id, Token));
        Assert.Empty(await repository.GetByProjectIdAsync(projectId, Token));
    }
}
