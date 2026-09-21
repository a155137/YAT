using YAT.Application.Analyses;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// What a capability analysis may be configured with: the analysis rules every analysis shares, plus a specification
// each variable can actually be measured against.
public class CapabilityAnalysisValidatorTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private readonly CapabilityAnalysisValidator _validator = new();

    private static WorksheetColumn Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric, Guid? worksheetId = null) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = worksheetId ?? WorksheetId,
        Index = 0,
        Name = name,
        DataType = dataType
    };

    private static CapabilityAnalysisConfiguration Configuration(
        IEnumerable<CapabilityVariable> variables,
        WorksheetColumn? group = null,
        IReadOnlyList<CapabilityStatistic>? statistics = null) =>
        new(WorksheetId, [.. variables], group?.Id, statistics ?? CapabilityAnalysisConfiguration.DefaultDisplayStatistics);

    // 0
    [Fact]
    public void ATwoSidedSpecificationIsValid()
    {
        var reg1 = Column("Reg1");

        Assert.True(_validator.Validate(Configuration([new CapabilityVariable(reg1.Id, 14500, 15500)]), [reg1]).IsValid);
    }

    // 1
    [Theory]
    [InlineData(null, 500d)]
    [InlineData(20d, null)]
    public void AOneSidedSpecificationIsValid(double? lower, double? upper)
    {
        var reg1 = Column("Reg1");

        Assert.True(_validator.Validate(Configuration([new CapabilityVariable(reg1.Id, lower, upper)]), [reg1]).IsValid);
    }

    // 2
    [Fact]
    public void EveryVariableKeepsItsOwnSpecification()
    {
        var reg1 = Column("Reg1");
        var reg2 = Column("Reg2");
        var reg3 = Column("Reg3");

        var configuration = Configuration(
        [
            new CapabilityVariable(reg1.Id, 14500, 15500),
            new CapabilityVariable(reg2.Id, 0.1, 0.2),
            new CapabilityVariable(reg3.Id, null, 500)
        ]);

        Assert.True(_validator.Validate(configuration, [reg1, reg2, reg3]).IsValid);
        Assert.Equal([reg1.Id, reg2.Id, reg3.Id], configuration.ToAnalysisConfiguration().VariableColumnIds);
    }

    // 3
    [Fact]
    public void AVariableWithoutAnyLimitCannotBeMeasured()
    {
        var reg1 = Column("Reg1");

        var result = _validator.Validate(Configuration([new CapabilityVariable(reg1.Id, null, null)]), [reg1]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(AnalysisValidationReason.SpecificationLimitMissing, error.Reason);
        Assert.Equal(reg1.Id, error.WorksheetColumnId);
    }

    // 4
    [Theory]
    [InlineData(15500d, 14500d)]
    [InlineData(15000d, 15000d)]
    public void TheLowerLimitMustBeBelowTheUpperOne(double lower, double upper)
    {
        var reg1 = Column("Reg1");

        var result = _validator.Validate(Configuration([new CapabilityVariable(reg1.Id, lower, upper)]), [reg1]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(AnalysisValidationReason.SpecificationLimitsOutOfOrder, error.Reason);
        Assert.Equal(reg1.Id, error.WorksheetColumnId);
    }

    // 5
    [Fact]
    public void ALimitThatIsNotAFiniteNumberIsRejectedAndSaysWhichSide()
    {
        var reg1 = Column("Reg1");

        var lower = _validator.Validate(Configuration([new CapabilityVariable(reg1.Id, double.NaN, 15500)]), [reg1]);
        var upper = _validator.Validate(Configuration([new CapabilityVariable(reg1.Id, 14500, double.PositiveInfinity)]), [reg1]);

        Assert.Equal(AnalysisValidationReason.SpecificationLimitNotNumeric, Assert.Single(lower.Errors).Reason);
        Assert.Equal(AnalysisSpecificationField.LowerSpecificationLimit, lower.Errors[0].Field);
        Assert.Equal(AnalysisSpecificationField.UpperSpecificationLimit, Assert.Single(upper.Errors).Field);
    }

    // 6
    [Fact]
    public void TheAnalysisRulesEveryAnalysisSharesStillApply()
    {
        var reg1 = Column("Reg1");
        var lot = Column("Lot", WorksheetDataType.String);

        var none = _validator.Validate(Configuration([]), [reg1]);
        var duplicate = _validator.Validate(
            Configuration([new CapabilityVariable(reg1.Id, 0, 1), new CapabilityVariable(reg1.Id, 0, 1)]), [reg1]);
        var text = _validator.Validate(Configuration([new CapabilityVariable(lot.Id, 0, 1)]), [reg1, lot]);
        var deleted = _validator.Validate(Configuration([new CapabilityVariable(Guid.NewGuid(), 0, 1)]), [reg1]);

        Assert.Equal(AnalysisValidationReason.NoVariableSelected, Assert.Single(none.Errors).Reason);
        Assert.Equal(AnalysisValidationReason.DuplicateVariable, Assert.Single(duplicate.Errors).Reason);
        Assert.Equal(AnalysisValidationReason.IncompatibleDataType, Assert.Single(text.Errors).Reason);
        Assert.Equal(AnalysisValidationReason.ColumnNotFound, Assert.Single(deleted.Errors).Reason);
    }

    // 7
    [Theory]
    [InlineData(WorksheetDataType.Numeric)]
    [InlineData(WorksheetDataType.String)]
    public void AGroupingColumnMayBeNumericOrString(WorksheetDataType dataType)
    {
        var reg1 = Column("Reg1");
        var site = Column("SITE", dataType);

        Assert.True(_validator.Validate(Configuration([new CapabilityVariable(reg1.Id, 0, 1)], site), [reg1, site]).IsValid);
    }

    // 8
    [Fact]
    public void AGroupingColumnOfAnotherWorksheetIsRejected()
    {
        var reg1 = Column("Reg1");
        var foreign = Column("SITE", WorksheetDataType.String, Guid.NewGuid());

        var result = _validator.Validate(Configuration([new CapabilityVariable(reg1.Id, 0, 1)], foreign), [reg1, foreign]);

        Assert.Equal(AnalysisValidationReason.ColumnFromAnotherWorksheet, Assert.Single(result.Errors).Reason);
    }

    // 9
    [Fact]
    public void AResultThatWouldShowNothingButItsStructuralColumnsIsRejected()
    {
        var reg1 = Column("Reg1");

        var result = _validator.Validate(Configuration([new CapabilityVariable(reg1.Id, 0, 1)], group: null, statistics: []), [reg1]);

        Assert.Equal(AnalysisValidationReason.NoStatisticSelected, Assert.Single(result.Errors).Reason);
    }

    // 10
    [Fact]
    public void TheDefaultDisplaySelectionShowsTheDataBeforeTheIndices()
    {
        Assert.Equal(
            [CapabilityStatistic.Count, CapabilityStatistic.Mean, CapabilityStatistic.WithinStandardDeviation],
            CapabilityAnalysisConfiguration.DefaultDisplayStatistics);

        Assert.DoesNotContain(CapabilityStatistic.Cp, CapabilityAnalysisConfiguration.DefaultDisplayStatistics);
        Assert.DoesNotContain(CapabilityStatistic.Cpk, CapabilityAnalysisConfiguration.DefaultDisplayStatistics);
        Assert.DoesNotContain(CapabilityStatistic.Missing, CapabilityAnalysisConfiguration.DefaultDisplayStatistics);
    }

    // 11
    [Fact]
    public void TheGenericPartOfTheConfigurationIsWhatTheDataQueryNeeds()
    {
        var reg1 = Column("Reg1");
        var site = Column("SITE", WorksheetDataType.String);

        var generic = Configuration([new CapabilityVariable(reg1.Id, 14500, 15500)], site).ToAnalysisConfiguration();

        Assert.Equal(WorksheetId, generic.WorksheetId);
        Assert.Equal([reg1.Id], generic.VariableColumnIds);
        Assert.Equal(site.Id, generic.GroupColumnId);
    }
}
