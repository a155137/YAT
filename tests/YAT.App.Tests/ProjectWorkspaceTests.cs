using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Ingestion;
using YAT.app.Composition;
using YAT.Domain.Entities;

namespace YAT.App.Tests;

// ProjectWorkspace lifecycle over real project files: create, open, checkpoint, Save As, close and cleanup.
public class ProjectWorkspaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            TemporaryRoot = Directory.File("temp");
            Composition = new CompositionRoot(new FixedTimeProvider(Now), TemporaryRoot);
            Workspace = Composition.CreateProjectWorkspace();
        }

        public TemporaryDirectory Directory { get; } = new();

        public string TemporaryRoot { get; }

        public CompositionRoot Composition { get; }

        public ProjectWorkspace Workspace { get; }

        public ProjectSession Current => Workspace.CurrentSession!;

        public string[] TemporaryFolders() =>
            System.IO.Directory.Exists(TemporaryRoot) ? System.IO.Directory.GetDirectories(TemporaryRoot) : [];

        public string[] ProjectFolderFiles() =>
            System.IO.Directory.GetFiles(Directory.DirectoryPath).Select(Path.GetFileName).Order().ToArray()!;

        public void Dispose()
        {
            Workspace.Dispose();
            Directory.Dispose();
        }
    }

    private static async Task<Project> StoredProjectAsync(ProjectSession session) =>
        (await session.Projects.GetByIdAsync(session.ProjectId!.Value, Token))!;

    private static async Task<IReadOnlyList<Worksheet>> StoredWorksheetsAsync(ProjectSession session) =>
        await session.Worksheets.GetByProjectIdAsync(session.ProjectId!.Value, Token);

    private static async Task<PasteExecutionResult> PasteAsync(ProjectSession session, Guid worksheetId, string text)
    {
        var data = new TabularTextParser().Parse(text);
        var existing = await session.WorksheetColumns.GetByWorksheetIdAsync(worksheetId, Token);
        var plan = new WorksheetPastePlanner(new ColumnDataTypeDetector()).Plan(worksheetId, existing, existing.Count, data);
        return await session.PasteExecution.ExecuteAsync(plan, data, Token);
    }

    private static async Task<ProjectStorageException> AssertStorageErrorAsync(ProjectStorageError error, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<ProjectStorageException>(action);
        Assert.Equal(error, exception.Error);
        return exception;
    }

    [Fact]
    public void StartsWithoutAProject()
    {
        using var runtime = new Runtime();

        Assert.Null(runtime.Workspace.CurrentSession);
        Assert.False(runtime.Workspace.IsTemporaryProject);
        Assert.Null(runtime.Workspace.ProjectFilePath);
    }

    [Fact]
    public async Task TemporaryProjectIsUntitledProjectWithSheet1InFileBackedTemporaryStorage()
    {
        using var runtime = new Runtime();

        var session = await runtime.Workspace.CreateTemporaryProjectAsync(Token);

        Assert.Same(session, runtime.Workspace.CurrentSession);
        Assert.True(runtime.Workspace.IsTemporaryProject);
        Assert.Null(runtime.Workspace.ProjectFilePath);

        var database = session.Database!;
        Assert.True(database.IsTemporary);
        Assert.True(File.Exists(database.FilePath));
        Assert.StartsWith(runtime.TemporaryRoot + Path.DirectorySeparatorChar, database.FilePath);

        var project = await StoredProjectAsync(session);
        Assert.Equal("Untitled Project", project.Name);
        Assert.Equal(Now, project.CreatedAt);
        var sheet1 = Assert.Single(await StoredWorksheetsAsync(session));
        Assert.Equal("Sheet1", sheet1.Name);
        Assert.Equal(project.Id, sheet1.ProjectId);
    }

    [Fact]
    public async Task EachTemporaryProjectHasItsOwnStorage()
    {
        using var runtime = new Runtime();
        using var otherRuntime = new Runtime();

        var first = await runtime.Workspace.CreateTemporaryProjectAsync(Token);
        var second = await otherRuntime.Workspace.CreateTemporaryProjectAsync(Token);

        Assert.NotEqual(first.Database!.FilePath, second.Database!.FilePath);
        Assert.NotEqual(first.ProjectId, second.ProjectId);
    }

    [Fact]
    public async Task ClosingATemporaryProjectDeletesItsStorage()
    {
        using var runtime = new Runtime();
        var session = await runtime.Workspace.CreateTemporaryProjectAsync(Token);
        var path = session.Database!.FilePath;

        await runtime.Workspace.CloseProjectAsync(Token);

        Assert.Null(runtime.Workspace.CurrentSession);
        Assert.False(File.Exists(path));
        Assert.Empty(runtime.TemporaryFolders());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Worksheets.GetByIdAsync(Guid.NewGuid(), Token));
    }

    [Fact]
    public async Task DisposingTheWorkspaceDeletesTemporaryStorage()
    {
        using var runtime = new Runtime();
        var session = await runtime.Workspace.CreateTemporaryProjectAsync(Token);

        runtime.Workspace.Dispose();
        runtime.Workspace.Dispose();

        Assert.False(File.Exists(session.Database!.FilePath));
        Assert.Empty(runtime.TemporaryFolders());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.Workspace.CreateTemporaryProjectAsync(Token));
    }

    [Fact]
    public async Task CreatedProjectFileIsNamedAfterTheFileAndSurvivesClosing()
    {
        using var runtime = new Runtime();
        var path = runtime.Directory.File("ALS_Characterization.yat");

        var session = await runtime.Workspace.CreateProjectAsync(path, Token);

        Assert.False(runtime.Workspace.IsTemporaryProject);
        Assert.Equal(path, runtime.Workspace.ProjectFilePath);
        Assert.Equal("ALS_Characterization", (await StoredProjectAsync(session)).Name);
        Assert.Equal(["Sheet1"], (await StoredWorksheetsAsync(session)).Select(worksheet => worksheet.Name));

        await runtime.Workspace.CloseProjectAsync(Token);

        Assert.True(File.Exists(path));
        Assert.Equal(["ALS_Characterization.yat"], runtime.ProjectFolderFiles());
        Assert.Empty(runtime.TemporaryFolders());

        // A closed project file is released: it can be copied and deleted.
        File.Copy(path, runtime.Directory.File("Copy.yat"));
        File.Delete(path);
    }

    [Fact]
    public async Task OpeningAProjectFileLoadsItsProjectAndReplacesTheTemporaryProject()
    {
        using var runtime = new Runtime();
        var path = runtime.Directory.File("MyProject.yat");
        var created = await runtime.Workspace.CreateProjectAsync(path, Token);
        var projectId = created.ProjectId;
        await runtime.Workspace.CloseProjectAsync(Token);
        var temporary = await runtime.Workspace.CreateTemporaryProjectAsync(Token);

        var opened = await runtime.Workspace.OpenProjectAsync(path, Token);

        Assert.Same(opened, runtime.Workspace.CurrentSession);
        Assert.Equal(projectId, opened.ProjectId);
        Assert.Equal(path, runtime.Workspace.ProjectFilePath);
        Assert.Equal("MyProject", (await StoredProjectAsync(opened)).Name);
        Assert.False(File.Exists(temporary.Database!.FilePath));
        Assert.Single(runtime.TemporaryFolders());
    }

    [Fact]
    public async Task CreatingAProjectOverAnExistingFileFailsAndKeepsTheCurrentProject()
    {
        using var runtime = new Runtime();
        var current = await runtime.Workspace.CreateTemporaryProjectAsync(Token);
        var path = runtime.Directory.File("Existing.yat");
        File.WriteAllText(path, "keep me");

        await AssertStorageErrorAsync(ProjectStorageError.DestinationExists, () => runtime.Workspace.CreateProjectAsync(path, Token));

        Assert.Equal("keep me", File.ReadAllText(path));
        Assert.Same(current, runtime.Workspace.CurrentSession);
        Assert.Single(await StoredWorksheetsAsync(current));
        Assert.Single(runtime.TemporaryFolders());
    }

    [Fact]
    public async Task OpenFailuresAreReportedAndKeepTheCurrentProject()
    {
        using var runtime = new Runtime();
        var current = await runtime.Workspace.CreateTemporaryProjectAsync(Token);
        var textFile = runtime.Directory.File("Notes.yat");
        File.WriteAllText(textFile, "not a project");

        await AssertStorageErrorAsync(ProjectStorageError.CannotOpen, () => runtime.Workspace.OpenProjectAsync(runtime.Directory.File("Missing.yat"), Token));
        await AssertStorageErrorAsync(ProjectStorageError.NotAYatProject, () => runtime.Workspace.OpenProjectAsync(textFile, Token));

        Assert.Same(current, runtime.Workspace.CurrentSession);
        Assert.Single(await StoredWorksheetsAsync(current));
        Assert.Single(runtime.TemporaryFolders());
        Assert.False(File.Exists(runtime.Directory.File("Missing.yat")));
    }

    [Fact]
    public async Task OpeningAProjectFromANewerVersionIsRejected()
    {
        using var runtime = new Runtime();
        var path = runtime.Directory.File("Newer.yat");
        await runtime.Workspace.CreateProjectAsync(path, Token);
        await runtime.Workspace.CloseProjectAsync(Token);
        using (var connection = new DuckDB.NET.Data.DuckDBConnection(new DuckDB.NET.Data.DuckDBConnectionStringBuilder { DataSource = path }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE yat_schema_info SET value = '2' WHERE key = 'schema_version'";
            command.ExecuteNonQuery();
        }

        await AssertStorageErrorAsync(ProjectStorageError.UnsupportedSchemaVersion, () => runtime.Workspace.OpenProjectAsync(path, Token));

        Assert.Null(runtime.Workspace.CurrentSession);
        Assert.Empty(runtime.TemporaryFolders());
    }

    [Fact]
    public async Task CheckpointWritesTheProjectFile()
    {
        using var runtime = new Runtime();
        var path = runtime.Directory.File("MyProject.yat");
        var session = await runtime.Workspace.CreateProjectAsync(path, Token);
        var sheet1 = Assert.Single(await StoredWorksheetsAsync(session));
        await PasteAsync(session, sheet1.Id, "Vth\n0.41\n0.43\n");

        await runtime.Workspace.CheckpointAsync(Token);

        Assert.False(File.Exists(path + ".wal"));
        Assert.Same(session, runtime.Workspace.CurrentSession);
    }

    [Fact]
    public async Task LifecycleOperationsWithoutAProjectAreRejected()
    {
        using var runtime = new Runtime();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Workspace.CheckpointAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Workspace.SaveAsAsync(runtime.Directory.File("A.yat"), Token));
        await runtime.Workspace.CloseProjectAsync(Token);
    }

    [Fact]
    public async Task SaveAsMaterializesTheTemporaryProjectSwitchesToTheFileAndRetiresTemporaryStorage()
    {
        using var runtime = new Runtime();
        var temporary = await runtime.Workspace.CreateTemporaryProjectAsync(Token);
        var temporaryPath = temporary.Database!.FilePath;
        var sheet1 = Assert.Single(await StoredWorksheetsAsync(temporary));
        var pasted = await PasteAsync(temporary, sheet1.Id, "Lot\tVth\nA\t0.41\nB\t\n");
        var destination = runtime.Directory.File("ALS_Characterization.yat");

        var saved = await runtime.Workspace.SaveAsAsync(destination, Token);

        Assert.Same(saved, runtime.Workspace.CurrentSession);
        Assert.NotSame(temporary, saved);
        Assert.False(runtime.Workspace.IsTemporaryProject);
        Assert.Equal(destination, runtime.Workspace.ProjectFilePath);
        Assert.Equal(temporary.ProjectId, saved.ProjectId);

        // The previous session is closed and its temporary storage deleted; only the new file's working folder remains.
        Assert.False(File.Exists(temporaryPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(temporaryPath)));
        Assert.Single(runtime.TemporaryFolders());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => temporary.Worksheets.GetByIdAsync(sheet1.Id, Token));

        // The saved project has the temporary project's data and is writable.
        Assert.Equal(["ALS_Characterization.yat"], runtime.ProjectFolderFiles().Where(name => !name.EndsWith(".wal", StringComparison.Ordinal)));
        var block = await saved.RawDataStore.ReadColumnsAsync(sheet1.Id, pasted.Columns.Select(column => column.ColumnId).ToArray(), 0, 10, Token);
        Assert.Equal(["A", "B"], Assert.IsType<StringRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal([0.41, null], Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values);
        await PasteAsync(saved, sheet1.Id, "Site\n1\n2\n");
        Assert.Equal(3, (await saved.WorksheetColumns.GetByWorksheetIdAsync(sheet1.Id, Token)).Count);
    }

    [Fact]
    public async Task FailedSaveAsKeepsTheTemporaryProjectCurrentAndItsStorage()
    {
        using var runtime = new Runtime();
        var temporary = await runtime.Workspace.CreateTemporaryProjectAsync(Token);
        var sheet1 = Assert.Single(await StoredWorksheetsAsync(temporary));
        var existing = runtime.Directory.File("Existing.yat");
        File.WriteAllText(existing, "keep me");

        await AssertStorageErrorAsync(ProjectStorageError.DestinationExists, () => runtime.Workspace.SaveAsAsync(existing, Token));
        await AssertStorageErrorAsync(
            ProjectStorageError.CopyFailed, () => runtime.Workspace.SaveAsAsync(Path.Combine(runtime.Directory.DirectoryPath, "missing", "B.yat"), Token));

        Assert.Same(temporary, runtime.Workspace.CurrentSession);
        Assert.True(runtime.Workspace.IsTemporaryProject);
        Assert.True(File.Exists(temporary.Database!.FilePath));
        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.Equal(["Existing.yat"], runtime.ProjectFolderFiles());
        await PasteAsync(temporary, sheet1.Id, "Vth\n1\n");
        Assert.Single(await temporary.WorksheetColumns.GetByWorksheetIdAsync(sheet1.Id, Token));
    }

    [Fact]
    public async Task SaveAsOfAProjectFileKeepsTheOriginalFile()
    {
        using var runtime = new Runtime();
        var original = runtime.Directory.File("A.yat");
        var a = await runtime.Workspace.CreateProjectAsync(original, Token);
        var sheet1 = Assert.Single(await StoredWorksheetsAsync(a));
        await PasteAsync(a, sheet1.Id, "Vth\n0.41\n");

        var b = await runtime.Workspace.SaveAsAsync(runtime.Directory.File("B.yat"), Token);
        await PasteAsync(b, sheet1.Id, "Site\n7\n");
        await runtime.Workspace.CloseProjectAsync(Token);

        Assert.Equal(["A.yat", "B.yat"], runtime.ProjectFolderFiles());
        var reopenedA = await runtime.Workspace.OpenProjectAsync(original, Token);
        Assert.Equal(["Vth"], (await reopenedA.WorksheetColumns.GetByWorksheetIdAsync(sheet1.Id, Token)).Select(column => column.Name));
        var reopenedB = await runtime.Workspace.OpenProjectAsync(runtime.Directory.File("B.yat"), Token);
        Assert.Equal(["Vth", "Site"], (await reopenedB.WorksheetColumns.GetByWorksheetIdAsync(sheet1.Id, Token)).Select(column => column.Name));
    }

    [Fact]
    public async Task ProjectFileRejectsASecondProject()
    {
        using var runtime = new Runtime();
        var session = await runtime.Workspace.CreateTemporaryProjectAsync(Token);

        var exception = await Assert.ThrowsAsync<ProjectStorageException>(() => session.Projects.AddAsync(
            new Project { Id = Guid.NewGuid(), Name = "Second", CreatedAt = Now, UpdatedAt = Now }, Token));

        Assert.Equal(ProjectStorageError.ProjectAlreadyExists, exception.Error);
    }

    [Fact]
    public void SessionDatabaseIsExposedOnlyThroughTheApplicationAbstraction()
    {
        Assert.Equal(typeof(IProjectDatabase), typeof(ProjectSession).GetProperty(nameof(ProjectSession.Database))!.PropertyType);
        Assert.DoesNotContain(
            typeof(ProjectWorkspace).GetProperties().Select(property => property.PropertyType.Namespace),
            name => name is not null && (name.StartsWith("YAT.Infrastructure", StringComparison.Ordinal) || name.StartsWith("DuckDB", StringComparison.Ordinal)));
    }
}
