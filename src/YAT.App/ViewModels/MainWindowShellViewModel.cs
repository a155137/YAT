using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Lifecycle;

namespace YAT.app.ViewModels;

// The main window's view model: the File menu (project lifecycle) and window title, around the current project's
// MainWindowViewModel (Project), which is replaced whenever another project is shown (New, Open, Save As).
public sealed partial class MainWindowShellViewModel : ViewModelBase
{
    private const string ApplicationName = "YAT";

    private MainWindowViewModel? _observedProject;

    public MainWindowShellViewModel(ProjectLifecycleController lifecycle, GraphSetupController graphs)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(graphs);
        Lifecycle = lifecycle;
        Graphs = graphs;
        Lifecycle.ProjectReplaced += OnProjectReplaced;
        ObserveProject();
    }

    public ProjectLifecycleController Lifecycle { get; }

    // Graph menu: set up a graph over the active worksheet of the current project.
    public GraphSetupController Graphs { get; }

    public MainWindowViewModel? Project => Lifecycle.Project;

    // "YAT — <project name>"; never the file name, path or a modified marker.
    public string WindowTitle => Project?.CurrentProject is { } project ? $"{ApplicationName} — {project.Name}" : ApplicationName;

    public Task<bool> StartAsync() => Lifecycle.StartAsync();

    // Whether the window may close (after the save prompt, if one was needed); the project is closed when it may.
    public Task<bool> RequestCloseAsync() => Lifecycle.CloseAsync();

    [RelayCommand]
    private Task NewProjectAsync() => Lifecycle.NewProjectAsync();

    [RelayCommand]
    private Task OpenProjectAsync() => Lifecycle.OpenProjectAsync();

    [RelayCommand]
    private Task SaveAsync() => Lifecycle.SaveAsync();

    [RelayCommand]
    private Task SaveAsAsync() => Lifecycle.SaveAsAsync();

    [RelayCommand]
    private Task ScatterPlotAsync() => ConfigureGraphAsync(GraphType.ScatterPlot);

    [RelayCommand]
    private Task HistogramAsync() => ConfigureGraphAsync(GraphType.Histogram);

    [RelayCommand]
    private Task ProbabilityPlotAsync() => ConfigureGraphAsync(GraphType.ProbabilityPlot);

    [RelayCommand]
    private Task EmpiricalCdfAsync() => ConfigureGraphAsync(GraphType.EmpiricalCdf);

    // Always the current project's session and its active worksheet, so a project switch is picked up automatically.
    private Task ConfigureGraphAsync(GraphType graphType) =>
        Graphs.ConfigureAsync(graphType, Lifecycle.CurrentSession, Project?.SelectedWorksheet, CancellationToken.None);

    private void OnProjectReplaced(object? sender, EventArgs e)
    {
        ObserveProject();
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void ObserveProject()
    {
        if (_observedProject is not null)
        {
            _observedProject.PropertyChanged -= OnProjectPropertyChanged;
        }

        _observedProject = Project;
        if (_observedProject is not null)
        {
            _observedProject.PropertyChanged += OnProjectPropertyChanged;
        }
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentProject))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }
}
