using System.Reflection;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Persistence;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Ctrl+V paste through MainWindowViewModel → MainWindowSession → ProjectSession, with a fake clipboard and real DuckDB storage.
public class ClipboardPasteWorkflowTests
{
    private const string CanonicalText =
        "No\tBin\tSITE\tReg1\tReg2\n" +
        "1\t1\t1\t5\t0.132\n" +
        "2\t2\t2\t7\t0.157\n" +
        "3\t1\t3\t2\t0.122\n";

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime(string databasePath = ":memory:")
        {
            var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
            ProjectSession = compositionRoot.CreateProjectSession(databasePath);
            ViewModel = compositionRoot.CreateMainWindowViewModel(compositionRoot.CreateMainWindowSession(ProjectSession, Clipboard));
        }

        public FakeClipboardTextReader Clipboard { get; } = new();

        public ProjectSession ProjectSession { get; }

        public MainWindowViewModel ViewModel { get; }

        public Worksheet Worksheet => ViewModel.SelectedWorksheet!;

        public IReadOnlyList<WorksheetColumn> VisibleColumns => ViewModel.SelectedWorksheetColumns!;

        public async Task<Worksheet> CreateWorksheetAsync()
        {
            ViewModel.ProjectName = "ALS_2026_09";
            await ViewModel.CreateProjectCommand.ExecuteAsync(null);
            ViewModel.WorksheetName = "WAT_Lot_A";
            await ViewModel.CreateWorksheetCommand.ExecuteAsync(null);
            return Assert.Single(ViewModel.Worksheets);
        }

        public async Task PasteAsync(string? text)
        {
            Clipboard.Text = text;
            await ViewModel.PasteCommand.ExecuteAsync(null);
        }

        public Task<IReadOnlyList<WorksheetColumn>> StoredColumnsAsync() =>
            ProjectSession.WorksheetColumns.GetByWorksheetIdAsync(Worksheet.Id, Token);

