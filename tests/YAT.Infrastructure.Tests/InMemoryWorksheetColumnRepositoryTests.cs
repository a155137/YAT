using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

public class InMemoryWorksheetColumnRepositoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WorksheetColumn Column(Guid id, int index) => new()
    {
        Id = id,
        WorksheetId = Guid.NewGuid(),
        Index = index,
        Name = "Vth",
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
}
