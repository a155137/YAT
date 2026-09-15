using YAT.Application.Exceptions;
using YAT.Application.Features.Projects.RenameProject;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;

namespace YAT.Application.Tests;

public class RenameProjectHandlerTests
{
    private static readonly DateTimeOffset Created = new(2026, 4, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = new(2026, 4, 3, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RenameProjectHandler CreateHandler(FakeProjectRepository projects)
        => new(projects, new FixedTimeProvider(Now));

    private static Project SeedProject(FakeProjectRepository projects)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Untitled Project",
            Description = "CP yield review",
            CreatedAt = Created,
            UpdatedAt = Created
        };
        projects.Seed(project);
        return project;
    }

    [Fact]
    public async Task RenamesTheProjectAndStoresIt()
    {
        var projects = new FakeProjectRepository();
        var project = SeedProject(projects);

        var renamed = await CreateHandler(projects).HandleAsync(new RenameProjectCommand(project.Id, "ALS_2026_09"), Token);

        Assert.Equal(project.Id, renamed.Id);
        Assert.Equal("ALS_2026_09", renamed.Name);
        Assert.Same(renamed, Assert.Single(projects.Updated));
        Assert.Same(renamed, await projects.GetByIdAsync(project.Id, Token));
    }

    [Fact]
    public async Task StoresARenamedCopyWithoutModifyingTheLoadedInstance()
    {
        var projects = new FakeProjectRepository();
        var project = SeedProject(projects);

        var renamed = await CreateHandler(projects).HandleAsync(new RenameProjectCommand(project.Id, "ALS_2026_09"), Token);

        Assert.NotSame(project, renamed);
        Assert.Equal("Untitled Project", project.Name);
        Assert.Equal(Created, project.UpdatedAt);
    }

    [Fact]
    public async Task KeepsOtherFieldsAndStampsUpdatedAt()
    {
        var projects = new FakeProjectRepository();
        var project = SeedProject(projects);

        var renamed = await CreateHandler(projects).HandleAsync(new RenameProjectCommand(project.Id, "ALS_2026_09"), Token);

        Assert.Equal("CP yield review", renamed.Description);
        Assert.Equal(Created, renamed.CreatedAt);
        Assert.Equal(Now, renamed.UpdatedAt);
    }

    [Fact]
    public async Task TrimsName()
    {
        var projects = new FakeProjectRepository();
        var project = SeedProject(projects);

        var renamed = await CreateHandler(projects).HandleAsync(new RenameProjectCommand(project.Id, "  ALS_2026_09  "), Token);

        Assert.Equal("ALS_2026_09", renamed.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsBlankName(string? name)
    {
        var projects = new FakeProjectRepository();
        var project = SeedProject(projects);

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateHandler(projects).HandleAsync(new RenameProjectCommand(project.Id, name!), Token));

        Assert.Empty(projects.Updated);
        Assert.Equal("Untitled Project", (await projects.GetByIdAsync(project.Id, Token))!.Name);
    }

    [Fact]
    public async Task RejectsUnknownProject()
    {
        var projects = new FakeProjectRepository();
        var missingProjectId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => CreateHandler(projects).HandleAsync(new RenameProjectCommand(missingProjectId, "ALS_2026_09"), Token));

        Assert.Equal(missingProjectId, exception.Id);
        Assert.Empty(projects.Updated);
    }
}
