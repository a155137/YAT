using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.ViewModels;

// One colour as the user edits it (Task #046): typed as "#RRGGBB" or picked from a few swatches. Text that is not a
// colour is kept as typed - so it can be corrected - but has no colour, and the editor it belongs to cannot be
// confirmed. Every colour of the appearance - each colour of a custom palette, the grid and the two backgrounds - is
// edited through this one editor, and checked the same way.
public sealed partial class GraphColorEditorViewModel : ObservableObject
{
    public GraphColorEditorViewModel(GraphColor color)
    {
        Text = color.ToString();
    }

    // The colours offered as swatches: the default series colours first, then neutrals and a few soft backgrounds.
    public static IReadOnlyList<GraphColor> Swatches { get; } =
    [
        .. GraphThemes.Light.SeriesPalette.Select(GraphAppearance.FromSkia),
        new(0xE3, 0x77, 0xC2), new(0x7F, 0x7F, 0x7F), new(0xFF, 0xD7, 0x00), new(0x00, 0x00, 0x00),
        new(0x40, 0x40, 0x40), new(0xA0, 0xA0, 0xA0), new(0xE1, 0xE1, 0xE1), new(0xFF, 0xFF, 0xFF),
        new(0x20, 0x20, 0x20), new(0x2A, 0x2A, 0x2A), new(0xF7, 0xF4, 0xEC), new(0xEA, 0xF2, 0xFB)
    ];

    // The colour as typed.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Color), nameof(IsValid))]
    public partial string Text { get; set; }

    // The colour the text is, or null when it is not "#RRGGBB".
    public GraphColor? Color => GraphColor.TryParse(Text, out var color) ? color : null;

    public bool IsValid => Color is not null;

    public void Choose(GraphColor color) => Text = color.ToString();

    // Once the text is a colour, it is written the one way colours are written ("#1f77b4" becomes "#1F77B4").
    public void Canonicalize()
    {
        if (Color is { } color && Text != color.ToString())
        {
            Text = color.ToString();
        }
    }
}

// A colour of the appearance that is either the theme's (Default) or one the user chose (Custom). The colour a Custom
// one starts from is the light theme's own, so choosing Custom changes nothing until a colour is picked.
public sealed partial class GraphColorChoiceViewModel : ObservableObject
{
    public GraphColorChoiceViewModel(GraphColor? chosen, GraphColor start)
    {
        Editor = new GraphColorEditorViewModel(chosen ?? start);
        Editor.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Value));
        SelectedMode = chosen is null ? ModeChoices[0] : ModeChoices[1];
    }

    public IReadOnlyList<SetupChoice<bool>> ModeChoices { get; } = [new(false, "Default"), new(true, "Custom")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom), nameof(Value))]
    public partial SetupChoice<bool> SelectedMode { get; set; }

    public bool IsCustom => SelectedMode?.Value == true;

    public GraphColorEditorViewModel Editor { get; }

    // Whether the colour can be confirmed: the theme's always, a custom one once it is "#RRGGBB".
    public bool IsValid => !IsCustom || Editor.IsValid;

    // The colour chosen, or null for the theme's (and for a custom colour that is not one yet - see IsValid).
    public GraphColor? Value => IsCustom ? Editor.Color : null;
}
