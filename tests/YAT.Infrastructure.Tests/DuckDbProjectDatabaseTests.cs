using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Tests.TestDoubles;

namespace YAT.Infrastructure.Tests;

// Project database lifecycle against real files: schema, validation, file locks, checkpoint, copies and cleanup.
public class DuckDbProjectDatabaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(Project Project, Worksheet Worksheet, Guid NumericColumnId, Guid StringColumnId)> WriteSampleAsync(
        DuckDbProjectDatabase database)
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "ALS_Characterization", CreatedAt = Now, UpdatedAt = Now };
        var worksheet = new Worksheet { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Sheet1", CreatedAt = Now, UpdatedAt = Now };
        var numericColumnId = Guid.NewGuid();
        var stringColumnId = Guid.NewGuid();

        await new DuckDbProjectRepository(database).AddAsync(project, Token);
        await new DuckDbWorksheetRepository(database).AddAsync(worksheet, Token);
        using var rawStore = new DuckDbWorksheetRawDataStore(database);
        await rawStore.WriteColumnsAsync(worksheet.Id, new RawDataBlock(
        [
            new NumericRawDataColumn(numericColumnId, [1.5, null, 3.25]),
            new StringRawDataColumn(stringColumnId, ["N1", null, ""])
        ]), Token);

        return (project, worksheet, numericColumnId, stringColumnId);
    }

    private static ProjectStorageException AssertStorageError(ProjectStorageError error, Action action)
    {
        var exception = Assert.Throws<ProjectStorageException>(action);
        Assert.Equal(error, exception.Error);
        return exception;
    }

    private static async Task<ProjectStorageException> AssertStorageErrorAsync(ProjectStorageError error, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<ProjectStorageException>(action);
        Assert.Equal(error, exception.Error);
        return exception;
    }

    [Fact]
    public void CreateWritesSchemaVersionOneWithMetadataAndRawTables()
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("MyProject.yat");

        using (var database = directory.Storage.Create(path))
        {
            Assert.Equal(path, database.FilePath);
            Assert.False(database.IsTemporary);
        }

        var (format, version, tables) = TemporaryProjectStorage.Inspect(path, connection => (
            TemporaryProjectStorage.Scalar(connection, "SELECT value FROM yat_schema_info WHERE key = 'format'"),
            TemporaryProjectStorage.Scalar(connection, "SELECT value FROM yat_schema_info WHERE key = 'schema_version'"),
            TemporaryProjectStorage.Scalar(connection, "SELECT string_agg(table_name, ',' ORDER BY table_name) FROM duckdb_tables()")));
        Assert.Equal("yat.project", format);
        Assert.Equal("1", version);
        Assert.Equal("project,raw_block,raw_column,raw_schema_info,worksheet,worksheet_column,yat_schema_info", tables);
    }

    [Fact]
    public void CreateTemporaryKeepsTheDatabaseInItsOwnFolderUnderTheTemporaryRoot()
    {
        using var directory = new TemporaryProjectStorage();

        using var first = directory.Storage.CreateTemporary();
        using var second = directory.Storage.CreateTemporary();

        Assert.True(first.IsTemporary);
        Assert.True(File.Exists(first.FilePath));
        Assert.Equal(DuckDbProjectStorage.TemporaryDatabaseFileName, Path.GetFileName(first.FilePath));
        Assert.Equal(directory.TemporaryRoot, Path.GetDirectoryName(Path.GetDirectoryName(first.FilePath)));
        Assert.NotEqual(Path.GetDirectoryName(first.FilePath), Path.GetDirectoryName(second.FilePath));
    }

    [Fact]
    public void CreateRefusesAnExistingFileAndLeavesItUnchanged()
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("Existing.yat");
        File.WriteAllText(path, "keep me");

        AssertStorageError(ProjectStorageError.DestinationExists, () => directory.Storage.Create(path));

        Assert.Equal("keep me", File.ReadAllText(path));
        Assert.Empty(directory.TemporaryFolders());
    }

    [Fact]
    public void CreateInAMissingFolderFailsWithoutLeavingFiles()
    {
        using var directory = new TemporaryProjectStorage();
        var path = Path.Combine(directory.DirectoryPath, "missing", "MyProject.yat");

        AssertStorageError(ProjectStorageError.CannotOpen, () => directory.Storage.Create(path));

        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Empty(directory.TemporaryFolders());
    }

    [Fact]
    public async Task OpenReadsTheStoredProject()
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("MyProject.yat");
        Project project;
        using (var database = directory.Storage.Create(path))
        {
            project = (await WriteSampleAsync(database)).Project;
        }

        using var reopened = directory.Storage.Open(path);

        Assert.False(reopened.IsTemporary);
        Assert.Equal(project.Name, (await new DuckDbProjectRepository(reopened).GetFileProjectAsync(Token))?.Name);
    }

    [Fact]
    public void OpenOfAMissingFileFailsWithoutCreatingIt()
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("Missing.yat");

        AssertStorageError(ProjectStorageError.CannotOpen, () => directory.Storage.Open(path));

        Assert.False(File.Exists(path));
        Assert.Empty(directory.TemporaryFolders());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a database")]
    [InlineData("0123456789abcdefghijklmnopqrstuvwxyz")]
    public void OpenRejectsAFileThatIsNotADatabase(string content)
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("Text.yat");
        File.WriteAllText(path, content);

        AssertStorageError(ProjectStorageError.NotAYatProject, () => directory.Storage.Open(path));

        Assert.Equal(content, File.ReadAllText(path));
        Assert.Empty(directory.TemporaryFolders());
    }

    [Fact]
    public void OpenRejectsADuckDbDatabaseWithoutYatMetadataAndDoesNotModifyIt()
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("Other.duckdb");
        TemporaryProjectStorage.Inspect(path, connection =>
        {
            TemporaryProjectStorage.Execute(connection, "CREATE TABLE measurements (id INTEGER, value DOUBLE)");
            return true;
        });

        AssertStorageError(ProjectStorageError.NotAYatProject, () => directory.Storage.Open(path));

        var tables = TemporaryProjectStorage.Inspect(path, connection =>
            TemporaryProjectStorage.Scalar(connection, "SELECT string_agg(table_name, ',') FROM duckdb_tables()"));
        Assert.Equal("measurements", tables);
    }

    [Theory]
    [InlineData("DELETE FROM yat_schema_info WHERE key = 'format'")]
    [InlineData("UPDATE yat_schema_info SET value = 'other.format' WHERE key = 'format'")]
    [InlineData("DELETE FROM yat_schema_info WHERE key = 'schema_version'")]
    [InlineData("UPDATE yat_schema_info SET value = 'one' WHERE key = 'schema_version'")]
    [InlineData("UPDATE yat_schema_info SET value = '0' WHERE key = 'schema_version'")]
    [InlineData("UPDATE yat_schema_info SET value = '-1' WHERE key = 'schema_version'")]
    [InlineData("DROP TABLE worksheet")]
    public void OpenRejectsMissingOrInvalidYatMetadata(string corruption)
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("Corrupt.yat");
        directory.Storage.Create(path).Dispose();
        TemporaryProjectStorage.Inspect(path, connection =>
        {
            TemporaryProjectStorage.Execute(connection, corruption);
            return true;
        });

        AssertStorageError(ProjectStorageError.NotAYatProject, () => directory.Storage.Open(path));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("17")]
    public void OpenRejectsANewerSchemaVersion(string version)
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("Newer.yat");
        directory.Storage.Create(path).Dispose();
        TemporaryProjectStorage.Inspect(path, connection =>
        {
            TemporaryProjectStorage.Execute(connection, $"UPDATE yat_schema_info SET value = '{version}' WHERE key = 'schema_version'");
            return true;
        });

        var exception = AssertStorageError(ProjectStorageError.UnsupportedSchemaVersion, () => directory.Storage.Open(path));

        Assert.Contains("newer version of YAT", exception.Message);
        Assert.Empty(directory.TemporaryFolders());
    }

    [Fact]
    public async Task DisposeReleasesTheFileSoItCanBeCopiedDeletedAndReopened()
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("MyProject.yat");
        var database = directory.Storage.Create(path);
        await WriteSampleAsync(database);

        database.Dispose();

        File.Copy(path, directory.File("Copy.yat"));
        Assert.False(File.Exists(path + ".wal"));
        using (var reopened = directory.Storage.Open(directory.File("Copy.yat")))
        {
            Assert.NotNull(await new DuckDbProjectRepository(reopened).GetFileProjectAsync(Token));
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OperationsAfterDisposeAreRejected()
    {
        using var directory = new TemporaryProjectStorage();
        var database = directory.Storage.Create(directory.File("MyProject.yat"));
        database.Dispose();
        database.Dispose();

        Assert.Throws<ObjectDisposedException>(() => database.Execute(_ => true));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => new DuckDbProjectRepository(database).GetByIdAsync(Guid.NewGuid(), Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => database.CheckpointAsync(Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => database.SaveCopyAsync(directory.File("Copy.yat"), Token));
    }

    [Fact]
    public async Task CheckpointWritesPendingChangesIntoTheDatabaseFile()
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("MyProject.yat");
        using var database = directory.Storage.Create(path);
        await WriteSampleAsync(database);

        await database.CheckpointAsync(Token);

        Assert.False(File.Exists(path + ".wal"));
        Assert.NotNull(await new DuckDbProjectRepository(database).GetFileProjectAsync(Token));
    }

    [Fact]
    public void ProjectFileWorkingFolderIsRemovedOnDispose()
    {
        using var directory = new TemporaryProjectStorage();
        var database = directory.Storage.Create(directory.File("MyProject.yat"));
        Assert.Single(directory.TemporaryFolders());

        database.Dispose();

        Assert.Empty(directory.TemporaryFolders());
        Assert.True(File.Exists(directory.File("MyProject.yat")));
    }

    [Fact]
    public async Task TemporaryStorageSurvivesDisposeUntilDeletedExplicitly()
    {
        using var directory = new TemporaryProjectStorage();
        var database = directory.Storage.CreateTemporary();
        var project = (await WriteSampleAsync(database)).Project;

        Assert.Throws<InvalidOperationException>(database.DeleteTemporaryStorage);
        database.Dispose();

        // The disposed temporary database can still be reopened (e.g. for inspection) until its storage is deleted.
        Assert.True(File.Exists(database.FilePath));
        using (var reopened = directory.Storage.Open(database.FilePath))
        {
            Assert.Equal(project.Id, (await new DuckDbProjectRepository(reopened).GetFileProjectAsync(Token))?.Id);
        }

        database.DeleteTemporaryStorage();

        Assert.False(File.Exists(database.FilePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(database.FilePath)));
        Assert.Empty(directory.TemporaryFolders());
        database.DeleteTemporaryStorage();
    }

    [Fact]
    public void DeletingStorageOfAProjectFileIsNotAllowed()
    {
        using var directory = new TemporaryProjectStorage();
        var database = directory.Storage.Create(directory.File("MyProject.yat"));
        database.Dispose();

        Assert.Throws<InvalidOperationException>(database.DeleteTemporaryStorage);
        Assert.True(File.Exists(database.FilePath));
    }

    [Fact]
    public async Task SaveCopyCreatesAValidatedCopyAndKeepsTheSourceOpenAndUsable()
    {
        using var directory = new TemporaryProjectStorage();
        using var source = directory.Storage.CreateTemporary();
        var sample = await WriteSampleAsync(source);
        var destination = directory.File("ALS_Characterization.yat");

        await source.SaveCopyAsync(destination, Token);

        Assert.True(File.Exists(destination));
        Assert.Equal(["ALS_Characterization.yat"], Directory.GetFiles(directory.DirectoryPath).Select(Path.GetFileName));

        // The source is still open and writable; later changes do not reach the copy.
        await new DuckDbWorksheetRepository(source).AddAsync(
            new Worksheet { Id = Guid.NewGuid(), ProjectId = sample.Project.Id, Name = "Sheet2", CreatedAt = Now, UpdatedAt = Now }, Token);
        Assert.Equal(2, (await new DuckDbWorksheetRepository(source).GetByProjectIdAsync(sample.Project.Id, Token)).Count);

        using var copy = directory.Storage.Open(destination);
        Assert.Equal(sample.Project.Id, (await new DuckDbProjectRepository(copy).GetFileProjectAsync(Token))?.Id);
        Assert.Equal(["Sheet1"], (await new DuckDbWorksheetRepository(copy).GetByProjectIdAsync(sample.Project.Id, Token)).Select(w => w.Name));
        using var rawStore = new DuckDbWorksheetRawDataStore(copy);
        var block = await rawStore.ReadColumnsAsync(sample.Worksheet.Id, [sample.NumericColumnId, sample.StringColumnId], 0, 10, Token);
        Assert.Equal([1.5, null, 3.25], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal(["N1", null, ""], Assert.IsType<StringRawDataColumn>(block.Columns[1]).Values);
    }

    [Fact]
    public async Task SaveCopyOfAProjectFileToAnotherFileKeepsBothUsable()
    {
        using var directory = new TemporaryProjectStorage();
        using var source = directory.Storage.Create(directory.File("A.yat"));
        var sample = await WriteSampleAsync(source);

        await source.SaveCopyAsync(directory.File("B.yat"), Token);
        await source.CheckpointAsync(Token);

        using var copy = directory.Storage.Open(directory.File("B.yat"));
        Assert.Equal(sample.Worksheet.Id, Assert.Single(await new DuckDbWorksheetRepository(copy).GetByProjectIdAsync(sample.Project.Id, Token)).Id);
        Assert.Equal(sample.Worksheet.Id, Assert.Single(await new DuckDbWorksheetRepository(source).GetByProjectIdAsync(sample.Project.Id, Token)).Id);
    }

    [Fact]
    public async Task SaveCopyNeverOverwritesAnExistingFile()
    {
        using var directory = new TemporaryProjectStorage();
        using var source = directory.Storage.CreateTemporary();
        await WriteSampleAsync(source);
        var destination = directory.File("Existing.yat");
        File.WriteAllText(destination, "keep me");

        await AssertStorageErrorAsync(ProjectStorageError.DestinationExists, () => source.SaveCopyAsync(destination, Token));
        await AssertStorageErrorAsync(ProjectStorageError.DestinationExists, () => source.SaveCopyAsync(source.FilePath, Token));

        Assert.Equal("keep me", File.ReadAllText(destination));
        Assert.Equal(["Existing.yat"], Directory.GetFiles(directory.DirectoryPath).Select(Path.GetFileName));
        Assert.NotNull(await new DuckDbProjectRepository(source).GetFileProjectAsync(Token));
    }

    [Fact]
    public async Task SaveCopyToAMissingFolderFailsAndKeepsTheSourceUsable()
    {
        using var directory = new TemporaryProjectStorage();
        using var source = directory.Storage.CreateTemporary();
        await WriteSampleAsync(source);

        await AssertStorageErrorAsync(
            ProjectStorageError.CopyFailed, () => source.SaveCopyAsync(Path.Combine(directory.DirectoryPath, "missing", "B.yat"), Token));

        Assert.NotNull(await new DuckDbProjectRepository(source).GetFileProjectAsync(Token));
        await source.CheckpointAsync(Token);
    }

    [Fact]
    public async Task ADestinationCreatedDuringTheCopyIsNotOverwrittenAndTheStagingFileIsRemoved()
    {
        using var directory = new TemporaryProjectStorage();
        using var source = directory.Storage.CreateTemporary();
        await WriteSampleAsync(source);
        var destination = directory.File("B.yat");
        string? stagingPath = null;
        source.BeforeCopyMoveHook = staging =>
        {
            stagingPath = staging;
            File.WriteAllText(destination, "created meanwhile");
        };

        await AssertStorageErrorAsync(ProjectStorageError.DestinationExists, () => source.SaveCopyAsync(destination, Token));

        Assert.NotNull(stagingPath);
        Assert.Equal(directory.DirectoryPath, Path.GetDirectoryName(stagingPath));
        Assert.False(File.Exists(stagingPath));
        Assert.Equal("created meanwhile", File.ReadAllText(destination));
        Assert.NotNull(await new DuckDbProjectRepository(source).GetFileProjectAsync(Token));
    }

    [Fact]
    public async Task AFailedCopyRemovesItsStagingFileAndLeavesTheSourceUsable()
    {
        using var directory = new TemporaryProjectStorage();
        using var source = directory.Storage.CreateTemporary();
        var sample = await WriteSampleAsync(source);
        var destination = directory.File("B.yat");
        source.BeforeCopyMoveHook = _ => throw new IOException("Simulated failure.");

        var exception = await AssertStorageErrorAsync(ProjectStorageError.CopyFailed, () => source.SaveCopyAsync(destination, Token));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath));
        Assert.Equal(sample.Project.Id, (await new DuckDbProjectRepository(source).GetFileProjectAsync(Token))?.Id);

        // The source can still be copied once the failure is gone.
        source.BeforeCopyMoveHook = null;
        await source.SaveCopyAsync(destination, Token);
        Assert.True(File.Exists(destination));
    }
}
