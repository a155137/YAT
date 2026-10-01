using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Graphs;
using YAT.app.Graphs;

namespace YAT.app.ViewModels;

// One palette in the Palette Manager's list: YAT Default (built in, read-only) or one of the user's.
public sealed partial class GraphPaletteEntry : ObservableObject
{
    internal GraphPaletteEntry(Guid? id, string name, GraphPalette palette, bool isDefault)
    {
        Id = id;
        Name = name;
        Palette = palette;
        IsDefault = isDefault;
    }

    // Null for YAT Default.
    public Guid? Id { get; }

    public bool IsBuiltIn => Id is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial GraphPalette Palette { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial bool IsDefault { get; set; }

    // "Presentation", "Presentation  (Default)", "YAT Default  (built-in)".
    public string DisplayName =>
        Name + (IsBuiltIn ? "  (built-in)" : string.Empty) + (IsDefault ? "  (Default)" : string.Empty);

    public override string ToString() => DisplayName;
}

// The Palette Manager (Task #050): the user's palettes - New, Duplicate (YAT Default too), Rename, their colours, Delete -
// and which one new graphs start with (Set as Default, Reset to YAT Default).
//
// Everything is done to a working copy of the library: Cancel leaves the library, and the file it is kept in, exactly as
// they were; Save puts the whole working copy in place at once, with one save (GraphPaletteLibraryService.Replace). The
// library's own rules decide what may be done: a name that is blank, YAT Default's or another palette's, or colours that
// are not 1 to 16 of them, are refused, said, and keep Save unavailable until they are put right.
//
// YAT Default is shown first and is never changed: it cannot be renamed, recoloured or deleted, only duplicated or made
// the default again. When the palette settings are read-only this session (a newer YAT's, or a file that could not be
// read), every palette can be looked at and nothing can be changed.
public sealed partial class GraphPaletteManagerViewModel : ObservableObject
{
    private readonly GraphPaletteLibraryService _service;
    private GraphPaletteLibrary _original;
    private GraphPaletteLibrary _working;
    private bool _loadingEditor;

    public GraphPaletteManagerViewModel(GraphPaletteLibraryService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _service = service;
        _original = service.Library;
        _working = service.Library;
        IsReadOnly = service.IsReadOnly;
        Notice = GraphPaletteLibraryAccess.Notice(service.LoadWarnings, service.IsReadOnly);

        Colors = new GraphPaletteColorsViewModel(service.YatDefault.Colors, isEditable: false);
        Colors.Changed += (_, _) => OnColorsEdited();

        NewCommand = new RelayCommand(New, () => !IsReadOnly);
        DuplicateCommand = new RelayCommand(Duplicate, () => !IsReadOnly && Selected is not null);
        DeleteCommand = new RelayCommand(Delete, () => CanEditSelected);
        SetDefaultCommand = new RelayCommand(SetDefault, () => !IsReadOnly && Selected is { IsDefault: false });
        ResetDefaultCommand = new RelayCommand(ResetDefault, () => !IsReadOnly && _working.DefaultPaletteId is not null);

        Rebuild(selectId: _working.DefaultPaletteId, selectBuiltIn: _working.DefaultPaletteId is null);
    }

