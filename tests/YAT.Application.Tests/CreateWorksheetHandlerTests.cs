using YAT.Application.Exceptions;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;

namespace YAT.Application.Tests;

public class CreateWorksheetHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 2, 8, 15, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static CreateWorksheetHandler CreateHandler(
        FakeProjectRepository projects,
        FakeWorksheetRepository worksheets)
        => new(projects, worksheets, new FixedTimeProvider(Now));

    private static Project SeedProject(FakeProjectRepository projects)
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "Yield Review" };
        projects.Seed(project);
        return project;
    }

    [Fact]
    public async Task CreatesWorksheetLinkedToTheProject()
    {
        var projects = new FakeProjectRepository();
        var worksheets = new FakeWorksheetRepository();
        var project = SeedProject(projects);
        var handler = CreateHandler(projects, worksheets);

        var worksheet = await handler.HandleAsync(new CreateWorksheetCommand(project.Id, "WAT_Lot_A"), Token);

        Assert.NotEqual(Guid.Empty, worksheet.Id);
        Assert.Equal(project.Id, worksheet.ProjectId);
        Assert.Equal("WAT_Lot_A", worksheet.Name);
    }

    [Fact]
    public async Task TrimsName()
    {
        var projects = new FakeProjectRepository();
        var worksheets = new FakeWorksheetRepository();
        var project = SeedProject(projects);
        var handler = CreateHandler(projects, worksheets);

        var worksheet = await handler.HandleAsync(new CreateWorksheetCommand(project.Id, "  Lot A  "), Token);

        Assert.Equal("Lot A", worksheet.Name);
    }

    [Fact]
    public async Task StartsWithEmptyCounts()
    {
        var projects = new FakeProjectRepository();
        var worksheets = new FakeWorksheetRepository();
        var project = SeedProject(projects);
        var handler = CreateHandler(projects, worksheets);

        var worksheet = await handler.HandleAsync(new CreateWorksheetCommand(project.Id, "WAT_Lot_A"), Token);

        Assert.Equal(0, worksheet.RowCount);
        Assert.Equal(0, worksheet.ColumnCount);
    }

    [Fact]
    public async Task StampsCreatedAtAndUpdatedAtWithTheSameTime()
    {
        var projects = new FakeProjectRepository();
        var worksheets = new FakeWorksheetRepository();
        var project = SeedProject(projects);
        var handler = CreateHandler(projects, worksheets);

        var worksheet = await handler.HandleAsync(new CreateWorksheetCommand(project.Id, "WAT_Lot_A"), Token);

        Assert.Equal(Now, worksheet.CreatedAt);
        Assert.Equal(worksheet.CreatedAt, worksheet.UpdatedAt);
    }

    [Fact]
    public async Task PersistsTheCreatedWorksheet()
    {
        var projects = new FakeProjectRepository();
        var worksheets = new FakeWorksheetRepository();
        var project = SeedProject(projects);
        var handler = CreateHandler(projects, worksheets);

        var worksheet = await handler.HandleAsync(new CreateWorksheetCommand(project.Id, "WAT_Lot_A"), Token);

        Assert.Same(worksheet, Assert.Single(worksheets.Added));
    }

    [Fact]
    public async Task RejectsUnknownProject()
    {
        var projects = new FakeProjectRepository();
        var worksheets = new FakeWorksheetRepository();
        var handler = CreateHandler(projects, worksheets);
        var missingProjectId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => handler.HandleAsync(new CreateWorksheetCommand(missingProjectId, "WAT_Lot_A"), Token));

        Assert.Equal(missingProjectId, exception.Id);
        Assert.Empty(worksheets.Added);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsBlankName(string? name)
    {
        var projects = new FakeProjectRepository();
        var worksheets = new FakeWorksheetRepository();
        var project = SeedProject(projects);
        var handler = CreateHandler(projects, worksheets);

        await Assert.ThrowsAsync<ValidationException>(
            () => handler.HandleAsync(new CreateWorksheetCommand(project.Id, name!), Token));

        Assert.Empty(worksheets.Added);
    }
}
