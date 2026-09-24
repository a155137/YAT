namespace YAT.Application.Graphs;

// How the variables of one graph setup are drawn when there are several of them.
public enum GraphVariableLayout
{
    // One graph with every variable in it.
    Together,

    // One graph for each variable.
    Separate
}

// What a confirmed graph setup asks for: its configuration and, for a graph type that offers
// GraphCapability.VariableLayout, whether several variables are drawn together or each in a graph of its own.
//
// The layout is how the graphs are launched, not part of any graph: every graph is still described by one
// GraphConfiguration, and a layout means something only when the configuration has more than one variable.
public sealed record GraphSetupRequest(
    GraphConfiguration Configuration,
    GraphVariableLayout Layout = GraphVariableLayout.Together)
{
    // The configuration of one variable's own graph when the variables are drawn separately: the same options, labels
    // and specification, and only that variable (with the grouping column) assigned - what setting up that one
    // variable would have given.
    public static GraphConfiguration ForVariable(GraphConfiguration configuration, Guid variableColumnId)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration with
        {
            Assignments =
            [
                .. configuration.Assignments.Where(assignment =>
                    assignment.Role != GraphVariableRole.Variable || assignment.WorksheetColumnId == variableColumnId)
            ]
        };
    }
}
