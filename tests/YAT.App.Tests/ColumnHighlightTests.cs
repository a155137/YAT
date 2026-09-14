using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.app.Views;

namespace YAT.App.Tests;

// Selection highlighting of headers and visible cells, driven by the ViewModel selection through the same
// WorksheetGridLayout.ApplyColumnStates call the window makes. Rules only: no rendering.
public class ColumnHighlightTests
{
    private const string EightColumns =
        "A\tB\tC\tD\tE\tF\tG\tH\n" +
        "1\t2\t3\t4\t5\t6\t7\t8\n";

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Runtime : IDisposable
    {
        private readonly Dictionary<TableViewColumn, Guid> _gridColumns = [];
        private ColumnHeaderDragSelection? _drag;

        public Runtime()
        {
            var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
            ProjectSession = compositionRoot.CreateProjectSession(":memory:");
            ViewModel = compositionRoot.CreateMainWindowViewModel(compositionRoot.CreateMainWindowSession(ProjectSession, Clipboard, Clipboard));
        }

        public ControlTheme SelectedCellTheme { get; } = new(typeof(TableViewCell));

        public FakeClipboard Clipboard { get; } = new();

        public ProjectSession ProjectSession { get; }

        public MainWindowViewModel ViewModel { get; }

        public async Task StartAsync()
        {
            await ViewModel.CreateDefaultWorkspaceAsync();
            await ViewModel.GridLoadTask;
            Clipboard.Text = EightColumns;
            await ViewModel.PasteCommand.ExecuteAsync(null);

            // Grid columns as the window builds them: a header element per worksheet column.
            foreach (var column in ViewModel.GridColumns)
            {
                _gridColumns[new TableViewColumn { Header = WorksheetGridLayout.CreateHeader(column) }] = column.ColumnId;
            }
        }

        public Guid Id(string name) => ViewModel.GridColumns.Single(column => column.Name == name).ColumnId;

        // What the window does whenever ActiveColumn or SelectedColumns changes.
        public void Apply() =>
            WorksheetGridLayout.ApplyColumnStates(
                _gridColumns,
                ViewModel.ActiveColumn?.Id,
                ViewModel.SelectedColumns.Select(column => column.Id),
                SelectedCellTheme);

        public TableViewColumn Column(string name) => _gridColumns.Single(pair => pair.Value == Id(name)).Key;

        public ColumnHeaderState HeaderState(string name) => WorksheetGridLayout.GetHeaderState((Border)Column(name).Header!);

        public bool CellsHighlighted(string name) => ReferenceEquals(Column(name).CellTheme, SelectedCellTheme);

        // "A:active B:selected C:-" with a trailing "*" when the column's cells are highlighted.
        public string Visual() => string.Join(" ", ViewModel.GridColumns.Select(column =>
            $"{column.Name}:{HeaderState(column.Name) switch { ColumnHeaderState.Active => "active", ColumnHeaderState.Selected => "selected", _ => "-" }}" +
            (CellsHighlighted(column.Name) ? "*" : string.Empty)));

        public void BeginDrag(string start)
        {
            _drag = new ColumnHeaderDragSelection();
            Assert.True(_drag.TryStart(Id(start), isLeftButton: true, isOnResizeGrip: false, KeyModifiers.None));
            ViewModel.SelectColumn(Id(start));
            Apply();
        }

        public void DragOver(string name)
        {
            if (_drag!.TryMoveTo(Id(name)))
            {
                ViewModel.SelectColumnRange(_drag.AnchorColumnId!.Value, _drag.CurrentColumnId!.Value);
            }

            Apply();
        }

        public void Dispose() => ProjectSession.Dispose();
    }

    // 1
    [Fact]
    public async Task SelectedColumnsHighlightTheirHeadersAndCells()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.ViewModel.SelectColumn(runtime.Id("B"));
        runtime.ViewModel.ToggleColumnSelection(runtime.Id("D"));
        runtime.ViewModel.ToggleColumnSelection(runtime.Id("E"));
        runtime.Apply();

