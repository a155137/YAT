using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.ViewModels;

// A graph's appearance as the user edits it (Task #046): the series colours - the theme's (Default) or a custom palette
// of one to sixteen colours - the grid - Auto, Show or Hide, in the theme's colour or a custom one - and the colours
// behind the plot and around it. The graph setup's Appearance... dialog and the Edit Appearance dialog of a drawn graph
// are this one editor, checked by the same rules in the same words.
//
// A custom palette is edited as a row of colours: pick one to change it, add one after it, or remove it. Default keeps
// the palette out of the appearance altogether - the colours shown for Custom are only where editing starts - so
// opening the editor never puts the theme's colours into a graph's configuration.
public sealed partial class GraphAppearanceEditorViewModel : ObservableObject
{
    private readonly GraphTypeDefinition _definition;

    // The graph theme's own look: how a new setup starts.
    public GraphAppearanceEditorViewModel(GraphTypeDefinition definition)
        : this(definition, GraphAppearanceOptions.Default)
    {
    }

    // The appearance as it is now, ready to be changed.
    public GraphAppearanceEditorViewModel(GraphTypeDefinition definition, GraphAppearanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        _definition = definition;
        var light = GraphThemes.Light;
        var start = options.Palette?.Colors ?? [.. light.SeriesPalette.Select(GraphAppearance.FromSkia)];
        PaletteColors = [.. start.Select(Watched)];
        PaletteColors.CollectionChanged += (_, _) => Changed();
        SelectedPaletteMode = options.Palette is null ? PaletteModeChoices[0] : PaletteModeChoices[1];
        SelectedPaletteColor = PaletteColors.Count > 0 ? PaletteColors[0] : null;

        SelectedGridMode =
            GridModeChoices.FirstOrDefault(choice => choice.Value == options.GridMode) ?? GridModeChoices[0];
        GridColor = Choice(options.GridColor, light.Grid);
        PlotBackground = Choice(options.PlotBackground, light.PlotBackground);
        GraphBackground = Choice(options.GraphBackground, light.Background);

        AddPaletteColorCommand = new RelayCommand(
            AddPaletteColor,
            () => IsCustomPalette && PaletteColors.Count < GraphPalette.MaximumColors);
        RemovePaletteColorCommand = new RelayCommand(
            RemovePaletteColor,
            () => IsCustomPalette
                && PaletteColors.Count > GraphPalette.MinimumColors
                && SelectedPaletteColor is not null);
    }

    public IReadOnlyList<SetupChoice<bool>> PaletteModeChoices { get; } = [new(false, "Default"), new(true, "Custom")];

    public IReadOnlyList<SetupChoice<GraphGridMode>> GridModeChoices { get; } =
    [
        new(GraphGridMode.Auto, "Auto"),
        new(GraphGridMode.Show, "Show"),
        new(GraphGridMode.Hide, "Hide")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPalette))]
    public partial SetupChoice<bool> SelectedPaletteMode { get; set; }

    // Whether the series are drawn in a palette of the user's own.
    public bool IsCustomPalette => SelectedPaletteMode?.Value == true;

    // The custom palette's colours, in order: series i takes colour i modulo their number.
    public ObservableCollection<GraphColorEditorViewModel> PaletteColors { get; }

    // The palette colour being edited.
    [ObservableProperty]
    public partial GraphColorEditorViewModel? SelectedPaletteColor { get; set; }

    public IRelayCommand AddPaletteColorCommand { get; }

    public IRelayCommand RemovePaletteColorCommand { get; }

    [ObservableProperty]
    public partial SetupChoice<GraphGridMode> SelectedGridMode { get; set; }

    public GraphColorChoiceViewModel GridColor { get; }

    public GraphColorChoiceViewModel PlotBackground { get; }

    public GraphColorChoiceViewModel GraphBackground { get; }

    // Colours are the graph's own and are drawn so in the light and the dark theme alike.
    public static string Note =>
        "Default follows the light or dark theme; a custom color stays the same in both. Series take the palette's " +
        "colors in order, starting again after the last.";

    // The appearance the editor describes, or null while a colour is not "#RRGGBB" (ValidationMessage says so).
    public GraphAppearanceOptions? Options
    {
        get
        {
            if (!ColorsAreValid)
            {
                return null;
            }

            var palette = IsCustomPalette
                ? new GraphPalette([.. PaletteColors.Select(color => color.Color!.Value)])
                : null;
            return new GraphAppearanceOptions(
                palette,
                SelectedGridMode.Value,
                GridColor.Value,
                PlotBackground.Value,
                GraphBackground.Value);
        }
    }

    // Why the appearance cannot be confirmed, or null when it can: a colour that is not "#RRGGBB", then the rules the
    // graph setup checks the appearance by.
    public string? ValidationMessage
    {
        get
        {
            var error = !ColorsAreValid
                ? new GraphValidationError(GraphValidationReason.AppearanceColorInvalid)
                : GraphConfigurationValidator.AppearanceErrors(Options!).FirstOrDefault();
            return error is null ? null : GraphValidationMessages.For(error, _definition);
        }
    }

    public bool IsValid => ValidationMessage is null;

    private bool ColorsAreValid =>
        (!IsCustomPalette || PaletteColors.All(color => color.IsValid))
        && GridColor.IsValid && PlotBackground.IsValid && GraphBackground.IsValid;

    partial void OnSelectedPaletteModeChanged(SetupChoice<bool> value) => Changed();

    partial void OnSelectedPaletteColorChanged(GraphColorEditorViewModel? value) => Changed();

    partial void OnSelectedGridModeChanged(SetupChoice<GraphGridMode> value) => Changed();

    // A new colour after the one being edited - the default colour that series would otherwise take - and edited next.
    private void AddPaletteColor()
    {
        var at = SelectedPaletteColor is { } selected ? PaletteColors.IndexOf(selected) + 1 : PaletteColors.Count;
        var palette = GraphThemes.Light.SeriesPalette;
        var added = Watched(GraphAppearance.FromSkia(palette[at % palette.Count]));
        PaletteColors.Insert(at, added);
        SelectedPaletteColor = added;
    }

    // The colour being edited goes; the one after it (or, at the end, before it) is edited next.
    private void RemovePaletteColor()
    {
        if (SelectedPaletteColor is not { } selected)
        {
            return;
        }

        var at = PaletteColors.IndexOf(selected);
        PaletteColors.RemoveAt(at);
        SelectedPaletteColor = PaletteColors[Math.Min(at, PaletteColors.Count - 1)];
    }

    private GraphColorEditorViewModel Watched(GraphColor color)
    {
        var editor = new GraphColorEditorViewModel(color);
        editor.PropertyChanged += OnPartChanged;
        return editor;
    }

    private GraphColorChoiceViewModel Choice(GraphColor? chosen, SkiaSharp.SKColor start)
    {
        var choice = new GraphColorChoiceViewModel(chosen, GraphAppearance.FromSkia(start));
        choice.PropertyChanged += OnPartChanged;
        return choice;
    }

    private void OnPartChanged(object? sender, PropertyChangedEventArgs e) => Changed();

    // Anything edited changes what the editor describes and whether it can be confirmed.
    private void Changed()
    {
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(IsValid));
        AddPaletteColorCommand?.NotifyCanExecuteChanged();
        RemovePaletteColorCommand?.NotifyCanExecuteChanged();
    }
}
