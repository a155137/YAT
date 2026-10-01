using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.ViewModels;

// What a palette choice of the appearance editor is.
public enum GraphPaletteOptionKind
{
    // The graph theme's own colours: no palette in the appearance at all.
    YatDefault,

    // A palette of the user's library: choosing it copies its colours into the graph.
    Library,

    // Colours of this graph's own.
    ThisGraph
}

// One entry of the appearance editor's palette list (Task #050). It names where colours come from; the graph keeps only
// the colours.
public sealed record GraphPaletteOption(GraphPaletteOptionKind Kind, string Name, GraphPalette? Palette = null, bool IsDefault = false)
{
    public const string YatDefaultName = GraphPaletteLibrary.YatDefaultName;

    public const string ThisGraphName = "Custom (this graph)";

    // "My Palette", or "My Palette  (Default)" for the palette new graphs start with.
    public override string ToString() => IsDefault ? $"{Name}  (Default)" : Name;
}

// A graph's appearance as the user edits it (Tasks #046 and #050): the series colours - YAT Default (the theme's own), a
// palette of the user's library, or colours of this graph's own, one to sixteen of them - the grid - Auto, Show or Hide,
// in the theme's colour or a custom one - and the colours behind the plot and around it. The graph setup's Appearance...
// dialog and the Edit Appearance dialog of a drawn graph are this one editor, checked by the same rules in the same
// words.
//
// A graph never refers to a library palette: choosing one copies its colours into the graph, and the graph keeps them
// whatever the library does next. Which palette the colours came from is only shown, by their colours: when exactly one
// palette of the library has them, its name; otherwise Custom (this graph). YAT Default keeps the palette out of the
// appearance altogether - the colours shown for the other choices are only where editing starts - so opening the editor
// never puts the theme's colours into a graph's configuration.
public sealed partial class GraphAppearanceEditorViewModel : ObservableObject
{
    private readonly GraphTypeDefinition _definition;
    private GraphPaletteChoices _choices;
    private bool _syncing;

    // The graph theme's own look: how a new setup starts.
    public GraphAppearanceEditorViewModel(GraphTypeDefinition definition)
        : this(definition, GraphAppearanceOptions.Default)
    {
    }

