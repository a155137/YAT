namespace YAT.Application.Graphs;

// Where one of a graph's labels comes from.
public enum GraphLabelMode
{
    // The label the graph type gives it: its name and variable for a title, the column or scale for an axis. A label
    // the graph type leaves empty stays empty.
    Auto,

    // The text the user typed, in place of the graph type's label.
    Custom,

    // No label at all, and no room kept for one.
    Hidden
}

// One label of a graph: where it comes from, and the text typed for Custom. Only Custom reads the text.
public sealed record GraphLabelOption(GraphLabelMode Mode = GraphLabelMode.Auto, string? Text = null)
{
    public static GraphLabelOption Auto { get; } = new();

    public static GraphLabelOption Hidden { get; } = new(GraphLabelMode.Hidden);

    public static GraphLabelOption Custom(string text) => new(GraphLabelMode.Custom, text);
}

// Which label of a graph an option or a problem is about.
public enum GraphLabelField
{
    Title,
    XAxisTitle,
    YAxisTitle
}

// The labels every graph has: its title and the titles of its two axes. Like GraphPresentationOptions these change only
// what is written around the plot, never the observations, the plot or the axes' ranges, so the graph data query and
// the graph types' builders never read them. They are applied last, to the finished frame (see GraphPresentation).
//
// Only a graph type that declares GraphCapability.Labels reads them; every other graph type ignores them, so a
// configuration can always carry the defaults.
public sealed record GraphLabelOptions(GraphLabelOption Title, GraphLabelOption XAxisTitle, GraphLabelOption YAxisTitle)
{
    // Every label as the graph type gives it: the graph as it always was.
    public static GraphLabelOptions Default { get; } =
        new(GraphLabelOption.Auto, GraphLabelOption.Auto, GraphLabelOption.Auto);

    public GraphLabelOption For(GraphLabelField field) => field switch
    {
        GraphLabelField.Title => Title,
        GraphLabelField.XAxisTitle => XAxisTitle,
        GraphLabelField.YAxisTitle => YAxisTitle,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown graph label.")
    };
}

// One reason label options cannot be used.
public enum GraphLabelProblemKind
{
    // The mode is not one of the defined values.
    UnknownMode,

    // Custom, but nothing is left of the text once it is normalized.
    CustomTextMissing
}

public sealed record GraphLabelProblem(GraphLabelField Field, GraphLabelProblemKind Kind);

// The rules label options obey, and how a label is worked out from its option and the graph type's own label.
public static class GraphLabelRules
{
    public static readonly IReadOnlyList<GraphLabelField> Fields =
        [GraphLabelField.Title, GraphLabelField.XAxisTitle, GraphLabelField.YAxisTitle];

    public static IReadOnlyList<GraphLabelProblem> Check(GraphLabelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<GraphLabelProblem>();
        foreach (var field in Fields)
        {
            var option = options.For(field);
            ArgumentNullException.ThrowIfNull(option);

            if (!Enum.IsDefined(option.Mode))
            {
                problems.Add(new GraphLabelProblem(field, GraphLabelProblemKind.UnknownMode));
            }
            else if (option.Mode == GraphLabelMode.Custom && Normalize(option.Text).Length == 0)
            {
                problems.Add(new GraphLabelProblem(field, GraphLabelProblemKind.CustomTextMissing));
            }
        }

        return problems;
    }

    // Custom text as a label is drawn: on one line, so line breaks and tabs become spaces, without spaces at either
    // end. Nothing else changes - not the spaces inside, not the case, not the length.
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Replace((char)0x0085, ' ')
            .Replace((char)0x2028, ' ')
            .Replace((char)0x2029, ' ')
            .Trim();
    }

    // The label a graph shows: the graph type's own for Auto (null when it has none), the normalized text for Custom,
    // none for Hidden. Options that break the rules above are refused; the setup never confirms them.
    public static string? Resolve(GraphLabelOption option, string? automatic)
    {
        ArgumentNullException.ThrowIfNull(option);

        return option.Mode switch
        {
            GraphLabelMode.Auto => automatic,
            GraphLabelMode.Hidden => null,
            GraphLabelMode.Custom when Normalize(option.Text) is { Length: > 0 } text => text,
            GraphLabelMode.Custom => throw new ArgumentException("A custom label needs text.", nameof(option)),
            _ => throw new ArgumentOutOfRangeException(nameof(option), option.Mode, "Unknown graph label mode.")
        };
    }
}
