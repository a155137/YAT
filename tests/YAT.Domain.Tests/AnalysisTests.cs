using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Domain.Tests;

public class AnalysisTests
{
    [Fact]
    public void AssignedValuesArePreserved()
    {
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var worksheetId = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 3, 10, 14, 0, 0, TimeSpan.Zero);

        var analysis = new Analysis
        {
            Id = id,
            ProjectId = projectId,
            WorksheetId = worksheetId,
            Name = "Vth Histogram",
            Type = AnalysisType.Histogram,
            Configuration = "{}",
            CreatedAt = created,
            UpdatedAt = created
        };

        Assert.Equal(id, analysis.Id);
        Assert.Equal(projectId, analysis.ProjectId);
        Assert.Equal(worksheetId, analysis.WorksheetId);
        Assert.Equal("Vth Histogram", analysis.Name);
        Assert.Equal(AnalysisType.Histogram, analysis.Type);
        Assert.Equal("{}", analysis.Configuration);
        Assert.Equal(created, analysis.CreatedAt);
    }

    [Fact]
    public void ConfigurationIsStoredVerbatimWithoutParsing()
    {
        const string arbitrary = "not-json-at-all";

        var analysis = new Analysis { Configuration = arbitrary };

        Assert.Equal(arbitrary, analysis.Configuration);
    }

    [Fact]
    public void ConfigurationCarriesMultipleTargetColumnsAndGrouping()
    {
        const string configuration = """
            {
              "targetColumns": ["Vth", "Idsat", "Ioff"],
              "groupBy": ["Lot", "Wafer"],
              "binMethod": "Auto",
              "binCount": 30
            }
            """;

        var analysis = new Analysis
        {
            Type = AnalysisType.Histogram,
            Configuration = configuration
        };

        Assert.Equal(configuration, analysis.Configuration);
    }

    [Theory]
    [InlineData(AnalysisType.Statistics)]
    [InlineData(AnalysisType.Histogram)]
    [InlineData(AnalysisType.ProbabilityPlot)]
    public void EveryAnalysisTypeSharesTheSameConfigurationSlot(AnalysisType type)
    {
        const string configuration = """{"targetColumns":["Vth","Idsat"],"groupBy":["Site"]}""";

        var analysis = new Analysis
        {
            Type = type,
            Configuration = configuration
        };

        Assert.Equal(type, analysis.Type);
        Assert.Equal(configuration, analysis.Configuration);
    }
}
