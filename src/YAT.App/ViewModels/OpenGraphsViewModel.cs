using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace YAT.app.ViewModels;

// One open graph window in the Graphs list (Task #061): its title, as its window shows it now, and whether it is the
// graph window last activated. What bringing it back takes - restoring, moving into view, activating - belongs to the
// window, which supplies it; nothing here knows about windows.
public sealed partial class OpenGraphItem : ObservableObject
{
    private readonly Action _bringToFront;
    private readonly Action? _close;

    internal OpenGraphItem(string title, Action bringToFront, Action? close = null)
    {
        ArgumentNullException.ThrowIfNull(bringToFront);
        Title = title;
        _bringToFront = bringToFront;
        _close = close;
    }

    [ObservableProperty]
    public partial string Title { get; internal set; }

    [ObservableProperty]
    public partial bool IsActive { get; internal set; }

    internal void BringToFront() => _bringToFront();

    internal void Close() => _close?.Invoke();
}

// The graph windows open now (Task #061), in the order they were opened: a navigator, not a store. A window is listed
// when it is shown, follows its title, is marked while it is the graph last activated, and leaves the list when it
// closes. Nothing is kept once YAT closes; the windows themselves are the graphs.
public sealed partial class OpenGraphsViewModel : ObservableObject
{
    public OpenGraphsViewModel()
    {
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Header));
            OnPropertyChanged(nameof(HasGraphs));
            CloseAllCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<OpenGraphItem> Items { get; } = [];

    // "Graphs (12)", or "Graphs" with none open.
    public string Header => Items.Count == 0
        ? "Graphs"
        : string.Create(CultureInfo.InvariantCulture, $"Graphs ({Items.Count})");

    public bool HasGraphs => Items.Count > 0;

    // The graph window last activated, while it is open; the list shows it selected.
    [ObservableProperty]
    public partial OpenGraphItem? ActiveItem { get; private set; }

    // Whether the Graphs list is shown in the main window (View > Graphs); shown to begin with.
    [ObservableProperty]
    public partial bool IsPanelVisible { get; set; } = true;

    // Lists a window that was just shown, under its title; bringToFront is what choosing it does, close closes it.
    public OpenGraphItem Add(string title, Action bringToFront, Action? close = null)
    {
        var item = new OpenGraphItem(title, bringToFront, close);
        Items.Add(item);
        return item;
    }

    // The window's title changed (a label edit).
    public void Rename(OpenGraphItem item, string title)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Title = title;
    }

    // The window was activated: it alone is marked.
    public void MarkActive(OpenGraphItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        foreach (var other in Items)
        {
            other.IsActive = ReferenceEquals(other, item);
        }

        ActiveItem = item;
    }

    // The window closed.
    public void Remove(OpenGraphItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Items.Remove(item);
        if (ReferenceEquals(ActiveItem, item))
        {
            ActiveItem = null;
        }
    }

    // Brings the chosen graph's window back: restored, in view and active.
    [RelayCommand]
    private void BringToFront(OpenGraphItem? item) => item?.BringToFront();

    [RelayCommand]
    private void TogglePanel() => IsPanelVisible = !IsPanelVisible;

    // Closes every open graph window through its normal window close lifecycle (snapshot avoids mutation while closing).
    [RelayCommand(CanExecute = nameof(HasGraphs))]
    private void CloseAll()
    {
        foreach (var item in Items.ToArray())
        {
            item.Close();
        }
    }
}
