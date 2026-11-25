using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;
using YAT.Infrastructure.Persistence.DuckDb;

namespace YAT.App.Tests;

public sealed class SaveAsOverwriteTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public TemporaryDirectory Directory { get; } = new();
        public FakeProjectLifecycleDialogs Dialogs { get; } = new();
        public FakeClipboard Clipboard { get; } = new();
        public ProjectWorkspace Workspace { get; }
        public ProjectLifecycleController Lifecycle { get; }

        public Runtime()
        {
            var root = new CompositionRoot(
                new FixedTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero)),
                Path.Combine(Directory.DirectoryPath, "temp"));
            Workspace = new ProjectWorkspace(root);
            Lifecycle = new ProjectLifecycleController(root, Workspace, Clipboard, Clipboard, Dialogs);
        }

        public MainWindowViewModel Project => Lifecycle.Project!;

        public string FilePath(string name) => Directory.File(name);

        public async Task StartAsync()
        {
            Assert.True(await Lifecycle.StartAsync());
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await Project.PasteCommand.ExecuteAsync(null);
        }

        public void Dispose()
        {
            Workspace.Dispose();
            Directory.Dispose();
        }
    }

    [Fact]
    public async Task SaveAsNewDestinationDoesNotPromptOverwriteAndPersistsData()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var path = runtime.FilePath("Fresh.yat");
        runtime.Dialogs.SavePaths.Enqueue(path);

        Assert.True(await runtime.Lifecycle.SaveAsAsync());
        Assert.Empty(runtime.Dialogs.OverwritePrompts);
        Assert.Empty(runtime.Dialogs.Errors);
        Assert.False(runtime.Lifecycle.IsTemporaryProject);
        Assert.Equal(path, runtime.Workspace.ProjectFilePath);
    }

    [Fact]
    public async Task SaveAsNormalizesExtensionBeforeConfirmingOverwriteAndReopensWithIntegrity()
    {
        using var targetRuntime = new Runtime();
        await targetRuntime.StartAsync();
        await targetRuntime.PasteAsync("OldCol\n99\n");
        var existingPath = targetRuntime.FilePath("Existing.yat");
        targetRuntime.Dialogs.SavePaths.Enqueue(existingPath);
        Assert.True(await targetRuntime.Lifecycle.SaveAsAsync());
        await targetRuntime.Lifecycle.CloseAsync();

        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Vth\n1.25\n2.50\n");

        // User enters name without ".yat"; controller normalizes to "...Existing.yat" before confirming overwrite.
        var withoutExt = Path.Combine(Path.GetDirectoryName(existingPath)!, "Existing");
        runtime.Dialogs.SavePaths.Enqueue(withoutExt);
        runtime.Dialogs.OverwriteConfirmations.Enqueue(true);

        Assert.True(await runtime.Lifecycle.SaveAsAsync());
        await runtime.Project.GridLoadTask;
        Assert.Equal([existingPath], runtime.Dialogs.OverwritePrompts);
        Assert.Empty(runtime.Dialogs.Errors);
        Assert.Equal(existingPath, runtime.Workspace.ProjectFilePath);
        Assert.Equal(["Vth"], runtime.Project.GridColumns.Select(c => c.Name));

        await runtime.Lifecycle.CloseAsync();

        using var reopenRuntime = new Runtime();
        await reopenRuntime.StartAsync();
        reopenRuntime.Dialogs.OpenPaths.Enqueue(existingPath);
        Assert.True(await reopenRuntime.Lifecycle.OpenProjectAsync());
        await reopenRuntime.Project.GridLoadTask;
        Assert.Equal(["Vth"], reopenRuntime.Project.GridColumns.Select(c => c.Name));
        Assert.Equal(2, reopenRuntime.Project.TotalRowCount);
    }

    [Fact]
    public async Task SaveAsToSameOpenProjectPathUsesCheckpointFlowWithoutOverwritePrompt()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var path = runtime.FilePath("Same.yat");
        runtime.Dialogs.SavePaths.Enqueue(path);
        Assert.True(await runtime.Lifecycle.SaveAsAsync());
        var projectBefore = runtime.Project;

        await runtime.PasteAsync("Vth\n3.3\n");
        runtime.Dialogs.SavePaths.Enqueue(path);

        Assert.True(await runtime.Lifecycle.SaveAsAsync());
        Assert.Empty(runtime.Dialogs.OverwritePrompts);
        Assert.Empty(runtime.Dialogs.Errors);
        Assert.Same(projectBefore, runtime.Project);

        await runtime.Lifecycle.CloseAsync();

        using var reopenRuntime = new Runtime();
        await reopenRuntime.StartAsync();
        reopenRuntime.Dialogs.OpenPaths.Enqueue(path);
        Assert.True(await reopenRuntime.Lifecycle.OpenProjectAsync());
        await reopenRuntime.Project.GridLoadTask;
        Assert.Equal(["Lot", "Vth"], reopenRuntime.Project.GridColumns.Select(c => c.Name));
    }

    [Fact]
    public async Task SaveAsRejectsDirectoryTargetWithoutPromptingOverwrite()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var folder = runtime.FilePath("Folder.yat");
        Directory.CreateDirectory(folder);
        runtime.Dialogs.SavePaths.Enqueue(folder);

        Assert.False(await runtime.Lifecycle.SaveAsAsync());
        Assert.Empty(runtime.Dialogs.OverwritePrompts);
        Assert.Single(runtime.Dialogs.Errors);
        Assert.True(Directory.Exists(folder));
        Assert.True(runtime.Lifecycle.IsTemporaryProject);
    }

    [Fact]
    public async Task SaveAsRejectsDestinationWithExistingWalAndPreservesFiles()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\n");
        var existing = runtime.FilePath("WithWal.yat");
        File.WriteAllText(existing, "existing-db");
        File.WriteAllText(existing + ".wal", "existing-wal");
        runtime.Dialogs.SavePaths.Enqueue(existing);

        Assert.False(await runtime.Lifecycle.SaveAsAsync());
        Assert.Empty(runtime.Dialogs.OverwritePrompts);
        Assert.Single(runtime.Dialogs.Errors);
        Assert.Equal("existing-db", File.ReadAllText(existing));
        Assert.Equal("existing-wal", File.ReadAllText(existing + ".wal"));
    }

    [Fact]
    public async Task SaveAsLockedDestinationFailsSafelyAndPreservesBothProjects()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("NewCol\n42\n");
        var lockedPath = runtime.FilePath("Locked.yat");

        using var otherDir = new TemporaryDirectory();
        var otherStorage = new DuckDbProjectStorage(Path.Combine(otherDir.DirectoryPath, "temp"));
        using var lockedDb = otherStorage.Create(lockedPath);

        runtime.Dialogs.SavePaths.Enqueue(lockedPath);
        runtime.Dialogs.OverwriteConfirmations.Enqueue(true);

        Assert.False(await runtime.Lifecycle.SaveAsAsync());
        Assert.Single(runtime.Dialogs.Errors);
        Assert.True(runtime.Lifecycle.IsTemporaryProject);
        Assert.Equal(["NewCol"], runtime.Project.GridColumns.Select(c => c.Name));
        Assert.True(File.Exists(lockedPath));
    }

    [Fact]
    public void ProjectStorageMessagesIncludesPreservedBackupPathAndWarningWhenRestoreFails()
    {
        var backupPath = @"D:\Projects\.Target.yat.123456.bak";
        var ex = new YAT.Application.Exceptions.ProjectStorageException(
            YAT.Application.Exceptions.ProjectStorageError.DestinationRestoreFailed,
            "Internal message.")
        {
            RecoveryBackupPath = backupPath
        };

        var userMessage = ProjectStorageMessages.For(ex);

        Assert.Contains("Do not delete", userMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(backupPath, userMessage, StringComparison.Ordinal);
    }
}
