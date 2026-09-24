using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Graphs;

namespace YAT.app.ViewModels;

// A box plot's own options as the user edits them (Task #047): the box width, in percent of a category's slot, and
// whether the means and the outliers are marked. The graph setup's Box Plot Options... dialog and the Edit Box Plot
// dialog of a drawn box plot are this one editor, checked by the same rule in the same words.
public sealed partial class GraphBoxPlotEditorViewModel : ObservableObject
{
    // Boxes as they have always been drawn: how a new setup starts.
    public GraphBoxPlotEditorViewModel()
        : this(BoxPlotOptions.Default)
    {
    }

    // The options as they are now, ready to be changed.
    public GraphBoxPlotEditorViewModel(BoxPlotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        BoxWidthText = options.BoxWidthPercent.ToString(CultureInfo.InvariantCulture);
        ShowMean = options.ShowMean;
        ShowOutliers = options.ShowOutliers;
    }

    // The box width as typed: a whole number of percent.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(ValidationMessage), nameof(IsValid))]
    public partial string BoxWidthText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options))]
    public partial bool ShowMean { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options))]
    public partial bool ShowOutliers { get; set; }

    // Marking or not changes only what is drawn.
    public static string Note => "Hidden outliers still count: the whiskers and the Y axis stay as they are.";

    // The options the editor describes, or null while the box width is not a whole number from 20 to 90
    // (ValidationMessage says so).
    public BoxPlotOptions? Options =>
        int.TryParse(BoxWidthText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var percent)
        && new BoxPlotOptions(percent, ShowMean, ShowOutliers) is { IsValid: true } options
            ? options
            : null;

    // Why the options cannot be confirmed, or null when they can - in the words the graph setup uses.
    public string? ValidationMessage =>
        Options is null
            ? GraphValidationMessages.For(
                new GraphValidationError(GraphValidationReason.BoxPlotWidthInvalid),
                GraphTypeDefinitions.For(GraphType.BoxPlot))
            : null;

    public bool IsValid => ValidationMessage is null;
}