        Assert.Equal("A:- B:selected* C:- D:selected* E:active* F:- G:- H:-", runtime.Visual());
    }

    // 2
    [Fact]
    public async Task TheActiveColumnStaysDistinctWhileSharingTheSelectedCellHighlight()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.ViewModel.SelectColumn(runtime.Id("C"));
        runtime.ViewModel.ExtendColumnSelection(runtime.Id("E"));
        runtime.Apply();

        Assert.Equal(ColumnHeaderState.Active, runtime.HeaderState("C"));
        Assert.Equal(ColumnHeaderState.Selected, runtime.HeaderState("D"));
        Assert.True(runtime.CellsHighlighted("C"));
        Assert.True(runtime.CellsHighlighted("D"));
        Assert.Contains(WorksheetGridLayout.ActiveClass, ((Border)runtime.Column("C").Header!).Classes);
        Assert.DoesNotContain(WorksheetGridLayout.ActiveClass, ((Border)runtime.Column("D").Header!).Classes);
    }

    // 3
    [Fact]
    public async Task DragGrowthHighlightsEachNewlySelectedColumn()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.BeginDrag("B");
        Assert.Equal("A:- B:active* C:- D:- E:- F:- G:- H:-", runtime.Visual());

        runtime.DragOver("C");
        Assert.Equal("A:- B:active* C:selected* D:- E:- F:- G:- H:-", runtime.Visual());

        runtime.DragOver("E");
        Assert.Equal("A:- B:active* C:selected* D:selected* E:selected* F:- G:- H:-", runtime.Visual());
    }

    // 4
    [Fact]
    public async Task DragShrinkClearsTheHighlightOfColumnsLeavingTheRange()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.BeginDrag("B");
        runtime.DragOver("F");
        Assert.Equal("A:- B:active* C:selected* D:selected* E:selected* F:selected* G:- H:-", runtime.Visual());

        runtime.DragOver("C");
        Assert.Equal("A:- B:active* C:selected* D:- E:- F:- G:- H:-", runtime.Visual());

        // Crossing back over the anchor: the range flips to the other side and C loses its highlight.
        runtime.DragOver("A");
        Assert.Equal("A:selected* B:active* C:- D:- E:- F:- G:- H:-", runtime.Visual());
        Assert.Null(runtime.Column("C").CellTheme);
    }

    // 5
    [Fact]
    public async Task CtrlShiftAndRightClickProduceTheSameVisualStates()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.ViewModel.SelectColumn(runtime.Id("A"));
        runtime.ViewModel.ToggleColumnSelection(runtime.Id("C"));
        runtime.Apply();
        Assert.Equal("A:selected* B:- C:active* D:- E:- F:- G:- H:-", runtime.Visual());

        runtime.ViewModel.ToggleColumnSelection(runtime.Id("C"));
        runtime.Apply();
        Assert.Equal("A:active* B:- C:- D:- E:- F:- G:- H:-", runtime.Visual());

        runtime.ViewModel.ExtendColumnSelection(runtime.Id("D"));
        runtime.Apply();
        Assert.Equal("A:active* B:selected* C:selected* D:selected* E:- F:- G:- H:-", runtime.Visual());

        runtime.ViewModel.SelectColumnForContextMenu(runtime.Id("C"));
        runtime.Apply();
        Assert.Equal("A:active* B:selected* C:selected* D:selected* E:- F:- G:- H:-", runtime.Visual());

        runtime.ViewModel.SelectColumnForContextMenu(runtime.Id("G"));
        runtime.Apply();
        Assert.Equal("A:- B:- C:- D:- E:- F:- G:active* H:-", runtime.Visual());
    }

    [Fact]
    public async Task ClearingTheSelectionReturnsEveryColumnToNormal()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.ViewModel.SelectColumn(runtime.Id("A"));
        runtime.ViewModel.ExtendColumnSelection(runtime.Id("H"));
        runtime.Apply();

        runtime.ViewModel.ClearColumnSelection();
        runtime.Apply();

        Assert.Equal("A:- B:- C:- D:- E:- F:- G:- H:-", runtime.Visual());
    }

    [Fact]
    public void NormalColumnsUseTheDefaultCellThemeAndSelectedOnesTheSelectedTheme()
    {
        var theme = new ControlTheme(typeof(TableViewCell));
        var column = new TableViewColumn { Header = WorksheetGridLayout.CreateHeader(null) };

        WorksheetGridLayout.ApplyColumnState(column, ColumnHeaderState.Selected, theme);
        Assert.Same(theme, column.CellTheme);

        WorksheetGridLayout.ApplyColumnState(column, ColumnHeaderState.Active, theme);
        Assert.Same(theme, column.CellTheme);

        WorksheetGridLayout.ApplyColumnState(column, ColumnHeaderState.Normal, theme);
        Assert.Null(column.CellTheme);
    }
}
