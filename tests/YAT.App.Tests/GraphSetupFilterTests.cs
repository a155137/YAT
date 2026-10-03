using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Filtering;
using YAT.Application.Graphs;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The graph setup's filter (Tasks #049, #053): All rows until a filter is applied, a row of its own whatever the group,
// the configuration carrying exactly the filter applied, the Filter dialog opened on it, and a filter that cannot be
// used said in the setup's words.
public class GraphSetupFilterTests
{
    private static readonly Worksheet Worksheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

    private static readonly WorksheetColumn Site = Column("Site", WorksheetDataType.Numeric, 0);
    private static readonly WorksheetColumn Tester = Column("Tester", WorksheetDataType.String, 1);
    private static readonly WorksheetColumn Reg = Column("PS_RAW", WorksheetDataType.Numeric, 2);

    private static readonly IReadOnlyList<WorksheetColumn> Columns = [Site, Tester, Reg];

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, int index) =>
        new() { Id = Guid.NewGuid(), WorksheetId = Worksheet.Id, Index = index, Name = name, DataType = dataType };

    private static Task<FilterValues> LoadAsync(Guid columnId, CancellationToken cancellationToken) =>
        Task.FromResult(columnId == Tester.Id
            ? new FilterValues(new StringRawDistinctValues(columnId, ["T01", "T02"], false, false))
            : new FilterValues(new NumericRawDistinctValues(columnId, [1, 2, 3], true, false)));

    private static GraphSetupViewModel Setup(GraphType graphType = GraphType.Histogram)
    {
        var setup = new GraphSetupViewModel(GraphTypeDefinitions.For(graphType), Worksheet, Columns, LoadAsync);
        var variables = setup.Roles.Single(role => role.AllowsMultiple);
        variables.SelectedOptions.Add(variables.Options.Single(option => option.WorksheetColumnId == Reg.Id));
        return setup;
    }

    private static void Group(GraphSetupViewModel setup, WorksheetColumn column)
    {
        var group = setup.Roles.Single(role => role.Role == GraphVariableRole.Group);
        group.Choose(group.Options.Single(option => option.WorksheetColumnId == column.Id));
    }

    private static RowFilter Of(params RowFilterCondition[] conditions) => new(conditions);

    [Fact]
    public void ASetupStartsWithAllRows()
    {
        var setup = Setup();

        Assert.True(setup.SupportsFilter);
        Assert.Null(setup.Filter);
        Assert.Equal("All rows", setup.FilterSummary);
        Assert.Null(setup.Confirm()!.Filter);
    }

    [Fact]
    public void ASetupWithoutAWayToReadValuesOffersNoFilter()
    {
        var setup = new GraphSetupViewModel(GraphTypeDefinitions.For(GraphType.Histogram), Worksheet, Columns);

        Assert.False(setup.SupportsFilter);
        Assert.Throws<InvalidOperationException>(() => setup.CreateFilterEditor());
    }

    [Fact]
    public void TheAppliedFilterIsTheConfigurationsFilterAndItsSummaryCountsItsConditions()
    {
        var setup = Setup();
        var filter = Of(new NumericValueSetCondition(Site.Id, [1, 3]), new NumericComparisonCondition(Reg.Id, NumericComparison.GreaterOrEqual, 14.5));
        var changes = new List<string?>();
        setup.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        setup.Filter = filter;

        Assert.Same(filter, setup.Confirm()!.Filter);
        Assert.Equal("2 conditions", setup.FilterSummary);
        Assert.Contains(nameof(GraphSetupViewModel.FilterSummary), changes);
        setup.Filter = Of(new NumericValueSetCondition(Site.Id, [1]));
        Assert.Equal("1 condition", setup.FilterSummary);
    }

    [Fact]
    public void AFilterIsKeptWhateverTheGroupBecomes()
    {
        var setup = Setup();
        setup.Filter = Of(new NumericValueSetCondition(Site.Id, [2]));

        Group(setup, Tester);

        var configuration = setup.Confirm()!;
        Assert.Equal(Tester.Id, configuration.FindColumnId(GraphVariableRole.Group));
        Assert.Equal(Site.Id, configuration.Filter!.Conditions[0].ColumnId);
    }

    [Fact]
    public void ANewConditionStartsOnTheGroupColumn()
    {
        var setup = Setup();
        Group(setup, Tester);

        var editor = setup.CreateFilterEditor();
        editor.AddConditionCommand.Execute(null);

        Assert.Equal(Tester.Id, Assert.Single(editor.Conditions).SelectedColumn!.Id);
    }

    [Fact]
    public async Task TheEditorOpensOnTheFilterBeingEdited()
    {
        var setup = Setup();
        Group(setup, Tester);
        setup.Filter = Of(new NumericValueSetCondition(Site.Id, [2], includeMissing: true));

        var editor = setup.CreateFilterEditor();

        var condition = Assert.Single(editor.Conditions);
        Assert.Equal(Site.Id, condition.SelectedColumn!.Id);
        Assert.Equal(RowFilterOperator.IsAnyOf, condition.SelectedOperator!.Operator);
        Assert.Equal("2, (Missing)", condition.ValuesSummary);
        var chooser = condition.CreateValueChooser()!;
        await chooser.Loading;
        Assert.Equal([false, true, false], chooser.Values.Select(value => value.IsSelected));
        Assert.True(chooser.IncludeMissing);
    }

    [Fact]
    public void AnEditorThatIsNotAppliedChangesNothing()
    {
        var setup = Setup();
        var filter = Of(new NumericValueSetCondition(Site.Id, [1]));
        setup.Filter = filter;

        var editor = setup.CreateFilterEditor();
        editor.ClearCommand.Execute(null);

        Assert.Same(filter, setup.Filter);
        Assert.Same(filter, setup.Confirm()!.Filter);
    }

    [Fact]
    public async Task ChoosingEveryValueOfACompleteListRemovesTheCondition()
    {
        var setup = Setup();
        setup.Filter = Of(new NumericValueSetCondition(Site.Id, [1]));
        var editor = setup.CreateFilterEditor();
        var condition = Assert.Single(editor.Conditions);
        var chooser = condition.CreateValueChooser()!;
        await chooser.Loading;
        chooser.SelectAllCommand.Execute(null);
        Assert.True(chooser.TryApply(out var choice));
        condition.ApplyValues(choice!);

        Assert.True(editor.TryApply(out var edit));
        setup.Filter = edit!.Filter;

        Assert.Null(setup.Filter);
        Assert.Equal("All rows", setup.FilterSummary);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.BoxPlot)]
    public void EveryGraphTypeCarriesTheFilter(GraphType graphType)
    {
        var setup = new GraphSetupViewModel(GraphTypeDefinitions.For(graphType), Worksheet, Columns, LoadAsync);
        foreach (var role in setup.Roles.Where(role => role.IsRequired))
        {
            if (role.AllowsMultiple)
            {
                role.SelectedOptions.Add(role.Options.Single(option => option.WorksheetColumnId == Reg.Id));
            }
            else
            {
                role.Choose(role.Options.Single(option => option.WorksheetColumnId == Reg.Id));
            }
        }

        setup.Filter = Of(new TextComparisonCondition(Tester.Id, "T02"), new NumericBetweenCondition(Site.Id, 1, 2));

        Assert.Equal(Of(new TextComparisonCondition(Tester.Id, "T02"), new NumericBetweenCondition(Site.Id, 1, 2)), setup.Confirm()!.Filter);
    }

    // ---- A filter that cannot be used ----

    [Fact]
    public void AFilterOfAColumnThatIsGoneIsSaidAndCannotBeConfirmed()
    {
        var setup = Setup();

        setup.Filter = Of(new NumericValueSetCondition(Guid.NewGuid(), [1]));

        Assert.False(setup.CanConfirm);
        Assert.Equal("The filter column is no longer available. Edit the filter.", setup.ValidationMessage);
        Assert.Equal("1 condition", setup.FilterSummary);
        Assert.Null(setup.Confirm());
    }

    [Fact]
    public void AFilterThatSelectsNothingIsSaidAndCannotBeConfirmed()
    {
        var setup = Setup();

        setup.Filter = Of(new NumericValueSetCondition(Site.Id, []));

        Assert.False(setup.CanConfirm);
        Assert.Equal("Choose at least one value, or (Missing), for each \"is any of\" or \"is not any of\" condition of the filter.", setup.ValidationMessage);
    }

    [Fact]
    public void AFilterOfTheWrongTypeIsSaid()
    {
        var setup = Setup();

        setup.Filter = Of(new TextValueSetCondition(Site.Id, ["1"]));

        Assert.Equal(
            "The filter's values do not match its column. Choose a Numeric or String column and its values.",
            setup.ValidationMessage);
    }

    [Fact]
    public void MoreThanTwentyConditionsAreSaidAndCannotBeConfirmed()
    {
        var setup = Setup();

        setup.Filter = new RowFilter(Enumerable.Range(0, RowFilter.MaximumConditions + 1).Select(index => new NumericComparisonCondition(Reg.Id, NumericComparison.Greater, index)));

        Assert.False(setup.CanConfirm);
        Assert.Equal("A filter can have at most 20 conditions. Edit the filter.", setup.ValidationMessage);
    }

    [Fact]
    public void RemovingAFilterThatCannotBeUsedMakesTheSetupConfirmableAgain()
    {
        var setup = Setup();
        setup.Filter = Of(new NumericValueSetCondition(Site.Id, []));

        setup.Filter = null;

        Assert.True(setup.CanConfirm);
        Assert.Null(setup.ValidationMessage);
    }

    [Fact]
    public void EveryFilterReasonHasItsOwnWords()
    {
        var definition = GraphTypeDefinitions.For(GraphType.Histogram);
        var generic = GraphValidationMessages.For(new GraphValidationError(GraphValidationReason.UnknownGraphType), definition);

        var messages = new[]
        {
            GraphValidationReason.FilterColumnNotFound,
            GraphValidationReason.FilterColumnFromAnotherWorksheet,
            GraphValidationReason.FilterColumnIncompatibleType,
            GraphValidationReason.FilterSelectionEmpty,
            GraphValidationReason.FilterTooManyConditions
        }.Select(reason => GraphValidationMessages.For(new GraphValidationError(reason), definition)).ToArray();

        Assert.Equal(5, messages.Distinct().Count());
        Assert.DoesNotContain(generic, messages);
        Assert.Equal("The filter column belongs to another worksheet. Edit the filter.", messages[1]);
    }
}
