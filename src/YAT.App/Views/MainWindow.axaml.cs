using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using YAT.app.ViewModels;

namespace YAT.app.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    // Worksheet paste uses the bubbling KeyDown event rather than Window.KeyBindings: key bindings are matched before
    // the focused control sees the key, which would take Ctrl+V away from text boxes. A focused TextBox handles its
    // own paste first, so this only runs when no control handled the gesture.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled
            || DataContext is not MainWindowViewModel viewModel
            || this.GetPlatformSettings()?.HotkeyConfiguration.Paste.Any(gesture => gesture.Matches(e)) != true)
        {
            return;
        }

        if (viewModel.PasteCommand.CanExecute(null))
        {
            viewModel.PasteCommand.Execute(null);
            e.Handled = true;
        }
    }
}
