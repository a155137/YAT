using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Domain.Entities;

namespace YAT.app.ViewModels;

// Navigation state of the Project Explorer tree: the current Project as the root item and its worksheets below it.
// MainWindowViewModel drives it with metadata it already holds; selecting a worksheet item asks the main window to
// make that worksheet active. It has no session access of its own.
public sealed partial class ProjectExplorerViewModel : ObservableObject
{
    private readonly ICommand _newWorksheetCommand;
    private readonly Action<Worksheet> _activateWorksheet;

    internal ProjectExplorerViewModel(ICommand newWorksheetCommand, Action<Worksheet> activateWorksheet)
    {
        _newWorksheetCommand = newWorksheetCommand;
        _activateWorksheet = activateWorksheet;
    }

    // Tree roots: empty without a Project, otherwise the single project item.
    public ObservableCollection<ProjectExplorerItem> Items { get; } = [];

    public ProjectExplorerProjectItem? ProjectItem { get; private set; }

    // Tree selection. Selecting a worksheet item activates that worksheet; selecting the project item changes nothing.
    [ObservableProperty]
    public partial ProjectExplorerItem? SelectedItem { get; set; }

    public IEnumerable<ProjectExplorerWorksheetItem> WorksheetItems =>
        ProjectItem?.Children.OfType<ProjectExplorerWorksheetItem>() ?? [];

    partial void OnSelectedItemChanged(ProjectExplorerItem? value)
    {
        if (value is ProjectExplorerWorksheetItem worksheetItem)
        {
            _activateWorksheet(worksheetItem.Worksheet);
        }
    }

    // Rebuilds the tree for the given Project (or clears it) with its worksheets in order.
    internal void ShowProject(Project? project, IEnumerable<Worksheet> worksheets, Guid? activeWorksheetId)
    {
        SelectedItem = null;
        Items.Clear();
        ProjectItem = null;

        if (project is not null)
        {
            ProjectItem = new ProjectExplorerProjectItem(project, _newWorksheetCommand);
            foreach (var worksheet in worksheets)
            {
                ProjectItem.Children.Add(new ProjectExplorerWorksheetItem(worksheet));
            }

            Items.Add(ProjectItem);
        }

        OnPropertyChanged(nameof(ProjectItem));
        SetActiveWorksheet(activeWorksheetId);
    }

    // Appends a newly created worksheet and expands the project so it is visible.
    internal void AddWorksheet(Worksheet worksheet)
    {
        if (ProjectItem is null)
        {
            return;
        }

        ProjectItem.Children.Add(new ProjectExplorerWorksheetItem(worksheet));
        ProjectItem.IsExpanded = true;
    }

    // Marks the active worksheet and moves the tree selection to it. Without one, a selected worksheet item is deselected.
    internal void SetActiveWorksheet(Guid? worksheetId)
    {
        ProjectExplorerWorksheetItem? active = null;
        foreach (var item in WorksheetItems)
        {
            item.IsActive = item.WorksheetId == worksheetId;
            if (item.IsActive)
            {
                active = item;
            }
        }

        if (active is not null)
        {
            SelectedItem = active;
        }
        else if (SelectedItem is ProjectExplorerWorksheetItem)
        {
            SelectedItem = null;
        }
    }
}
