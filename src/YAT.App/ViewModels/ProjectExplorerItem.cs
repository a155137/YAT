using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Domain.Entities;

namespace YAT.app.ViewModels;

// One Project Explorer tree item. Items are navigation metadata only (identity, name, tree state): they never hold
// worksheet values. The item kinds are separate types, so a project can later hold children other than worksheets.
public abstract partial class ProjectExplorerItem : ObservableObject
{
    public abstract string Name { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

// Root item: the current Project, with its worksheets as direct children in creation order.
public sealed class ProjectExplorerProjectItem : ProjectExplorerItem
{
    internal ProjectExplorerProjectItem(Project project, ICommand newWorksheetCommand)
    {
        Project = project;
        NewWorksheetCommand = newWorksheetCommand;
        IsExpanded = true;
    }

    public Project Project { get; }

    public override string Name => Project.Name;

    public ObservableCollection<ProjectExplorerItem> Children { get; } = [];

    // Context menu "New Worksheet": the main window's command, which creates the next "SheetN" in this project.
    public ICommand NewWorksheetCommand { get; }
}

// A worksheet of the project: its metadata entity only.
public sealed partial class ProjectExplorerWorksheetItem : ProjectExplorerItem
{
    internal ProjectExplorerWorksheetItem(Worksheet worksheet)
    {
        Worksheet = worksheet;
    }

    public Worksheet Worksheet { get; }

    public Guid WorksheetId => Worksheet.Id;

    public override string Name => Worksheet.Name;

    // The worksheet shown in the main workspace. Kept apart from tree selection, which can move to the project item.
    [ObservableProperty]
    public partial bool IsActive { get; internal set; }
}
