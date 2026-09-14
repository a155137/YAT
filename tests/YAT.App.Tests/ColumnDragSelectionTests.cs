using Avalonia.Input;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.app.Views;

namespace YAT.App.Tests;

// Drag selection across column headers: the view-side drag tracker plus the ViewModel's range selection.
public class ColumnDragSelectionTests
{
    private const string EightColumns =
        "A\tB\tC\tD\tE\tF\tG\tH\n" +
        "1\t2\t3\t4\t5\t6\t7\t8\n";

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
            ProjectSession = compositionRoot.CreateProjectSession(":memory:");
            ViewModel = compositionRoot.CreateMainWindowViewModel(compositionRoot.CreateMainWindowSession(ProjectSession, Clipboard, Clipboard));
        }

        public FakeClipboard Clipboard { get; } = new();

        public ProjectSession ProjectSession { get; }

        public MainWindowViewModel ViewModel { get; }

        public string[] Headers => ViewModel.GridColumns.Select(column => column.Name).ToArray();

        public string[] Selected => ViewModel.SelectedColumns.Select(column => column.Name).ToArray();

        public string? Active => ViewModel.ActiveColumn?.Name;

        public async Task StartAsync()
        {
            await ViewModel.CreateDefaultWorkspaceAsync();
            await ViewModel.GridLoadTask;
            await PasteAsync(EightColumns);
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await ViewModel.PasteCommand.ExecuteAsync(null);
        }

        public Guid Id(string name) => ViewModel.GridColumns.Single(column => column.Name == name).ColumnId;

        // Replays what MainWindow does for a plain left-button drag: select the start column on press, then select the
        // range from the anchor to each newly entered header. Returns the selection after each pointer step.
        public List<string[]> Drag(string start, params string[] headersEntered)
        {
            var drag = new ColumnHeaderDragSelection();
            var snapshots = new List<string[]>();

            Assert.True(drag.TryStart(Id(start), isLeftButton: true, isOnResizeGrip: false, KeyModifiers.None));
            ViewModel.SelectColumn(drag.AnchorColumnId!.Value);
            snapshots.Add(Selected);

            foreach (var name in headersEntered)
            {
                if (drag.TryMoveTo(Id(name)))
                {
                    ViewModel.SelectColumnRange(drag.AnchorColumnId!.Value, drag.CurrentColumnId!.Value);
                }

                snapshots.Add(Selected);
            }

            drag.End();
            return snapshots;
        }

        public void Dispose() => ProjectSession.Dispose();
    }

    // 1
    [Fact]
    public async Task DraggingFromAToDSelectsAThroughD()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.Drag("A", "B", "C", "D");

        Assert.Equal(["A", "B", "C", "D"], runtime.Selected);
    }

    // 2, 3
    [Fact]
    public async Task DraggingBackwardsFromDToBSelectsBThroughDWithTheStartColumnActive()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.Drag("D", "C", "B");

        Assert.Equal(["B", "C", "D"], runtime.Selected);
        Assert.Equal("D", runtime.Active);
    }

    // 3, 4
    [Fact]
    public async Task SelectionUpdatesLiveAndTheDragStartStaysActive()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var activeAtEachStep = new List<string?>();
        runtime.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedColumns))
            {
                activeAtEachStep.Add(runtime.Active);
            }
        };

        var snapshots = runtime.Drag("C", "D", "E", "D", "B");

        Assert.Equal(
            [["C"], ["C", "D"], ["C", "D", "E"], ["C", "D"], ["B", "C"]],
            snapshots);
        Assert.All(activeAtEachStep, active => Assert.Equal("C", active));
    }

    // 5
    [Fact]
    public async Task PressAndReleaseWithoutMovingIsASingleColumnClick()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Drag("A", "B", "C");

        runtime.Drag("F");

        Assert.Equal(["F"], runtime.Selected);
        Assert.Equal("F", runtime.Active);
    }

    // 6
    [Fact]
    public void ResizeGripRightButtonAndModifiedPressesDoNotStartADrag()
    {
        var drag = new ColumnHeaderDragSelection();
        var column = Guid.NewGuid();

        Assert.False(drag.TryStart(column, isLeftButton: true, isOnResizeGrip: true, KeyModifiers.None));
        Assert.False(drag.TryStart(column, isLeftButton: false, isOnResizeGrip: false, KeyModifiers.None));
        Assert.False(drag.TryStart(column, isLeftButton: true, isOnResizeGrip: false, KeyModifiers.Control));
        Assert.False(drag.TryStart(column, isLeftButton: true, isOnResizeGrip: false, KeyModifiers.Meta));
        Assert.False(drag.TryStart(column, isLeftButton: true, isOnResizeGrip: false, KeyModifiers.Shift));
        Assert.False(drag.TryStart(null, isLeftButton: true, isOnResizeGrip: false, KeyModifiers.None));
        Assert.False(drag.IsDragging);
        Assert.False(drag.TryMoveTo(Guid.NewGuid()));
    }

    [Fact]
    public void DragTrackerReportsOnlyChangesOfTheHeaderUnderThePointer()
    {
        var drag = new ColumnHeaderDragSelection();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.True(drag.TryStart(a, isLeftButton: true, isOnResizeGrip: false, KeyModifiers.None));
        Assert.Equal(a, drag.AnchorColumnId);
        Assert.False(drag.TryMoveTo(a));
        Assert.False(drag.TryMoveTo(null));
        Assert.True(drag.TryMoveTo(b));
        Assert.Equal(b, drag.CurrentColumnId);
        Assert.False(drag.TryMoveTo(b));

        drag.End();
        Assert.False(drag.IsDragging);
        Assert.Null(drag.AnchorColumnId);
        Assert.False(drag.TryMoveTo(a));

        // A new press always starts over, and a rejected press ends any drag still being tracked.
        Assert.True(drag.TryStart(b, isLeftButton: true, isOnResizeGrip: false, KeyModifiers.None));
        Assert.False(drag.TryStart(a, isLeftButton: false, isOnResizeGrip: false, KeyModifiers.None));
        Assert.False(drag.IsDragging);
    }

    // 7
    [Fact]
    public async Task CtrlShiftAndRightClickStillWorkAfterADrag()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Drag("B", "C", "D");

        runtime.ViewModel.ToggleColumnSelection(runtime.Id("G"));
        Assert.Equal(["B", "C", "D", "G"], runtime.Selected);
        Assert.Equal("G", runtime.Active);

        runtime.ViewModel.ExtendColumnSelection(runtime.Id("E"));
        Assert.Equal(["E", "F", "G"], runtime.Selected);
        Assert.Equal("G", runtime.Active);

        runtime.ViewModel.SelectColumnForContextMenu(runtime.Id("F"));
        Assert.Equal(["E", "F", "G"], runtime.Selected);

        runtime.ViewModel.SelectColumnForContextMenu(runtime.Id("A"));
        Assert.Equal(["A"], runtime.Selected);
    }

    // 8
    [Fact]
    public async Task DeleteAfterADragDeletesTheDraggedRange()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.Drag("F", "E", "D", "C");
        await runtime.ViewModel.DeleteSelectedColumnsCommand.ExecuteAsync(null);
        await runtime.ViewModel.GridLoadTask;

        Assert.Equal(["A", "B", "G", "H"], runtime.Headers);
        Assert.Equal([0, 1, 2, 3], runtime.ViewModel.GridColumns.Select(column => column.Index));
        Assert.Equal(["1", "2", "7", "8"], runtime.ViewModel.GridRows[0].Cells);
        Assert.Empty(runtime.Selected);
    }

    // 9
    [Fact]
    public async Task PasteStartsFromTheDragAnchor()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.Drag("G", "F", "E");
        await runtime.PasteAsync("X\n9\n");

        Assert.Equal(["A", "B", "C", "D", "E", "F", "X", "H"], runtime.Headers);
        Assert.Equal("X", runtime.Active);
    }

    [Fact]
    public async Task RangeWithAnUnknownColumnChangesNothing()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Drag("B", "C");

        runtime.ViewModel.SelectColumnRange(runtime.Id("B"), Guid.NewGuid());
        runtime.ViewModel.SelectColumnRange(Guid.NewGuid(), runtime.Id("E"));

        Assert.Equal(["B", "C"], runtime.Selected);
        Assert.Equal("B", runtime.Active);
    }
}
