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
}
