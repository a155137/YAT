using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Exceptions;
using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Domain.Entities;
using CreateProjectRequest = YAT.Application.Features.Projects.CreateProject.CreateProjectCommand;
using CreateWorksheetRequest = YAT.Application.Features.Worksheets.CreateWorksheet.CreateWorksheetCommand;

namespace YAT.app.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly CreateProjectHandler _createProject;
    private readonly CreateWorksheetHandler _createWorksheet;

    public MainWindowViewModel(CreateProjectHandler createProject, CreateWorksheetHandler createWorksheet)
    {
        _createProject = createProject;
        _createWorksheet = createWorksheet;
    }

    [ObservableProperty]
    public partial string ProjectName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProjectDescription { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentProject))]
    [NotifyCanExecuteChangedFor(nameof(CreateWorksheetCommand))]
    public partial Project? CurrentProject { get; private set; }

    public bool HasCurrentProject => CurrentProject is not null;

    [ObservableProperty]
    public partial string WorksheetName { get; set; } = string.Empty;

    // Worksheet metadata created in this session for the current Project only; never row data.
    public ObservableCollection<Worksheet> Worksheets { get; } = [];

    [ObservableProperty]
    public partial Worksheet? SelectedWorksheet { get; set; }

    [ObservableProperty]
    public partial bool IsProjectPanelVisible { get; private set; } = true;

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateWorksheetCommand))]
    public partial bool IsBusy { get; private set; }

    [RelayCommand(CanExecute = nameof(CanCreateProject))]
    private async Task CreateProjectAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var description = string.IsNullOrWhiteSpace(ProjectDescription) ? null : ProjectDescription;
            var project = await _createProject.HandleAsync(
                new CreateProjectRequest(ProjectName, description), cancellationToken);

            // One active Project at a time: switching clears the session Worksheet list.
            Worksheets.Clear();
            SelectedWorksheet = null;
            CurrentProject = project;
            ProjectName = string.Empty;
            ProjectDescription = string.Empty;
        }
        catch (ValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreateProject() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreateWorksheet))]
    private async Task CreateWorksheetAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        if (CurrentProject is null)
        {
            ErrorMessage = "Create a Project before adding Worksheets.";
            return;
        }

        IsBusy = true;
        try
        {
            var worksheet = await _createWorksheet.HandleAsync(
                new CreateWorksheetRequest(CurrentProject.Id, WorksheetName), cancellationToken);

            Worksheets.Add(worksheet);
            SelectedWorksheet = worksheet;
            WorksheetName = string.Empty;
        }
        catch (Exception ex) when (ex is ValidationException or EntityNotFoundException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreateWorksheet() => !IsBusy && CurrentProject is not null;

    [RelayCommand]
    private void ToggleProjectPanel() => IsProjectPanelVisible = !IsProjectPanelVisible;
}
