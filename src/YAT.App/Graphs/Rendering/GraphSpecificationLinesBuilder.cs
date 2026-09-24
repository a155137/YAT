using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Analyses;

namespace YAT.app.Graphs.Rendering;

// Puts a graph's specification on its frame: one vertical line for each value the specification has - LSL, Target,
// USL, in that order - labelled with its value, and an X axis wide enough to show every one of them.
//
// It works on the frame alone. The plot, its statistics, its bins, its fitted lines and its sampling were decided by
// the graph type's builder from the data and are not touched; only the displayed X range (and so its ticks) may grow.
// That is why it needs no graph data: nothing is read, copied, sorted or recomputed, whatever the size of the graph.
//
// A graph whose type does not declare SpecificationLines, and an empty specification, keep the very frame they came
// with.
public static class GraphSpecificationLinesBuilder
{
    public const string LowerLimitLabel = "LSL";
    public const string TargetLabel = "Target";
    public const string UpperLimitLabel = "USL";

    public static GraphRenderModel Attach(GraphRenderModel frame, GraphTypeDefinition definition, Specification specification)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(specification);

        if (!definition.Supports(GraphCapability.SpecificationLines) || specification.IsEmpty)
        {
            return frame;
        }

        var lines = Lines(specification);
        if (lines.Count == 0)
        {
            return frame;
        }

        // Lines of other kinds a frame may carry are kept; a specification's own are replaced, so attaching the same
        // specification again changes nothing.
        var withLines = frame.WithReferenceLines(
        [
            .. frame.ReferenceLines.Where(line => !IsSpecificationLine(line)),
            .. lines
        ]);

        var axis = frame.XAxis;
        var range = GraphAxisRanges.Including(axis.Range, [.. lines.Select(line => line.Value)]);
        return range == axis.Range
            ? withLines
            : withLines.WithXAxis(
                new GraphAxisModel(range, GraphAxisTicks.Nice(range), axis.Title) { Scale = axis.Scale });
    }

    // The lines of a specification, LSL first and USL last. A value that is not finite has no place on an axis and gets
    // no line; a validated specification has none.
    public static IReadOnlyList<GraphReferenceLine> Lines(Specification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var lines = new List<GraphReferenceLine>(3);
        Add(lines, specification.LowerLimit, LowerLimitLabel, GraphReferenceLineKind.SpecificationLimit);
        Add(lines, specification.Target, TargetLabel, GraphReferenceLineKind.Target);
        Add(lines, specification.UpperLimit, UpperLimitLabel, GraphReferenceLineKind.SpecificationLimit);
        return lines;
    }

    // "LSL 14.5": the name, then the value as YAT shows every statistic (G8, invariant culture).
    public static string Label(string name, double value) => $"{name} {AnalysisNumberFormat.Statistic(value)}";

    private static void Add(List<GraphReferenceLine> lines, double? value, string name, GraphReferenceLineKind kind)
    {
        if (value is { } number && double.IsFinite(number))
        {
            lines.Add(new GraphReferenceLine(GraphReferenceAxis.X, number, Label(name, number), kind));
        }
    }

    private static bool IsSpecificationLine(GraphReferenceLine line) =>
        line.Kind is GraphReferenceLineKind.SpecificationLimit or GraphReferenceLineKind.Target;
}
