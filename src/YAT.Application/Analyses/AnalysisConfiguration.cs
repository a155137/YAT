namespace YAT.Application.Analyses;

// What a worksheet column is used for in an analysis: a measured variable to summarise, or the categorical column the
// observations are grouped by. Later analyses may add roles (specification limits, panels, weights).
public enum AnalysisColumnRole
{
    Variable,
    Group
}

// An analysis of one worksheet: the variables it summarises and, optionally, the column it groups them by. Columns are
// referenced by their stable Id, because a column's name and index may change without changing what was configured.
//
// It is the analysis counterpart of a graph's configuration, deliberately its own type: an analysis produces a table,
// not a plot, and the two must be free to grow apart. Validate it with AnalysisConfigurationValidator before using it;
// nothing here reads worksheet values.
public sealed record AnalysisConfiguration(Guid WorksheetId, IReadOnlyList<Guid> VariableColumnIds, Guid? GroupColumnId);