        public void Dispose() => ProjectSession.Dispose();
    }

    private static string[] Names(IEnumerable<WorksheetColumn> columns) => columns.Select(column => column.Name).ToArray();

    private static int[] Indexes(IEnumerable<WorksheetColumn> columns) => columns.Select(column => column.Index).ToArray();

    // 1
    [Fact]
    public async Task PasteRoutesThroughMainWindowSessionIntoTheProjectSession()
    {
        using var runtime = new Runtime();
        await runtime.CreateWorksheetAsync();

        await runtime.PasteAsync(CanonicalText);

        Assert.Equal(1, runtime.Clipboard.ReadCount);
        var stored = await runtime.StoredColumnsAsync();
        Assert.Equal(["No", "Bin", "SITE", "Reg1", "Reg2"], Names(stored));

        var block = await runtime.ProjectSession.RawDataStore.ReadColumnsAsync(
            runtime.Worksheet.Id, [stored[2].Id, stored[4].Id], 0, int.MaxValue, Token);
        Assert.Equal([1, 2, 3], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal([0.132, 0.157, 0.122], Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values);
        Assert.Null(runtime.ViewModel.ErrorMessage);
        Assert.False(runtime.ViewModel.IsBusy);
        Assert.False(runtime.ViewModel.IsPasteInProgress);
    }

    // 2
    [Fact]
    public async Task SelectedColumnIndexIsTheStartColumn()
    {
        using var runtime = new Runtime();
        await runtime.CreateWorksheetAsync();
        await runtime.PasteAsync("No\tBin\tSITE\n1\t1\t1\n");
        var before = runtime.VisibleColumns.ToArray();

        runtime.ViewModel.SelectedColumn = before[1];
        await runtime.PasteAsync("Reg1\tReg2\n5\t0.132\n");

        Assert.Equal(["No", "Reg1", "Reg2"], Names(runtime.VisibleColumns));
        Assert.Equal([0, 1, 2], Indexes(runtime.VisibleColumns));
        Assert.Equal(before.Select(column => column.Id), runtime.VisibleColumns.Select(column => column.Id));
        Assert.Equal(before[1].Id, runtime.ViewModel.SelectedColumn?.Id);
    }

    // 3
    [Fact]
    public async Task WithoutSelectionPasteAppendsAfterTheHighestExistingIndex()
    {
        using var runtime = new Runtime();
        await runtime.CreateWorksheetAsync();
        await runtime.PasteAsync("No\tBin\n1\t1\n");

        // A column created outside the UI with a gap: the next free position is max Index + 1, not the count.
        await runtime.ProjectSession.WorksheetColumns.AddAsync(new WorksheetColumn
        {
            Id = Guid.NewGuid(),
            WorksheetId = runtime.Worksheet.Id,
            Index = 4,
            Name = "Temp",
            DataType = WorksheetDataType.Numeric
        }, Token);
        Assert.Null(runtime.ViewModel.SelectedColumn);

        await runtime.PasteAsync("Reg1\n5\n");

        Assert.Equal(["No", "Bin", "Temp", "Reg1"], Names(runtime.VisibleColumns));
        Assert.Equal([0, 1, 4, 5], Indexes(runtime.VisibleColumns));
    }

    // 4
    [Fact]
    public async Task EmptyWorksheetWithoutSelectionStartsAtZero()
    {
        using var runtime = new Runtime();
        await runtime.CreateWorksheetAsync();

        await runtime.PasteAsync("SITE\tReg1\n1\t5\n");

        Assert.Equal([0, 1], Indexes(runtime.VisibleColumns));
    }

    // 5
    [Fact]
    public async Task SuccessfulPasteRefreshesColumnsFromTheRepository()
    {
        using var runtime = new Runtime();
        await runtime.CreateWorksheetAsync();
        var visible = runtime.ViewModel.SelectedWorksheetColumns;

        await runtime.PasteAsync("SITE\tReg1\n1\t5\n");

        var stored = await runtime.StoredColumnsAsync();
        Assert.Same(visible, runtime.ViewModel.SelectedWorksheetColumns);
        Assert.Equal(stored.Count, runtime.VisibleColumns.Count);
        Assert.All(stored.Zip(runtime.VisibleColumns), pair => Assert.Same(pair.First, pair.Second));
        Assert.Equal("1 row · 2 columns", runtime.ViewModel.SelectedWorksheetSummary);
    }

    // 6
    [Fact]
    public async Task UiSeesRenamedAndNewColumnsAfterPaste()
    {
        using var runtime = new Runtime();
        await runtime.CreateWorksheetAsync();
        await runtime.PasteAsync("SITE\tReg1\n1\t5\n");
        var site = runtime.VisibleColumns[0];

        await runtime.PasteAsync("SITE\t  \n2\t3\n");
        Assert.Equal(["SITE", "Reg1", "SITE_1", "Column4"], Names(runtime.VisibleColumns));

        runtime.ViewModel.SelectedColumn = runtime.VisibleColumns[0];
        await runtime.PasteAsync("Lot\nN123\n");

        var replaced = runtime.VisibleColumns[0];
        Assert.Equal(site.Id, replaced.Id);
        Assert.Equal("Lot", replaced.Name);
        Assert.Equal(WorksheetDataType.String, replaced.DataType);
        Assert.Equal(["Lot", "Reg1", "SITE_1", "Column4"], Names(runtime.VisibleColumns));
    }

    // 7
    [Fact]
    public void MainWindowViewModelExposesNoRawDataTypes()
    {
        string[] forbiddenNamespaces =
        [
            "YAT.Application.Ingestion",
            "YAT.Application.Abstractions.Persistence",
            "YAT.Application.Queries",
            "YAT.app.Clipboard"
        ];
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static IEnumerable<Type> WithGenericArguments(Type type) =>
            type.IsGenericType ? [type, .. type.GetGenericArguments().SelectMany(WithGenericArguments)] : [type];

        var types = typeof(MainWindowViewModel).GetProperties(members).Select(property => property.PropertyType)
            .Concat(typeof(MainWindowViewModel).GetFields(members).Select(field => field.FieldType))
            .SelectMany(WithGenericArguments)
            .ToArray();

        Assert.DoesNotContain(types, type => forbiddenNamespaces.Contains(type.Namespace));
        Assert.DoesNotContain(types, type => type.IsArray || type == typeof(double?) || type == typeof(IReadOnlyList<string>));
    }

    // 8
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n")]
    public async Task EmptyClipboardIsANonDestructiveNoOp(string? clipboardText)
    {
        using var runtime = new Runtime();
        await runtime.CreateWorksheetAsync();
        await runtime.PasteAsync("SITE\n1\n");
        var before = runtime.VisibleColumns.ToArray();

        await runtime.PasteAsync(clipboardText);

        Assert.Equal(2, runtime.Clipboard.ReadCount);
        Assert.Equal(before, runtime.VisibleColumns);
        Assert.Equal(before, await runtime.StoredColumnsAsync());
        Assert.Null(runtime.ViewModel.ErrorMessage);
        Assert.False(runtime.ViewModel.IsBusy);
    }

    // 9
    [Fact]
    public async Task ValidationFailureShowsMessageAndPersistsNothing()
    {
        using var runtime = new Runtime();
        await runtime.CreateWorksheetAsync();

        await runtime.PasteAsync("SITE\tReg1\n1\n");

        Assert.Equal("Line 2 has 1 cell, but the header has 2 columns.", runtime.ViewModel.ErrorMessage);
        Assert.Empty(runtime.VisibleColumns);
        Assert.Empty(await runtime.StoredColumnsAsync());
        Assert.False(runtime.ViewModel.IsBusy);
        Assert.False(runtime.ViewModel.IsPasteInProgress);
    }

    [Fact]
    public async Task StorageFailureShowsGenericMessageWithoutInternalDetails()
    {
        using var directory = new TemporaryDirectory();
        // A directory cannot be opened as a DuckDB database file, so the raw write fails inside the storage layer.
        using var runtime = new Runtime(directory.DirectoryPath);
        await runtime.CreateWorksheetAsync();

        await runtime.PasteAsync("SITE\tReg1\n1\t5\n");

        Assert.Equal("The pasted data could not be stored.", runtime.ViewModel.ErrorMessage);
        Assert.Empty(runtime.VisibleColumns);
        Assert.Empty(await runtime.StoredColumnsAsync());
        Assert.False(runtime.ViewModel.IsBusy);
        Assert.False(runtime.ViewModel.IsPasteInProgress);
    }

    // 10
    [Fact]
    public async Task CancellationIsNotShownAsAnErrorAndLeavesStateUnchanged()
    {
        using var runtime = new Runtime();
        await runtime.CreateWorksheetAsync();
        await runtime.PasteAsync("SITE\n1\n");
        await runtime.PasteAsync("A\tB\n1\n");
        var previousError = runtime.ViewModel.ErrorMessage;
        Assert.NotNull(previousError);
        var before = runtime.VisibleColumns.ToArray();

        runtime.Clipboard.Gate = new TaskCompletionSource().Task;
        runtime.Clipboard.Text = "Reg1\n5\n";
        var paste = runtime.ViewModel.PasteCommand.ExecuteAsync(null);
        Assert.True(runtime.ViewModel.IsPasteInProgress);

        runtime.ViewModel.PasteCommand.Cancel();
        await paste;

        Assert.Equal(previousError, runtime.ViewModel.ErrorMessage);
        Assert.Equal(before, runtime.VisibleColumns);
        Assert.Equal(before, await runtime.StoredColumnsAsync());
        Assert.False(runtime.ViewModel.IsBusy);
        Assert.False(runtime.ViewModel.IsPasteInProgress);
    }

    // 11
    [Fact]
    public async Task SecondPasteWhileOneIsRunningIsPrevented()
    {
        using var runtime = new Runtime();
        await runtime.CreateWorksheetAsync();
        var release = new TaskCompletionSource();
        runtime.Clipboard.Gate = release.Task;
        runtime.Clipboard.Text = "SITE\n1\n";

        var first = runtime.ViewModel.PasteCommand.ExecuteAsync(null);

        Assert.True(runtime.ViewModel.IsPasteInProgress);
        Assert.False(runtime.ViewModel.PasteCommand.CanExecute(null));
        Assert.False(runtime.ViewModel.CreateWorksheetCommand.CanExecute(null));
        Assert.False(runtime.ViewModel.AddColumnCommand.CanExecute(null));

        // The window only executes the command when it can execute, so a second Ctrl+V is dropped, not queued.
        if (runtime.ViewModel.PasteCommand.CanExecute(null))
        {
            runtime.ViewModel.PasteCommand.Execute(null);
        }

        release.SetResult();
        await first;

        Assert.Equal(1, runtime.Clipboard.ReadCount);
        Assert.Equal(["SITE"], Names(runtime.VisibleColumns));
        Assert.True(runtime.ViewModel.PasteCommand.CanExecute(null));
    }

    [Fact]
    public async Task PasteIsUnavailableWithoutSelectedWorksheet()
    {
        using var runtime = new Runtime();
        runtime.Clipboard.Text = "SITE\n1\n";

        Assert.False(runtime.ViewModel.PasteCommand.CanExecute(null));
        await runtime.ViewModel.PasteCommand.ExecuteAsync(null);

        Assert.Equal(0, runtime.Clipboard.ReadCount);
        Assert.Null(runtime.ViewModel.ErrorMessage);
    }

    [Fact]
    public async Task ChangingWorksheetClearsSelectedColumn()
    {
        using var runtime = new Runtime();
        var worksheet = await runtime.CreateWorksheetAsync();
        await runtime.PasteAsync("SITE\n1\n");
        runtime.ViewModel.SelectedColumn = runtime.VisibleColumns[0];

        runtime.ViewModel.SelectedWorksheet = null;
        Assert.Null(runtime.ViewModel.SelectedColumn);

        runtime.ViewModel.SelectedWorksheet = worksheet;
        Assert.Null(runtime.ViewModel.SelectedColumn);
    }
}
