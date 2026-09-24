using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Graphs;

namespace YAT.app.ViewModels;

// A graph's legend options as the user edits them (Task #044): whether the legend is shown - Auto, Show or Hide - and
// on which side of the plot. The graph setup and the Edit Legend dialog of a drawn graph both edit the legend through
// this one editor. The position is kept while the legend is hidden, so it comes back where it was; it is only not
// editable then. Whether a graph has a legend at all is the graph type's to decide from its data, so nothing here
// depends on it.
public sealed partial class GraphLegendEditorViewModel : ObservableObject
{
    // The graph type's legend, on the right: how a new setup starts.
    public GraphLegendEditorViewModel()
        : this(GraphLegendOptions.Default)
    {
    }

    // The legend options as they are now, ready to be changed.
    public GraphLegendEditorViewModel(GraphLegendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        SelectedMode = ModeChoices.FirstOrDefault(choice => choice.Value == options.Mode) ?? ModeChoices[0];
        SelectedPosition =
            PositionChoices.FirstOrDefault(choice => choice.Value == options.Position) ?? PositionChoices[0];
    }

    // In the order the editor offers them; the first of each is the default.
    public IReadOnlyList<SetupChoice<GraphLegendMode>> ModeChoices { get; } =
    [
        new(GraphLegendMode.Auto, "Auto"),
        new(GraphLegendMode.Show, "Show"),
        new(GraphLegendMode.Hide, "Hide")
    ];

    public IReadOnlyList<SetupChoice<GraphLegendPosition>> PositionChoices { get; } =
    [
        new(GraphLegendPosition.Right, "Right"),
        new(GraphLegendPosition.Left, "Left"),
        new(GraphLegendPosition.Top, "Top"),
        new(GraphLegendPosition.Bottom, "Bottom")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPositionEnabled), nameof(Options))]
    public partial SetupChoice<GraphLegendMode> SelectedMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options))]
    public partial SetupChoice<GraphLegendPosition> SelectedPosition { get; set; }

    // A hidden legend has no side to choose.
    public bool IsPositionEnabled => SelectedMode?.Value != GraphLegendMode.Hide;

    // A legend is only ever the graph's own: one of groups, or of several variables drawn together.
    public static string Note => "A legend is shown for groups or for several variables drawn together.";

    public GraphLegendOptions Options => new(SelectedMode.Value, SelectedPosition.Value);
}
