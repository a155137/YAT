using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.app.Views;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Presentation rules of the worksheet grid. Rules only: no rendering, no pixel measurements.
public class WorksheetGridLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static WorksheetGridColumn Column(string name, WorksheetDataType dataType) =>
        new(Guid.NewGuid(), 0, name, dataType);

    // 1
    [Fact]
    public void NumericCellsAlignRightAndOtherCellsAlignLeft()
    {
        Assert.Equal(HorizontalAlignment.Right, WorksheetGridLayout.GetCellAlignment(WorksheetDataType.Numeric));
        Assert.Equal(HorizontalAlignment.Left, WorksheetGridLayout.GetCellAlignment(WorksheetDataType.String));
        Assert.Equal(HorizontalAlignment.Left, WorksheetGridLayout.GetCellAlignment(WorksheetDataType.DateTime));
        Assert.Equal(HorizontalAlignment.Left, WorksheetGridLayout.GetCellAlignment(WorksheetDataType.Boolean));
    }

    // 2
    [Fact]
    public void ColumnWidthDependsOnlyOnNameAndDataType()
    {
        var first = Column("Reg2", WorksheetDataType.Numeric);
        var second = new WorksheetGridColumn(Guid.NewGuid(), 7, "Reg2", WorksheetDataType.Numeric);

        Assert.Equal(WorksheetGridLayout.GetColumnWidth(first), WorksheetGridLayout.GetColumnWidth(second));
        Assert.Equal(WorksheetGridLayout.GetColumnWidth(first), WorksheetGridLayout.GetColumnWidth(first));
    }

    [Theory]
    [InlineData("No", WorksheetDataType.Numeric, 88)]
    [InlineData("Lot", WorksheetDataType.String, 96)]
    [InlineData("Temperature_Deg_C", WorksheetDataType.Numeric, 148)]
    [InlineData("Temperature_Deg_C", WorksheetDataType.String, 148)]
    [InlineData("A_very_long_parameter_name_from_the_tester", WorksheetDataType.Numeric, 160)]
    [InlineData("A_very_long_parameter_name_from_the_tester", WorksheetDataType.String, 240)]
    public void ColumnWidthFollowsTheHeaderWithinPerTypeLimits(string name, WorksheetDataType dataType, double expectedWidth)
    {
        Assert.Equal(expectedWidth, WorksheetGridLayout.GetColumnWidth(Column(name, dataType)));
    }

    [Fact]
    public void ShortTextColumnsStartWiderThanShortNumericColumns()
    {
        Assert.True(
            WorksheetGridLayout.GetColumnWidth(Column("Lot", WorksheetDataType.String))
            > WorksheetGridLayout.GetColumnWidth(Column("Bin", WorksheetDataType.Numeric)));
    }

    // Task #019 test 5: Normal, Selected and Active headers are distinct states.
    [Fact]
    public void HeaderStatesAreNormalSelectedAndActive()
    {
        var header = WorksheetGridLayout.CreateHeader(Column("SITE", WorksheetDataType.Numeric));

        Assert.Equal("SITE", WorksheetGridLayout.GetHeaderText(header));
        Assert.Contains(WorksheetGridLayout.HeaderClass, header.Classes);
        Assert.Equal(ColumnHeaderState.Normal, WorksheetGridLayout.GetHeaderState(header));
        Assert.Equal(FontWeight.Normal, WorksheetGridLayout.GetHeaderFontWeight(header));

        WorksheetGridLayout.SetHeaderState(header, ColumnHeaderState.Selected);
        Assert.Equal(ColumnHeaderState.Selected, WorksheetGridLayout.GetHeaderState(header));
        Assert.Contains(WorksheetGridLayout.SelectedClass, header.Classes);
        Assert.DoesNotContain(WorksheetGridLayout.ActiveClass, header.Classes);
        Assert.Equal(FontWeight.SemiBold, WorksheetGridLayout.GetHeaderFontWeight(header));

        WorksheetGridLayout.SetHeaderState(header, ColumnHeaderState.Active);
        Assert.Equal(ColumnHeaderState.Active, WorksheetGridLayout.GetHeaderState(header));
        Assert.Contains(WorksheetGridLayout.SelectedClass, header.Classes);
        Assert.Contains(WorksheetGridLayout.ActiveClass, header.Classes);

        WorksheetGridLayout.SetHeaderState(header, ColumnHeaderState.Normal);
        Assert.Equal(ColumnHeaderState.Normal, WorksheetGridLayout.GetHeaderState(header));
        Assert.DoesNotContain(WorksheetGridLayout.SelectedClass, header.Classes);
        Assert.Equal(FontWeight.Normal, WorksheetGridLayout.GetHeaderFontWeight(header));
    }

    [Fact]
    public void HeaderStateRuleGivesTheActiveColumnPrecedence()
    {
        var active = Guid.NewGuid();
        var selected = Guid.NewGuid();
        IReadOnlySet<Guid> selection = new HashSet<Guid> { active, selected };

        Assert.Equal(ColumnHeaderState.Active, WorksheetGridLayout.GetHeaderState(active, active, selection));
        Assert.Equal(ColumnHeaderState.Selected, WorksheetGridLayout.GetHeaderState(selected, active, selection));
        Assert.Equal(ColumnHeaderState.Normal, WorksheetGridLayout.GetHeaderState(Guid.NewGuid(), active, selection));
        Assert.Equal(ColumnHeaderState.Normal, WorksheetGridLayout.GetHeaderState(selected, null, new HashSet<Guid>()));
    }

    [Fact]
    public void RowNumberHeaderIsAnEmptyHeader()
    {
        var header = WorksheetGridLayout.CreateHeader(null);

        Assert.Equal(string.Empty, WorksheetGridLayout.GetHeaderText(header));
        Assert.Equal(ColumnHeaderState.Normal, WorksheetGridLayout.GetHeaderState(header));
    }

    // 18: spreadsheet grid lines on headers; the selection tint and accent bar stay on the inner element, so grid lines
    // keep their neutral brush and the header keeps its size in every selection state.
    [Fact]
    public void HeadersDrawGridLinesAndKeepTheAccentBarInside()
    {
        var header = WorksheetGridLayout.CreateHeader(Column("Reg1", WorksheetDataType.Numeric));
        var marker = Assert.IsType<Border>(header.Child);

        Assert.Equal(new Avalonia.Thickness(0, 0, 1, 1), header.BorderThickness);
        Assert.Equal(new Avalonia.Thickness(0, 0, 0, 2), marker.BorderThickness);

        WorksheetGridLayout.SetHeaderState(header, ColumnHeaderState.Active);

        Assert.Equal(new Avalonia.Thickness(0, 0, 1, 1), header.BorderThickness);
        Assert.Equal(new Avalonia.Thickness(0, 0, 0, 2), marker.BorderThickness);
        Assert.Equal("SystemControlForegroundBaseLowBrush", WorksheetGridLayout.GridLineBrushKey);
    }

    // The window restyles headers when ActiveColumn/SelectedColumns change; header clicks and paste keep driving them.
    [Fact]
    public async Task HeaderSelectionRaisesSelectionChangesAndStillTargetsPaste()
    {
        var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
        using var projectSession = compositionRoot.CreateProjectSession(":memory:");
        var clipboard = new FakeClipboard();
        var viewModel = compositionRoot.CreateMainWindowViewModel(compositionRoot.CreateMainWindowSession(projectSession, clipboard, clipboard));
        await viewModel.CreateDefaultWorkspaceAsync();
        await viewModel.GridLoadTask;
        clipboard.Text = "No\tBin\tSITE\n1\t1\t1\n";
        await viewModel.PasteCommand.ExecuteAsync(null);

        var notifications = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.ActiveColumn) or nameof(MainWindowViewModel.SelectedColumns))
            {
                notifications++;
            }
        };

        var bin = viewModel.GridColumns[1];
        viewModel.SelectColumn(bin.ColumnId);
        Assert.Equal(2, notifications);

        clipboard.Text = "Lot\nN1\n";
        await viewModel.PasteCommand.ExecuteAsync(null);

        Assert.Equal(["No", "Lot", "SITE"], viewModel.GridColumns.Select(column => column.Name));
        Assert.Equal(bin.ColumnId, viewModel.ActiveColumn?.Id);
        Assert.True(notifications >= 4);
    }
}
