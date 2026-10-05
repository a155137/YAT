using System.Globalization;
using YAT.Analytics.Statistics;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Turns the observations of one or more measured variables into the boxes of a box plot: it splits every variable
// into its groups, hands each group's observations to YAT.Analytics, and lays the results out along a categorical
// X axis.
//
// The X axis is categories, not values: every variable (or variable and group) gets a slot of its own, one unit wide,
// and the boxes are drawn in those slots. Categories follow the order the variables were selected in, and within a
// variable the order its groups were first observed in - or, for a numeric group column, from the smallest value up
// with "(Missing)" last (Task #059) - the same grouping order the other graphs use.
//
// A group keeps its colour across variables: SITE 1 is the same colour in Reg1 and in Reg2, which is what makes a
// grouped box plot comparable at a glance.
//
// Every box is computed from all of the observations of its variable and group. Only the number of outlier markers
// that are drawn is capped, and only once every box is known.
public sealed class BoxPlotRenderModelBuilder
{
    // The series of observations whose group value is empty. They are plotted, never dropped.
    public const string MissingGroupLabel = "(Missing)";

    // Group values of a numeric group column, and nothing else, are formatted with this.
    private const string GroupValueFormat = "0.####";

    // How far a category's slot reaches on either side of its centre; the slot itself is one unit wide.
    private const double CategoryHalfWidth = 0.5;

    // Cancellation is checked every this many observations (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    private readonly int _maximumRenderedPoints;

