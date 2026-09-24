using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The box plot's own options (Task #047): the box width in percent, then whether the means and the outliers are
// marked. The graph setup's Box Plot Options... dialog and the Edit Box Plot dialog show them with this one editor,
// bound to a GraphBoxPlotEditorViewModel.
//
// Each control is named after the property it edits, so a dialog can find it and tests can drive it.
internal static class GraphBoxPlotEditor
{
    public static Control Create(GraphBoxPlotEditorViewModel boxPlot)
    {
        ArgumentNullException.ThrowIfNull(boxPlot);

        var width = new TextBox
        {
            Width = 80,
            MinHeight = 0,
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Name = nameof(GraphBoxPlotEditorViewModel.BoxWidthText)
        };
        width.Bind(
            TextBox.TextProperty,
            new Binding(nameof(GraphBoxPlotEditorViewModel.BoxWidthText)) { Source = boxPlot, Mode = BindingMode.TwoWay });

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Box width", VerticalAlignment = VerticalAlignment.Center },
                        width,
                        new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                Option(boxPlot, "Show mean", nameof(GraphBoxPlotEditorViewModel.ShowMean)),
                Option(boxPlot, "Show outliers", nameof(GraphBoxPlotEditorViewModel.ShowOutliers))
            }
        };
    }

    private static CheckBox Option(GraphBoxPlotEditorViewModel boxPlot, string text, string property)
    {
        var option = new CheckBox { Content = text, Name = property };
        option.Bind(ToggleButton.IsCheckedProperty, new Binding(property) { Source = boxPlot, Mode = BindingMode.TwoWay });
        return option;
    }
}
