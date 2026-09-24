using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Graphs;

namespace YAT.app.ViewModels;

// A graph's statistics options as the user edits them (Task #045): whether the statistics panel is shown - Auto, Show
// or Hide - and which of its statistics, Mean, StDev and N. The graph setup and the Edit Statistics dialog of a drawn
// graph both edit the statistics through this one editor. The statistics chosen are kept while the panel is hidden, so
// they come back as they were; they are only not editable then. Whether a graph has a panel at all is the graph
// type's to decide, so nothing here depends on it.
public sealed partial class GraphStatisticsEditorViewModel : ObservableObject
{
    private readonly GraphTypeDefinition _definition;

    // The graph type's panel with every statistic: how a new setup starts.
    public GraphStatisticsEditorViewModel(GraphTypeDefinition definition)
        : this(definition, GraphStatisticsOptions.Default)
    {
    }

    // The statistics options as they are now, ready to be changed.
    public GraphStatisticsEditorViewModel(GraphTypeDefinition definition, GraphStatisticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        _definition = definition;

        SelectedMode = ModeChoices.FirstOrDefault(choice => choice.Value == options.Mode) ?? ModeChoices[0];
        ShowMean = options.ShowMean;
        ShowStandardDeviation = options.ShowStandardDeviation;
        ShowCount = options.ShowCount;
    }

    // In the order the editor offers them; the first is the default.
    public IReadOnlyList<SetupChoice<GraphStatisticsMode>> ModeChoices { get; } =
    [
        new(GraphStatisticsMode.Auto, "Auto"),
        new(GraphStatisticsMode.Show, "Show"),
        new(GraphStatisticsMode.Hide, "Hide")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreItemsEnabled), nameof(Options), nameof(ValidationMessage), nameof(IsValid))]
    public partial SetupChoice<GraphStatisticsMode> SelectedMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(ValidationMessage), nameof(IsValid))]
    public partial bool ShowMean { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(ValidationMessage), nameof(IsValid))]
    public partial bool ShowStandardDeviation { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(ValidationMessage), nameof(IsValid))]
    public partial bool ShowCount { get; set; }

    // A hidden panel has no statistics to choose; the ones chosen stay as they are.
    public bool AreItemsEnabled => SelectedMode?.Value != GraphStatisticsMode.Hide;

    // Why the options cannot be confirmed - a panel that is shown needs at least one statistic - or null when they can.
    // The rule and the words are the graph setup's own.
    public string? ValidationMessage =>
        GraphConfigurationValidator.StatisticsErrors(Options).FirstOrDefault() is { } error
            ? GraphValidationMessages.For(error, _definition)
            : null;

    public bool IsValid => ValidationMessage is null;

    // The panel is only ever the graph's own, worked out with it: nothing is computed again when this changes.
    public static string Note => "Statistics are those of the data the graph was drawn from.";

    public GraphStatisticsOptions Options => new(SelectedMode.Value, ShowMean, ShowStandardDeviation, ShowCount);
}
