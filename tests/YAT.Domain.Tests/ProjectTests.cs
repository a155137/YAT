using YAT.Domain.Entities;

namespace YAT.Domain.Tests;

public class ProjectTests
{
    [Fact]
    public void AssignedValuesArePreserved()
    {
        var id = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.FromHours(8));
        var updated = created.AddHours(3);

        var project = new Project
        {
            Id = id,
            Name = "Q1 Yield Review",
            Description = "Cross-lot comparison",
            CreatedAt = created,
            UpdatedAt = updated
        };

        Assert.Equal(id, project.Id);
        Assert.Equal("Q1 Yield Review", project.Name);
        Assert.Equal("Cross-lot comparison", project.Description);
        Assert.Equal(created, project.CreatedAt);
        Assert.Equal(updated, project.UpdatedAt);
    }

    [Fact]
    public void DescriptionIsOptional()
    {
        var project = new Project { Name = "Untitled" };

        Assert.Null(project.Description);

        project.Description = "Added later";
        Assert.Equal("Added later", project.Description);

        project.Description = null;
        Assert.Null(project.Description);
    }
}
