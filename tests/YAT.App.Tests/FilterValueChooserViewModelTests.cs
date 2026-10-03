using YAT.Application.Abstractions.Persistence;
using YAT.Application.Filtering;
using YAT.app.ViewModels;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The Choose Values dialog of a value-set condition (Task #049's value list, a sub-dialog since #053): the column's
// values read asynchronously, typed values and Missing, Select All and Clear, the summary, a column with too many values
// refused - never offered as if they were all of them - and Apply giving the condition with the list it was chosen from.
public class FilterValueChooserViewModelTests
{
    private static readonly FilterColumnOption Reg = new(Guid.NewGuid(), "PS_RAW", WorksheetDataType.Numeric);
    private static readonly FilterColumnOption Site = new(Guid.NewGuid(), "Site", WorksheetDataType.Numeric);
    private static readonly FilterColumnOption Tester = new(Guid.NewGuid(), "Tester", WorksheetDataType.String);
    private static readonly FilterColumnOption Wide = new(Guid.NewGuid(), "Serial", WorksheetDataType.String);

    // Site 1..4 with Missing, Tester T01..T03 without, Serial more than can be offered, PS_RAW numbers that print alike.
    internal static FilterValues ValuesOf(Guid columnId)
    {
        if (columnId == Site.Id)
        {
            return new FilterValues(new NumericRawDistinctValues(columnId, [1, 2, 3, 4], hasMissing: true, hasMore: false));
        }

        if (columnId == Tester.Id)
        {
            return new FilterValues(new StringRawDistinctValues(columnId, ["T01", "T02", "T03"], hasMissing: false, hasMore: false));
        }

        if (columnId == Wide.Id)
        {
            return new FilterValues(new StringRawDistinctValues(
                columnId, [.. Enumerable.Range(0, ValueSetCondition.MaximumDistinctValues).Select(index => $"S{index}")], false, hasMore: true));
        }

        return new FilterValues(new NumericRawDistinctValues(columnId, [10.5, 0.1 + 0.2, 0.3], hasMissing: false, hasMore: false));
    }

    private static Task<FilterValues> Load(Guid columnId, CancellationToken token) => Task.FromResult(ValuesOf(columnId));

    private sealed class PendingLoader
    {
        public List<(Guid ColumnId, CancellationToken Token, TaskCompletionSource<FilterValues> Answer)> Requests { get; } = [];

        public Task<FilterValues> LoadAsync(Guid columnId, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<FilterValues>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((columnId, cancellationToken, answer));
            return answer.Task;
        }
    }

    private static FilterValueChooserViewModel Chooser(FilterColumnOption column, ValueSetCondition? current = null, bool exclude = false) =>
        new(column, current, exclude, Load);

    private static FilterValueChoice Apply(FilterValueChooserViewModel chooser)
    {
        Assert.True(chooser.TryApply(out var choice));
        return choice!;
    }

    private static void Select(FilterValueChooserViewModel chooser, params string[] labels)
    {
        foreach (var value in chooser.Values)
        {
            value.IsSelected = labels.Contains(value.Label);
        }
    }

    // ---- What is offered ----

    [Fact]
    public void ANewIsAnyOfStartsWithEveryValueAndMissingSelected()
    {
        var chooser = Chooser(Site);

        Assert.Equal("Choose Values - Site", chooser.Title);
        Assert.Equal(["1", "2", "3", "4"], chooser.Values.Select(value => value.Label));
        Assert.All(chooser.Values, value => Assert.True(value.IsSelected));
        Assert.True(chooser.HasMissingOption);
        Assert.True(chooser.IncludeMissing);
        Assert.Equal("Every value selected: this condition keeps every row.", chooser.Summary);
    }

    [Fact]
    public void ANewIsNotAnyOfStartsWithNothingSelected()
    {
        var chooser = Chooser(Site, exclude: true);

        Assert.All(chooser.Values, value => Assert.False(value.IsSelected));
        Assert.False(chooser.IncludeMissing);
        Assert.False(chooser.CanApply);
        Assert.Equal("Select at least one value, or (Missing).", chooser.Summary);
    }

    [Fact]
    public void AColumnWithoutMissingValuesOffersNoMissing()
    {
        var chooser = Chooser(Tester);

        Assert.False(chooser.HasMissingOption);
        Assert.False(chooser.IncludeMissing);
        Assert.Equal(["T01", "T02", "T03"], chooser.Values.Select(value => value.Label));
    }

    [Fact]
    public void ValuesThatPrintAlikeAreNotShownAlike()
    {
        var chooser = Chooser(Reg);

        Assert.Equal(["10.5", "0.30000000000000004", "0.3"], chooser.Values.Select(value => value.Label));
    }

    // ---- Applying ----

    [Fact]
    public void SomeNumbersSelectedAreANumericValueSetOfThoseValues()
    {
        var chooser = Chooser(Site);
        Select(chooser, "2", "4");
        chooser.IncludeMissing = false;

        var choice = Apply(chooser);

        Assert.Equal(new NumericValueSetCondition(Site.Id, [2, 4]), choice.Condition);
        Assert.Equal(Site.Id, choice.Available.ColumnId);
        Assert.Equal("2 of 4 values selected.", chooser.Summary);
    }

    [Fact]
    public void SomeTextSelectedIsATextValueSetAndExcludeIsKept()
    {
        var chooser = Chooser(Tester, exclude: true);
        Select(chooser, "T02");

        Assert.Equal(new TextValueSetCondition(Tester.Id, ["T02"], exclude: true), Apply(chooser).Condition);
    }

    [Fact]
    public void OnlyMissingIsAValueSetOfMissingRows()
    {
        var chooser = Chooser(Site);
        chooser.ClearCommand.Execute(null);
        chooser.IncludeMissing = true;

        Assert.Equal(new NumericValueSetCondition(Site.Id, [], includeMissing: true), Apply(chooser).Condition);
        Assert.Equal("0 of 4 values selected, and (Missing).", chooser.Summary);
    }

    [Fact]
    public void ClearSelectsNothingAndApplyIsUnavailable()
    {
        var chooser = Chooser(Site);

        chooser.ClearCommand.Execute(null);

        Assert.All(chooser.Values, value => Assert.False(value.IsSelected));
        Assert.False(chooser.IncludeMissing);
        Assert.False(chooser.CanApply);
        Assert.False(chooser.TryApply(out _));
    }

    [Fact]
    public void SelectAllSelectsEveryValueAndMissing()
    {
        var chooser = Chooser(Site, exclude: true);

        chooser.SelectAllCommand.Execute(null);

        Assert.All(chooser.Values, value => Assert.True(value.IsSelected));
        Assert.True(chooser.IncludeMissing);
        Assert.Equal("4 of 4 values selected, and (Missing).", chooser.Summary);
    }

    [Fact]
    public void EditingAConditionShowsItsValuesAndMissing()
    {
        var chooser = Chooser(Site, new NumericValueSetCondition(Site.Id, [3], includeMissing: true));

        Assert.Equal(["3"], chooser.Values.Where(value => value.IsSelected).Select(value => value.Label));
        Assert.True(chooser.IncludeMissing);
        Assert.Equal(new NumericValueSetCondition(Site.Id, [3], includeMissing: true), Apply(chooser).Condition);
    }

    // ---- A truncated list ----

    [Fact]
    public void AColumnWithTooManyValuesSaysSoAndCannotBeApplied()
    {
        var chooser = Chooser(Wide);

        Assert.True(chooser.HasTooManyValues);
        Assert.Empty(chooser.Values);
        Assert.False(chooser.CanApply);
        Assert.False(chooser.TryApply(out _));
        Assert.Equal(FilterValueChooserViewModel.TooManyValuesMessage, chooser.Message);
        Assert.Contains("more than 1,000 unique values", chooser.Message);
        Assert.False(chooser.SelectAllCommand.CanExecute(null));
    }

    // ---- Reading the values ----

    [Fact]
    public async Task WhileTheValuesAreReadNothingCanBeApplied()
    {
        var loader = new PendingLoader();
        var chooser = new FilterValueChooserViewModel(Site, null, false, loader.LoadAsync);

        Assert.True(chooser.IsLoading);
        Assert.False(chooser.CanApply);
        Assert.Equal("Reading the values of this column...", chooser.Summary);

        loader.Requests[0].Answer.SetResult(ValuesOf(Site.Id));
        await chooser.Loading;

        Assert.False(chooser.IsLoading);
        Assert.True(chooser.CanApply);
    }

    [Fact]
    public async Task AReadThatFailsSaysSo()
    {
        var chooser = new FilterValueChooserViewModel(Site, null, false, (_, _) => Task.FromException<FilterValues>(new InvalidOperationException("broken")));
        await chooser.Loading;

        Assert.Equal(FilterValueChooserViewModel.ReadFailedMessage, chooser.Message);
        Assert.False(chooser.CanApply);
    }

    [Fact]
    public async Task ClosingTheDialogCancelsTheRead()
    {
        var loader = new PendingLoader();
        var chooser = new FilterValueChooserViewModel(Site, null, false, loader.LoadAsync);

        chooser.Cancel();
        loader.Requests[0].Answer.SetResult(ValuesOf(Site.Id));
        await chooser.Loading;

        Assert.True(loader.Requests[0].Token.IsCancellationRequested);
        Assert.Empty(chooser.Values);
        Assert.Null(chooser.Message);
    }

    [Fact]
    public void AValueSetIsDescribedByItsFirstValuesAndMissing()
    {
        Assert.Equal("1, 2, 3, 4, +1 more, (Missing)", FilterValueChooserViewModel.Describe(new NumericValueSetCondition(Site.Id, [1, 2, 3, 4, 5], includeMissing: true)));
        Assert.Equal("\"\", T1", FilterValueChooserViewModel.Describe(new TextValueSetCondition(Tester.Id, ["", "T1"])));
    }
}
