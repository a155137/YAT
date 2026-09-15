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
    private readonly Func<ProjectExplorerItem, string, Task<bool>> _rename;

    // The item whose rename is being stored; other commits, its cancel and new renames are ignored until it completes.
    private ProjectExplorerItem? _committingItem;

    // rename: stores the new name for the item through the application (and refreshes the item from the result);
    // returns false when the name was rejected.
    internal ProjectExplorerViewModel(
        ICommand newWorksheetCommand,
        Action<Worksheet> activateWorksheet,
        Func<ProjectExplorerItem, string, Task<bool>> rename)
    {
        _newWorksheetCommand = newWorksheetCommand;
        _activateWorksheet = activateWorksheet;
        _rename = rename;
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

    // Shows the stored state of a renamed project or worksheet; items keep their identity, expansion and selection.
    internal void UpdateProject(Project project)
    {
        if (ProjectItem is not null && ProjectItem.Project.Id == project.Id)
        {
            ProjectItem.Project = project;
        }
    }

    internal void UpdateWorksheet(Worksheet worksheet)
    {
        if (WorksheetItems.FirstOrDefault(item => item.WorksheetId == worksheet.Id) is { } item)
        {
            item.Worksheet = worksheet;
        }
    }

    // Double-click on an item name: edits the current name inline. Another item still being edited is cancelled.
    public void BeginRename(ProjectExplorerItem item)
    {
        if (_committingItem is not null)
        {
            return;
        }

        foreach (var editing in AllItems().Where(candidate => candidate.IsEditing && candidate != item))
        {
            CancelRename(editing);
        }

        item.EditName = item.Name;
        item.IsEditing = true;
    }

    // Enter: stores the edited name. A rejected name keeps the item in edit mode; the main window shows the reason.
    public Task<bool> CommitRenameAsync(ProjectExplorerItem item) => CommitAsync(item, cancelWhenRejected: false);

    // Focus loss: stores the edited name. A rejected name ends editing and restores the current name; the reason stays shown.
    public Task<bool> CommitOrCancelRenameAsync(ProjectExplorerItem item) => CommitAsync(item, cancelWhenRejected: true);

    // Esc: ends editing without storing anything.
    public void CancelRename(ProjectExplorerItem item)
    {
        if (_committingItem == item)
        {
            return;
        }

        item.IsEditing = false;
        item.EditName = item.Name;
    }

    // Returns whether a new name was stored. An unchanged name ends editing without a rename.
    private async Task<bool> CommitAsync(ProjectExplorerItem item, bool cancelWhenRejected)
    {
        if (!item.IsEditing || _committingItem is not null)
        {
            return false;
        }

        if (string.Equals(item.EditName.Trim(), item.Name, StringComparison.Ordinal))
        {
            CancelRename(item);
            return false;
        }

        bool renamed;
        _committingItem = item;
        try
        {
            renamed = await _rename(item, item.EditName);
        }
        finally
        {
            _committingItem = null;
        }

        if (renamed || cancelWhenRejected)
        {
            CancelRename(item);
        }

        return renamed;
    }

    private IEnumerable<ProjectExplorerItem> AllItems() =>
        Items.Concat(ProjectItem?.Children ?? []);

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
