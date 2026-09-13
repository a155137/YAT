using YAT.Domain.Enums;

namespace YAT.app.ViewModels;

// Selector item for the optional semantic type; Value null is displayed as "None".
public sealed record SemanticTypeOption(ColumnSemanticType? Value, string Label)
{
    // Used by accessibility/automation text in place of the record's generated ToString().
    public override string ToString() => Label;
}
