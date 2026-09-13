using YAT.Domain.Enums;

namespace YAT.Domain.Entities;

public class WorksheetColumn
{
    public Guid Id { get; set; }

    public Guid WorksheetId { get; set; }

    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public WorksheetDataType DataType { get; set; }

    public ColumnSemanticType? SemanticType { get; set; }

    public string? Unit { get; set; }
}
