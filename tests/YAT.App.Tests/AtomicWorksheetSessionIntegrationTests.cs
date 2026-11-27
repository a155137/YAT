using YAT.Application.Abstractions.Persistence;
using YAT.Application.Graphs;
using YAT.Domain.Enums;
using YAT.app.Clipboard;
using YAT.app.Composition;
using YAT.app.Lifecycle;

namespace YAT.App.Tests;

public sealed class AtomicWorksheetSessionIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public AtomicWorksheetSessionIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft02a-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class StubClipboard : IClipboardTextReader, IClipboardTextWriter
    {
        public string? Text { get; set; }
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Text);
        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class StubDialogs : IProjectLifecycleDialogs
    {
        public string? SavePath { get; set; }
        public Task<SaveChangesChoice> AskSaveChangesAsync(string projectName, bool closing) => Task.FromResult(SaveChangesChoice.Discard);
        public Task<string?> PickProjectToOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveLocationAsync(string suggestedFileName) => Task.FromResult(SavePath);
        public Task<bool> ConfirmOverwriteAsync(string destinationPath) => Task.FromResult(true);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }

    [Fact]
    public async Task SessionAtomicImportUpdatesDirtyStateOnlyOnCommitDoesNotAutoPublishUiAndSurvivesSaveAsReopen()
    {
        var composition = new CompositionRoot(TimeProvider.System, Path.Combine(_tempDir, "projects"));
        using var workspace = composition.CreateProjectWorkspace();
        var clipboard = new StubClipboard();
        var dialogs = new StubDialogs();
        var lifecycle = composition.CreateProjectLifecycle(workspace, clipboard, clipboard, dialogs);

        Assert.True(await lifecycle.StartAsync());
        Assert.False(lifecycle.HasMeaningfulChanges);

        var session = lifecycle.CurrentSession!;
        var vm = lifecycle.Project!;
        Assert.Single(vm.Worksheets);
        var originalSelectedId = vm.SelectedWorksheet!.Id;

        var columns = new[]
        {
            new WorksheetColumnDefinition("Lot_ID", WorksheetDataType.String),
            new WorksheetColumnDefinition("Bin", WorksheetDataType.String),
            new WorksheetColumnDefinition("VDD", WorksheetDataType.Numeric, Unit: "V")
        };

        // 1. Failed import rolls back and MUST NOT set HasMeaningfulChanges or touch UI.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.CreateWorksheetWithStreamingDataAsync(
                "FailedImport",
                columns,
                writer =>
                {
                    writer.BeginRow();
                    writer.WriteString("L1");
                    writer.WriteString("01");
                    writer.WriteDouble(1.8);
                    writer.EndRow();
                    throw new InvalidOperationException("boom");
                },
                Token));

        Assert.False(lifecycle.HasMeaningfulChanges);
        Assert.Single(vm.Worksheets);
        Assert.Equal(originalSelectedId, vm.SelectedWorksheet!.Id);

        // 2. Cancelled import rolls back and MUST NOT set HasMeaningfulChanges.
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.CreateWorksheetWithStreamingDataAsync("CancelledImport", columns, _ => { }, cts.Token));
        }
        Assert.False(lifecycle.HasMeaningfulChanges);

        // 3. Committed import sets HasMeaningfulChanges on temporary project, does NOT auto-publish/activate UI.
        var created = await session.CreateWorksheetWithStreamingDataAsync(
            "Imported_FT",
            columns,
            writer =>
            {
                writer.BeginRow();
                writer.WriteString("LOT01");
                writer.WriteString("001");
                writer.WriteDouble(1.8);
                writer.EndRow();

                writer.BeginRow();
                writer.WriteString("LOT01");
                writer.WriteString("002");
                writer.WriteNull();
                writer.EndRow();
            },
            Token);

        Assert.True(lifecycle.HasMeaningfulChanges);
        Assert.Equal(2, created.Worksheet.RowCount);
        Assert.Equal(3, created.Columns.Count);
        // UI view model is not auto-mutated; FT-03/FT-04 explicitly publishes committed worksheets.
        Assert.Single(vm.Worksheets);
        Assert.Equal(originalSelectedId, vm.SelectedWorksheet!.Id);

        // 4. Existing clipboard paste into Sheet1 continues to work unchanged.
        clipboard.Text = "A\tB\n1\t2\n3\t4\n";
        var pasteResult = await session.PasteFromClipboardAsync(originalSelectedId, null, Token);
        Assert.True(pasteResult.IsPasted);

        // 5. Save As persists both Sheet1 and Imported_FT and reopens cleanly.
        var savedPath = Path.Combine(_tempDir, "saved_atomic.yat");
        dialogs.SavePath = savedPath;
        Assert.True(await lifecycle.SaveAsAsync());
        Assert.False(lifecycle.HasMeaningfulChanges);

        var reopenedSession = lifecycle.CurrentSession!;
        var reopenedVm = lifecycle.Project!;
        Assert.Equal(2, reopenedVm.Worksheets.Count);
        Assert.Contains(reopenedVm.Worksheets, w => w.Id == created.Worksheet.Id && w.Name == "Imported_FT");

        // Virtualized paging & graph query over reopened imported worksheet.
        var pageMeta = await reopenedSession.LoadWorksheetPageMetadataAsync(created.Worksheet.Id, 0, Token);
        Assert.Equal(2, pageMeta.TotalRowCount);
        var window = await reopenedSession.LoadWorksheetColumnWindowAsync(
            created.Worksheet.Id, 0, [created.Columns[1].Id, created.Columns[2].Id], Token);
        Assert.Equal(["001", "002"], Assert.IsType<StringRawDataColumn>(window.Columns[0].Values).Values);
        Assert.Equal([1.8, null], Assert.IsType<NumericRawDataColumn>(window.Columns[1].Values).Values);

        var graphData = await reopenedSession.LoadGraphDataAsync(
            new GraphConfiguration(
                GraphType.Histogram,
                created.Worksheet.Id,
                [
                    new GraphColumnAssignment(GraphVariableRole.Variable, created.Columns[2].Id),
                    new GraphColumnAssignment(GraphVariableRole.Group, created.Columns[0].Id)
                ]),
            Token);
        Assert.Equal(1, graphData.Count); // 1 non-null VDD observation; row 2 has Missing VDD
        var group = graphData switch
        {
            MultiVariableGraphData multi => Assert.IsType<StringGroupData>(multi.Variables[0].Group),
            UnivariateGraphData uni => Assert.IsType<StringGroupData>(uni.Group),
            _ => throw new InvalidOperationException($"Unexpected GraphData type: {graphData.GetType().Name}")
        };
        Assert.Equal("LOT01", group.Values.Span[0]);

        await lifecycle.CloseAsync();
    }

    private sealed class PostCommitCancellingCreator(IAtomicWorksheetCreator inner, CancellationTokenSource cts) : IAtomicWorksheetCreator
    {
        public async Task<CreatedWorksheetResult> CreateWorksheetAsync(
            AtomicWorksheetCreateRequest request,
            Action<IRawDataRowWriter> writeRows,
            CancellationToken cancellationToken = default)
        {
            var committed = await inner.CreateWorksheetAsync(request, writeRows, cancellationToken);
            // Deterministically signal cancellation immediately after the underlying DuckDB transaction has committed.
            cts.Cancel();
            return committed;
        }
    }

    [Fact]
    public async Task MainWindowSessionPostCommitCancellationReturnsCommittedResultAndNotifiesProjectModifiedExactlyOnce()
    {
        var composition = new CompositionRoot(TimeProvider.System, Path.Combine(_tempDir, "projects_post_commit"));
        using var workspace = composition.CreateProjectWorkspace();
        var clipboard = new StubClipboard();
        var projectSession = await workspace.CreateTemporaryProjectAsync(Token);

        using var cts = new CancellationTokenSource();
        var wrappedCreator = new PostCommitCancellingCreator(projectSession.AtomicWorksheetCreator!, cts);

        // Construct a real ProjectSession + MainWindowSession wired through CompositionRoot, replacing only the
        // IAtomicWorksheetCreator with the deterministic post-commit cancelling wrapper.
        var wrappedProjectSession = new ProjectSession(
            projectSession.Projects,
            projectSession.Worksheets,
            projectSession.WorksheetColumns,
            projectSession.RawDataStore,
            projectSession.PasteExecution,
            projectSession.Database,
            wrappedCreator)
        {
            ProjectId = projectSession.ProjectId
        };

        var mainSession = composition.CreateMainWindowSession(wrappedProjectSession, clipboard, clipboard);
        var modifiedCount = 0;
        mainSession.ProjectModified += (_, _) => modifiedCount++;

        var cols = new[]
        {
            new WorksheetColumnDefinition("Lot_ID", WorksheetDataType.String),
            new WorksheetColumnDefinition("VDD", WorksheetDataType.Numeric)
        };

        var committed = await mainSession.CreateWorksheetWithStreamingDataAsync(
            "PostCommitSheet",
            cols,
            writer =>
            {
                writer.BeginRow();
                writer.WriteString("LOT_PC");
                writer.WriteDouble(3.3);
                writer.EndRow();
            },
            cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal("PostCommitSheet", committed.Worksheet.Name);
        Assert.Equal(1, committed.Worksheet.RowCount);
        Assert.Equal(1, modifiedCount);

        // Verify the committed worksheet is actually persisted in DuckDB.
        var persisted = await projectSession.Worksheets.GetByIdAsync(committed.Worksheet.Id, Token);
        Assert.NotNull(persisted);
        Assert.Equal(1, persisted!.RowCount);
    }
}
