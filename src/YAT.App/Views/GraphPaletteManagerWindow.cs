using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.Application.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The Palette Manager (Task #050): the palettes on the left - YAT Default, built in, then the user's - with New,
// Duplicate and Delete under them; the selected palette's name and colours on the right, with Set as Default and Reset
// to YAT Default; Save and Cancel. Nothing is changed until Save, which puts every edit in place at once; Cancel, or
// closing the window, changes nothing. When the palette settings are read-only this session the manager says so at the
// top, and everything can be looked at but not changed. Modal to the window it was opened from.
internal sealed class GraphPaletteManagerWindow : Window
{
    private const string NotSavedMessage =
        "Your palette changes are in use, but they could not be saved, so they may be lost when YAT closes.";

    private GraphPaletteManagerWindow(GraphPaletteManagerViewModel manager)
    {
        DataContext = manager;
        Title = "Palettes";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var notice = new TextBlock
        {
            Name = "PaletteSettingsNotice",
            Text = manager.Notice,
            IsVisible = manager.Notice is not null,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 600,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x67, 0x00))
        };

        var list = new ListBox
        {
            Name = "PaletteList",
            ItemsSource = manager.Palettes,
            Width = 250,
            Height = 300,
            ItemTemplate = new FuncDataTemplate<GraphPaletteEntry>((_, _) =>
            {
                var swatches = new ItemsControl
                {
                    ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 }),
                    ItemTemplate = new FuncDataTemplate<GraphColor>((color, _) => new Border
                    {
                        Width = 8,
                        Height = 12,
                        Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B))
                    })
                };
                swatches.Bind(ItemsControl.ItemsSourceProperty, new Binding("Palette.Colors"));

                var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(GraphPaletteEntry.DisplayName)));
                return new StackPanel { Spacing = 3, Children = { name, swatches } };
            })
        };
        list.Bind(ListBox.SelectedItemProperty, new Binding(nameof(GraphPaletteManagerViewModel.Selected)) { Source = manager, Mode = BindingMode.TwoWay });

        var nameBox = new TextBox { Name = "PaletteName", Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        nameBox.Bind(TextBox.TextProperty, new Binding(nameof(GraphPaletteManagerViewModel.NameText)) { Source = manager, Mode = BindingMode.TwoWay });
        nameBox.Bind(IsEnabledProperty, new Binding(nameof(GraphPaletteManagerViewModel.CanEditSelected)) { Source = manager });

        var problem = new TextBlock
        {
            Name = "PaletteProblem",
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 600
        };

        var save = new Button { Name = "Save", Content = "Save", MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        save.Classes.Add("accent");
        save.Click += async (_, _) =>
        {
            if (manager.Save() is not { } result)
            {
                return;
            }

            // The palettes are in use whatever happened to the file; a save that failed is said, plainly, once.
            if (result.Status != GraphPaletteSaveStatus.Saved)
            {
                await LifecycleMessageWindow.ShowErrorAsync(
                    this,
                    result.Error is { } error ? $"{NotSavedMessage}{Environment.NewLine}{Environment.NewLine}{error}" : NotSavedMessage);
            }

            Close(true);
        };

        var cancel = new Button { Name = "Cancel", Content = "Cancel", MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        cancel.Click += (_, _) => Close(false);

        void Show()
        {
            save.IsEnabled = manager.CanSave;
            problem.Text = manager.Problem;
            problem.IsVisible = manager.Problem is not null;
        }

        manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GraphPaletteManagerViewModel.CanSave) or nameof(GraphPaletteManagerViewModel.Problem))
            {
                Show();
            }
        };
        Show();

        var left = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Button { Name = "NewPalette", Content = "New", Command = manager.NewCommand, MinWidth = 72 },
                        new Button { Name = "DuplicatePalette", Content = "Duplicate", Command = manager.DuplicateCommand, MinWidth = 72 },
                        new Button { Name = "DeletePalette", Content = "Delete", Command = manager.DeleteCommand, MinWidth = 72 }
                    }
                }
            }
        };

        var right = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(20, 0, 0, 0),
            Children =
            {
                new TextBlock { Text = "Name" },
                nameBox,
                new TextBlock { Text = "Colors", Margin = new Thickness(0, 6, 0, 0) },
                GraphPaletteColorsEditor.Create(manager.Colors),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 10, 0, 0),
                    Children =
                    {
                        new Button { Name = "SetDefaultPalette", Content = "Set as Default", Command = manager.SetDefaultCommand },
                        new Button { Name = "ResetDefaultPalette", Content = "Reset to YAT Default", Command = manager.ResetDefaultCommand }
                    }
                },
                new TextBlock
                {
                    Text = "New graphs start with the default palette's colors. Graphs already made keep their own.",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 340
                }
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 12,
            Children =
            {
                notice,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { left, right } },
                problem,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { save, cancel }
                }
            }
        };
    }

    // Shows the manager over its owner. True when it saved (the library changed, whether or not the file could be
    // written); false when it was cancelled or closed.
    public static async Task<bool> ShowAsync(Window owner, GraphPaletteManagerViewModel manager) =>
        await new GraphPaletteManagerWindow(manager).ShowDialog<bool?>(owner) == true;
}