    // The appearance as it is now, ready to be changed. choices: the user's palettes to offer (none without them).
    public GraphAppearanceEditorViewModel(
        GraphTypeDefinition definition,
        GraphAppearanceOptions options,
        GraphPaletteChoices? choices = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        _definition = definition;
        _choices = choices ?? GraphPaletteChoices.None;
        var light = GraphThemes.Light;
        var start = options.Palette?.Colors ?? [.. light.SeriesPalette.Select(GraphAppearance.FromSkia)];
        PaletteEditor = new GraphPaletteColorsViewModel(start, isEditable: false);
        PaletteEditor.Changed += (_, _) => OnPaletteColorsChanged();
        PaletteEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphPaletteColorsViewModel.Selected))
            {
                OnPropertyChanged(nameof(SelectedPaletteColor));
                Changed();
            }
        };

        _syncing = true;
        try
        {
            Rebuild(options.Palette is null ? GraphPaletteOptionKind.YatDefault : null);
        }
        finally
        {
            _syncing = false;
        }

        PaletteEditor.IsEditable = IsCustomPalette;

        SelectedGridMode =
            GridModeChoices.FirstOrDefault(choice => choice.Value == options.GridMode) ?? GridModeChoices[0];
        GridColor = Choice(options.GridColor, light.Grid);
        PlotBackground = Choice(options.PlotBackground, light.PlotBackground);
        GraphBackground = Choice(options.GraphBackground, light.Background);
    }

    // YAT Default, the library's palettes in order, and Custom (this graph).
    public ObservableCollection<GraphPaletteOption> PaletteOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPalette))]
    public partial GraphPaletteOption? SelectedPalette { get; set; }

    public IReadOnlyList<SetupChoice<GraphGridMode>> GridModeChoices { get; } =
    [
        new(GraphGridMode.Auto, "Auto"),
        new(GraphGridMode.Show, "Show"),
        new(GraphGridMode.Hide, "Hide")
    ];

    // Whether the series are drawn in a palette in the appearance - a library palette's colours or this graph's own -
    // rather than YAT Default.
    public bool IsCustomPalette => SelectedPalette is { Kind: not GraphPaletteOptionKind.YatDefault };

    // The palette's colours, editable while the series are drawn in a palette.
    public GraphPaletteColorsViewModel PaletteEditor { get; }

    // The palette's colours, in order: series i takes colour i modulo their number.
    public ObservableCollection<GraphColorEditorViewModel> PaletteColors => PaletteEditor.Colors;

    // The palette colour being edited.
    public GraphColorEditorViewModel? SelectedPaletteColor
    {
        get => PaletteEditor.Selected;
        set => PaletteEditor.Selected = value;
    }

    public IRelayCommand AddPaletteColorCommand => PaletteEditor.AddCommand;

    public IRelayCommand RemovePaletteColorCommand => PaletteEditor.RemoveCommand;

    // Anything the user should know about the palette settings (a damaged file recovered, a file that cannot be written);
    // null when nothing.
    public string? PaletteNotice => _choices.Notice;

    [ObservableProperty]
    public partial SetupChoice<GraphGridMode> SelectedGridMode { get; set; }

    public GraphColorChoiceViewModel GridColor { get; }

    public GraphColorChoiceViewModel PlotBackground { get; }

    public GraphColorChoiceViewModel GraphBackground { get; }

    // Colours are the graph's own and are drawn so in the light and the dark theme alike.
    public static string Note =>
        "YAT Default follows the light or dark theme; any other palette or color stays the same in both. Series take the " +
        "palette's colors in order, starting again after the last. A library palette's colors are copied into the graph.";

    // The appearance the editor describes, or null while a colour is not "#RRGGBB" (ValidationMessage says so).
    public GraphAppearanceOptions? Options
    {
        get
        {
            if (!ColorsAreValid)
            {
                return null;
            }

            return new GraphAppearanceOptions(
                IsCustomPalette ? PaletteEditor.Palette : null,
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
        (!IsCustomPalette || PaletteEditor.AllValid)
        && GridColor.IsValid && PlotBackground.IsValid && GraphBackground.IsValid;

    // The user's palettes as they are now (after the Palette Manager saved): the list is offered again, and the choice
    // follows the colours, which stay exactly as they were - a graph never changes because its library did.
    public void UpdateChoices(GraphPaletteChoices choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        _choices = choices;
        _syncing = true;
        try
        {
            Rebuild(SelectedPalette?.Kind == GraphPaletteOptionKind.YatDefault ? GraphPaletteOptionKind.YatDefault : null);
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(PaletteNotice));
        Changed();
    }

    partial void OnSelectedPaletteChanged(GraphPaletteOption? value)
    {
        if (_syncing || value is null)
        {
            return;
        }

        // A library palette's colours become the graph's; YAT Default and this graph's own keep the colours shown.
        if (value is { Kind: GraphPaletteOptionKind.Library, Palette: { } palette })
        {
            _syncing = true;
            try
            {
                PaletteEditor.Load(palette.Colors);
            }
            finally
            {
                _syncing = false;
            }
        }

        PaletteEditor.IsEditable = IsCustomPalette;
        Changed();
    }

    partial void OnSelectedGridModeChanged(SetupChoice<GraphGridMode> value) => Changed();

    // The colours were edited: they are this graph's own now - or, when they are exactly one library palette's, shown as
    // that palette.
    private void OnPaletteColorsChanged()
    {
        if (!_syncing && IsCustomPalette)
        {
            _syncing = true;
            try
            {
                SelectedPalette = Matching(PaletteEditor.Palette);
            }
            finally
            {
                _syncing = false;
            }
        }

        Changed();
    }

    // The list again, with the choice that fits: YAT Default when it is kept, otherwise the one palette with the colours
    // being edited, or Custom (this graph).
    private void Rebuild(GraphPaletteOptionKind? keep)
    {
        PaletteOptions.Clear();
        PaletteOptions.Add(new GraphPaletteOption(GraphPaletteOptionKind.YatDefault, GraphPaletteOption.YatDefaultName, null, _choices.YatDefaultIsDefault));
        foreach (var choice in _choices.Palettes)
        {
            PaletteOptions.Add(new GraphPaletteOption(GraphPaletteOptionKind.Library, choice.Name, choice.Palette, choice.IsDefault));
        }

        PaletteOptions.Add(new GraphPaletteOption(GraphPaletteOptionKind.ThisGraph, GraphPaletteOption.ThisGraphName));
        SelectedPalette = keep == GraphPaletteOptionKind.YatDefault ? PaletteOptions[0] : Matching(PaletteEditor.Palette);
    }

    private GraphPaletteOption Matching(GraphPalette? colors) =>
        _choices.Match(colors) is { } match
            ? PaletteOptions.First(option => option.Kind == GraphPaletteOptionKind.Library && option.Name == match.Name)
            : PaletteOptions[^1];

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
    }
}
