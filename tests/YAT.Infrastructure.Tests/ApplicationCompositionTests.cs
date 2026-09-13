using YAT.Application.Exceptions;
using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.InMemory;
using YAT.Infrastructure.Tests.TestDoubles;

namespace YAT.Infrastructure.Tests;

public class ApplicationCompositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Composition
    {
        private readonly InMemoryProjectRepository _projects = new();
        private readonly InMemoryWorksheetRepository _worksheets = new();
        private readonly InMemoryWorksheetColumnRepository _worksheetColumns = new();
        private readonly TimeProvider _timeProvider = new FixedTimeProvider(Now);

        public CreateProjectHandler CreateProject => new(_projects, _timeProvider);

        public CreateWorksheetHandler CreateWorksheet => new(_projects, _worksheets, _timeProvider);

        public AddWorksheetColumnHandler AddWorksheetColumn => new(_worksheets, _worksheetColumns);
    }

    [Fact]
    public async Task WorksheetIsCreatedAgainstAProjectStoredByAnotherHandler()
    {
        var composition = new Composition();

        var project = await composition.CreateProject.HandleAsync(
            new CreateProjectCommand("Yield Review", null), Token);

        var worksheet = await composition.CreateWorksheet.HandleAsync(
            new CreateWorksheetCommand(project.Id, "WAT_Lot_A"), Token);

        Assert.Equal(project.Id, worksheet.ProjectId);
    }

    [Fact]
    public async Task ColumnIsAddedToAWorksheetStoredByAnotherHandler()
    {
        var composition = new Composition();

        var project = await composition.CreateProject.HandleAsync(
            new CreateProjectCommand("Yield Review", null), Token);

        var worksheet = await composition.CreateWorksheet.HandleAsync(
            new CreateWorksheetCommand(project.Id, "WAT_Lot_A"), Token);

        var column = await composition.AddWorksheetColumn.HandleAsync(
            new AddWorksheetColumnCommand(
                worksheet.Id, 0, "Vth", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter, "mV"),
            Token);

        Assert.Equal(worksheet.Id, column.WorksheetId);
    }

    [Fact]
    public async Task UnknownProjectIsRejectedThroughTheRealRepository()
    {
        var composition = new Composition();

        await Assert.ThrowsAsync<EntityNotFoundException>(
            () => composition.CreateWorksheet.HandleAsync(
                new CreateWorksheetCommand(Guid.NewGuid(), "WAT_Lot_A"), Token));
    }
}
