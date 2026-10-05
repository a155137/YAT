using System.Globalization;
using YAT.Application.Graphs;
using YAT.Domain.Enums;

namespace YAT.app.Graphs.Rendering;

// Several variables drawn together in one histogram, probability plot or empirical CDF (GraphVariableLayout.Together).
//
// They become one variable whose series are the variables - or, with a grouping column, each variable's groups - so the
// graph types' own builders and the statistics panel draw them exactly as they draw groups: one set of bins for every
// observation, one fitted line and one curve per series, each series counted, scaled and summarised on its own
// observations, colours taken from the palette in series order. Nothing about the variables is computed here.
//
// A series is labelled "Variable", or "Variable / Group" ("Variable / (Missing)" for rows without a group value). The
// series follow the order the variables were selected in and, within a variable, the order the groups were first seen
// in anywhere - across all variables - so a group has the same place in every variable; the groups of a numeric column
// go from the smallest value up instead, "(Missing)" last (GraphGroupOrder, Task #059). Within a series the
// observations keep their worksheet order, so a series holds exactly the observations, in the same order, that the
// variable's own graph would give that group.
public static class GraphVariablesTogether
{
    // The X axis title of variables drawn together.
    public const string AxisTitle = "Data";

    // The header of the series column in the legend and the statistics panel.
    public const string VariableHeader = "Variable";

    // Cancellation is checked every this many observations (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    // The same labels the graph types give groups.
    private const string MissingGroupLabel = "(Missing)";
    private const string GroupValueFormat = "0.####";

    // The name the variables share on the graph: "Reg1, Reg2".
    public static string Name(IEnumerable<string> variables) => string.Join(", ", variables);

    // The header of the series: "Variable", or "Variable / Lot" when the series are groups of the variables.
    public static string Header(string? groupColumn) =>
        groupColumn is null ? VariableHeader : $"{VariableHeader} / {groupColumn}";

    public static string SeriesLabel(string variable, string? group) =>
        group is null ? variable : $"{variable} / {group}";

    // The variables as one variable of series. Only the values and a label for each are copied; the observations of a
    // million-row worksheet are copied once, in one pass per variable after the groups are ranked.
    public static UnivariateGraphData Combine(
        MultiVariableGraphData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        var variables = data.Variables;
        var groupColumn = variables
            .Select(variable => variable.Group?.Column)
            .FirstOrDefault(column => column is not null);

        // Every group, ranked by where it is first seen: the first variable's rows first, then the next variable's.
        var ranks = new Dictionary<GroupKey, int>();
        var rankOf = new int[variables.Count][];
        var total = 0;
        for (var index = 0; index < variables.Count; index++)
        {
            var variable = variables[index];
            var values = variable.Values.Span;
            var rows = new int[variable.Count];
            for (var row = 0; row < variable.Count; row++)
            {
                if ((row & CancellationCheckMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // The raw store rejects non-finite numbers, so this is a guard, as it is in the builders.
                if (!double.IsFinite(values[row]))
                {
                    rows[row] = -1;
                    continue;
                }

                var key = GroupKey.Of(variable.Group, row);
                if (!ranks.TryGetValue(key, out var rank))
                {
                    rank = ranks.Count;
                    ranks.Add(key, rank);
                }

                rows[row] = rank;
                total++;
            }

            rankOf[index] = rows;
        }

        // Numeric groups from the smallest up, "(Missing)" last; text groups as first seen (GraphGroupOrder, Task #059).
        var byNumber = ranks.Where(entry => entry.Key.IsNumber).ToDictionary(entry => entry.Key.Number, entry => entry.Value);
        if (byNumber.Count > 0)
        {
            var missing = ranks.Where(entry => entry.Key.IsMissing).Select(entry => entry.Value).DefaultIfEmpty(-1).First();
            var order = GraphGroupOrder.Order(ranks.Count, byNumber, missing);
            var placeOf = new int[order.Length];
            for (var place = 0; place < order.Length; place++)
            {
                placeOf[order[place]] = place;
            }

            foreach (var key in ranks.Keys.ToArray())
            {
                ranks[key] = placeOf[ranks[key]];
            }

            foreach (var rows in rankOf)
            {
                for (var row = 0; row < rows.Length; row++)
                {
                    if (rows[row] >= 0)
                    {
                        rows[row] = placeOf[rows[row]];
                    }
                }
            }
        }

        var labelsByRank = new string?[ranks.Count];
        foreach (var (key, rank) in ranks)
        {
            labelsByRank[rank] = key.Label;
        }

        // Each variable's observations laid out group by group in rank order, each group in worksheet order.
        var combined = new double[total];
        var series = new string?[total];
        var written = 0;
        for (var index = 0; index < variables.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variable = variables[index];
            var values = variable.Values.Span;
            var rows = rankOf[index];

            var starts = new int[ranks.Count + 1];
            foreach (var rank in rows)
            {
                if (rank >= 0)
                {
                    starts[rank + 1]++;
                }
            }

            for (var rank = 0; rank < ranks.Count; rank++)
            {
                starts[rank + 1] += starts[rank];
            }

            var kept = starts[ranks.Count];

            var labels = new string[ranks.Count];
            for (var rank = 0; rank < ranks.Count; rank++)
            {
                labels[rank] = SeriesLabel(variable.Variable.Name, labelsByRank[rank]);
            }

            for (var row = 0; row < rows.Length; row++)
            {
                var rank = rows[row];
                if (rank < 0)
                {
                    continue;
                }

                var at = written + starts[rank]++;
                combined[at] = values[row];
                series[at] = labels[rank];
            }

            written += kept;
        }

        return new UnivariateGraphData(
            data.GraphType,
            data.WorksheetId,
            new GraphColumnInfo(
                Guid.Empty,
                Name(variables.Select(variable => variable.Variable.Name)),
                WorksheetDataType.Numeric),
            combined,
            new StringGroupData(
                new GraphColumnInfo(Guid.Empty, Header(groupColumn?.Name), WorksheetDataType.String),
                series));
    }

    // A group as the graph types tell groups apart: a text value, a number, or no value; null when there is no
    // grouping column at all.
    private readonly record struct GroupKey(int Kind, string? Text, double Number)
    {
        private const int None = 0;
        private const int Missing = 1;
        private const int TextValue = 2;
        private const int NumberValue = 3;

        public bool IsNumber => Kind == NumberValue;

        public bool IsMissing => Kind == Missing;

        public string? Label => Kind switch
        {
            None => null,
            Missing => MissingGroupLabel,
            TextValue => Text,
            _ => Number.ToString(GroupValueFormat, CultureInfo.InvariantCulture)
        };

        public static GroupKey Of(GraphGroupData? group, int row)
        {
            if (group is null)
            {
                return new GroupKey(None, null, 0);
            }

            if (group.IsMissing(row))
            {
                return new GroupKey(Missing, null, 0);
            }

            return group switch
            {
                StringGroupData text => new GroupKey(TextValue, text.Values.Span[row], 0),
                NumericGroupData numeric => new GroupKey(NumberValue, null, numeric.Values.Span[row]!.Value),
                _ => throw new ArgumentException("Unknown group data.", nameof(group))
            };
        }
    }
}