    // YAT Default first, then the user's palettes in order.
    public ObservableCollection<GraphPaletteEntry> Palettes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelected))]
    public partial GraphPaletteEntry? Selected { get; set; }

    // The selected palette's name as typed.
    [ObservableProperty]
    public partial string NameText { get; set; } = string.Empty;

    // The selected palette's colours.
    public GraphPaletteColorsViewModel Colors { get; }

    // Nothing can be changed this session (Notice says why).
    public bool IsReadOnly { get; }

    // What the user should know about the palette settings: a damaged file recovered, palettes left out, a file that
    // cannot be written. Null when nothing.
    public string? Notice { get; }

    // Why what was just typed or edited was not taken; null when it was.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string? Problem { get; private set; }

    // Whether the selected palette may be renamed, recoloured and deleted: one of the user's, in a session that may save.
    public bool CanEditSelected => !IsReadOnly && Selected is { IsBuiltIn: false };

    // Whether anything was changed since the manager opened.
    public bool HasChanges => !ReferenceEquals(_working, _original);

    public bool CanSave => !IsReadOnly && HasChanges && Problem is null;

    // The library as edited so far.
    public GraphPaletteLibrary WorkingLibrary => _working;

    public IRelayCommand NewCommand { get; }

    public IRelayCommand DuplicateCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public IRelayCommand SetDefaultCommand { get; }

    public IRelayCommand ResetDefaultCommand { get; }

    // Puts the working copy in place, at once, and saves it. Null when there is nothing that may be saved.
    public GraphPaletteSaveResult? Save()
    {
        if (!CanSave)
        {
            return null;
        }

        var result = _service.Replace(_working);
        _original = _working;
        Refresh();
        return result;
    }

    partial void OnSelectedChanged(GraphPaletteEntry? value)
    {
        // Whatever was being typed and not taken is dropped: the editor shows the palette as the working copy has it.
        _loadingEditor = true;
        try
        {
            NameText = value?.Name ?? string.Empty;
            Colors.Load(value?.Palette.Colors ?? _service.YatDefault.Colors);
            Colors.IsEditable = CanEditSelected;
            Problem = null;
        }
        finally
        {
            _loadingEditor = false;
        }

        Refresh();
    }

    partial void OnNameTextChanged(string value)
    {
        if (_loadingEditor || !CanEditSelected || Selected?.Id is not { } id)
        {
            return;
        }

        var result = _working.Rename(id, value);
        if (Take(result))
        {
            Selected.Name = _working.Find(id)!.Name;
        }
    }

    private void OnColorsEdited()
    {
        if (_loadingEditor || !CanEditSelected || Selected?.Id is not { } id)
        {
            return;
        }

        if (Colors.Palette is not { } palette)
        {
            Problem = "Enter each color as #RRGGBB, for example #1F77B4.";
            Refresh();
            return;
        }

        if (Take(_working.EditColors(id, palette)))
        {
            Selected.Palette = _working.Find(id)!.Palette;

            // A name typed and not taken is still not taken: said again, so Save stays unavailable.
            if (GraphPaletteLibrary.Normalize(NameText) != Selected.Name)
            {
                OnNameTextChanged(NameText);
            }
        }
    }

    private void New()
    {
        var result = _working.Add(UniqueName("New palette"), _service.YatDefault);
        if (Take(result))
        {
            Rebuild(result.PaletteId, selectBuiltIn: false);
        }
    }

    // A copy of the selected palette - YAT Default's colours for YAT Default - under a name of its own.
    private void Duplicate()
    {
        if (Selected is not { } source)
        {
            return;
        }

        var name = UniqueName($"{source.Name} copy");
        var result = source.Id is { } id ? _working.Duplicate(id, name) : _working.Add(name, _service.YatDefault);
        if (Take(result))
        {
            Rebuild(result.PaletteId, selectBuiltIn: false);
        }
    }

    // The palette goes; the one after it (or, at the end, before it) is selected. Deleting the default makes YAT Default
    // the default again.
    private void Delete()
    {
        if (Selected?.Id is not { } id)
        {
            return;
        }

        var at = Palettes.IndexOf(Selected);
        if (Take(_working.Delete(id)))
        {
            var next = at < Palettes.Count - 1 ? Palettes[at + 1] : Palettes[at - 1];
            Rebuild(next.Id, selectBuiltIn: next.IsBuiltIn);
        }
    }

    private void SetDefault()
    {
        if (Selected is { } selected && Take(_working.SetDefault(selected.Id)))
        {
            Rebuild(selected.Id, selectBuiltIn: selected.IsBuiltIn);
        }
    }

    // YAT Default for new graphs again; the user's palettes stay.
    private void ResetDefault()
    {
        if (Take(_working.ResetDefault()))
        {
            Rebuild(Selected?.Id, selectBuiltIn: Selected?.IsBuiltIn ?? true);
        }
    }

    // The library's answer: taken into the working copy, or said.
    private bool Take(GraphPaletteLibraryResult result)
    {
        if (result.Succeeded)
        {
            _working = result.Library;
            Problem = null;
            Refresh();
            return true;
        }

        Problem = result.Problem switch
        {
            GraphPaletteLibraryProblem.NameMissing => "Enter a name for the palette.",
            GraphPaletteLibraryProblem.NameReserved => $"\"{GraphPaletteLibrary.YatDefaultName}\" is the name of the built-in palette.",
            GraphPaletteLibraryProblem.NameTaken => "Another palette already has this name.",
            GraphPaletteLibraryProblem.PaletteInvalid => $"A palette has {GraphPalette.MinimumColors} to {GraphPalette.MaximumColors} colors.",
            _ => "This palette is no longer there."
        };
        Refresh();
        return false;
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var number = 2; Taken(candidate); number++)
        {
            candidate = $"{name} {number}";
        }

        return candidate;

        bool Taken(string text) =>
            string.Equals(text, GraphPaletteLibrary.YatDefaultName, StringComparison.OrdinalIgnoreCase)
            || _working.CustomPalettes.Any(palette => string.Equals(palette.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    // The list as the working copy has it, with this palette selected.
    private void Rebuild(Guid? selectId, bool selectBuiltIn)
    {
        Palettes.Clear();
        Palettes.Add(new GraphPaletteEntry(null, GraphPaletteLibrary.YatDefaultName, _service.YatDefault, _working.DefaultPaletteId is null));
        foreach (var palette in _working.CustomPalettes)
        {
            Palettes.Add(new GraphPaletteEntry(palette.Id, palette.Name, palette.Palette, palette.Id == _working.DefaultPaletteId));
        }

        Selected = selectBuiltIn ? Palettes[0] : Palettes.FirstOrDefault(entry => entry.Id == selectId) ?? Palettes[0];
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanEditSelected));
        OnPropertyChanged(nameof(WorkingLibrary));
        NewCommand?.NotifyCanExecuteChanged();
        DuplicateCommand?.NotifyCanExecuteChanged();
        DeleteCommand?.NotifyCanExecuteChanged();
        SetDefaultCommand?.NotifyCanExecuteChanged();
        ResetDefaultCommand?.NotifyCanExecuteChanged();
    }
}
