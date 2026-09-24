using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// Graph configuration validation against the graph specification and the worksheet's column metadata.
public class GraphConfigurationValidatorTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly GraphConfigurationValidator Validator = new();

    private static readonly WorksheetColumn Reg1 = Column("Reg1", WorksheetDataType.Numeric, 0);
    private static readonly WorksheetColumn Reg2 = Column("Reg2", WorksheetDataType.Numeric, 1);
    private static readonly WorksheetColumn Site = Column("SITE", WorksheetDataType.Numeric, 2);
    private static readonly WorksheetColumn Lot = Column("Lot", WorksheetDataType.String, 3);

    private static readonly IReadOnlyList<WorksheetColumn> Columns = [Reg1, Reg2, Site, Lot];

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, int index, Guid? worksheetId = null) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = worksheetId ?? WorksheetId,
        Index = index,
        Name = name,
        DataType = dataType
    };

    private static GraphConfiguration Configuration(GraphType graphType, params (GraphVariableRole Role, WorksheetColumn Column)[] assignments) =>
        new(graphType, WorksheetId, [.. assignments.Select(assignment => new GraphColumnAssignment(assignment.Role, assignment.Column.Id))]);

    private static GraphValidationResult Validate(GraphConfiguration configuration) => Validator.Validate(configuration, Columns);

    private static void AssertSingleError(GraphValidationResult result, GraphValidationReason reason, GraphVariableRole? role = null)
    {
        var error = Assert.Single(result.Errors);
        Assert.Equal(reason, error.Reason);
        if (role is not null)
        {
            Assert.Equal(role, error.Role);
        }

        Assert.False(result.IsValid);
    }

    // ---- Scatter plot ----

    [Fact]
    public void ScatterPlotWithNumericXAndYIsValid()
    {
        var result = Validate(Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, Reg1), (GraphVariableRole.Y, Reg2)));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ScatterPlotMayUseTheSameColumnForXAndY()
    {
        Assert.True(Validate(Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, Reg1), (GraphVariableRole.Y, Reg1))).IsValid);
    }

    [Fact]
    public void ScatterPlotWithoutXIsRejected()
    {
        AssertSingleError(
            Validate(Configuration(GraphType.ScatterPlot, (GraphVariableRole.Y, Reg2))),
            GraphValidationReason.MissingRequiredRole,
            GraphVariableRole.X);
    }

    [Fact]
    public void ScatterPlotWithoutYIsRejected()
    {
        AssertSingleError(
            Validate(Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, Reg1))),
            GraphValidationReason.MissingRequiredRole,
            GraphVariableRole.Y);
    }

    [Fact]
    public void ScatterPlotWithoutAnyAssignmentReportsBothRequiredRoles()
    {
        var result = Validate(Configuration(GraphType.ScatterPlot));

        Assert.Equal(
            [(GraphValidationReason.MissingRequiredRole, GraphVariableRole.X), (GraphValidationReason.MissingRequiredRole, GraphVariableRole.Y)],
            result.Errors.Select(error => (error.Reason, error.Role)));
    }

    [Theory]
    [InlineData(GraphVariableRole.X)]
    [InlineData(GraphVariableRole.Y)]
    public void ScatterPlotRejectsAStringColumnOnAnAxis(GraphVariableRole role)
    {
        var other = role == GraphVariableRole.X ? GraphVariableRole.Y : GraphVariableRole.X;

        AssertSingleError(
            Validate(Configuration(GraphType.ScatterPlot, (role, Lot), (other, Reg1))),
            GraphValidationReason.IncompatibleDataType,
            role);
    }

    [Fact]
    public void ScatterPlotAcceptsANumericOrStringGroup()
    {
        Assert.True(Validate(Configuration(
            GraphType.ScatterPlot, (GraphVariableRole.X, Reg1), (GraphVariableRole.Y, Reg2), (GraphVariableRole.Group, Site))).IsValid);
        Assert.True(Validate(Configuration(
            GraphType.ScatterPlot, (GraphVariableRole.X, Reg1), (GraphVariableRole.Y, Reg2), (GraphVariableRole.Group, Lot))).IsValid);
    }

    // ---- Single-variable graphs ----

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void SingleVariableGraphsAcceptANumericVariableWithOrWithoutAGroup(GraphType graphType)
    {
        Assert.True(Validate(Configuration(graphType, (GraphVariableRole.Variable, Reg1))).IsValid);
        Assert.True(Validate(Configuration(graphType, (GraphVariableRole.Variable, Reg1), (GraphVariableRole.Group, Lot))).IsValid);
        Assert.True(Validate(Configuration(graphType, (GraphVariableRole.Variable, Reg1), (GraphVariableRole.Group, Site))).IsValid);
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void SingleVariableGraphsRejectAMissingOrStringVariable(GraphType graphType)
    {
        AssertSingleError(Validate(Configuration(graphType)), GraphValidationReason.MissingRequiredRole, GraphVariableRole.Variable);
        AssertSingleError(
            Validate(Configuration(graphType, (GraphVariableRole.Variable, Lot))),
            GraphValidationReason.IncompatibleDataType,
            GraphVariableRole.Variable);
    }

    // ---- Roles, columns and worksheets ----

    [Fact]
    public void ARoleTheGraphTypeDoesNotHaveIsRejected()
    {
        AssertSingleError(
            Validate(Configuration(GraphType.Histogram, (GraphVariableRole.Variable, Reg1), (GraphVariableRole.X, Reg2))),
            GraphValidationReason.UnsupportedRole,
            GraphVariableRole.X);
    }

    [Fact]
    public void ARoleAssignedTwiceIsRejected()
    {
        // A role that takes one column: the scatter plot's X axis (from Task #041 a histogram's variables take several).
        var configuration = new GraphConfiguration(GraphType.ScatterPlot, WorksheetId,
        [
            new GraphColumnAssignment(GraphVariableRole.X, Reg1.Id),
            new GraphColumnAssignment(GraphVariableRole.X, Reg2.Id),
            new GraphColumnAssignment(GraphVariableRole.Y, Reg1.Id)
        ]);

        AssertSingleError(Validate(configuration), GraphValidationReason.DuplicateRole, GraphVariableRole.X);
    }

    [Fact]
    public void AColumnThatNoLongerExistsIsRejected()
    {
        var missingColumnId = Guid.NewGuid();
        var configuration = new GraphConfiguration(GraphType.Histogram, WorksheetId,
            [new GraphColumnAssignment(GraphVariableRole.Variable, missingColumnId)]);

        var error = Assert.Single(Validate(configuration).Errors);
        Assert.Equal((GraphValidationReason.ColumnNotFound, GraphVariableRole.Variable, missingColumnId), (error.Reason, error.Role, error.WorksheetColumnId));
    }

    [Fact]
    public void AColumnOfAnotherWorksheetIsRejected()
    {
        var foreignColumn = Column("Reg1", WorksheetDataType.Numeric, 0, Guid.NewGuid());
        var configuration = new GraphConfiguration(GraphType.Histogram, WorksheetId,
            [new GraphColumnAssignment(GraphVariableRole.Variable, foreignColumn.Id)]);

        var result = Validator.Validate(configuration, [.. Columns, foreignColumn]);

        AssertSingleError(result, GraphValidationReason.ColumnFromAnotherWorksheet, GraphVariableRole.Variable);
    }

    [Fact]
    public void AnUnknownGraphTypeIsRejected()
    {
        var configuration = new GraphConfiguration((GraphType)99, WorksheetId, []);

        AssertSingleError(Validate(configuration), GraphValidationReason.UnknownGraphType);
    }

    // ---- Stable identity ----

    [Fact]
    public void AssignmentsFollowColumnIdsWhenNamesAndIndexesChange()
    {
        var configuration = Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, Reg1), (GraphVariableRole.Y, Reg2));

        // The same columns after a rename and a reindex (e.g. a column before them was deleted).
        var renamed = new WorksheetColumn
        {
            Id = Reg1.Id, WorksheetId = WorksheetId, Index = 7, Name = "Vth", DataType = WorksheetDataType.Numeric
        };
        var reindexed = new WorksheetColumn
        {
            Id = Reg2.Id, WorksheetId = WorksheetId, Index = 0, Name = "Idsat", DataType = WorksheetDataType.Numeric
        };

        var result = Validator.Validate(configuration, [renamed, reindexed]);

        Assert.True(result.IsValid);
        Assert.Equal(Reg1.Id, configuration.FindColumnId(GraphVariableRole.X));
        Assert.Equal(Reg2.Id, configuration.FindColumnId(GraphVariableRole.Y));
        Assert.Null(configuration.FindColumnId(GraphVariableRole.Group));
    }

    [Fact]
    public void ConfigurationsStoreWorksheetAndColumnIdsOnly()
    {
        var configuration = Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, Reg1), (GraphVariableRole.Y, Reg2));

        Assert.Equal(WorksheetId, configuration.WorksheetId);
        Assert.Equal([Reg1.Id, Reg2.Id], configuration.Assignments.Select(assignment => assignment.WorksheetColumnId));

        var assignmentProperties = typeof(GraphColumnAssignment).GetProperties().Select(property => property.Name).Order();
        Assert.Equal([nameof(GraphColumnAssignment.Role), nameof(GraphColumnAssignment.WorksheetColumnId)], assignmentProperties);
    }

    // ---- Specification (#036) ----

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void AValidSpecificationIsAcceptedWhereItIsDrawn(GraphType graphType)
    {
        var configuration = Configuration(graphType, (GraphVariableRole.Variable, Reg1)) with
        {
            Specification = new Specification(14.5, 15, 15.5)
        };

        Assert.True(Validate(configuration).IsValid);
    }

    [Theory]
    [InlineData(16.0, null, 15.0, GraphValidationReason.SpecificationLimitsOutOfOrder, SpecificationField.UpperLimit)]
    [InlineData(15.0, null, 15.0, GraphValidationReason.SpecificationLimitsOutOfOrder, SpecificationField.UpperLimit)]
    [InlineData(14.5, 16.0, 15.5, GraphValidationReason.SpecificationTargetOutsideLimits, SpecificationField.Target)]
    [InlineData(double.NaN, null, null, GraphValidationReason.SpecificationValueNotNumeric, SpecificationField.LowerLimit)]
    [InlineData(null, double.PositiveInfinity, null, GraphValidationReason.SpecificationValueNotNumeric, SpecificationField.Target)]
    public void AnInvalidSpecificationIsReportedWithItsField(
        double? lower, double? target, double? upper, GraphValidationReason reason, SpecificationField field)
    {
        var configuration = Configuration(GraphType.Histogram, (GraphVariableRole.Variable, Reg1)) with
        {
            Specification = new Specification(lower, target, upper)
        };

        var error = Assert.Single(Validate(configuration).Errors);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.Field);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void GraphsThatDrawNoSpecificationIgnoreIt(GraphType graphType)
    {
        var configuration = (graphType == GraphType.ScatterPlot
            ? Configuration(graphType, (GraphVariableRole.X, Reg1), (GraphVariableRole.Y, Reg2))
            : Configuration(graphType, (GraphVariableRole.Variable, Reg1))) with
        {
            Specification = new Specification(16, null, 15)
        };

        Assert.True(Validate(configuration).IsValid);
    }

    [Fact]
    public void ColumnProblemsComeBeforeSpecificationProblems()
    {
        var configuration = Configuration(GraphType.Histogram) with { Specification = new Specification(16, null, 15) };

        var reasons = Validate(configuration).Errors.Select(error => error.Reason);

        Assert.Equal([GraphValidationReason.MissingRequiredRole, GraphValidationReason.SpecificationLimitsOutOfOrder], reasons);
    }
}