    public BoxPlotRenderModelBuilder(int maximumRenderedPoints = DisplaySampling.DefaultMaximumRenderedPoints)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRenderedPoints, 1);
        _maximumRenderedPoints = maximumRenderedPoints;
    }

    // The box plot of these observations, or null when none of them can be plotted. The labels name the worksheet
    // columns; they are given, not looked up.
    public BoxPlotRenderModel? Build(MultiVariableGraphData data, BoxPlotLabels labels, CancellationToken cancellationToken = default) =>
        Build(data, labels, BoxPlotOptions.Default, cancellationToken);

    // The same, drawn with these box plot options (Task #047). The options change nothing that is worked out here -
    // no box, no category, no axis - they are only kept with the model for the renderer.
    public BoxPlotRenderModel? Build(
        MultiVariableGraphData data,
        BoxPlotLabels labels,
        BoxPlotOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.BoxWidthPercent, "A box width is 20 to 90 percent.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (labels.Variables.Count != data.Variables.Count)
        {
            throw new ArgumentException("A box plot needs one label per variable.", nameof(labels));
        }

        var categories = new List<string>();
        var summaries = new List<(int CategoryIndex, int SeriesIndex, string Label, BoxPlotSummary Summary)>();

        // Group colours are decided once for the whole graph, in the order the groups are first observed anywhere - or,
        // for a numeric group column, from the smallest value up with "(Missing)" last (GraphGroupOrder, Task #059).
        var seriesByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var legendEntries = new List<GraphLegendEntry>();
        var isGrouped = data.Variables.Any(variable => variable.Group is not null);
        foreach (var label in NumericGroupLabels(data, cancellationToken))
        {
            if (seriesByGroup.TryAdd(label, seriesByGroup.Count))
            {
                legendEntries.Add(new GraphLegendEntry(label, seriesByGroup[label]));
            }
        }

        for (var index = 0; index < data.Variables.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var variable = data.Variables[index];
            var variableLabel = labels.Variables[index];
            var series = Split(variable, cancellationToken);

            if (series.Count == 0)
            {
                // A variable without a single observation still says what it was: its slot stays, and no box is drawn
                // in it.
                categories.Add(variableLabel);
                continue;
            }

            foreach (var buffer in series)
            {
                var categoryIndex = categories.Count;
                categories.Add(isGrouped ? $"{variableLabel} / {buffer.Label}" : variableLabel);

                var seriesIndex = index;
                if (isGrouped)
                {
                    if (!seriesByGroup.TryGetValue(buffer.Label, out seriesIndex))
                    {
                        seriesIndex = seriesByGroup.Count;
                        seriesByGroup.Add(buffer.Label, seriesIndex);
                        legendEntries.Add(new GraphLegendEntry(buffer.Label, seriesIndex));
                    }
                }

                // All of the observations of this variable and group, and nothing else.
                summaries.Add((categoryIndex, seriesIndex, categories[categoryIndex],
                    BoxPlotSummary.ComputeInPlaceSorting(buffer.Values)));
            }
        }

        var drawable = summaries.Where(item => item.Summary.Count > 0).ToArray();
        if (drawable.Length == 0)
        {
            return null;
        }

        // Only now, with every box decided, is the drawing of the outliers capped.
        var outlierCount = drawable.Sum(item => item.Summary.Outliers.Count);
        var quotas = DisplaySampling.Quotas(
            [.. drawable.Select(item => item.Summary.Outliers.Count)], outlierCount, _maximumRenderedPoints);

        var boxes = new List<BoxPlotBoxRenderModel>(drawable.Length);
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;

        for (var index = 0; index < drawable.Length; index++)
        {
            var (categoryIndex, seriesIndex, label, summary) = drawable[index];
            boxes.Add(new BoxPlotBoxRenderModel(
                label,
                categoryIndex,
                seriesIndex,
                summary.LowerWhisker!.Value,
                summary.FirstQuartile!.Value,
                summary.Median!.Value,
                summary.ThirdQuartile!.Value,
                summary.UpperWhisker!.Value,
                summary.Mean!.Value,
                Sample(summary.Outliers, quotas[index]),
                summary.Count,
                summary.Outliers.Count));

            // The axis describes the whole statistical model, not only what happens to be drawn: the quartiles and the
            // median (an interpolated quartile can lie beyond its whisker), the whiskers, the mean - which extreme
            // outliers can pull past a whisker - and every outlier, drawn or not, so the scale does not change when the
            // display is capped.
            foreach (var value in (double[])
                     [
                         summary.FirstQuartile.Value, summary.Median.Value, summary.ThirdQuartile.Value,
                         summary.LowerWhisker.Value, summary.UpperWhisker.Value, summary.Mean!.Value
                     ])
            {
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }

            foreach (var outlier in summary.Outliers)
            {
                minimum = Math.Min(minimum, outlier);
                maximum = Math.Max(maximum, outlier);
            }
        }

        var horizontal = new GraphAxisRange(1 - CategoryHalfWidth, categories.Count + CategoryHalfWidth);
        var vertical = GraphAxisRanges.FromValues(minimum, maximum);

        var frame = new GraphRenderModel(
            Title(labels),
            new GraphAxisModel(horizontal, CategoryTicks(categories), isGrouped ? labels.GroupColumn : null)
            {
                Scale = GraphAxisScale.Categorical
            },
            new GraphAxisModel(vertical, GraphAxisTicks.Nice(vertical)),
            // Without a group column the X axis already names every box, so a legend would repeat it.
            isGrouped && legendEntries.Count > 0 ? new GraphLegendModel(legendEntries, labels.GroupColumn) : null);

        return new BoxPlotRenderModel(frame, categories, boxes, data.Count, outlierCount) { Options = options };
    }

    // Where a category sits on the X axis: the first is at 1, the second at 2, and each one owns half a unit either
    // side of its centre.
    public static double CategoryPosition(int categoryIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(categoryIndex);
        return categoryIndex + 1;
    }

    private static IReadOnlyList<GraphAxisTick> CategoryTicks(IReadOnlyList<string> categories) =>
        [.. categories.Select((label, index) => new GraphAxisTick(CategoryPosition(index), label))];

    // The observations of one variable, split into the series a box is drawn for: one per group in the order they are
    // drawn (GraphGroupOrder), or a single unnamed series when the graph has no group column.
    private static List<SeriesBuffer> Split(UnivariateGraphData data, CancellationToken cancellationToken)
    {
        var series = new List<SeriesBuffer>();
        var values = data.Values.Span;
        var group = data.Group;
        var textGroups = group is StringGroupData text ? text.Values.Span : default;
        var numericGroups = group is NumericGroupData numeric ? numeric.Values.Span : default;

        Dictionary<string, SeriesBuffer>? byText = null;
        Dictionary<double, SeriesBuffer>? byNumber = null;
        SeriesBuffer? missing = null;
        SeriesBuffer? ungrouped = null;

        for (var row = 0; row < data.Count; row++)
        {
            if ((row & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // The raw store rejects non-finite numbers, so this is a guard, not a filter with data behind it.
            var value = values[row];
            if (!double.IsFinite(value))
            {
                continue;
            }

            SeriesBuffer buffer;
            if (group is null)
            {
                buffer = ungrouped ??= Add(series, string.Empty);
            }
            else if (group.IsMissing(row))
            {
                buffer = missing ??= Add(series, MissingGroupLabel);
            }
            else if (!textGroups.IsEmpty)
            {
                var key = textGroups[row]!;
                byText ??= new Dictionary<string, SeriesBuffer>(StringComparer.Ordinal);
                if (!byText.TryGetValue(key, out var found))
                {
                    found = Add(series, key);
                    byText.Add(key, found);
                }

                buffer = found;
            }
            else
            {
                var key = numericGroups[row]!.Value;
                byNumber ??= [];
                if (!byNumber.TryGetValue(key, out var found))
                {
                    found = Add(series, key.ToString(GroupValueFormat, CultureInfo.InvariantCulture));
                    byNumber.Add(key, found);
                }

                buffer = found;
            }

            buffer.Add(value);
        }

        // Numeric groups from the smallest up, "(Missing)" last; text groups as first seen (GraphGroupOrder, Task #059).
        return GraphGroupOrder.Arrange(series, byNumber, missing);
    }

    // The labels of a numeric group column's groups across every variable, in the order they are drawn: every value a
    // kept observation has, from the smallest up, then "(Missing)" if any kept observation has none. Empty for a text
    // group column, whose groups keep the order they are first observed in.
    private static IEnumerable<string> NumericGroupLabels(MultiVariableGraphData data, CancellationToken cancellationToken)
    {
        if (!data.Variables.Any(variable => variable.Group is NumericGroupData))
        {
            return [];
        }

        var numbers = new HashSet<double>();
        var anyMissing = false;
        foreach (var variable in data.Variables)
        {
            if (variable.Group is not NumericGroupData group)
            {
                continue;
            }

            var values = variable.Values.Span;
            var groups = group.Values.Span;
            for (var row = 0; row < variable.Count; row++)
            {
                if ((row & CancellationCheckMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (!double.IsFinite(values[row]))
                {
                    continue;
                }

                if (group.IsMissing(row))
                {
                    anyMissing = true;
                }
                else
                {
                    numbers.Add(groups[row]!.Value);
                }
            }
        }

        return [.. numbers.Order().Select(number => number.ToString(GroupValueFormat, CultureInfo.InvariantCulture)),
            .. anyMissing ? [MissingGroupLabel] : Array.Empty<string>()];
    }

    private static SeriesBuffer Add(List<SeriesBuffer> series, string label)
    {
        var buffer = new SeriesBuffer(label);
        series.Add(buffer);
        return buffer;
    }

    // The outliers this box draws: all of them, or a deterministic sample of them when the display budget is spent.
    private static ReadOnlyMemory<double> Sample(IReadOnlyList<double> outliers, int quota)
    {
        if (quota >= outliers.Count)
        {
            return outliers.ToArray();
        }

        if (quota == 0)
        {
            return ReadOnlyMemory<double>.Empty;
        }

        var sampled = new double[quota];
        for (var index = 0; index < quota; index++)
        {
            sampled[index] = outliers[DisplaySampling.SampleIndex(index, outliers.Count, quota)];
        }

        return sampled;
    }

    private static string Title(BoxPlotLabels labels) => $"Boxplot of {string.Join(", ", labels.Variables)}";

    // The observations of one box, gathered in worksheet row order and sorted in place by Analytics afterwards.
    private sealed class SeriesBuffer(string label)
    {
        private double[] _items = new double[256];
        private int _count;

        public string Label { get; } = label;

        public Span<double> Values => _items.AsSpan(0, _count);

        public void Add(double value)
        {
            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }

            _items[_count++] = value;
        }
    }
}
