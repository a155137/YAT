using System.Globalization;
using System.Text;

namespace YAT.App.Tests.Robustness;

// One dataset the robustness harness runs through the graphs: worksheet rows of one measured variable, optionally a
// grouping column and a paired second variable (for scatter plots).
//
// Rows are kept as the worksheet would hold them - a null is an empty cell - so the same case can be compacted the
// way the graph data pipeline compacts it (builder level) or written to a real project and read back (end to end).
// Every value is finite: the harness looks for failures on valid data, not for rejection of invalid data.
internal sealed record RobustnessCase
{
    public required string Name { get; init; }

    public required string Family { get; init; }

    // Where the case came from: "named", or "generated seed=<seed> case=<n>" - enough to rebuild it exactly.
    public required string Origin { get; init; }

    // The generator parameters (or a short note for a named case), for the failure message.
    public string Parameters { get; init; } = string.Empty;

    // One entry per worksheet row; null is an empty cell.
    public required double?[] Values { get; init; }

    // One group label per row, or null when the case has no grouping column. A null entry is an empty group cell.
    public string?[]? Groups { get; init; }

    // Whether the grouping column is Numeric (its labels are numbers written in the invariant culture).
    public bool NumericGroups { get; init; }

    // One Y per row for a scatter plot (Values are then X), or null when the case is univariate only.
    public double?[]? PairedY { get; init; }

    public int RowCount => Values.Length;

    public bool IsGrouped => Groups is not null;

    public bool IsPaired => PairedY is not null;

    // The observations a univariate graph keeps: the rows with a value.
    public int ObservationCount => Values.Count(value => value is not null);

    // The observations a scatter plot keeps: the rows with both values.
    public int PairCount => PairedY is null ? 0 : Values.Where((value, row) => value is not null && PairedY[row] is not null).Count();

    // Everything needed to recognise and rebuild the case, put in front of every failure it causes. Small cases carry
    // their values in round-trip form, so they can be pasted into a unit test as they are.
    public string Describe(string graph)
    {
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"[{graph}] case={Name} family={Family} {Origin} rows={RowCount} N={ObservationCount}");

        if (IsPaired)
        {
            text.Append(CultureInfo.InvariantCulture, $" pairs={PairCount}");
        }

        if (IsGrouped)
        {
            text.Append(CultureInfo.InvariantCulture, $" groups={Groups!.Where(label => label is not null).Distinct().Count()}{(NumericGroups ? "(numeric)" : string.Empty)}");
        }

        if (Parameters.Length > 0)
        {
            text.Append(' ').Append(Parameters);
        }

        if (RowCount <= 64)
        {
            text.Append("\n  values: [").Append(string.Join(", ", Values.Select(Format))).Append(']');
            if (PairedY is not null)
            {
                text.Append("\n  paired y: [").Append(string.Join(", ", PairedY.Select(Format))).Append(']');
            }

            if (Groups is not null)
            {
                text.Append("\n  groups: [").Append(string.Join(", ", Groups.Select(label => label is null ? "null" : $"\"{label}\""))).Append(']');
            }
        }

        return text.ToString();
    }

    public override string ToString() => Name;

    private static string Format(double? value) =>
        value is { } number ? number.ToString("R", CultureInfo.InvariantCulture) : "null";
}
