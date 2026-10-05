using Avalonia;
using YAT.app.ViewModels;
using YAT.app.Views;

namespace YAT.App.Tests;

// The Graphs list (Task #061): the graph windows open now, in the order they opened, under their titles as they change,
// the one last activated marked, gone when closed; choosing one brings its window back; the panel shown or hidden from
// the View menu. And where a chosen window is put so it can be seen: inside its screen's working area.
public class GraphWindowListTests
{
    // ---- The list ----

    [Fact]
    public void WindowsAreListedInTheOrderTheyOpenUnderTheirTitles()
    {
        var graphs = new OpenGraphsViewModel();
        Assert.Equal("Graphs", graphs.Header);
        Assert.False(graphs.HasGraphs);

        graphs.Add("Histogram of Vout", () => { });
        graphs.Add("Boxplot of Reg1, Reg2, Reg3", () => { });
        graphs.Add("Histogram of Vout", () => { });

        Assert.Equal(["Histogram of Vout", "Boxplot of Reg1, Reg2, Reg3", "Histogram of Vout"], graphs.Items.Select(item => item.Title));
        Assert.Equal("Graphs (3)", graphs.Header);
        Assert.True(graphs.HasGraphs);
        Assert.All(graphs.Items, item => Assert.False(item.IsActive));
    }

    [Fact]
    public void FiftyWindowsAreFiftyItemsAndEachClosedOneLeaves()
    {
        var graphs = new OpenGraphsViewModel();
        var items = Enumerable.Range(1, 50).Select(index => graphs.Add($"Histogram of Vout{index:00}", () => { })).ToList();
        Assert.Equal("Graphs (50)", graphs.Header);

        graphs.Remove(items[0]);
        graphs.Remove(items[24]);
        graphs.Remove(items[49]);

        Assert.Equal(47, graphs.Items.Count);
        Assert.DoesNotContain(items[24], graphs.Items);
        Assert.Equal("Histogram of Vout02", graphs.Items[0].Title);
        Assert.Equal("Graphs (47)", graphs.Header);
    }

    [Fact]
    public void ATitleFollowsItsWindow()
    {
        var graphs = new OpenGraphsViewModel();
        var item = graphs.Add("Histogram of Vout", () => { });
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        graphs.Rename(item, "Output voltage");

        Assert.Equal("Output voltage", item.Title);
        Assert.Contains(nameof(OpenGraphItem.Title), changed);
    }

    [Fact]
    public void OnlyTheGraphLastActivatedIsMarked()
    {
        var graphs = new OpenGraphsViewModel();
        var first = graphs.Add("A", () => { });
        var second = graphs.Add("B", () => { });

        graphs.MarkActive(first);
        Assert.Equal([true, false], graphs.Items.Select(item => item.IsActive));

        graphs.MarkActive(second);
        Assert.Equal([false, true], graphs.Items.Select(item => item.IsActive));
        Assert.Same(second, graphs.ActiveItem);

        graphs.Remove(second);
        Assert.False(Assert.Single(graphs.Items).IsActive);
        Assert.Null(graphs.ActiveItem);
    }

    [Fact]
    public void ChoosingAGraphBringsOnlyItsWindowBack()
    {
        var graphs = new OpenGraphsViewModel();
        var brought = new List<string>();
        var first = graphs.Add("Same title", () => brought.Add("first"));
        var second = graphs.Add("Same title", () => brought.Add("second"));

        graphs.BringToFrontCommand.Execute(second);
        graphs.BringToFrontCommand.Execute(first);
        graphs.BringToFrontCommand.Execute(null);

        Assert.Equal(["second", "first"], brought);
    }

    [Fact]
    public void ThePanelIsShownToBeginWithAndToggledFromTheViewMenu()
    {
        var graphs = new OpenGraphsViewModel();
        Assert.True(graphs.IsPanelVisible);

        graphs.TogglePanelCommand.Execute(null);
        Assert.False(graphs.IsPanelVisible);

        graphs.TogglePanelCommand.Execute(null);
        Assert.True(graphs.IsPanelVisible);
    }

    // ---- Into view ----

    private static readonly PixelRect Screen = new(0, 0, 1600, 852);

    [Fact]
    public void AWindowInViewStaysWhereItIs()
    {
        Assert.Equal(new PixelPoint(222, 112), GraphWindowPlacement.IntoView(new PixelRect(222, 112, 760, 520), Screen));
        Assert.Equal(new PixelPoint(840, 332), GraphWindowPlacement.IntoView(new PixelRect(840, 332, 760, 520), Screen));
    }

    [Theory]
    [InlineData(1398, 1288, 840, 332)]
    [InlineData(1000, 200, 840, 200)]
    [InlineData(300, 600, 300, 332)]
    [InlineData(-400, -50, 0, 0)]
    [InlineData(5000, 5000, 840, 332)]
    public void AWindowBeyondTheScreenIsMovedTheLeastWayIntoIt(int x, int y, int expectedX, int expectedY) =>
        Assert.Equal(new PixelPoint(expectedX, expectedY), GraphWindowPlacement.IntoView(new PixelRect(x, y, 760, 520), Screen));

    [Fact]
    public void AWindowLargerThanTheScreenShowsItsTopLeft()
    {
        Assert.Equal(new PixelPoint(0, 0), GraphWindowPlacement.IntoView(new PixelRect(300, 300, 2000, 1200), Screen));
        Assert.Equal(new PixelPoint(1920, 0), GraphWindowPlacement.IntoView(new PixelRect(1700, -30, 1000, 1200), new PixelRect(1920, 0, 1280, 1000)));
    }
}
