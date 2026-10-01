using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// Validation of a graph's filter (Task #049): against the column metadata only - a Numeric or String column of the
// graph's worksheet, values of its type, something selected - whatever roles the column has, for every graph type.
public class GraphConfigurationFilterValidationTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly GraphConfigurationValidator Validator = new();

    private static readonly WorksheetColumn Reg1 = Column("Reg1", WorksheetDataType.Numeric, 0);
    private static readonly WorksheetColumn Reg2 = Column("Reg2", WorksheetDataType.Numeric, 1);
    private static readonly WorksheetColumn Site = Column("SITE", WorksheetDataType.Numeric, 2);
    private static readonly WorksheetColumn Lot = Column("Lot", WorksheetDataType.String, 3);
    private static readonly WorksheetColumn Tested = Column("Tested", WorksheetDataType.DateTime, 4);
    private static readonly WorksheetColumn Passed = Column("Passed", WorksheetDataType.Boolean, 5);
    private static readonly WorksheetColumn Elsewhere = Column("Site", WorksheetDataType.Numeric, 0, Guid.NewGuid());

    private static readonly IReadOnlyList<WorksheetColumn> Columns = [Reg1, Reg2, Site, Lot, Tested, Passed, Elsewhere];

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, int index, Guid? worksheetId = null) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = worksheetId ?? WorksheetId,
        Index = index,
        Name = name,
        DataType = dataType
    };

    // A valid configuration of each graph type, without a filter.
    public static TheoryData<GraphType> GraphTypes => [.. GraphTypeDefinitions.All.Select(definition => definition.GraphType)];

    private static GraphConfiguration Configuration(GraphType graphType, GraphValueFilter? filter = null)
    {
        IReadOnlyList<GraphColumnAssignment> assignments = graphType == GraphType.ScatterPlot
            ? [new(GraphVariableRole.X, Reg1.Id), new(GraphVariableRole.Y, Reg2.Id)]
            : [new(GraphVariableRole.Variable, Reg1.Id)];
        return new GraphConfiguration(graphType, WorksheetId, assignments) { Filter = filter };
    }

    private static GraphValidationResult Validate(GraphConfiguration configuration) => Validator.Validate(configuration, Columns);

    private static GraphValidationError SingleError(GraphValidationResult result)
    {
        Assert.False(result.IsValid);
        return Assert.Single(result.Errors);
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void EveryGraphTypeIsValidWithoutAFilter(GraphType graphType)
    {
        Assert.True(Validate(Configuration(graphType)).IsValid);
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void EveryGraphTypeTakesANumericOrTextFilter(GraphType graphType)
    {
        Assert.True(Validate(Configuration(graphType, new NumericValueFilter(Site.Id, [1, 3, 5, 7]))).IsValid);
        Assert.True(Validate(Configuration(graphType, new TextValueFilter(Lot.Id, ["L01"]))).IsValid);
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void EveryGraphTypeChecksItsFilter(GraphType graphType)
    {
        var error = SingleError(Validate(Configuration(graphType, new NumericValueFilter(Site.Id, []))));

        Assert.Equal(GraphValidationReason.FilterSelectionEmpty, error.Reason);
    }

    [Fact]
    public void OnlyMissingSelectedIsValid()
    {
        Assert.True(Validate(Configuration(GraphType.Histogram, new TextValueFilter(Lot.Id, [], includeMissing: true))).IsValid);
    }

    [Fact]
    public void NothingSelectedIsRefused()
    {
        var error = SingleError(Validate(Configuration(GraphType.Histogram, new TextValueFilter(Lot.Id, []))));

        Assert.Equal(GraphValidationReason.FilterSelectionEmpty, error.Reason);
        Assert.Equal(Lot.Id, error.WorksheetColumnId);
        Assert.Null(error.Role);
    }

    [Fact]
    public void TheFilterColumnNeedNotBeAssignedToTheGraph()
    {
        var configuration = Configuration(GraphType.BoxPlot, new NumericValueFilter(Site.Id, [2])) with
        {
            Assignments = [new(GraphVariableRole.Variable, Reg1.Id), new(GraphVariableRole.Group, Lot.Id)]
        };

        Assert.True(Validate(configuration).IsValid);
    }

    [Fact]
    public void TheFilterColumnMayBeTheGroupColumn()
    {
        var configuration = Configuration(GraphType.Histogram, new NumericValueFilter(Site.Id, [2])) with
        {
            Assignments = [new(GraphVariableRole.Variable, Reg1.Id), new(GraphVariableRole.Group, Site.Id)]
        };

        Assert.True(Validate(configuration).IsValid);
    }

    [Fact]
    public void TheFilterColumnMayBeAMeasuredColumn()
    {
        // Whether a column can filter is its type, never its role in the graph.
        Assert.True(Validate(Configuration(GraphType.Histogram, new NumericValueFilter(Reg1.Id, [10.5]))).IsValid);
    }

    [Fact]
    public void AFilterColumnThatIsGoneIsRefused()
    {
        var gone = Guid.NewGuid();
        var error = SingleError(Validate(Configuration(GraphType.Histogram, new NumericValueFilter(gone, [1]))));

        Assert.Equal(GraphValidationReason.FilterColumnNotFound, error.Reason);
        Assert.Equal(gone, error.WorksheetColumnId);
    }

    [Fact]
    public void AFilterColumnOfAnotherWorksheetIsRefused()
    {
        var error = SingleError(Validate(Configuration(GraphType.Histogram, new NumericValueFilter(Elsewhere.Id, [1]))));

        Assert.Equal(GraphValidationReason.FilterColumnFromAnotherWorksheet, error.Reason);
        Assert.Equal(Elsewhere.Id, error.WorksheetColumnId);
    }

    [Fact]
    public void NumbersCannotFilterATextColumn()
    {
        var error = SingleError(Validate(Configuration(GraphType.Histogram, new NumericValueFilter(Lot.Id, [1]))));

        Assert.Equal(GraphValidationReason.FilterColumnIncompatibleType, error.Reason);
        Assert.Equal(Lot.Id, error.WorksheetColumnId);
    }

    [Fact]
    public void TextCannotFilterANumericColumn()
    {
        var error = SingleError(Validate(Configuration(GraphType.Histogram, new TextValueFilter(Site.Id, ["1"]))));

        Assert.Equal(GraphValidationReason.FilterColumnIncompatibleType, error.Reason);
    }

    [Fact]
    public void AColumnThatIsNeitherNumericNorTextCannotFilter()
    {
        Assert.Equal(
            GraphValidationReason.FilterColumnIncompatibleType,
            SingleError(Validate(Configuration(GraphType.Histogram, new TextValueFilter(Tested.Id, ["2026"])))).Reason);
        Assert.Equal(
            GraphValidationReason.FilterColumnIncompatibleType,
            SingleError(Validate(Configuration(GraphType.Histogram, new TextValueFilter(Passed.Id, ["True"])))).Reason);
    }

    [Fact]
    public void AnEmptySelectionOfAMissingColumnReportsOnlyTheColumn()
    {
        var error = SingleError(Validate(Configuration(GraphType.Histogram, new NumericValueFilter(Guid.NewGuid(), []))));

        Assert.Equal(GraphValidationReason.FilterColumnNotFound, error.Reason);
    }

    [Fact]
    public void FilterErrorsAreReportedWithTheOtherErrors()
    {
        var configuration = new GraphConfiguration(GraphType.Histogram, WorksheetId, [])
        {
            Filter = new NumericValueFilter(Site.Id, [])
        };

        var reasons = Validate(configuration).Errors.Select(error => error.Reason).ToArray();

        Assert.Contains(GraphValidationReason.MissingRequiredRole, reasons);
        Assert.Contains(GraphValidationReason.FilterSelectionEmpty, reasons);
    }
}
