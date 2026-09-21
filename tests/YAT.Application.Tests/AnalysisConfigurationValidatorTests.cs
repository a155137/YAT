using YAT.Application.Analyses;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// What an analysis may be configured with: at least one variable, each one only once, Numeric variables, and a
// grouping column that is Numeric or String.
public class AnalysisConfigurationValidatorTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();
    private static readonly Guid OtherWorksheetId = Guid.NewGuid();

    private readonly AnalysisConfigurationValidator _validator = new();

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, Guid? worksheetId = null) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = worksheetId ?? WorksheetId,
        Index = 0,
        Name = name,
        DataType = dataType
    };

    private static AnalysisConfiguration Configuration(IEnumerable<WorksheetColumn> variables, WorksheetColumn? group = null) =>
        new(WorksheetId, [.. variables.Select(column => column.Id)], group?.Id);

    // 0
    [Fact]
    public void OneNumericVariableIsEnough()
    {
        var reg1 = Column("Reg1", WorksheetDataType.Numeric);

        Assert.True(_validator.Validate(Configuration([reg1]), [reg1]).IsValid);
    }

    // 1
    [Fact]
    public void SeveralNumericVariablesAreAllowedInOneAnalysis()
    {
        var reg1 = Column("Reg1", WorksheetDataType.Numeric);
        var reg2 = Column("Reg2", WorksheetDataType.Numeric);
        var reg3 = Column("Reg3", WorksheetDataType.Numeric);

        Assert.True(_validator.Validate(Configuration([reg1, reg2, reg3]), [reg1, reg2, reg3]).IsValid);
    }

    // 2
    [Fact]
    public void AnAnalysisWithoutVariablesHasNothingToSummarise()
    {
        var reg1 = Column("Reg1", WorksheetDataType.Numeric);

        var result = _validator.Validate(new AnalysisConfiguration(WorksheetId, [], null), [reg1]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(AnalysisValidationReason.NoVariableSelected, error.Reason);
        Assert.Equal(AnalysisColumnRole.Variable, error.Role);
    }

    // 3
    [Fact]
    public void TheSameVariableCannotBeSelectedTwice()
    {
        var reg1 = Column("Reg1", WorksheetDataType.Numeric);

        var result = _validator.Validate(new AnalysisConfiguration(WorksheetId, [reg1.Id, reg1.Id], null), [reg1]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(AnalysisValidationReason.DuplicateVariable, error.Reason);
        Assert.Equal(reg1.Id, error.WorksheetColumnId);
    }

    // 4
    [Fact]
    public void AColumnThatIsNoLongerPartOfTheWorksheetIsReported()
    {
        var reg1 = Column("Reg1", WorksheetDataType.Numeric);
        var deleted = Column("Reg2", WorksheetDataType.Numeric);

        var result = _validator.Validate(Configuration([reg1, deleted]), [reg1]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(AnalysisValidationReason.ColumnNotFound, error.Reason);
        Assert.Equal(deleted.Id, error.WorksheetColumnId);
    }

    // 5
    [Fact]
    public void AColumnOfAnotherWorksheetIsReported()
    {
        var foreign = Column("Reg1", WorksheetDataType.Numeric, OtherWorksheetId);

        var result = _validator.Validate(Configuration([foreign]), [foreign]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(AnalysisValidationReason.ColumnFromAnotherWorksheet, error.Reason);
    }

    // 6
    [Fact]
    public void AVariableMustBeNumeric()
    {
        var lot = Column("Lot", WorksheetDataType.String);

        var result = _validator.Validate(Configuration([lot]), [lot]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(AnalysisValidationReason.IncompatibleDataType, error.Reason);
        Assert.Equal(AnalysisColumnRole.Variable, error.Role);
        Assert.Equal(lot.Id, error.WorksheetColumnId);
    }

    // 7
    [Theory]
    [InlineData(WorksheetDataType.Numeric)]
    [InlineData(WorksheetDataType.String)]
    public void AGroupingColumnMayBeNumericOrString(WorksheetDataType dataType)
    {
        var reg1 = Column("Reg1", WorksheetDataType.Numeric);
        var group = Column("SITE", dataType);

        Assert.True(_validator.Validate(Configuration([reg1], group), [reg1, group]).IsValid);
    }

    // 8
    [Theory]
    [InlineData(WorksheetDataType.DateTime)]
    [InlineData(WorksheetDataType.Boolean)]
    public void AGroupingColumnOfAnotherTypeIsRejected(WorksheetDataType dataType)
    {
        var reg1 = Column("Reg1", WorksheetDataType.Numeric);
        var group = Column("When", dataType);

        var result = _validator.Validate(Configuration([reg1], group), [reg1, group]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(AnalysisValidationReason.IncompatibleDataType, error.Reason);
        Assert.Equal(AnalysisColumnRole.Group, error.Role);
    }

    // 9
    [Fact]
    public void AGroupingColumnMayAlsoBeOneOfTheVariables()
    {
        // Nothing forbids summarising the same column that groups: both roles are checked on their own.
        var reg1 = Column("Reg1", WorksheetDataType.Numeric);

        Assert.True(_validator.Validate(Configuration([reg1], reg1), [reg1]).IsValid);
    }

    // 10
    [Fact]
    public void GroupingIsOptional()
    {
        var reg1 = Column("Reg1", WorksheetDataType.Numeric);

        Assert.True(_validator.Validate(new AnalysisConfiguration(WorksheetId, [reg1.Id], null), [reg1]).IsValid);
    }
}
