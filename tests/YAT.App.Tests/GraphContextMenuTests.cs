using System.Reflection;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using YAT.app.Views;

namespace YAT.App.Tests;

// The graph window's right-click menu: a Copy Image item made the same way as File > Copy Image, running the very command
// Ctrl+C runs, on a host that covers the whole graph area, so where the click lands never changes what is copied.
//
// Filling a menu's item list needs Avalonia's UI thread, which a unit test does not have; the window puts the item in the
// menu, and that step is checked in the running application.
[Collection(GraphMenuItemCollection.Name)]
public class GraphContextMenuTests
{
    // 1
    [Fact]
    public void CopyImageIsLabelledAndBoundLikeTheFileMenuItem()
    {
        var item = GraphWindow.CopyImageItem(new RelayCommand(() => { }));

        Assert.Equal("_Copy Image", item.Header);
        Assert.Equal(GraphWindow.CopyImageGesture, item.InputGesture);
    }

    // 2
    [Fact]
    public void CopyImageRunsTheCommandItWasGivenWithoutAParameter()
    {
        var runs = 0;
        var command = new RelayCommand(() => runs++);

        var item = GraphWindow.CopyImageItem(command);
        Assert.Same(command, item.Command);
        Assert.Null(item.CommandParameter);

        item.Command!.Execute(item.CommandParameter);
        Assert.Equal(1, runs);
    }

    // 3
    [Fact]
    public void TheGraphAreaTakesTheRightClickForTheWholeCanvas()
    {
        var canvas = new GraphCanvas();
        var contextMenu = new ContextMenu();

        var area = GraphWindow.GraphArea(canvas, contextMenu);

        Assert.Same(canvas, area.Child);
        Assert.Same(Brushes.Transparent, area.Background);
        Assert.Equal(default, area.Padding);
        Assert.Equal(default, area.BorderThickness);
        Assert.Same(contextMenu, area.ContextMenu);
        Assert.Null(canvas.ContextMenu);
    }

    // 4: the window keeps a single copy command for the menu, the shortcut and the right-click.
    [Fact]
    public void AGraphWindowHasOneCopyCommand()
    {
        var commands = typeof(GraphWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(field => typeof(ICommand).IsAssignableFrom(field.FieldType));

        Assert.Equal("_copyImage", Assert.Single(commands).Name);
    }
}
