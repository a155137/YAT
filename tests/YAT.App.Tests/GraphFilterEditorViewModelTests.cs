using YAT.Application.Abstractions.Persistence;
using YAT.Application.Graphs;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The Filter dialog's editor (Task #049): the columns a filter can use, their values read asynchronously (and a read
// for a column the user left ignored), typed values and Missing, Select All and Clear, the summary, a column with too
// many values refused, and Apply giving the canonical filter - none for every row.
public class GraphFilterEditorViewModelTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly WorksheetColumn Reg = Column("PS_RAW", WorksheetDataType.Numeric, 0);
    private static readonly WorksheetColumn Site = Column("Site", WorksheetDataType.Numeric, 1);
    private static readonly WorksheetColumn Tester = Column("Tester", WorksheetDataType.String, 2);
    private static readonly WorksheetColumn Tested = Column("Tested", WorksheetDataType.DateTime, 3);
    private static readonly WorksheetColumn Wide = Column("Serial", WorksheetDataType.String, 4);

    private static readonly IReadOnlyList<WorksheetColumn> Columns = [Reg, Site, Tester, Tested, Wide];

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, int index) =>
        new() { Id = Guid.NewGuid(), WorksheetId = WorksheetId, Index = index, Name = name, DataType = dataType };

    // The values each column lists: Site 1..4 with Missing, Tester T01..T03 without, Serial more than can be offered.
    private static GraphFilterValues ValuesOf(Guid columnId)
    {
        if (columnId == Site.Id)
        {
            return new GraphFilterValues(new NumericRawDistinctValues(columnId, [1, 2, 3, 4], hasMissing: true, hasMore: false));
        }

        if (columnId == Tester.Id)
        {
            return new GraphFilterValues(new StringRawDistinctValues(columnId, ["T01", "T02", "T03"], hasMissing: false, hasMore: false));
        }

        if (columnId == Wide.Id)
        {
            return new GraphFilterValues(new StringRawDistinctValues(
                columnId, [.. Enumerable.Range(0, GraphValueFilter.MaximumDistinctValues).Select(index => $"S{index}")], false, hasMore: true));
        }

        return new GraphFilterValues(new NumericRawDistinctValues(columnId, [10.5, 0.1 + 0.2, 0.3], hasMissing: false, hasMore: false));
    }

    // A loader that answers at once.
    private sealed class Loader
    {
        public List<Guid> Requests { get; } = [];

        public Task<GraphFilterValues> LoadAsync(Guid columnId, CancellationToken cancellationToken)
        {
            Requests.Add(columnId);
            return Task.FromResult(ValuesOf(columnId));
        }
    }

    // A loader that answers when the test says so.
    private sealed class PendingLoader
    {
        public List<(Guid ColumnId, CancellationToken Token, TaskCompletionSource<GraphFilterValues> Answer)> Requests { get; } = [];

        public Task<GraphFilterValues> LoadAsync(Guid columnId, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<GraphFilterValues>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((columnId, cancellationToken, answer));
            return answer.Task;
        }
    }

    private static GraphFilterEditorViewModel Editor(GraphValueFilter? current = null, Guid? group = null, Loader? loader = null) =>
        new(Columns, current, group, (loader ?? new Loader()).LoadAsync);

    private static GraphColumnOption Option(GraphFilterEditorViewModel editor, WorksheetColumn column) =>
        editor.Columns.Single(option => option.WorksheetColumnId == column.Id);

    private static GraphValueFilter? Apply(GraphFilterEditorViewModel editor)
    {
        Assert.True(editor.TryApply(out var edit));
        return Assert.IsType<GraphFilterEdit>(edit).Filter;
    }

    private static void Select(GraphFilterEditorViewModel editor, params string[] labels)
    {
        foreach (var value in editor.Values)
        {
            value.IsSelected = labels.Contains(value.Label);
        }
    }

    // ---- Columns ----

    [Fact]
    public void EveryNumericAndStringColumnCanFilterWhateverItsRole()
    {
        var editor = Editor();

        Assert.Equal(["PS_RAW", "Site", "Tester", "Serial"], editor.Columns.Select(column => column.Name));
    }

    [Fact]
    public void WithoutAFilterOrAGroupNoColumnIsChosenAndNothingCanBeApplied()
    {
        var loader = new Loader();
        var editor = Editor(loader: loader);

        Assert.Null(editor.SelectedColumn);
        Assert.Empty(editor.Values);
        Assert.False(editor.CanApply);
        Assert.False(editor.TryApply(out _));
        Assert.Equal("Choose the column to select rows by.", editor.Summary);
        Assert.Empty(loader.Requests);
    }

    [Fact]
    public void TheGroupColumnIsChosenFirst()
    {
        var editor = Editor(group: Site.Id);

        Assert.Equal(Site.Id, editor.SelectedColumn!.WorksheetColumnId);
        Assert.Equal(["1", "2", "3", "4"], editor.Values.Select(value => value.Label));
    }

    [Fact]
    public void AGroupColumnThatCannotFilterIsNotChosen()
    {
        Assert.Null(Editor(group: Tested.Id).SelectedColumn);
    }

    [Fact]
    public void AFiltersOwnColumnIsChosenOverTheGroup()
    {
        var editor = Editor(new TextValueFilter(Tester.Id, ["T02"]), group: Site.Id);

        Assert.Equal(Tester.Id, editor.SelectedColumn!.WorksheetColumnId);
    }

    // ---- A new column ----

    [Fact]
    public void ANewColumnStartsWithEveryValueAndMissingSelected()
    {
        var editor = Editor(group: Site.Id);

        Assert.All(editor.Values, value => Assert.True(value.IsSelected));
        Assert.True(editor.HasMissingOption);
        Assert.True(editor.IncludeMissing);
        Assert.True(editor.CanApply);
        Assert.Equal("Every value selected: all rows.", editor.Summary);
    }

    [Fact]
    public void EverythingSelectedIsEveryRow()
    {
        var editor = Editor(group: Site.Id);

        Assert.Null(Apply(editor));
    }

    [Fact]
    public void AColumnWithoutMissingValuesOffersNoMissing()
    {
        var editor = Editor(group: Tester.Id);

        Assert.False(editor.HasMissingOption);
        Assert.False(editor.IncludeMissing);
        Assert.Null(Apply(editor));
    }

    [Fact]
    public void ChangingTheColumnReadsItsValues()
    {
        var loader = new Loader();
        var editor = Editor(group: Site.Id, loader: loader);

        editor.SelectedColumn = Option(editor, Tester);

        Assert.Equal([Site.Id, Tester.Id], loader.Requests);
        Assert.Equal(["T01", "T02", "T03"], editor.Values.Select(value => value.Label));
        Assert.False(editor.HasMissingOption);
    }

    // ---- Selecting ----

    [Fact]
    public void SomeNumbersSelectedAreANumericFilterOfThoseValues()
    {
        var editor = Editor(group: Site.Id);
        Select(editor, "1", "3");
        editor.IncludeMissing = false;

        var filter = Assert.IsType<NumericValueFilter>(Apply(editor));

        Assert.Equal(Site.Id, filter.ColumnId);
        Assert.Equal([1, 3], filter.Values);
        Assert.False(filter.IncludeMissing);
        Assert.Equal("2 of 4 values selected.", editor.Summary);
    }

    [Fact]
    public void SomeTextSelectedIsATextFilterOfThoseValues()
    {
        var editor = Editor(group: Tester.Id);
        Select(editor, "T03");

        var filter = Assert.IsType<TextValueFilter>(Apply(editor));

        Assert.Equal(["T03"], filter.Values);
    }

    [Fact]
    public void AllValuesButNotMissingKeepsAFilter()
    {
        var editor = Editor(group: Site.Id);
        editor.IncludeMissing = false;

        var filter = Assert.IsType<NumericValueFilter>(Apply(editor));

        Assert.Equal([1, 2, 3, 4], filter.Values);
        Assert.False(filter.IncludeMissing);
        Assert.Equal("4 of 4 values selected.", editor.Summary);
    }

    [Fact]
    public void OnlyMissingIsAFilterOfMissingRows()
    {
        var editor = Editor(group: Site.Id);
        editor.ClearCommand.Execute(null);
        editor.IncludeMissing = true;

        var filter = Assert.IsType<NumericValueFilter>(Apply(editor));

        Assert.Empty(filter.Values);
        Assert.True(filter.IncludeMissing);
        Assert.Equal("0 of 4 values selected, and (Missing).", editor.Summary);
    }

    [Fact]
    public void ValuesThatPrintAlikeAreNotShownAlike()
    {
        var editor = Editor(group: Reg.Id);

        Assert.Equal(["10.5", "0.30000000000000004", "0.3"], editor.Values.Select(value => value.Label));
        Select(editor, "0.3");

        Assert.Equal([0.3], Assert.IsType<NumericValueFilter>(Apply(editor)).Values);
    }

    // ---- Select All and Clear ----

    [Fact]
    public void ClearSelectsNothingAndApplyIsUnavailable()
    {
        var editor = Editor(group: Site.Id);

        editor.ClearCommand.Execute(null);

        Assert.All(editor.Values, value => Assert.False(value.IsSelected));
        Assert.False(editor.IncludeMissing);
        Assert.False(editor.CanApply);
        Assert.False(editor.TryApply(out var edit));
        Assert.Null(edit);
        Assert.Equal("Select at least one value, or (Missing).", editor.Summary);
    }

    [Fact]
    public void SelectAllSelectsEveryValueAndMissing()
    {
        var editor = Editor(group: Site.Id);
        editor.ClearCommand.Execute(null);

        editor.SelectAllCommand.Execute(null);

        Assert.All(editor.Values, value => Assert.True(value.IsSelected));
        Assert.True(editor.IncludeMissing);
        Assert.True(editor.CanApply);
        Assert.Null(Apply(editor));
    }

    [Fact]
    public void OneValueSelectedAfterClearCanBeApplied()
    {
        var editor = Editor(group: Site.Id);
        editor.ClearCommand.Execute(null);

        editor.Values[1].IsSelected = true;

        Assert.True(editor.CanApply);
        Assert.Equal([2], Assert.IsType<NumericValueFilter>(Apply(editor)).Values);
    }

    [Fact]
    public void SelectAllAndClearNeedSomethingToSelect()
    {
        var editor = Editor();

        Assert.False(editor.SelectAllCommand.CanExecute(null));
        Assert.False(editor.ClearCommand.CanExecute(null));

        editor.SelectedColumn = Option(editor, Site);

        Assert.True(editor.SelectAllCommand.CanExecute(null));
        Assert.True(editor.ClearCommand.CanExecute(null));
    }

    // ---- Editing a filter ----

    [Fact]
    public void EditingAFilterShowsItsValuesAndMissing()
    {
        var editor = Editor(new NumericValueFilter(Site.Id, [4, 2], includeMissing: true), group: Tester.Id);

        Assert.Equal(Site.Id, editor.SelectedColumn!.WorksheetColumnId);
        Assert.Equal([false, true, false, true], editor.Values.Select(value => value.IsSelected));
        Assert.True(editor.IncludeMissing);
        Assert.Equal("2 of 4 values selected, and (Missing).", editor.Summary);
    }

    [Fact]
    public void EditingAFilterWithoutMissingLeavesMissingUnselected()
    {
        var editor = Editor(new TextValueFilter(Tester.Id, ["T01"]));

        Assert.Equal([true, false, false], editor.Values.Select(value => value.IsSelected));
        Assert.False(editor.IncludeMissing);
    }

    [Fact]
    public void ApplyingAnUnchangedFilterGivesTheSameFilter()
    {
        var filter = new NumericValueFilter(Site.Id, [2, 4], includeMissing: true);

        Assert.Equal(filter, Apply(Editor(filter)));
    }

    [Fact]
    public void LeavingAndComingBackToTheFiltersColumnStartsAfresh()
    {
        var editor = Editor(new NumericValueFilter(Site.Id, [2]));

        editor.SelectedColumn = Option(editor, Tester);
        editor.SelectedColumn = Option(editor, Site);

        Assert.All(editor.Values, value => Assert.True(value.IsSelected));
        Assert.True(editor.IncludeMissing);
    }

    [Fact]
    public void AFilterOfAColumnThatIsGoneStartsFromTheGroup()
    {
        var editor = Editor(new NumericValueFilter(Guid.NewGuid(), [1]), group: Tester.Id);

        Assert.Equal(Tester.Id, editor.SelectedColumn!.WorksheetColumnId);
        Assert.All(editor.Values, value => Assert.True(value.IsSelected));
    }

    // ---- Too many values ----

    [Fact]
    public void AColumnWithTooManyValuesSaysSoAndCannotBeApplied()
    {
        var editor = Editor(group: Wide.Id);

        Assert.True(editor.HasTooManyValues);
        Assert.Empty(editor.Values);
        Assert.False(editor.HasMissingOption);
        Assert.False(editor.CanApply);
        Assert.False(editor.TryApply(out _));
        Assert.Equal(GraphFilterEditorViewModel.TooManyValuesMessage, editor.Message);
        Assert.Equal(GraphFilterEditorViewModel.TooManyValuesMessage, editor.Summary);
        Assert.Contains("more than 1,000 unique values", editor.Message);
        Assert.False(editor.SelectAllCommand.CanExecute(null));
    }

    [Fact]
    public void AnotherColumnAfterOneWithTooManyValuesCanBeApplied()
    {
        var editor = Editor(group: Wide.Id);

        editor.SelectedColumn = Option(editor, Tester);

        Assert.False(editor.HasTooManyValues);
        Assert.Null(editor.Message);
        Assert.True(editor.CanApply);
    }

    // ---- Reading the values ----

    [Fact]
    public async Task WhileTheValuesAreReadNothingCanBeApplied()
    {
        var loader = new PendingLoader();
        var editor = new GraphFilterEditorViewModel(Columns, null, Site.Id, loader.LoadAsync);

        Assert.True(editor.IsLoading);
        Assert.False(editor.CanApply);
        Assert.Equal("Reading the values of this column...", editor.Summary);

        loader.Requests[0].Answer.SetResult(ValuesOf(Site.Id));
        await editor.Loading;

        Assert.False(editor.IsLoading);
        Assert.Equal(4, editor.Values.Count);
        Assert.True(editor.CanApply);
    }

    [Fact]
    public async Task AReadForAColumnTheUserLeftIsCancelledAndIgnored()
    {
        var loader = new PendingLoader();
        var editor = new GraphFilterEditorViewModel(Columns, null, Site.Id, loader.LoadAsync);
        var first = editor.Loading;

        editor.SelectedColumn = Option(editor, Tester);

        Assert.True(loader.Requests[0].Token.IsCancellationRequested);
        Assert.False(loader.Requests[1].Token.IsCancellationRequested);

        // The newer read answers first, then the stale one: only the newer one is shown.
        loader.Requests[1].Answer.SetResult(ValuesOf(Tester.Id));
        await editor.Loading;
        loader.Requests[0].Answer.SetResult(ValuesOf(Site.Id));
        await first;

        Assert.Equal(["T01", "T02", "T03"], editor.Values.Select(value => value.Label));
        Assert.Equal(Tester.Id, Assert.IsType<TextValueFilter>(Apply(Select3(editor))).ColumnId);
    }

    private static GraphFilterEditorViewModel Select3(GraphFilterEditorViewModel editor)
    {
        Select(editor, "T03");
        return editor;
    }

    [Fact]
    public async Task AStaleReadThatIsCancelledChangesNothing()
    {
        var loader = new PendingLoader();
        var editor = new GraphFilterEditorViewModel(Columns, null, Site.Id, loader.LoadAsync);
        var first = editor.Loading;

        editor.SelectedColumn = Option(editor, Tester);
        loader.Requests[0].Answer.SetCanceled(loader.Requests[0].Token);
        await first;
        loader.Requests[1].Answer.SetResult(ValuesOf(Tester.Id));
        await editor.Loading;

        Assert.Null(editor.Message);
        Assert.Equal(3, editor.Values.Count);
    }

    [Fact]
    public async Task ClosingTheDialogCancelsTheRead()
    {
        var loader = new PendingLoader();
        var editor = new GraphFilterEditorViewModel(Columns, null, Site.Id, loader.LoadAsync);

        editor.Cancel();

        Assert.True(loader.Requests[0].Token.IsCancellationRequested);
        loader.Requests[0].Answer.SetResult(ValuesOf(Site.Id));
        await editor.Loading;
        Assert.Empty(editor.Values);
        Assert.False(editor.CanApply);
    }

    [Fact]
    public async Task ValuesThatCannotBeReadAreSaidAndNothingCanBeApplied()
    {
        var loader = new PendingLoader();
        var editor = new GraphFilterEditorViewModel(Columns, null, Site.Id, loader.LoadAsync);

        loader.Requests[0].Answer.SetException(new InvalidOperationException("storage"));
        await editor.Loading;

        Assert.False(editor.IsLoading);
        Assert.Equal(GraphFilterEditorViewModel.ReadFailedMessage, editor.Message);
        Assert.False(editor.CanApply);
        Assert.Empty(editor.Values);
    }

    [Fact]
    public async Task AFailedReadForAColumnTheUserLeftIsNotSaid()
    {
        var loader = new PendingLoader();
        var editor = new GraphFilterEditorViewModel(Columns, null, Site.Id, loader.LoadAsync);
        var first = editor.Loading;

        editor.SelectedColumn = Option(editor, Tester);
        loader.Requests[0].Answer.SetException(new InvalidOperationException("storage"));
        await first;

        Assert.Null(editor.Message);
        Assert.True(editor.IsLoading);
    }

    // ---- The setup's summary ----

    [Fact]
    public void NoFilterIsAllRows()
    {
        Assert.Equal("All rows", GraphFilterEditorViewModel.Describe(null, null));
    }

    [Fact]
    public void AFilterIsItsColumnAndItsValues()
    {
        Assert.Equal("Site: 1, 3, 5, 7", GraphFilterEditorViewModel.Describe(new NumericValueFilter(Site.Id, [1, 3, 5, 7]), "Site"));
        Assert.Equal("Tester: T02, (Missing)", GraphFilterEditorViewModel.Describe(new TextValueFilter(Tester.Id, ["T02"], true), "Tester"));
        Assert.Equal("Lot: (Missing)", GraphFilterEditorViewModel.Describe(new TextValueFilter(Tester.Id, [], true), "Lot"));
        Assert.Equal("Lot: \"\"", GraphFilterEditorViewModel.Describe(new TextValueFilter(Tester.Id, [""]), "Lot"));
    }

    [Fact]
    public void AFilterOfManyValuesShowsTheFirstFew()
    {
        Assert.Equal(
            "Site: 1, 2, 3, 4, +3 more, (Missing)",
            GraphFilterEditorViewModel.Describe(new NumericValueFilter(Site.Id, [1, 2, 3, 4, 5, 6, 7], true), "Site"));
    }

    [Fact]
    public void AFilterOfAColumnThatIsGoneSaysSo()
    {
        Assert.Equal("(column not available): 1", GraphFilterEditorViewModel.Describe(new NumericValueFilter(Site.Id, [1]), null));
    }
}
