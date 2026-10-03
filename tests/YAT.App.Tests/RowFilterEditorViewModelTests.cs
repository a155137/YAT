using YAT.Application.Abstractions.Persistence;
using YAT.Application.Filtering;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The Filter dialog (Task #053): conditions one per row - column, the operators its type offers, the value(s) - each
// saying what is wrong with it; Add condition up to twenty, Remove, Clear; Apply only while every condition can be
// applied, giving the filter of the conditions as entered (none without any); and a filter shown again for editing.
public class RowFilterEditorViewModelTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly WorksheetColumn Site = Column("Site", WorksheetDataType.Numeric, 0);
    private static readonly WorksheetColumn Current = Column("Current", WorksheetDataType.Numeric, 1);
    private static readonly WorksheetColumn Tester = Column("Tester", WorksheetDataType.String, 2);
    private static readonly WorksheetColumn Tested = Column("Tested", WorksheetDataType.DateTime, 3);
    private static readonly WorksheetColumn Serial = Column("Serial", WorksheetDataType.String, 4);

    private static readonly IReadOnlyList<WorksheetColumn> Columns = [Site, Current, Tester, Tested, Serial];

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, int index) =>
        new() { Id = Guid.NewGuid(), WorksheetId = WorksheetId, Index = index, Name = name, DataType = dataType };

    private static Task<FilterValues> Load(Guid columnId, CancellationToken token) =>
        Task.FromResult(columnId == Serial.Id
            ? new FilterValues(new StringRawDistinctValues(columnId, [.. Enumerable.Range(0, 1000).Select(index => $"S{index}")], false, hasMore: true))
            : columnId == Tester.Id
                ? new FilterValues(new StringRawDistinctValues(columnId, ["T1", "T2"], false, false))
                : new FilterValues(new NumericRawDistinctValues(columnId, [1, 3, 5, 7], true, false)));

    private static RowFilterEditorViewModel Editor(RowFilter? current = null, Guid? suggested = null) => new(Columns, current, Load, suggested);

    private static RowFilterConditionViewModel Add(RowFilterEditorViewModel editor, WorksheetColumn column, RowFilterOperator op)
    {
        editor.AddConditionCommand.Execute(null);
        var condition = editor.Conditions[^1];
        condition.SelectedColumn = condition.Columns.Single(option => option.Id == column.Id);
        condition.SelectedOperator = condition.Operators.Single(option => option.Operator == op);
        return condition;
    }

    private static async Task Choose(RowFilterConditionViewModel condition, params string[] labels)
    {
        var chooser = condition.CreateValueChooser()!;
        await chooser.Loading;
        chooser.ClearCommand.Execute(null);
        foreach (var value in chooser.Values.Where(value => labels.Contains(value.Label)))
        {
            value.IsSelected = true;
        }

        chooser.IncludeMissing = labels.Contains(FilterValueChooserViewModel.MissingLabel);
        Assert.True(chooser.TryApply(out var choice));
        condition.ApplyValues(choice!);
    }

    private static RowFilter? Apply(RowFilterEditorViewModel editor)
    {
        Assert.True(editor.TryApply(out var edit));
        return edit!.Filter;
    }

    // ---- What is offered ----

    [Fact]
    public void NumericAndStringColumnsAreOfferedWithTheOperatorsOfTheirType()
    {
        var editor = Editor();
        editor.AddConditionCommand.Execute(null);
        var condition = Assert.Single(editor.Conditions);

        Assert.Equal(["Site", "Current", "Tester", "Serial"], editor.Columns.Select(column => column.Name));
        Assert.Equal(["is any of", "is not any of", "=", "!=", "<", "<=", ">", ">=", "between"], condition.Operators.Select(option => option.Label));
        condition.SelectedColumn = condition.Columns.Single(option => option.Id == Tester.Id);
        Assert.Equal(["is any of", "is not any of", "is", "is not"], condition.Operators.Select(option => option.Label));
    }

    [Fact]
    public void ANewConditionStartsOnTheSuggestedColumnWithIsAnyOf()
    {
        var editor = Editor(suggested: Tester.Id);
        editor.AddConditionCommand.Execute(null);

        var condition = Assert.Single(editor.Conditions);
        Assert.Equal(Tester.Id, condition.SelectedColumn!.Id);
        Assert.Equal(RowFilterOperator.IsAnyOf, condition.SelectedOperator!.Operator);
        Assert.Equal("Choose the values.", condition.Error);
        Assert.False(editor.CanApply);
    }

    [Fact]
    public void WithoutConditionsApplyIsEveryRow()
    {
        var editor = Editor();

        Assert.True(editor.CanApply);
        Assert.Equal("No conditions: all rows are used.", editor.Summary);
        Assert.Null(Apply(editor));
    }

    // ---- Each operator ----

    [Theory]
    [InlineData(RowFilterOperator.Equal, NumericComparison.Equal)]
    [InlineData(RowFilterOperator.NotEqual, NumericComparison.NotEqual)]
    [InlineData(RowFilterOperator.Less, NumericComparison.Less)]
    [InlineData(RowFilterOperator.LessOrEqual, NumericComparison.LessOrEqual)]
    [InlineData(RowFilterOperator.Greater, NumericComparison.Greater)]
    [InlineData(RowFilterOperator.GreaterOrEqual, NumericComparison.GreaterOrEqual)]
    public void AComparisonTakesANumber(RowFilterOperator op, NumericComparison comparison)
    {
        var editor = Editor();
        var condition = Add(editor, Current, op);

        Assert.True(condition.ShowsValue);
        Assert.False(condition.ShowsUpper);
        Assert.Equal("Enter a number.", condition.Error);
        condition.ValueText = "14.5";

        Assert.Null(condition.Error);
        Assert.Equal(new RowFilter(new NumericComparisonCondition(Current.Id, comparison, 14.5)), Apply(editor));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,000")]
    [InlineData("")]
    public void AValueThatIsNotAFiniteNumberIsRefused(string text)
    {
        var editor = Editor();
        var condition = Add(editor, Current, RowFilterOperator.GreaterOrEqual);
        condition.ValueText = text;

        Assert.Equal("Enter a number.", condition.Error);
        Assert.False(editor.CanApply);
        Assert.False(editor.TryApply(out _));
    }

    [Fact]
    public void BetweenTakesTwoNumbersTheLowerNotAboveTheUpper()
    {
        var editor = Editor();
        var condition = Add(editor, Current, RowFilterOperator.Between);

        Assert.True(condition.ShowsUpper);
        Assert.Equal("Enter two numbers.", condition.Error);
        condition.ValueText = "15.5";
        condition.UpperText = "14.5";
        Assert.Equal("The lower number must not be above the upper number.", condition.Error);
        Assert.False(editor.CanApply);

        condition.UpperText = "15.5";
        Assert.Null(condition.Error);
        Assert.Equal(new RowFilter(new NumericBetweenCondition(Current.Id, 15.5, 15.5)), Apply(editor));
    }

    [Fact]
    public void IsAndIsNotTakeExactText()
    {
        var editor = Editor();
        var condition = Add(editor, Tester, RowFilterOperator.IsNot);

        Assert.Equal("Enter the text to compare with.", condition.Error);
        condition.ValueText = "   ";
        Assert.Equal("Enter the text to compare with.", condition.Error);
        condition.ValueText = " T1";

        Assert.Equal(new RowFilter(new TextComparisonCondition(Tester.Id, " T1", negated: true)), Apply(editor));
    }

    [Fact]
    public async Task IsAnyOfAndIsNotAnyOfTakeTheValuesChosen()
    {
        var editor = Editor();
        var sites = Add(editor, Site, RowFilterOperator.IsAnyOf);
        await Choose(sites, "1", "3", "5", "7");
        var notBin = Add(editor, Site, RowFilterOperator.IsNotAnyOf);
        await Choose(notBin, "3", FilterValueChooserViewModel.MissingLabel);

        Assert.Equal("1, 3, 5, 7", sites.ValuesSummary);
        Assert.Equal("3, (Missing)", notBin.ValuesSummary);
        Assert.Equal(
            new RowFilter(
            [
                new NumericValueSetCondition(Site.Id, [1, 3, 5, 7]),
                new NumericValueSetCondition(Site.Id, [3], includeMissing: true, exclude: true)
            ]),
            Apply(editor));
    }

    [Fact]
    public async Task SwitchingBetweenIsAnyOfAndIsNotAnyOfKeepsTheValues()
    {
        var editor = Editor();
        var condition = Add(editor, Site, RowFilterOperator.IsAnyOf);
        await Choose(condition, "3");

        condition.SelectedOperator = condition.Operators.Single(option => option.Operator == RowFilterOperator.IsNotAnyOf);

        Assert.Equal(new RowFilter(new NumericValueSetCondition(Site.Id, [3], exclude: true)), Apply(editor));
    }

    [Fact]
    public async Task ChangingTheColumnStartsTheValuesAfresh()
    {
        var editor = Editor();
        var condition = Add(editor, Site, RowFilterOperator.IsAnyOf);
        await Choose(condition, "3");

        condition.SelectedColumn = condition.Columns.Single(option => option.Id == Current.Id);

        Assert.Equal("Choose the values.", condition.Error);
        Assert.Equal("No values chosen", condition.ValuesSummary);
    }

    // ---- Every value selected ----

    [Fact]
    public async Task EveryValueAndMissingOfACompleteListIsNoCondition()
    {
        var editor = Editor();
        var all = Add(editor, Site, RowFilterOperator.IsAnyOf);
        await Choose(all, "1", "3", "5", "7", FilterValueChooserViewModel.MissingLabel);
        var current = Add(editor, Current, RowFilterOperator.Greater);
        current.ValueText = "1";

        Assert.Equal(new RowFilter(new NumericComparisonCondition(Current.Id, NumericComparison.Greater, 1)), Apply(editor));
    }

    [Fact]
    public void EveryValueShownOfATruncatedListIsKept()
    {
        var editor = Editor();
        var condition = Add(editor, Serial, RowFilterOperator.IsAnyOf);
        var shown = Enumerable.Range(0, 1000).Select(index => $"S{index}").ToArray();
        var truncated = new FilterValues(new StringRawDistinctValues(Serial.Id, shown, false, hasMore: true));

        // The chooser never offers a truncated list; a choice made from one is still never "every row".
        condition.ApplyValues(new FilterValueChoice(new TextValueSetCondition(Serial.Id, shown), truncated));

        Assert.Equal(new RowFilter(new TextValueSetCondition(Serial.Id, shown)), Apply(editor));
    }

    // ---- Adding, removing, clearing ----

    [Fact]
    public void AtMostTwentyConditions()
    {
        var editor = Editor();
        for (var index = 0; index < RowFilter.MaximumConditions; index++)
        {
            Add(editor, Current, RowFilterOperator.Greater).ValueText = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.False(editor.AddConditionCommand.CanExecute(null));
        editor.AddConditionCommand.Execute(null);
        Assert.Equal(RowFilter.MaximumConditions, editor.Conditions.Count);
        Assert.Equal(RowFilter.MaximumConditions, Apply(editor)!.Conditions.Count);
    }

    [Fact]
    public void RemoveAndClear()
    {
        var editor = Editor();
        var first = Add(editor, Current, RowFilterOperator.Greater);
        first.ValueText = "1";
        Add(editor, Current, RowFilterOperator.Less);
        Assert.False(editor.CanApply);
        Assert.Equal("Complete or remove the conditions marked.", editor.Summary);

        editor.Conditions[1].RemoveCommand.Execute(null);
        Assert.True(editor.CanApply);
        Assert.Equal("1 condition.", editor.Summary);

        editor.ClearCommand.Execute(null);
        Assert.Empty(editor.Conditions);
        Assert.Null(Apply(editor));
    }

    [Fact]
    public void DuplicateAndContradictoryConditionsAreKeptAsEntered()
    {
        var editor = Editor();
        Add(editor, Current, RowFilterOperator.Greater).ValueText = "15";
        Add(editor, Current, RowFilterOperator.Greater).ValueText = "15";
        Add(editor, Current, RowFilterOperator.Less).ValueText = "15";

        Assert.Equal(3, Apply(editor)!.Conditions.Count);
    }

    // ---- Editing a filter ----

    [Fact]
    public void AFilterIsShownAgainConditionByCondition()
    {
        var filter = new RowFilter(
        [
            new NumericValueSetCondition(Site.Id, [1, 3], includeMissing: true),
            new NumericComparisonCondition(Current.Id, NumericComparison.LessOrEqual, 15.5),
            new NumericBetweenCondition(Current.Id, 14.5, 15.5),
            new TextComparisonCondition(Tester.Id, "T2", negated: true),
            new TextValueSetCondition(Tester.Id, ["T1"], exclude: true)
        ]);

        var editor = Editor(filter);

        Assert.Equal(
            ["is any of", "<=", "between", "is not", "is not any of"],
            editor.Conditions.Select(condition => condition.SelectedOperator!.Label));
        Assert.Equal(("15.5", "14.5", "15.5", "T2"), (editor.Conditions[1].ValueText, editor.Conditions[2].ValueText, editor.Conditions[2].UpperText, editor.Conditions[3].ValueText));
        Assert.True(editor.CanApply);
        Assert.Equal(filter, Apply(editor));
    }

    [Fact]
    public void AConditionOnAColumnThatIsGoneSaysSo()
    {
        var editor = Editor(new RowFilter(new NumericComparisonCondition(Guid.NewGuid(), NumericComparison.Equal, 1)));

        var condition = Assert.Single(editor.Conditions);
        Assert.Null(condition.SelectedColumn);
        Assert.Equal("The column of this condition is no longer available.", condition.Error);
        Assert.False(editor.CanApply);
    }
}
