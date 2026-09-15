using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

public class InMemoryProjectRepositoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReturnsTheStoredInstance()
    {
        var repository = new InMemoryProjectRepository();
        var project = new Project { Id = Guid.NewGuid(), Name = "Yield Review" };

        await repository.AddAsync(project, Token);
        var found = await repository.GetByIdAsync(project.Id, Token);

        Assert.Same(project, found);
    }

    [Fact]
    public async Task ReturnsNullForUnknownId()
    {
        var repository = new InMemoryProjectRepository();

        var found = await repository.GetByIdAsync(Guid.NewGuid(), Token);

        Assert.Null(found);
    }

    [Fact]
    public async Task OverwritesEntryWithTheSameId()
    {
        var repository = new InMemoryProjectRepository();
        var id = Guid.NewGuid();
        var original = new Project { Id = id, Name = "Original" };
        var replacement = new Project { Id = id, Name = "Replacement" };

        await repository.AddAsync(original, Token);
        await repository.AddAsync(replacement, Token);
        var found = await repository.GetByIdAsync(id, Token);

        Assert.Same(replacement, found);
    }

    [Fact]
    public async Task KeepsProjectsWithDistinctIdsSeparate()
    {
        var repository = new InMemoryProjectRepository();
        var first = new Project { Id = Guid.NewGuid(), Name = "First" };
        var second = new Project { Id = Guid.NewGuid(), Name = "Second" };

        await repository.AddAsync(first, Token);
        await repository.AddAsync(second, Token);

        Assert.Same(first, await repository.GetByIdAsync(first.Id, Token));
        Assert.Same(second, await repository.GetByIdAsync(second.Id, Token));
    }

    [Fact]
    public async Task UpdateReplacesTheStoredProject()
    {
        var repository = new InMemoryProjectRepository();
        var project = new Project { Id = Guid.NewGuid(), Name = "Untitled Project" };
        await repository.AddAsync(project, Token);
        var renamed = new Project { Id = project.Id, Name = "ALS_2026_09" };

        await repository.UpdateAsync(renamed, Token);

        Assert.Same(renamed, await repository.GetByIdAsync(project.Id, Token));
        Assert.Equal("Untitled Project", project.Name);
    }

    [Fact]
    public async Task UpdateOfAnUnknownProjectThrowsAndStoresNothing()
    {
        var repository = new InMemoryProjectRepository();
        var unknown = new Project { Id = Guid.NewGuid(), Name = "ALS_2026_09" };

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(() => repository.UpdateAsync(unknown, Token));

        Assert.Equal(unknown.Id, exception.Id);
        Assert.Null(await repository.GetByIdAsync(unknown.Id, Token));
    }
}
