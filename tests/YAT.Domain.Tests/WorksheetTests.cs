using YAT.Domain.Entities;

namespace YAT.Domain.Tests;

public class WorksheetTests
{
    [Fact]
    public void AssignedValuesArePreserved()
    {
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var worksheet = new Worksheet
        {
            Id = id,
            ProjectId = projectId,
            Name = "WAT_Lot_A",
            RowCount = 1_500_000,
            ColumnCount = 42,
            CreatedAt = created,
            UpdatedAt = created
        };

        Assert.Equal(id, worksheet.Id);
        Assert.Equal(projectId, worksheet.ProjectId);
        Assert.Equal("WAT_Lot_A", worksheet.Name);
        Assert.Equal(1_500_000, worksheet.RowCount);
        Assert.Equal(42, worksheet.ColumnCount);
        Assert.Equal(created, worksheet.CreatedAt);
    }

    [Fact]
    public void RowCountHoldsValuesBeyondInt32Range()
    {
        var beyondInt32 = (long)int.MaxValue + 1;

        var worksheet = new Worksheet { RowCount = beyondInt32 };

        Assert.Equal(beyondInt32, worksheet.RowCount);
        Assert.True(worksheet.RowCount > int.MaxValue);
    }

    [Fact]
    public void ProjectIdIsIndependentOfWorksheetId()
    {
        var worksheet = new Worksheet
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid()
        };

        Assert.NotEqual(worksheet.Id, worksheet.ProjectId);
    }
}
