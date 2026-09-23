using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Graphs;

namespace YAT.app.ViewModels;

// A graph's labels as the user edits them: the graph title, the X-axis title and the Y-axis title, each Auto, Custom or
// Hidden, with the text used for Custom. The graph setup and the Edit Labels dialog of a drawn graph both edit labels
// through this one editor, so both offer the same choices, keep typed text the same way, and refuse the same options
// for the same reasons (GraphConfigurationValidator.LabelErrors) in the same words.
//
// A label's text is read only while it is Custom, and is kept when its mode changes, so switching away from Custom and
// back loses nothing; a label left on Auto or Hidden goes into the options without text.
public sealed partial class GraphLabelsEditorViewModel : ObservableObject
{
    // Every label on Auto with nothing typed: how a new setup starts.
    public GraphLabelsEditorViewModel()
        : this(GraphLabelOptions.Default)
    {
    }

    // The labels as they are now, ready to be changed: a Custom label shows its text as it is drawn.
    public GraphLabelsEditorViewModel(GraphLabelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        SelectedGraphTitleMode = Choice(options.Title.Mode);
        SelectedXAxisTitleMode = Choice(options.XAxisTitle.Mode);
        SelectedYAxisTitleMode = Choice(options.YAxisTitle.Mode);
        GraphTitleText = TextOf(options.Title);
        XAxisTitleText = TextOf(options.XAxisTitle);
        YAxisTitleText = TextOf(options.YAxisTitle);
    }

    // Where each label can come from, in the order the editor offers them; the first is the default.
    public IReadOnlyList<SetupChoice<GraphLabelMode>> LabelModeChoices { get; } =
    [
        new(GraphLabelMode.Auto, "Auto"),
        new(GraphLabelMode.Custom, "Custom"),
        new(GraphLabelMode.Hidden, "Hidden")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGraphTitleTextEnabled), nameof(Options), nameof(IsValid))]
    public partial SetupChoice<GraphLabelMode> SelectedGraphTitleMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXAxisTitleTextEnabled), nameof(Options), nameof(IsValid))]
    public partial SetupChoice<GraphLabelMode> SelectedXAxisTitleMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsYAxisTitleTextEnabled), nameof(Options), nameof(IsValid))]
    public partial SetupChoice<GraphLabelMode> SelectedYAxisTitleMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(IsValid))]
    public partial string GraphTitleText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(IsValid))]
    public partial string XAxisTitleText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(IsValid))]
    public partial string YAxisTitleText { get; set; }

    public bool IsGraphTitleTextEnabled => SelectedGraphTitleMode?.Value == GraphLabelMode.Custom;

    public bool IsXAxisTitleTextEnabled => SelectedXAxisTitleMode?.Value == GraphLabelMode.Custom;

    public bool IsYAxisTitleTextEnabled => SelectedYAxisTitleMode?.Value == GraphLabelMode.Custom;

    // The labels the editor describes. A label's text goes into them only when the label is Custom, so labels all left
    // on Auto are exactly the default labels, whatever was typed on the way.
    public GraphLabelOptions Options => new(
        Label(SelectedGraphTitleMode, GraphTitleText),
        Label(SelectedXAxisTitleMode, XAxisTitleText),
        Label(SelectedYAxisTitleMode, YAxisTitleText));

    // What is wrong with the labels, in field order; empty when they can be used.
    public IReadOnlyList<GraphValidationError> Errors => GraphConfigurationValidator.LabelErrors(Options);

    public bool IsValid => Errors.Count == 0;

    // Editing the title the user picked on the graph: a title on Auto becomes Custom with the text the graph shows, so
    // what is typed replaces it; a Custom title stays as it is. Only a title the graph shows can be picked, so a Hidden
    // one is never begun this way.
    public void BeginEditing(GraphLabelField field, string? shownText)
    {
        switch (field)
        {
            case GraphLabelField.Title when SelectedGraphTitleMode.Value == GraphLabelMode.Auto:
                SelectedGraphTitleMode = Choice(GraphLabelMode.Custom);
                GraphTitleText = shownText ?? string.Empty;
                break;
            case GraphLabelField.XAxisTitle when SelectedXAxisTitleMode.Value == GraphLabelMode.Auto:
                SelectedXAxisTitleMode = Choice(GraphLabelMode.Custom);
                XAxisTitleText = shownText ?? string.Empty;
                break;
            case GraphLabelField.YAxisTitle when SelectedYAxisTitleMode.Value == GraphLabelMode.Auto:
                SelectedYAxisTitleMode = Choice(GraphLabelMode.Custom);
                YAxisTitleText = shownText ?? string.Empty;
                break;
        }
    }

    private SetupChoice<GraphLabelMode> Choice(GraphLabelMode mode) =>
        LabelModeChoices.FirstOrDefault(choice => choice.Value == mode)
        ?? throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown graph label mode.");

    // Custom text as it is drawn: typed line breaks were never on the graph.
    private static string TextOf(GraphLabelOption option) =>
        option.Mode == GraphLabelMode.Custom ? GraphLabelRules.Normalize(option.Text) : string.Empty;

    private static GraphLabelOption Label(SetupChoice<GraphLabelMode> mode, string? text) =>
        mode.Value == GraphLabelMode.Custom
            ? GraphLabelOption.Custom(text ?? string.Empty)
            : new GraphLabelOption(mode.Value);
}
