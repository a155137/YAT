using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Graphs;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The graph setup's filter (Task #049): All rows until a filter is applied, a row of its own whatever the group, the
// configuration carrying exactly the filter applied, and a filter that cannot be used said in the setup's words.
public class GraphSetupFilterTests
{
    private static readonly Worksheet Worksheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

    private static readonly WorksheetColumn Site = Column("Site", WorksheetDataType.Numeric, 0);
    private static readonly WorksheetColumn Tester = Column("Tester", WorksheetDataType.String, 1);
    private static readonly WorksheetColumn Reg = Column("PS_RAW", WorksheetDataType.Numeric, 2);

    private static readonly IReadOnlyList<WorksheetColumn> Columns = [Site, Tester, Reg];

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, int index) =>
        new() { Id = Guid.NewGuid(), WorksheetId = Worksheet.Id, Index = index, Name = name, DataType = dataType };

    private static Task<GraphFilterValues> LoadAsync(Guid columnId, CancellationToken cancellationToken) =>
        Task.FromResult(columnId == Tester.Id
            ? new GraphFilterValues(new StringRawDistinctValues(columnId, ["T01", "T02"], false, false))
            : new GraphFilterValues(new NumericRawDistinctValues(columnId, [1, 2, 3], true, false)));

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
    public void TheAppliedFilterIsTheConfigurationsFilter()
    {
        var setup = Setup();
        var filter = new NumericValueFilter(Site.Id, [1, 3]);
        var changes = new List<string?>();
        setup.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        setup.Filter = filter;

        Assert.Same(filter, setup.Confirm()!.Filter);
        Assert.Equal("Site: 1, 3", setup.FilterSummary);
        Assert.Contains(nameof(GraphSetupViewModel.FilterSummary), changes);
    }

    [Fact]
    public void AFilterIsKeptWhateverTheGroupBecomes()
    {
        var setup = Setup();
        setup.Filter = new NumericValueFilter(Site.Id, [2]);

        Group(setup, Tester);

        var configuration = setup.Confirm()!;
        Assert.Equal(Tester.Id, configuration.FindColumnId(GraphVariableRole.Group));
        Assert.Equal(Site.Id, configuration.Filter!.ColumnId);
    }

    [Fact]
    public void TheEditorStartsFromTheGroupColumn()
    {
        var setup = Setup();
        Group(setup, Tester);

        var editor = setup.CreateFilterEditor();

        Assert.Equal(Tester.Id, editor.SelectedColumn!.WorksheetColumnId);
        Assert.Equal(["T01", "T02"], editor.Values.Select(value => value.Label));
    }

    [Fact]
    public void TheEditorStartsFromTheFilterBeingEdited()
    {
        var setup = Setup();
        Group(setup, Tester);
        setup.Filter = new NumericValueFilter(Site.Id, [2], includeMissing: true);

        var editor = setup.CreateFilterEditor();

        Assert.Equal(Site.Id, editor.SelectedColumn!.WorksheetColumnId);
        Assert.Equal([false, true, false], editor.Values.Select(value => value.IsSelected));
        Assert.True(editor.IncludeMissing);
    }

    [Fact]
    public void WithoutAGroupTheEditorWaitsForAColumn()
    {
        Assert.Null(Setup().CreateFilterEditor().SelectedColumn);
    }

    [Fact]
    public void AnEditorThatIsNotAppliedChangesNothing()
    {
        var setup = Setup();
        var filter = new NumericValueFilter(Site.Id, [1]);
        setup.Filter = filter;

        var editor = setup.CreateFilterEditor();
        editor.ClearCommand.Execute(null);
        editor.Values[2].IsSelected = true;

        Assert.Same(filter, setup.Filter);
        Assert.Same(filter, setup.Confirm()!.Filter);
    }

    [Fact]
    public void ApplyingEveryRowRemovesTheFilter()
    {
        var setup = Setup();
        setup.Filter = new NumericValueFilter(Site.Id, [1]);
        var editor = setup.CreateFilterEditor();
        editor.SelectAllCommand.Execute(null);

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

        setup.Filter = new TextValueFilter(Tester.Id, ["T02"]);

        Assert.Equal(new TextValueFilter(Tester.Id, ["T02"]), setup.Confirm()!.Filter);
    }

    // ---- A filter that cannot be used ----

    [Fact]
    public void AFilterOfAColumnThatIsGoneIsSaidAndCannotBeConfirmed()
    {
        var setup = Setup();

        setup.Filter = new NumericValueFilter(Guid.NewGuid(), [1]);

        Assert.False(setup.CanConfirm);
        Assert.Equal("The filter column is no longer available. Edit the filter.", setup.ValidationMessage);
        Assert.Equal("(column not available): 1", setup.FilterSummary);
        Assert.Null(setup.Confirm());
    }

    [Fact]
    public void AFilterThatSelectsNothingIsSaidAndCannotBeConfirmed()
    {
        var setup = Setup();

        setup.Filter = new NumericValueFilter(Site.Id, []);

        Assert.False(setup.CanConfirm);
        Assert.Equal("Select at least one filter value, or (Missing).", setup.ValidationMessage);
    }

    [Fact]
    public void AFilterOfTheWrongTypeIsSaid()
    {
        var setup = Setup();

        setup.Filter = new TextValueFilter(Site.Id, ["1"]);

        Assert.Equal(
            "The filter's values do not match its column. Choose a Numeric or String column and its values.",
            setup.ValidationMessage);
    }

    [Fact]
    public void RemovingAFilterThatCannotBeUsedMakesTheSetupConfirmableAgain()
    {
        var setup = Setup();
        setup.Filter = new NumericValueFilter(Site.Id, []);

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
            GraphValidationReason.FilterSelectionEmpty
        }.Select(reason => GraphValidationMessages.For(new GraphValidationError(reason), definition)).ToArray();

        Assert.Equal(4, messages.Distinct().Count());
        Assert.DoesNotContain(generic, messages);
        Assert.Equal("The filter column belongs to another worksheet. Edit the filter.", messages[1]);
    }
}
