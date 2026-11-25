using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Tests.TestDoubles;

namespace YAT.Infrastructure.Tests;

public sealed class DuckDbSaveCopyOverwriteTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(Project Project, Worksheet Worksheet, Guid NumericColumnId)> WriteSampleAsync(
        DuckDbProjectDatabase database,
        string projectName,
        double?[] values)
    {
        var project = new Project { Id = Guid.NewGuid(), Name = projectName, CreatedAt = Now, UpdatedAt = Now };
        var worksheet = new Worksheet { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Sheet1", CreatedAt = Now, UpdatedAt = Now };
        var numericColumnId = Guid.NewGuid();

        await new DuckDbProjectRepository(database).AddAsync(project, Token);
        await new DuckDbWorksheetRepository(database).AddAsync(worksheet, Token);
        using var rawStore = new DuckDbWorksheetRawDataStore(database);
        await rawStore.WriteColumnsAsync(worksheet.Id, new RawDataBlock(
        [
            new NumericRawDataColumn(numericColumnId, values)
        ]), Token);

        return (project, worksheet, numericColumnId);
    }

    [Fact]
    public async Task SaveCopyWithoutOverwriteStillRejectsExistingDestination()
    {
        using var directory = new TemporaryProjectStorage();
        using var source = directory.Storage.CreateTemporary();
        await WriteSampleAsync(source, "Source", [1.0, 2.0]);
        var destination = directory.File("Existing.yat");
        File.WriteAllText(destination, "original");

        var ex = await Assert.ThrowsAsync<ProjectStorageException>(() => source.SaveCopyAsync(destination, Token));
        Assert.Equal(ProjectStorageError.DestinationExists, ex.Error);
        Assert.Equal("original", File.ReadAllText(destination));
    }

    [Fact]
    public async Task SaveCopyWithOverwriteReplacesClosedDestinationAndReopensWithIntegrity()
    {
        using var directory = new TemporaryProjectStorage();
        var destination = directory.File("Target.yat");

        using (var oldDb = (DuckDbProjectDatabase)directory.Storage.Create(destination))
        {
            await WriteSampleAsync(oldDb, "OldProject", [99.0]);
        }

        using var source = directory.Storage.CreateTemporary();
        var sample = await WriteSampleAsync(source, "NewProject", [10.5, null, 30.25]);

        await source.SaveCopyAsync(destination, overwrite: true, Token);

        using var reopened = (DuckDbProjectDatabase)directory.Storage.Open(destination);
        var loadedProject = await new DuckDbProjectRepository(reopened).GetFileProjectAsync(Token);
        Assert.NotNull(loadedProject);
        Assert.Equal(sample.Project.Id, loadedProject.Id);
        Assert.Equal("NewProject", loadedProject.Name);

        using var rawStore = new DuckDbWorksheetRawDataStore(reopened);
        var block = await rawStore.ReadColumnsAsync(sample.Worksheet.Id, [sample.NumericColumnId], 0, 10, Token);
        var col = Assert.IsType<NumericRawDataColumn>(Assert.Single(block.Columns));
        Assert.Equal([10.5, null, 30.25], col.Values);
    }

    [Fact]
    public async Task SaveCopyWithOverwriteRejectsSelfTarget()
    {
        using var directory = new TemporaryProjectStorage();
        var path = directory.File("Open.yat");
        using var source = (DuckDbProjectDatabase)directory.Storage.Create(path);
        await WriteSampleAsync(source, "Open", [1.0]);

        var ex = await Assert.ThrowsAsync<ProjectStorageException>(() => source.SaveCopyAsync(path, overwrite: true, Token));
        Assert.Equal(ProjectStorageError.DestinationExists, ex.Error);
    }

    [Fact]
    public async Task SaveCopyWithOverwriteRejectsDirectoryTarget()
    {
        using var directory = new TemporaryProjectStorage();
        using var source = directory.Storage.CreateTemporary();
        await WriteSampleAsync(source, "Source", [1.0]);
        var folderTarget = directory.File("Folder.yat");
        Directory.CreateDirectory(folderTarget);

        var ex = await Assert.ThrowsAsync<ProjectStorageException>(() => source.SaveCopyAsync(folderTarget, overwrite: true, Token));
        Assert.Equal(ProjectStorageError.DestinationUncertain, ex.Error);
        Assert.True(Directory.Exists(folderTarget));
    }

    [Fact]
    public async Task SaveCopyWithOverwriteRejectsDestinationWithExistingWalAndPreservesFiles()
    {
        using var directory = new TemporaryProjectStorage();
        var destination = directory.File("WithWal.yat");
        using (var oldDb = (DuckDbProjectDatabase)directory.Storage.Create(destination))
        {
            await WriteSampleAsync(oldDb, "OldWithWal", [5.0]);
        }

        var walPath = destination + ".wal";
        File.WriteAllText(walPath, "pending-wal");

        using var source = directory.Storage.CreateTemporary();
        await WriteSampleAsync(source, "Source", [1.0, 2.0]);

        var ex = await Assert.ThrowsAsync<ProjectStorageException>(() => source.SaveCopyAsync(destination, overwrite: true, Token));
        Assert.Equal(ProjectStorageError.DestinationUncertain, ex.Error);
        Assert.True(File.Exists(destination));
        Assert.Equal("pending-wal", File.ReadAllText(walPath));
    }

    [Fact]
    public async Task SaveCopyWithOverwriteRejectsLockedDestinationAndPreservesBoth()
    {
        using var directory = new TemporaryProjectStorage();
        var destination = directory.File("Locked.yat");
        using var otherProcessDb = (DuckDbProjectDatabase)directory.Storage.Create(destination);
        var oldSample = await WriteSampleAsync(otherProcessDb, "LockedProject", [42.0]);
        await otherProcessDb.CheckpointAsync(Token);

        using var source = directory.Storage.CreateTemporary();
        var srcSample = await WriteSampleAsync(source, "SourceProject", [7.0, 8.0]);

        var ex = await Assert.ThrowsAsync<ProjectStorageException>(() => source.SaveCopyAsync(destination, overwrite: true, Token));
        Assert.Equal(ProjectStorageError.DestinationInUse, ex.Error);

        Assert.Equal(oldSample.Project.Id, (await new DuckDbProjectRepository(otherProcessDb).GetFileProjectAsync(Token))?.Id);
        Assert.Equal(srcSample.Project.Id, (await new DuckDbProjectRepository(source).GetFileProjectAsync(Token))?.Id);
    }

    [Fact]
    public async Task SaveCopyWithOverwriteStagingFailurePreservesOriginalDestinationAndSource()
    {
        using var directory = new TemporaryProjectStorage();
        var destination = directory.File("Target.yat");
        File.WriteAllText(destination, "original-bytes");

        using var source = directory.Storage.CreateTemporary();
        var sample = await WriteSampleAsync(source, "Source", [1.0]);
        string? stagingPath = null;
        source.BeforeCopyMoveHook = staging =>
        {
            stagingPath = staging;
            throw new IOException("Simulated staging/validation failure.");
        };

        var ex = await Assert.ThrowsAsync<ProjectStorageException>(() => source.SaveCopyAsync(destination, overwrite: true, Token));
        Assert.Equal(ProjectStorageError.CopyFailed, ex.Error);
        Assert.Equal("original-bytes", File.ReadAllText(destination));
        Assert.NotNull(stagingPath);
        Assert.False(File.Exists(stagingPath));
        Assert.Equal(sample.Project.Id, (await new DuckDbProjectRepository(source).GetFileProjectAsync(Token))?.Id);
    }

    [Fact]
    public async Task SaveCopyWithOverwriteReplaceFailurePreservesOriginalDestinationAndCleansTempFiles()
    {
        using var directory = new TemporaryProjectStorage();
        var destination = directory.File("Target.yat");
        File.WriteAllText(destination, "original-bytes");

        using var source = directory.Storage.CreateTemporary();
        var sample = await WriteSampleAsync(source, "Source", [1.0]);
        source.BeforeReplaceHook = (_, _) => throw new IOException("Simulated replace failure.");

        var ex = await Assert.ThrowsAsync<ProjectStorageException>(() => source.SaveCopyAsync(destination, overwrite: true, Token));
        Assert.Equal(ProjectStorageError.DestinationInUse, ex.Error);
        Assert.Equal("original-bytes", File.ReadAllText(destination));
        Assert.Single(Directory.GetFiles(directory.DirectoryPath));
        Assert.Equal(sample.Project.Id, (await new DuckDbProjectRepository(source).GetFileProjectAsync(Token))?.Id);
    }

    [Fact]
    public async Task SaveCopyWithOverwritePreservesBackupWhenRestoreFails()
    {
        using var directory = new TemporaryProjectStorage();
        var destination = directory.File("Target.yat");
        File.WriteAllText(destination, "original-bytes");

        using var source = directory.Storage.CreateTemporary();
        await WriteSampleAsync(source, "Source", [1.0]);

        string? capturedBackup = null;
        FileStream? backupLock = null;
        source.BeforeReplaceHook = (_, backup) =>
        {
            capturedBackup = backup;
            File.Move(destination, backup);
            backupLock = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            throw new IOException("Simulated replace failure after backup move.");
        };

        try
        {
            var ex = await Assert.ThrowsAsync<ProjectStorageException>(() => source.SaveCopyAsync(destination, overwrite: true, Token));
            Assert.Equal(ProjectStorageError.DestinationRestoreFailed, ex.Error);
            Assert.Equal(capturedBackup, ex.RecoveryBackupPath);
        }
        finally
        {
            backupLock?.Dispose();
        }

        Assert.NotNull(capturedBackup);
        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(capturedBackup));
        Assert.Equal("original-bytes", File.ReadAllText(capturedBackup));
    }

    [Fact]
    public async Task SaveCopyWithOverwriteRejectsWalCreatedConcurrentlyBeforeReplace()
    {
        using var directory = new TemporaryProjectStorage();
        var destination = directory.File("Target.yat");
        File.WriteAllText(destination, "original-bytes");

        using var source = directory.Storage.CreateTemporary();
        await WriteSampleAsync(source, "Source", [1.0]);

        var walPath = destination + ".wal";
        source.BeforeCopyMoveHook = _ => File.WriteAllText(walPath, "late-wal");

        var ex = await Assert.ThrowsAsync<ProjectStorageException>(() => source.SaveCopyAsync(destination, overwrite: true, Token));
        Assert.Equal(ProjectStorageError.DestinationUncertain, ex.Error);
        Assert.Equal("original-bytes", File.ReadAllText(destination));
        Assert.Equal("late-wal", File.ReadAllText(walPath));
    }
}
