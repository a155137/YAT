using YAT.Domain.Enums;

namespace YAT.Domain.Entities;

public class Analysis
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid WorksheetId { get; set; }

    public string Name { get; set; } = string.Empty;

    public AnalysisType Type { get; set; }

    public string Configuration { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
