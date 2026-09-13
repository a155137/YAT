using YAT.Application.Exceptions;
using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Tests.TestDoubles;

namespace YAT.Application.Tests;

public class CreateProjectHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static CreateProjectHandler CreateHandler(FakeProjectRepository projects)
        => new(projects, new FixedTimeProvider(Now));

    [Fact]
    public async Task CreatesProjectWithGeneratedId()
    {
        var projects = new FakeProjectRepository();
        var handler = CreateHandler(projects);

        var project = await handler.HandleAsync(new CreateProjectCommand("Yield Review", null), Token);

        Assert.NotEqual(Guid.Empty, project.Id);
    }

    [Fact]
    public async Task TrimsName()
    {
        var projects = new FakeProjectRepository();
        var handler = CreateHandler(projects);

        var project = await handler.HandleAsync(new CreateProjectCommand("  Yield Review  ", null), Token);

        Assert.Equal("Yield Review", project.Name);
    }

    [Fact]
    public async Task PreservesDescription()
    {
        var projects = new FakeProjectRepository();
        var handler = CreateHandler(projects);

        var described = await handler.HandleAsync(new CreateProjectCommand("A", "Cross-lot comparison"), Token);
        var undescribed = await handler.HandleAsync(new CreateProjectCommand("B", null), Token);

        Assert.Equal("Cross-lot comparison", described.Description);
        Assert.Null(undescribed.Description);
    }

    [Fact]
    public async Task StampsCreatedAtAndUpdatedAtWithTheSameTime()
    {
        var projects = new FakeProjectRepository();
        var handler = CreateHandler(projects);

        var project = await handler.HandleAsync(new CreateProjectCommand("Yield Review", null), Token);

        Assert.Equal(Now, project.CreatedAt);
        Assert.Equal(project.CreatedAt, project.UpdatedAt);
    }

    [Fact]
    public async Task PersistsTheCreatedProject()
    {
        var projects = new FakeProjectRepository();
        var handler = CreateHandler(projects);

        var project = await handler.HandleAsync(new CreateProjectCommand("Yield Review", null), Token);

        Assert.Same(project, Assert.Single(projects.Added));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsBlankName(string? name)
    {
        var projects = new FakeProjectRepository();
        var handler = CreateHandler(projects);

        await Assert.ThrowsAsync<ValidationException>(
            () => handler.HandleAsync(new CreateProjectCommand(name!, null), Token));

        Assert.Empty(projects.Added);
    }
}
