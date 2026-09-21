using DuckDB.NET.Data;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Persistence;
using YAT.app.Analyses;
using YAT.app.Composition;
using YAT.app.Graphs;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// The desktop project lifecycle (File menu and closing) with scripted dialogs over real project files.
public class ProjectLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime(TemporaryDirectory? directory = null)
        {
            _ownsDirectory = directory is null;
            Directory = directory ?? new TemporaryDirectory();
            TemporaryRoot = Directory.File("temp");
            Composition = new CompositionRoot(new FixedTimeProvider(Now), TemporaryRoot);
            Workspace = Composition.CreateProjectWorkspace();
            Lifecycle = Composition.CreateProjectLifecycle(Workspace, Clipboard, Clipboard, Dialogs);
            Graphs = Composition.CreateGraphSetup(GraphDialogs, GraphWindows);
            Statistics = Composition.CreateDescriptiveStatistics(new FakeAnalysisSetupDialogs(), new FakeAnalysisResultPresenter());
            Capability = Composition.CreateCapabilityAnalysis(new FakeCapabilityAnalysisSetupDialogs(), new FakeAnalysisResultPresenter());
            Shell = Composition.CreateMainWindowShellViewModel(Lifecycle, Graphs, Statistics, Capability);
        }

        private readonly bool _ownsDirectory;

        public TemporaryDirectory Directory { get; }

        public string TemporaryRoot { get; }

        public CompositionRoot Composition { get; }

        public ProjectWorkspace Workspace { get; }

        public FakeClipboard Clipboard { get; } = new();

        public FakeProjectLifecycleDialogs Dialogs { get; } = new();

        public FakeGraphSetupDialogs GraphDialogs { get; } = new();

        public FakeGraphWindowPresenter GraphWindows { get; } = new();

        public GraphSetupController Graphs { get; }

        public DescriptiveStatisticsController Statistics { get; }

        public CapabilityAnalysisController Capability { get; }

        public ProjectLifecycleController Lifecycle { get; }

        public MainWindowShellViewModel Shell { get; }

        public MainWindowViewModel Project => Lifecycle.Project!;

        public ProjectSession CurrentProjectSession => Workspace.CurrentSession!;

        public string File(string name) => Directory.File(name);

        public string[] TemporaryFolders() =>
            System.IO.Directory.Exists(TemporaryRoot) ? System.IO.Directory.GetDirectories(TemporaryRoot) : [];

        public async Task StartAsync()
        {
            Assert.True(await Shell.StartAsync());
            await Project.GridLoadTask;
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await Project.PasteCommand.ExecuteAsync(null);
            await Project.GridLoadTask;
        }

        public async Task RenameAsync(ProjectExplorerItem item, string name)
        {
            Project.ProjectExplorer.BeginRename(item);
            item.EditName = name;
            await Project.ProjectExplorer.CommitRenameAsync(item);
        }

        public ProjectExplorerWorksheetItem Item(string name) =>
            Assert.Single(Project.ProjectExplorer.WorksheetItems, item => item.Name == name);

        public async Task AwaitGridAsync() => await Project.GridLoadTask;

        public void Dispose()
        {
            Workspace.Dispose();
            if (_ownsDirectory)
            {
                Directory.Dispose();
            }
        }
    }

    // A project file created and closed by another application run.
    private static async Task<string> CreateProjectFileAsync(TemporaryDirectory directory, string fileName, string pastedText, string? projectName = null)
    {
        var path = directory.File(fileName);
        using var runtime = new Runtime(directory);
        await runtime.StartAsync();
        await runtime.PasteAsync(pastedText);
        if (projectName is not null)
        {
            await runtime.RenameAsync(runtime.Project.ProjectExplorer.ProjectItem!, projectName);
        }

        runtime.Dialogs.SavePaths.Enqueue(path);
        Assert.True(await runtime.Lifecycle.SaveAsAsync());
        Assert.True(await runtime.Lifecycle.CloseAsync());
        return path;
    }

    private static async Task<IReadOnlyList<string>> StoredColumnNamesAsync(ProjectSession session, Guid worksheetId) =>
        (await session.WorksheetColumns.GetByWorksheetIdAsync(worksheetId, Token)).Select(column => column.Name).ToArray();

    private static async Task<IReadOnlyList<string>> ColumnNamesInFileAsync(TemporaryDirectory directory, string path)
    {
        using var runtime = new Runtime(directory);
        runtime.Dialogs.OpenPaths.Enqueue(path);
        await runtime.StartAsync();
        Assert.True(await runtime.Lifecycle.OpenProjectAsync());
        await runtime.AwaitGridAsync();
        return runtime.Project.GridColumns.Select(column => column.Name).ToArray();
    }

    // ---- Startup and meaningful changes ----

    [Fact]
    public async Task StartupShowsAnUntouchedTemporaryProject()
    {
        using var runtime = new Runtime();

        await runtime.StartAsync();

        Assert.True(runtime.Lifecycle.IsTemporaryProject);
        Assert.False(runtime.Lifecycle.HasMeaningfulChanges);
        Assert.Null(runtime.Lifecycle.ProjectFilePath);
        Assert.Equal("Untitled Project", runtime.Project.CurrentProject!.Name);
        Assert.Equal("Sheet1", runtime.Project.SelectedWorksheet!.Name);
        Assert.Same(runtime.Project, runtime.Shell.Project);
        Assert.Equal("YAT — Untitled Project", runtime.Shell.WindowTitle);
        Assert.NotNull(runtime.Lifecycle.CurrentSession);
    }

    [Fact]
    public async Task SuccessfulPasteMarksTheTemporaryProjectChanged()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        await runtime.PasteAsync("Lot\tVth\nA\t0.41\n");

        Assert.True(runtime.Lifecycle.HasMeaningfulChanges);
    }

    [Fact]
    public async Task FailedOrEmptyPasteLeavesTheFlagUnchanged()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        await runtime.PasteAsync("Lot\tVth\nA\n");
        Assert.NotNull(runtime.Project.ErrorMessage);
        runtime.Clipboard.Text = "  ";
        await runtime.Project.PasteCommand.ExecuteAsync(null);

        Assert.False(runtime.Lifecycle.HasMeaningfulChanges);
    }

    [Fact]
    public async Task SuccessfulRenamesMarkChangedAndRejectedRenamesDoNot()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        await runtime.RenameAsync(runtime.Item("Sheet1"), "   ");
        Assert.False(runtime.Lifecycle.HasMeaningfulChanges);
        runtime.Project.ProjectExplorer.CancelRename(runtime.Item("Sheet1"));

        await runtime.RenameAsync(runtime.Item("Sheet1"), "Lot Data");
        Assert.True(runtime.Lifecycle.HasMeaningfulChanges);
    }

    [Fact]
    public async Task ProjectRenameMarksChangedAndUpdatesTheWindowTitle()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var titleChanges = 0;
        runtime.Shell.PropertyChanged += (_, e) => titleChanges += e.PropertyName == nameof(MainWindowShellViewModel.WindowTitle) ? 1 : 0;

        await runtime.RenameAsync(runtime.Project.ProjectExplorer.ProjectItem!, "ALS Characterization");

        Assert.True(runtime.Lifecycle.HasMeaningfulChanges);
        Assert.Equal("YAT — ALS Characterization", runtime.Shell.WindowTitle);
        Assert.True(titleChanges > 0);
    }

    [Fact]
    public async Task NewWorksheetAndDeleteColumnMarkChanged()
    {
        using var first = new Runtime();
        await first.StartAsync();
        await first.Project.NewWorksheetCommand.ExecuteAsync(null);
        Assert.True(first.Lifecycle.HasMeaningfulChanges);

        // Delete raises ProjectModified for the current session (the flag itself was already set by the paste before it).
        using var second = new Runtime();
        await second.StartAsync();
        await second.PasteAsync("Lot\tVth\nA\t0.41\n");
        var session = second.Lifecycle.CurrentSession!;
        var modifications = 0;
        session.ProjectModified += (_, _) => modifications++;
        second.Project.SelectColumn(second.Project.SelectedWorksheetColumns![0].Id);
        await second.Project.DeleteSelectedColumnsCommand.ExecuteAsync(null);
        Assert.Equal(1, modifications);
        Assert.True(second.Lifecycle.HasMeaningfulChanges);
    }

    [Fact]
    public async Task EditsOfAProjectFileAreNotMeaningfulChanges()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        runtime.Dialogs.SavePaths.Enqueue(runtime.File("Saved.yat"));
        Assert.True(await runtime.Lifecycle.SaveAsAsync());

        Assert.False(runtime.Lifecycle.HasMeaningfulChanges);
        await runtime.PasteAsync("Vth\n0.41\n");
        await runtime.RenameAsync(runtime.Item("Sheet1"), "Renamed");

        Assert.False(runtime.Lifecycle.HasMeaningfulChanges);

        // Closing a project file never prompts.
        Assert.True(await runtime.Lifecycle.CloseAsync());
        Assert.Empty(runtime.Dialogs.Prompts);
    }

    // ---- New Project ----

    [Fact]
    public async Task NewProjectWithoutChangesReplacesTheProjectWithoutAPrompt()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var previous = runtime.Project;
        var previousPath = runtime.CurrentProjectSession.Database!.FilePath;

        Assert.True(await runtime.Lifecycle.NewProjectAsync());

        Assert.Empty(runtime.Dialogs.Prompts);
        Assert.NotSame(previous, runtime.Project);
        Assert.Equal("Untitled Project", runtime.Project.CurrentProject!.Name);
        Assert.Equal(["Sheet1"], runtime.Project.ProjectExplorer.WorksheetItems.Select(item => item.Name));
        Assert.False(File.Exists(previousPath));
        Assert.Single(runtime.TemporaryFolders());
    }

    [Fact]
    public async Task NewProjectAfterChangesDiscardReplacesTheProject()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var previousPath = runtime.CurrentProjectSession.Database!.FilePath;
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Discard);

        Assert.True(await runtime.Lifecycle.NewProjectAsync());

        Assert.Equal([("Untitled Project", false)], runtime.Dialogs.Prompts);
        Assert.Empty(runtime.Dialogs.SuggestedFileNames);
        Assert.False(runtime.Lifecycle.HasMeaningfulChanges);
        Assert.Empty(runtime.Project.GridColumns);
        Assert.False(File.Exists(previousPath));
        Assert.Single(runtime.TemporaryFolders());
    }

    [Fact]
    public async Task NewProjectAfterChangesCancelKeepsEverything()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var project = runtime.Project;
        var session = runtime.CurrentProjectSession;
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Cancel);

        Assert.False(await runtime.Lifecycle.NewProjectAsync());

        Assert.Same(project, runtime.Project);
        Assert.Same(session, runtime.CurrentProjectSession);
        Assert.True(runtime.Lifecycle.HasMeaningfulChanges);
        Assert.True(File.Exists(session.Database!.FilePath));
        await runtime.PasteAsync("Vth\n1\n");
        Assert.Equal(["Lot", "Vth"], runtime.Project.GridColumns.Select(column => column.Name));
    }

    [Fact]
    public async Task NewProjectAfterChangesSaveStoresTheProjectFileThenReplacesIt()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var path = runtime.File("Kept.yat");
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Save);
        runtime.Dialogs.SavePaths.Enqueue(path);

        Assert.True(await runtime.Lifecycle.NewProjectAsync());

        Assert.Equal(["Untitled Project"], runtime.Dialogs.SuggestedFileNames);
        Assert.True(runtime.Lifecycle.IsTemporaryProject);
        Assert.Empty(runtime.Project.GridColumns);
        Assert.Equal(["Lot"], await ColumnNamesInFileAsync(runtime.Directory, path));
    }

    [Fact]
    public async Task NewProjectAfterChangesSaveCancelledCancelsNew()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var project = runtime.Project;
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Save);
        runtime.Dialogs.SavePaths.Enqueue(null);

        Assert.False(await runtime.Lifecycle.NewProjectAsync());

        Assert.Same(project, runtime.Project);
        Assert.True(runtime.Lifecycle.IsTemporaryProject);
        Assert.True(runtime.Lifecycle.HasMeaningfulChanges);
        Assert.Empty(runtime.Dialogs.Errors);
    }

    [Fact]
    public async Task NewProjectAfterChangesFailedSaveShowsTheErrorAndCancelsNew()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var project = runtime.Project;
        var existing = runtime.File("Existing.yat");
        File.WriteAllText(existing, "keep me");
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Save);
        runtime.Dialogs.SavePaths.Enqueue(existing);

        Assert.False(await runtime.Lifecycle.NewProjectAsync());

        Assert.Equal(["A file with that name already exists. Please choose another file name."], runtime.Dialogs.Errors);
        Assert.Same(project, runtime.Project);
        Assert.Equal("keep me", File.ReadAllText(existing));
        await runtime.PasteAsync("Vth\n1\n");
        Assert.Equal(["Lot", "Vth"], runtime.Project.GridColumns.Select(column => column.Name));
    }

    // ---- Open Project ----

    [Fact]
    public async Task OpeningAProjectRebindsTheWindowToIt()
    {
        using var directory = new TemporaryDirectory();
        var path = await CreateProjectFileAsync(directory, "ALS.yat", "Lot\tVth\nA01\t0.412\nA02\t\n", "ALS Characterization");
        using var runtime = new Runtime(directory);
        await runtime.StartAsync();
        var previous = runtime.Project;
        var previousPath = runtime.CurrentProjectSession.Database!.FilePath;
        await runtime.RenameAsync(runtime.Item("Sheet1"), "  ");
        Assert.NotNull(previous.ErrorMessage);
        runtime.Project.ProjectExplorer.CancelRename(runtime.Item("Sheet1"));
        runtime.Dialogs.OpenPaths.Enqueue(path);
        var projectChanges = 0;
        runtime.Shell.PropertyChanged += (_, e) => projectChanges += e.PropertyName == nameof(MainWindowShellViewModel.Project) ? 1 : 0;

        Assert.True(await runtime.Lifecycle.OpenProjectAsync());
        await runtime.AwaitGridAsync();

        var opened = runtime.Project;
        Assert.NotSame(previous, opened);
        Assert.Same(opened, runtime.Shell.Project);
        Assert.Equal(1, projectChanges);
        Assert.Equal("YAT — ALS Characterization", runtime.Shell.WindowTitle);
        Assert.Equal(path, runtime.Lifecycle.ProjectFilePath);
        Assert.False(runtime.Lifecycle.IsTemporaryProject);
        Assert.False(runtime.Lifecycle.HasMeaningfulChanges);

        // Only the opened project's state: tree, first worksheet, its grid; no previous error or selection.
        Assert.Equal(["Sheet1"], opened.ProjectExplorer.WorksheetItems.Select(item => item.Name));
        Assert.Same(opened.Worksheets[0], opened.SelectedWorksheet);
        Assert.Equal(["Lot", "Vth"], opened.GridColumns.Select(column => column.Name));
        Assert.Equal(2, opened.TotalRowCount);
        Assert.Equal(["A02", ""], opened.GridRows[1].Cells);
        Assert.Null(opened.ErrorMessage);
        Assert.Empty(opened.SelectedColumns);
        Assert.Null(opened.ActiveColumn);

        Assert.False(File.Exists(previousPath));
    }

    [Fact]
    public async Task ColumnSelectionDoesNotLeakIntoAnOpenedProjectWithTheSameColumns()
    {
        using var directory = new TemporaryDirectory();
        var path = await CreateProjectFileAsync(directory, "Other.yat", "Lot\tVth\nB\t1\n");
        using var runtime = new Runtime(directory);
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\tVth\nA\t0.41\n");
        runtime.Project.SelectColumn(runtime.Project.SelectedWorksheetColumns![1].Id);
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Discard);
        runtime.Dialogs.OpenPaths.Enqueue(path);

        Assert.True(await runtime.Lifecycle.OpenProjectAsync());
        await runtime.AwaitGridAsync();

        Assert.Empty(runtime.Project.SelectedColumns);
        Assert.Null(runtime.Project.ActiveColumn);
        Assert.Equal(["B", "1"], runtime.Project.GridRows[0].Cells);
    }

    [Fact]
    public async Task OpenAfterChangesCancelKeepsTheProjectWithoutAPicker()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var project = runtime.Project;
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Cancel);

        Assert.False(await runtime.Lifecycle.OpenProjectAsync());

        Assert.Same(project, runtime.Project);
        Assert.Equal([("Untitled Project", false)], runtime.Dialogs.Prompts);
        Assert.True(runtime.Lifecycle.HasMeaningfulChanges);
    }

    [Fact]
    public async Task OpenAfterChangesDiscardOpensTheChosenProject()
    {
        using var directory = new TemporaryDirectory();
        var path = await CreateProjectFileAsync(directory, "Other.yat", "Wafer\n7\n");
        using var runtime = new Runtime(directory);
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var temporaryPath = runtime.CurrentProjectSession.Database!.FilePath;
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Discard);
        runtime.Dialogs.OpenPaths.Enqueue(path);

        Assert.True(await runtime.Lifecycle.OpenProjectAsync());
        await runtime.AwaitGridAsync();

        Assert.Equal(["Wafer"], runtime.Project.GridColumns.Select(column => column.Name));
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task OpenAfterChangesSaveStoresTheProjectFirst()
    {
        using var directory = new TemporaryDirectory();
        var other = await CreateProjectFileAsync(directory, "Other.yat", "Wafer\n7\n");
        using var runtime = new Runtime(directory);
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var saved = runtime.File("Saved.yat");
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Save);
        runtime.Dialogs.SavePaths.Enqueue(saved);
        runtime.Dialogs.OpenPaths.Enqueue(other);

        Assert.True(await runtime.Lifecycle.OpenProjectAsync());
        await runtime.AwaitGridAsync();

        Assert.Equal(other, runtime.Lifecycle.ProjectFilePath);
        Assert.Equal(["Lot"], await ColumnNamesInFileAsync(directory, saved));
    }

    [Fact]
    public async Task OpenAfterChangesSaveCancelledCancelsOpen()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var project = runtime.Project;
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Save);
        runtime.Dialogs.SavePaths.Enqueue(null);

        Assert.False(await runtime.Lifecycle.OpenProjectAsync());

        Assert.Same(project, runtime.Project);
        Assert.Empty(runtime.Dialogs.OpenPaths);
    }

    [Fact]
    public async Task CancelledOpenPickerChangesNothing()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var project = runtime.Project;
        runtime.Dialogs.OpenPaths.Enqueue(null);

        Assert.False(await runtime.Lifecycle.OpenProjectAsync());

        Assert.Same(project, runtime.Project);
        Assert.Empty(runtime.Dialogs.Errors);
    }

    [Fact]
    public async Task OpeningTheCurrentProjectFileAgainChangesNothing()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var path = runtime.File("Current.yat");
        runtime.Dialogs.SavePaths.Enqueue(path);
        Assert.True(await runtime.Lifecycle.SaveAsAsync());
        var project = runtime.Project;
        runtime.Dialogs.OpenPaths.Enqueue(path);

        Assert.True(await runtime.Lifecycle.OpenProjectAsync());

        Assert.Same(project, runtime.Project);
    }

    [Fact]
    public async Task InvalidFilesAreReportedAndTheCurrentProjectStaysUsable()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Discard);
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Discard);
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Discard);
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Discard);
        var project = runtime.Project;

        var textFile = runtime.File("Notes.yat");
        File.WriteAllText(textFile, "not a project");
        var plainDatabase = runtime.File("Plain.yat");
        using (var connection = new DuckDBConnection(new DuckDBConnectionStringBuilder { DataSource = plainDatabase }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE measurements (value DOUBLE)";
            command.ExecuteNonQuery();
        }

        var newer = await CreateProjectFileAsync(runtime.Directory, "Newer.yat", "Vth\n1\n");
        using (var connection = new DuckDBConnection(new DuckDBConnectionStringBuilder { DataSource = newer }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE yat_schema_info SET value = '2' WHERE key = 'schema_version'";
            command.ExecuteNonQuery();
        }

        foreach (var path in new[] { textFile, plainDatabase, newer, runtime.File("Missing.yat") })
        {
            runtime.Dialogs.OpenPaths.Enqueue(path);
            Assert.False(await runtime.Lifecycle.OpenProjectAsync());
        }

        Assert.Equal(
        [
            "The selected file is not a valid YAT project.",
            "The selected file is not a valid YAT project.",
            "This project was created by a newer version of YAT.",
            "The project could not be opened."
        ], runtime.Dialogs.Errors);
        Assert.Same(project, runtime.Project);
        Assert.True(runtime.Lifecycle.IsTemporaryProject);
        await runtime.PasteAsync("Vth\n1\n");
        Assert.Equal(["Lot", "Vth"], runtime.Project.GridColumns.Select(column => column.Name));
        Assert.Null(runtime.Project.ErrorMessage);
    }

    // ---- Save and Save As ----

    [Fact]
    public async Task SaveOfATemporaryProjectIsSaveAsAndTheSavedProjectKeepsWorking()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\tVth\nA\t0.41\n");
        await runtime.Project.NewWorksheetCommand.ExecuteAsync(null);
        await runtime.AwaitGridAsync();
        var previous = runtime.Project;
        var previousSession = runtime.Lifecycle.CurrentSession!;
        var temporaryPath = runtime.CurrentProjectSession.Database!.FilePath;
        runtime.Dialogs.SavePaths.Enqueue(runtime.File("Test"));

        Assert.True(await runtime.Lifecycle.SaveAsync());
        await runtime.AwaitGridAsync();

        var path = runtime.File("Test.yat");
        Assert.Equal(["Untitled Project"], runtime.Dialogs.SuggestedFileNames);
        Assert.True(File.Exists(path));
        Assert.Equal(path, runtime.Lifecycle.ProjectFilePath);
        Assert.False(runtime.Lifecycle.IsTemporaryProject);
        Assert.False(File.Exists(temporaryPath));
        Assert.NotSame(previous, runtime.Project);
        Assert.NotSame(previousSession, runtime.Lifecycle.CurrentSession);
        Assert.Equal("YAT — Untitled Project", runtime.Shell.WindowTitle);

        // The active worksheet (Sheet2) stays active in the saved project; the tree is unchanged.
        Assert.Equal(["Sheet1", "Sheet2"], runtime.Project.ProjectExplorer.WorksheetItems.Select(item => item.Name));
        Assert.Equal("Sheet2", runtime.Project.SelectedWorksheet!.Name);

        // Future edits go to the saved file; the retired session rejects stale use.
        await runtime.PasteAsync("Site\n1\n");
        await Assert.ThrowsAsync<ProjectSessionClosedException>(() => previousSession.LoadProjectAsync(Token));
        var sheet2 = runtime.Project.SelectedWorksheet.Id;
        Assert.True(await runtime.Lifecycle.CloseAsync());

        using var reopened = new Runtime(runtime.Directory);
        reopened.Dialogs.OpenPaths.Enqueue(path);
        await reopened.StartAsync();
        Assert.True(await reopened.Lifecycle.OpenProjectAsync());
        Assert.Equal(["Site"], await StoredColumnNamesAsync(reopened.CurrentProjectSession, sheet2));
        Assert.Equal(["Lot", "Vth"], await StoredColumnNamesAsync(reopened.CurrentProjectSession, reopened.Project.Worksheets[0].Id));
    }

    [Fact]
    public async Task SaveOfAProjectFileOnlyCheckpoints()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var path = runtime.File("Saved.yat");
        runtime.Dialogs.SavePaths.Enqueue(path);
        Assert.True(await runtime.Lifecycle.SaveAsAsync());
        await runtime.PasteAsync("Lot\nA\n");
        var project = runtime.Project;
        var session = runtime.CurrentProjectSession;

        Assert.True(await runtime.Lifecycle.SaveAsync());

        Assert.Same(project, runtime.Project);
        Assert.Same(session, runtime.CurrentProjectSession);
        Assert.Single(runtime.Dialogs.SuggestedFileNames);
        Assert.False(File.Exists(path + ".wal"));
        await runtime.PasteAsync("Vth\n1\n");
        Assert.Equal(["Lot", "Vth"], runtime.Project.GridColumns.Select(column => column.Name));
    }

    [Fact]
    public async Task SaveAsOfAProjectFileMovesFutureEditsToTheNewFileOnly()
    {
        using var directory = new TemporaryDirectory();
        var a = await CreateProjectFileAsync(directory, "A.yat", "Lot\nA\n", "ALS Characterization");
        using (var runtime = new Runtime(directory))
        {
            runtime.Dialogs.OpenPaths.Enqueue(a);
            await runtime.StartAsync();
            Assert.True(await runtime.Lifecycle.OpenProjectAsync());
            var b = directory.File("CustomerA_2026.yat");
            runtime.Dialogs.SavePaths.Enqueue(b);

            Assert.True(await runtime.Lifecycle.SaveAsAsync());
            await runtime.AwaitGridAsync();

            Assert.Equal(["A.yat"], runtime.Dialogs.SuggestedFileNames);
            Assert.Equal(b, runtime.Lifecycle.ProjectFilePath);
            Assert.Equal("ALS Characterization", runtime.Project.CurrentProject!.Name);
            Assert.Equal("YAT — ALS Characterization", runtime.Shell.WindowTitle);
            await runtime.PasteAsync("Vth\n1\n");
            Assert.True(await runtime.Lifecycle.CloseAsync());
        }

        Assert.Equal(["Lot"], await ColumnNamesInFileAsync(directory, a));
        Assert.Equal(["Lot", "Vth"], await ColumnNamesInFileAsync(directory, directory.File("CustomerA_2026.yat")));
    }

    [Fact]
    public async Task SaveAsNeverOverwritesAndKeepsTheCurrentProject()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var project = runtime.Project;
        File.WriteAllText(runtime.File("Existing.yat"), "keep me");
        runtime.Dialogs.SavePaths.Enqueue(runtime.File("Existing"));

        Assert.False(await runtime.Lifecycle.SaveAsAsync());

        Assert.Equal(["A file with that name already exists. Please choose another file name."], runtime.Dialogs.Errors);
        Assert.Equal("keep me", File.ReadAllText(runtime.File("Existing.yat")));
        Assert.Same(project, runtime.Project);
        Assert.True(runtime.Lifecycle.IsTemporaryProject);
        Assert.True(runtime.Lifecycle.HasMeaningfulChanges);
    }

    [Fact]
    public async Task FailedSaveAsKeepsTheCurrentProjectUsable()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var project = runtime.Project;
        runtime.Dialogs.SavePaths.Enqueue(Path.Combine(runtime.Directory.DirectoryPath, "missing", "B.yat"));

        Assert.False(await runtime.Lifecycle.SaveAsAsync());

        Assert.Equal(["The project could not be saved to the selected location."], runtime.Dialogs.Errors);
        Assert.Same(project, runtime.Project);
        await runtime.PasteAsync("Vth\n1\n");
        Assert.Equal(["Lot", "Vth"], runtime.Project.GridColumns.Select(column => column.Name));
    }

    [Fact]
    public async Task SuggestedFileNameOfATemporaryProjectIsItsNameWithoutInvalidCharacters()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.RenameAsync(runtime.Project.ProjectExplorer.ProjectItem!, "Lot A/B: \"Q3\"");
        runtime.Dialogs.SavePaths.Enqueue(null);

        Assert.False(await runtime.Lifecycle.SaveAsAsync());

        Assert.Equal(["Lot A_B_ _Q3_"], runtime.Dialogs.SuggestedFileNames);
    }

    [Fact]
    public async Task ShellStateCarriesOverAProjectSwitch()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Project.ToggleProjectPanelCommand.Execute(null);

        Assert.True(await runtime.Lifecycle.NewProjectAsync());

        Assert.False(runtime.Project.IsProjectPanelVisible);
    }

    // ---- Closing ----

    [Fact]
    public async Task ClosingAnUntouchedProjectDoesNotPromptAndCleansUp()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var session = runtime.Lifecycle.CurrentSession!;

        Assert.True(await runtime.Lifecycle.CloseAsync());

        Assert.Empty(runtime.Dialogs.Prompts);
        Assert.Empty(runtime.TemporaryFolders());
        await Assert.ThrowsAsync<ProjectSessionClosedException>(() => session.LoadProjectAsync(Token));
        Assert.False(await runtime.Lifecycle.NewProjectAsync());
        Assert.True(await runtime.Lifecycle.CloseAsync());
    }

    [Fact]
    public async Task ClosingAfterChangesDiscardClosesAndCleansUp()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Discard);

        Assert.True(await runtime.Lifecycle.CloseAsync());

        Assert.Equal([("Untitled Project", true)], runtime.Dialogs.Prompts);
        Assert.Empty(runtime.TemporaryFolders());
    }

    [Fact]
    public async Task ClosingAfterChangesCancelKeepsTheProjectOpen()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Cancel);

        Assert.False(await runtime.Lifecycle.CloseAsync());

        Assert.Single(runtime.TemporaryFolders());
        await runtime.PasteAsync("Vth\n1\n");
        Assert.Equal(["Lot", "Vth"], runtime.Project.GridColumns.Select(column => column.Name));
    }

    [Fact]
    public async Task ClosingAfterChangesSaveStoresTheProjectAndCloses()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var path = runtime.File("OnExit.yat");
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Save);
        runtime.Dialogs.SavePaths.Enqueue(path);

        Assert.True(await runtime.Lifecycle.CloseAsync());

        Assert.Empty(runtime.TemporaryFolders());
        Assert.Equal(["Lot"], await ColumnNamesInFileAsync(runtime.Directory, path));
    }

    [Fact]
    public async Task ClosingAfterChangesWithCancelledOrFailedSaveKeepsTheWindowOpen()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        File.WriteAllText(runtime.File("Existing.yat"), "keep me");
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Save);
        runtime.Dialogs.SavePaths.Enqueue(null);
        runtime.Dialogs.Choices.Enqueue(SaveChangesChoice.Save);
        runtime.Dialogs.SavePaths.Enqueue(runtime.File("Existing.yat"));

        Assert.False(await runtime.Lifecycle.CloseAsync());
        Assert.False(await runtime.Lifecycle.CloseAsync());

        Assert.Single(runtime.Dialogs.Errors);
        Assert.Single(runtime.TemporaryFolders());
        Assert.True(runtime.Lifecycle.HasMeaningfulChanges);
        await runtime.PasteAsync("Vth\n1\n");
        Assert.Equal(["Lot", "Vth"], runtime.Project.GridColumns.Select(column => column.Name));
    }

    // ---- Coordination ----

    [Fact]
    public async Task ASwitchWaitsForTheRunningSessionOperation()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var project = runtime.Project;
        var session = runtime.Lifecycle.CurrentSession!;

        // Holding the session like a running paste would.
        await session.SuspendAsync(Token);
        var newProject = runtime.Lifecycle.NewProjectAsync();
        await Task.Delay(150, Token);

        Assert.False(newProject.IsCompleted);
        Assert.Same(project, runtime.Project);

        session.Resume();
        Assert.True(await newProject);
        Assert.NotSame(project, runtime.Project);
        await Assert.ThrowsAsync<ProjectSessionClosedException>(() => session.LoadProjectAsync(Token));
    }

    [Fact]
    public async Task OperationsQueuedDuringASwitchDoNotReachEitherProject()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var oldProject = runtime.Project;
        var oldSession = runtime.Lifecycle.CurrentSession!;
        var oldWorksheetId = oldProject.SelectedWorksheet!.Id;

        await oldSession.SuspendAsync(Token);
        var newProject = runtime.Lifecycle.NewProjectAsync();
        runtime.Clipboard.Text = "Lot\nA\n";
        var stalePaste = oldProject.PasteCommand.ExecuteAsync(null);
        oldSession.Resume();

        Assert.True(await newProject);
        await stalePaste;

        Assert.Null(oldProject.ErrorMessage);
        Assert.Empty(runtime.Project.GridColumns);
        Assert.Empty(await runtime.CurrentProjectSession.WorksheetColumns.GetByWorksheetIdAsync(oldWorksheetId, Token));
        Assert.False(runtime.Lifecycle.HasMeaningfulChanges);
    }

    [Fact]
    public async Task OverlappingLifecycleRequestsAreIgnored()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var session = runtime.Lifecycle.CurrentSession!;
        await session.SuspendAsync(Token);

        var first = runtime.Lifecycle.NewProjectAsync();
        Assert.False(await runtime.Lifecycle.NewProjectAsync());
        Assert.False(await runtime.Lifecycle.CloseAsync());

        session.Resume();
        Assert.True(await first);
    }

    [Fact]
    public async Task ShellCommandsRunTheLifecycle()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var project = runtime.Project;
        var path = runtime.File("ViaCommand.yat");
        runtime.Dialogs.SavePaths.Enqueue(path);

        await runtime.Shell.SaveCommand.ExecuteAsync(null);
        Assert.Equal(path, runtime.Lifecycle.ProjectFilePath);

        await runtime.Shell.NewProjectCommand.ExecuteAsync(null);
        Assert.True(runtime.Lifecycle.IsTemporaryProject);

        runtime.Dialogs.OpenPaths.Enqueue(path);
        await runtime.Shell.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal(path, runtime.Lifecycle.ProjectFilePath);

        runtime.Dialogs.SavePaths.Enqueue(runtime.File("ViaCommand2.yat"));
        await runtime.Shell.SaveAsCommand.ExecuteAsync(null);
        Assert.Equal(runtime.File("ViaCommand2.yat"), runtime.Lifecycle.ProjectFilePath);
        Assert.NotSame(project, runtime.Shell.Project);
    }

    [Fact]
    public async Task StorageFailuresOfWorksheetOperationsAreShownInTheErrorBar()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        // Break the project's metadata table from outside, so the next paste fails in storage.
        await runtime.CurrentProjectSession.Database!.CheckpointAsync(Token);
        using (var connection = new DuckDBConnection(new DuckDBConnectionStringBuilder { DataSource = runtime.CurrentProjectSession.Database.FilePath }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE worksheet_column";
            command.ExecuteNonQuery();
        }

        await runtime.PasteAsync("Lot\nA\n");

        Assert.Equal("The project operation could not be completed.", runtime.Project.ErrorMessage);
        Assert.False(runtime.Lifecycle.HasMeaningfulChanges);
        Assert.False(runtime.Project.IsBusy);
    }

    [Fact]
    public void ShellAndLifecycleExposeNoInfrastructureOrAvaloniaTypes()
    {
        string[] forbidden = ["YAT.Infrastructure", "Avalonia", "DuckDB"];
        var exposed = new[] { typeof(ProjectLifecycleController), typeof(MainWindowShellViewModel), typeof(IProjectLifecycleDialogs) }
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType)
                .Concat(type.GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))))
            .Select(type => type.Namespace ?? string.Empty);

        Assert.DoesNotContain(exposed, name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.Equal(typeof(IProjectDatabase), typeof(ProjectSession).GetProperty(nameof(ProjectSession.Database))!.PropertyType);
    }
}
