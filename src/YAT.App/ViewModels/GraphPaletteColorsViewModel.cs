using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.ViewModels;

// A palette's colours as the user edits them (Tasks #046 and #050): a row of colours - pick one to change it, add one
// after it, remove it, or move it left or right - each typed or picked the way every colour of YAT is
// (GraphColorEditorViewModel). The appearance editor and the Palette Manager edit palettes with this one editor, so
// they behave alike. One to sixteen colours: Add and Remove stop there.
public sealed partial class GraphPaletteColorsViewModel : ObservableObject
{
    private bool _loading;

    public GraphPaletteColorsViewModel(IReadOnlyList<GraphColor> colors, bool isEditable = true)
    {
        ArgumentNullException.ThrowIfNull(colors);

        Colors = [];
        Colors.CollectionChanged += (_, _) => OnChanged();
        IsEditable = isEditable;
        AddCommand = new RelayCommand(Add, () => IsEditable && Colors.Count < GraphPalette.MaximumColors);
        RemoveCommand = new RelayCommand(
            Remove,
            () => IsEditable && Colors.Count > GraphPalette.MinimumColors && Selected is not null);
        MoveLeftCommand = new RelayCommand(() => Move(-1), () => IsEditable && Selected is { } selected && Colors.IndexOf(selected) > 0);
        MoveRightCommand = new RelayCommand(
            () => Move(1),
            () => IsEditable && Selected is { } selected && Colors.IndexOf(selected) < Colors.Count - 1);
        Load(colors);
    }

    // The colours, in order: series i takes colour i modulo their number.
    public ObservableCollection<GraphColorEditorViewModel> Colors { get; }

    // The colour being edited.
    [ObservableProperty]
    public partial GraphColorEditorViewModel? Selected { get; set; }

    // Whether the colours may be changed at all (YAT Default's, or a library that is read-only, may not).
    [ObservableProperty]
    public partial bool IsEditable { get; set; }

    public IRelayCommand AddCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    public IRelayCommand MoveLeftCommand { get; }

    public IRelayCommand MoveRightCommand { get; }

    // Whether every colour is "#RRGGBB".
    public bool AllValid => Colors.All(color => color.IsValid);

    // The palette the colours make, or null while one of them is not a colour.
    public GraphPalette? Palette => AllValid ? new GraphPalette([.. Colors.Select(color => color.Color!.Value)]) : null;

    // Raised after anything about the colours changed: one of them, their number or their order.
    public event EventHandler? Changed;

    // Other colours, all at once - one change, not one per colour. The first is edited next.
    public void Load(IReadOnlyList<GraphColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);

        _loading = true;
        try
        {
            foreach (var color in Colors)
            {
                color.PropertyChanged -= OnColorChanged;
            }

            Colors.Clear();
            foreach (var color in colors)
            {
                Colors.Add(Watched(color));
            }

            Selected = Colors.Count > 0 ? Colors[0] : null;
        }
        finally
        {
            _loading = false;
        }

        OnChanged();
    }

    partial void OnSelectedChanged(GraphColorEditorViewModel? value) => NotifyCommands();

    partial void OnIsEditableChanged(bool value) => NotifyCommands();

    // A new colour after the one being edited - the default colour that series would otherwise take - and edited next.
    private void Add()
    {
        var at = Selected is { } selected ? Colors.IndexOf(selected) + 1 : Colors.Count;
        var palette = GraphThemes.Light.SeriesPalette;
        var added = Watched(GraphAppearance.FromSkia(palette[at % palette.Count]));
        Colors.Insert(at, added);
        Selected = added;
    }

    // The colour being edited goes; the one after it (or, at the end, before it) is edited next.
    private void Remove()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        var at = Colors.IndexOf(selected);
        selected.PropertyChanged -= OnColorChanged;
        Colors.RemoveAt(at);
        Selected = Colors[Math.Min(at, Colors.Count - 1)];
    }

    // The colour being edited changes places with its neighbour, and is still the one edited.
    private void Move(int step)
    {
        if (Selected is not { } selected)
        {
            return;
        }

        var at = Colors.IndexOf(selected);
        Colors.Move(at, at + step);
        Selected = selected;
        NotifyCommands();
    }

    private GraphColorEditorViewModel Watched(GraphColor color)
    {
        var editor = new GraphColorEditorViewModel(color);
        editor.PropertyChanged += OnColorChanged;
        return editor;
    }

    private void OnColorChanged(object? sender, PropertyChangedEventArgs e) => OnChanged();

    private void OnChanged()
    {
        if (_loading)
        {
            return;
        }

        NotifyCommands();
        OnPropertyChanged(nameof(AllValid));
        OnPropertyChanged(nameof(Palette));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyCommands()
    {
        AddCommand?.NotifyCanExecuteChanged();
        RemoveCommand?.NotifyCanExecuteChanged();
        MoveLeftCommand?.NotifyCanExecuteChanged();
        MoveRightCommand?.NotifyCanExecuteChanged();
    }
}
