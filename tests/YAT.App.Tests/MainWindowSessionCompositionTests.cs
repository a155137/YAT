using System.Reflection;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Ingestion;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.Domain.Entities;

namespace YAT.App.Tests;

// The UI reaches worksheet and column metadata only through a MainWindowSession wired over a ProjectSession.
public class MainWindowSessionCompositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
            ProjectSession = compositionRoot.CreateProjectSession(":memory:");
            ViewModel = compositionRoot.CreateMainWindowViewModel(
                compositionRoot.CreateMainWindowSession(ProjectSession, new FakeClipboard(), new FakeClipboard()));
        }

        public ProjectSession ProjectSession { get; }

        public MainWindowViewModel ViewModel { get; }

        public async Task<Worksheet> CreateWorksheetAsync()
        {
            ViewModel.ProjectName = "ALS_2026_09";
            await ViewModel.CreateProjectCommand.ExecuteAsync(null);
            ViewModel.WorksheetName = "WAT_Lot_A";
            await ViewModel.CreateWorksheetCommand.ExecuteAsync(null);
            return Assert.Single(ViewModel.Worksheets);
        }

        public async Task<WorksheetColumn> AddColumnAsync(string name)
        {
            ViewModel.ColumnName = name;
            await ViewModel.AddColumnCommand.ExecuteAsync(null);
            Assert.Null(ViewModel.ErrorMessage);
            return ViewModel.SelectedWorksheetColumns!.Last();
        }

        public void Dispose() => ProjectSession.Dispose();
    }

    private static IEnumerable<Type> WithGenericArguments(Type type) =>
        type.IsGenericType ? [type, .. type.GetGenericArguments().SelectMany(WithGenericArguments)] : [type];

    // 1
    [Fact]
    public async Task UiWorksheetOperationsUseTheProjectSessionWorksheetRepository()
    {
        using var runtime = new Runtime();

        var worksheet = await runtime.CreateWorksheetAsync();

        Assert.Same(worksheet, await runtime.ProjectSession.Worksheets.GetByIdAsync(worksheet.Id, Token));
    }

    // 2
    [Fact]
    public async Task UiColumnOperationsUseTheProjectSessionColumnRepository()
    {
        using var runtime = new Runtime();
        var worksheet = await runtime.CreateWorksheetAsync();

        var vth = await runtime.AddColumnAsync("Vth");
        var idsat = await runtime.AddColumnAsync("Idsat");

        var stored = await runtime.ProjectSession.WorksheetColumns.GetByWorksheetIdAsync(worksheet.Id, Token);
        Assert.Equal([vth, idsat], stored);
    }

    [Fact]
    public async Task ProjectSessionPasteSeesMetadataCreatedThroughTheUi()
    {
        using var runtime = new Runtime();
        var worksheet = await runtime.CreateWorksheetAsync();
        var vth = await runtime.AddColumnAsync("Vth");

        var data = new TabularTextParser().Parse("SITE\tReg1\n1\t5\n2\t7\n");
        var existing = await runtime.ProjectSession.WorksheetColumns.GetByWorksheetIdAsync(worksheet.Id, Token);
        var plan = new WorksheetPastePlanner(new ColumnDataTypeDetector()).Plan(worksheet.Id, existing, 0, data);
        var result = await runtime.ProjectSession.PasteExecution.ExecuteAsync(plan, data, Token);

        Assert.Equal(vth.Id, result.Columns[0].ColumnId);
        Assert.False(result.Columns[0].IsNew);
        Assert.Equal(
            ["SITE", "Reg1"],
            (await runtime.ProjectSession.WorksheetColumns.GetByWorksheetIdAsync(worksheet.Id, Token)).Select(column => column.Name));
    }

    // 3
    [Fact]
    public void CompositionRootHoldsNoWorksheetOrColumnRepositoriesOfItsOwn()
    {
        Type[] projectScoped = [typeof(IWorksheetRepository), typeof(IWorksheetColumnRepository), typeof(IWorksheetRawDataStore), typeof(PasteExecutionService)];

        var fieldTypes = typeof(CompositionRoot).GetFields(InstanceFields).Select(field => field.FieldType).ToArray();

        Assert.DoesNotContain(fieldTypes, fieldType => projectScoped.Any(scoped => scoped.IsAssignableFrom(fieldType)));
        Assert.Empty(typeof(CompositionRoot).GetProperties());
    }

    [Fact]
    public async Task UiSessionsOverDifferentProjectSessionsDoNotShareWorksheets()
    {
        using var first = new Runtime();
        using var second = new Runtime();

        var worksheet = await first.CreateWorksheetAsync();

        Assert.Null(await second.ProjectSession.Worksheets.GetByIdAsync(worksheet.Id, Token));
    }

    // 4
    [Fact]
    public void MainWindowViewModelDependsOnlyOnMainWindowSession()
    {
        Assert.Equal(
            [typeof(MainWindowSession)],
            Assert.Single(typeof(MainWindowViewModel).GetConstructors()).GetParameters().Select(parameter => parameter.ParameterType));

        string[] forbiddenNamespacePrefixes =
        [
            "YAT.Application.Abstractions",
            "YAT.Application.Features",
            "YAT.Application.Ingestion",
            "YAT.Infrastructure",
            "YAT.app.Clipboard",
            "Avalonia",
            "DuckDB"
        ];
        Type[] forbiddenTypes = [typeof(CompositionRoot), typeof(ProjectSession)];

        var referencedTypes = typeof(MainWindowViewModel).GetFields(InstanceFields)
            .SelectMany(field => WithGenericArguments(field.FieldType))
            .ToArray();

        Assert.DoesNotContain(referencedTypes, type => forbiddenTypes.Contains(type));
        Assert.DoesNotContain(
            referencedTypes,
            type => forbiddenNamespacePrefixes.Any(prefix => (type.Namespace ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal)));
    }

    // 5
    [Fact]
    public void MainWindowSessionExposesOnlyApplicationCommandsAndDomainMetadata()
    {
        string[] allowedNamespaces =
        [
            "System",
            "System.Collections.Generic",
            "System.Threading",
            "System.Threading.Tasks",
            "YAT.Application.Features.Projects.CreateProject",
            "YAT.Application.Features.Projects.RenameProject",
            "YAT.Application.Features.Worksheets.CreateWorksheet",
            "YAT.Application.Features.Worksheets.RenameWorksheet",
            "YAT.Application.Features.Worksheets.AddWorksheetColumn",
            "YAT.app.Composition",
            "YAT.Domain.Entities"
        ];

        var exposedTypes = typeof(MainWindowSession)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
            .SelectMany(WithGenericArguments)
            .ToArray();

        Assert.Empty(exposedTypes.Select(type => type.Namespace).Distinct().Except(allowedNamespaces));
        Assert.DoesNotContain(exposedTypes, type => type == typeof(ProjectSession) || type == typeof(CompositionRoot));
        Assert.Empty(typeof(MainWindowSession).GetProperties());
        Assert.Empty(typeof(MainWindowSession).GetConstructors());

        // Paste orchestration privately uses the session's column repository and paste execution (Task #015),
        // but never holds the ProjectSession itself, the raw data store or the worksheet repository.
        Assert.DoesNotContain(
            typeof(MainWindowSession).GetFields(InstanceFields),
            field => typeof(ProjectSession).IsAssignableFrom(field.FieldType)
                || typeof(IWorksheetRawDataStore).IsAssignableFrom(field.FieldType)
                || typeof(IWorksheetRepository).IsAssignableFrom(field.FieldType));
    }

    [Fact]
    public void ClipboardPasteResultCarriesOnlyMetadata()
    {
        Assert.Equal(
            [("IsPasted", typeof(bool)), ("WorksheetColumns", typeof(IReadOnlyList<WorksheetColumn>))],
            typeof(ClipboardPasteResult).GetProperties()
                .Select(property => (property.Name, property.PropertyType))
                .OrderBy(property => property.Name, StringComparer.Ordinal));
        Assert.Empty(typeof(ClipboardPasteResult).GetConstructors());
    }

    // 7
    [Fact]
    public async Task ProjectSessionStaysOwnedByTheCallerAndDisposesAfterUiUse()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(MainWindowSession)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(MainWindowViewModel)));

        var runtime = new Runtime();
        var worksheet = await runtime.CreateWorksheetAsync();
        var column = await runtime.AddColumnAsync("Vth");
        await runtime.ProjectSession.RawDataStore.WriteColumnsAsync(
            worksheet.Id, new RawDataBlock([new NumericRawDataColumn(column.Id, [0.45])]), Token);

        runtime.Dispose();
        runtime.Dispose();

        // Disposal released the session's raw store; the UI never owned or disposed it.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.ProjectSession.RawDataStore.ReadColumnsAsync(
            worksheet.Id, [column.Id], 0, 1, Token));
    }

    [Fact]
    public void CreatingMainWindowSessionRequiresProjectSessionAndClipboard()
    {
        var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
        using var projectSession = compositionRoot.CreateProjectSession(":memory:");

        Assert.Throws<ArgumentNullException>(() => compositionRoot.CreateMainWindowSession(null!, new FakeClipboard(), new FakeClipboard()));
        Assert.Throws<ArgumentNullException>(() => compositionRoot.CreateMainWindowSession(projectSession, null!, new FakeClipboard()));
        Assert.Throws<ArgumentNullException>(() => compositionRoot.CreateMainWindowSession(projectSession, new FakeClipboard(), null!));
    }
}
