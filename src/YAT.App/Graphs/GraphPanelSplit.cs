using System.Globalization;
using YAT.Application.Graphs;

namespace YAT.app.Graphs;

// The observations of one panel of a graph drawn in panels (Task #058), and the title it is drawn under.
public sealed record GraphPanelData(string Title, GraphData Data);

// Splits a graph's observations by the values of its Panel column (Task #058). The rows were filtered when they were
// read, so only the rows the filter kept are split. A numeric column's panels go from its smallest value up; a text
// column's follow the order their values are first seen - the first variable's rows first, then the next variable's,
// as groups are ranked when variables are drawn together. Rows without a panel value make a "(Missing)" panel of their
// own: after every number in a numeric column, where it is first seen in a text column. Within
// a panel the observations keep their order, each with its own group value.
//
// At most GraphPanelLayout.MaximumPanels panels, Missing included: a column with more values is refused with a message
// asking the user to filter first, before anything is built.
public static class GraphPanelSplit
{
    public const string MissingLabel = "(Missing)";

    // Panel values of a numeric column are written the way group values are.
    private const string ValueFormat = "0.####";

    public static bool HasPanels(GraphData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data is MultiVariableGraphData multi
            ? multi.Variables.Any(variable => variable.Panel is not null)
            : data.Panel is not null;
    }

    public static string TooManyPanelsMessage(string column) =>
        $"The panel column \"{column}\" has more than {Rendering.GraphPanelLayout.MaximumPanels} values in the rows used. " +
        $"Filter the rows down to {Rendering.GraphPanelLayout.MaximumPanels} values or fewer first.";

    // "Site = 1", "Lot = A", "Site = (Missing)".
    public static string Title(string column, string value) => $"{column} = {value}";

    public static IReadOnlyList<GraphPanelData> Split(GraphData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sets = data is MultiVariableGraphData multi ? [.. multi.Variables] : new[] { data };
        var column = sets.Select(set => set.Panel?.Column.Name).FirstOrDefault(name => name is not null)
            ?? throw new ArgumentException("The graph has no panel column.", nameof(data));

        // Every panel value, ranked where it is first seen; each observation's rank, set by set.
        var ranks = new Dictionary<PanelKey, int>();
        var keys = new List<PanelKey>();
        var rowsOf = new List<int>[sets.Length][];
        for (var set = 0; set < sets.Length; set++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var panel = sets[set].Panel;
            var count = sets[set].Count;
            var byRank = new List<List<int>>();
            for (var row = 0; row < count; row++)
            {
                var key = PanelKey.Of(panel, row);
                if (!ranks.TryGetValue(key, out var rank))
                {
                    rank = ranks.Count;
                    if (rank >= Rendering.GraphPanelLayout.MaximumPanels)
                    {
                        throw new GraphPreparationException(TooManyPanelsMessage(column));
                    }

                    ranks.Add(key, rank);
                    keys.Add(key);
                }

                while (byRank.Count <= rank)
                {
                    byRank.Add([]);
                }

                byRank[rank].Add(row);
            }

            rowsOf[set] = [.. byRank];
        }

        var order = Order(keys, numeric: sets.Any(set => set.Panel is NumericGroupData));
        var panels = new GraphPanelData[order.Length];
        for (var place = 0; place < order.Length; place++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rank = order[place];
            var title = Title(column, keys[rank].Label);
            panels[place] = data switch
            {
                MultiVariableGraphData several => new GraphPanelData(title, new MultiVariableGraphData(
                    several.GraphType,
                    several.WorksheetId,
                    [.. several.Variables.Select((variable, set) => Subset(variable, RowsOf(rowsOf[set], rank)))])),
                _ => new GraphPanelData(title, Subset(data, RowsOf(rowsOf[0], rank)))
            };
        }

        return panels;
    }

    // The panels in the order they are drawn, as ranks: a numeric column's values from the smallest up and "(Missing)"
    // last; a text column's where they are first seen, "(Missing)" included - as a group column's groups are drawn
    // (Rendering.GraphGroupOrder, Task #059).
    private static int[] Order(List<PanelKey> keys, bool numeric)
    {
        var byNumber = numeric
            ? keys.Select((key, rank) => (key, rank))
                .Where(item => !item.key.IsMissing)
                .ToDictionary(item => item.key.Number, item => item.rank)
            : null;
        return Rendering.GraphGroupOrder.Order(keys.Count, byNumber, keys.FindIndex(key => key.IsMissing));
    }

    private static IReadOnlyList<int> RowsOf(List<int>[] byRank, int rank) => rank < byRank.Length ? byRank[rank] : [];

    private static GraphData Subset(GraphData data, IReadOnlyList<int> rows) => data switch
    {
        ScatterGraphData scatter => new ScatterGraphData(
            scatter.WorksheetId, scatter.X, scatter.Y, Pick(scatter.XValues, rows), Pick(scatter.YValues, rows), Subset(scatter.Group, rows)),
        UnivariateGraphData univariate => Subset(univariate, rows),
        _ => throw new ArgumentException($"A {data.GraphType} graph cannot be drawn in panels.", nameof(data))
    };

    private static UnivariateGraphData Subset(UnivariateGraphData variable, IReadOnlyList<int> rows) =>
        new(variable.GraphType, variable.WorksheetId, variable.Variable, Pick(variable.Values, rows), Subset(variable.Group, rows));

    private static GraphGroupData? Subset(GraphGroupData? group, IReadOnlyList<int> rows) => group switch
    {
        null => null,
        NumericGroupData numeric => new NumericGroupData(numeric.Column, Pick(numeric.Values, rows)),
        StringGroupData text => new StringGroupData(text.Column, Pick(text.Values, rows)),
        _ => throw new ArgumentException("Unknown group data.", nameof(group))
    };

    private static T[] Pick<T>(ReadOnlyMemory<T> values, IReadOnlyList<int> rows)
    {
        var span = values.Span;
        var picked = new T[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            picked[index] = span[rows[index]];
        }

        return picked;
    }

    // A panel value as panels are told apart: a text value, a number, or no value.
    private readonly record struct PanelKey(int Kind, string? Text, double Number)
    {
        private const int Missing = 0;
        private const int TextValue = 1;
        private const int NumberValue = 2;

        public bool IsMissing => Kind == Missing;

        public string Label => Kind switch
        {
            Missing => MissingLabel,
            TextValue => Text!,
            _ => Number.ToString(ValueFormat, CultureInfo.InvariantCulture)
        };

        public static PanelKey Of(GraphGroupData? panel, int row)
        {
            if (panel is null || panel.IsMissing(row))
            {
                return new PanelKey(Missing, null, 0);
            }

            return panel switch
            {
                StringGroupData text => new PanelKey(TextValue, text.Values.Span[row], 0),
                NumericGroupData numeric => new PanelKey(NumberValue, null, numeric.Values.Span[row]!.Value),
                _ => throw new ArgumentException("Unknown panel data.", nameof(panel))
            };
        }
    }
}
