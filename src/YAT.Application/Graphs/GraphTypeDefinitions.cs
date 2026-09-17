using YAT.Domain.Enums;

namespace YAT.Application.Graphs;

// The V1 graph specifications. Measured values are Numeric; a Group may also be a String column (how groups are drawn
// is not decided here: the configuration only records the assignment).
public static class GraphTypeDefinitions
{
    private static readonly IReadOnlyList<WorksheetDataType> Numeric = [WorksheetDataType.Numeric];

    private static readonly IReadOnlyList<WorksheetDataType> NumericOrString = [WorksheetDataType.Numeric, WorksheetDataType.String];

    private static readonly GraphRoleDefinition GroupRole = new(GraphVariableRole.Group, "Group", IsRequired: false, NumericOrString);

    private static readonly IReadOnlyList<GraphTypeDefinition> Definitions =
    [
        new(GraphType.ScatterPlot, "Scatter Plot",
        [
            new(GraphVariableRole.X, "X-axis", IsRequired: true, Numeric),
            new(GraphVariableRole.Y, "Y-axis", IsRequired: true, Numeric),
            GroupRole
        ]),
        // A histogram names its roles the way the people who use it do; the role and the column id are what identify
        // them, so the wording is presentation only.
        new(GraphType.Histogram, "Histogram",
        [
            new(GraphVariableRole.Variable, "Graph variables", IsRequired: true, Numeric),
            new(GraphVariableRole.Group, "Categorical variable for grouping", IsRequired: false, NumericOrString)
        ]),
        new(GraphType.ProbabilityPlot, "Probability Plot", [SingleVariableRole, GroupRole]),
        new(GraphType.EmpiricalCdf, "Empirical CDF", [SingleVariableRole, GroupRole])
    ];

    private static GraphRoleDefinition SingleVariableRole =>
        new(GraphVariableRole.Variable, "Variable", IsRequired: true, Numeric);

    // In menu order.
    public static IReadOnlyList<GraphTypeDefinition> All => Definitions;

    public static bool TryGet(GraphType graphType, out GraphTypeDefinition definition)
    {
        definition = Definitions.FirstOrDefault(candidate => candidate.GraphType == graphType)!;
        return definition is not null;
    }

    public static GraphTypeDefinition For(GraphType graphType) =>
        TryGet(graphType, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(graphType), graphType, "Unknown graph type.");
}
