using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// How a graph looks (Task #046): a colour is "#RRGGBB" and nothing else, a palette is its colours in order, and an
// appearance is carried by the configuration and checked by its validation - a defined grid mode, and a custom palette
// of one to sixteen colours. The default chooses nothing.
public class GraphAppearanceOptionsTests
{
    private static readonly GraphConfigurationValidator Validator = new();

    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly Guid Reg1 = Guid.NewGuid();

    private static readonly IReadOnlyList<WorksheetColumn> Columns =
    [
        new() { Id = Reg1, WorksheetId = WorksheetId, Name = "Reg1", Index = 0, DataType = WorksheetDataType.Numeric }
    ];

    private static IReadOnlyList<GraphValidationReason> Reasons(GraphConfiguration configuration) =>
        [.. Validator.Validate(configuration, Columns).Errors.Select(error => error.Reason)];

    private static GraphConfiguration Histogram(GraphAppearanceOptions appearance) =>
        new(GraphType.Histogram, WorksheetId, [new GraphColumnAssignment(GraphVariableRole.Variable, Reg1)])
        {
            AppearanceOptions = appearance
        };

    private static GraphPalette Palette(int colors) =>
        new([.. Enumerable.Range(0, colors).Select(index => new GraphColor((byte)index, (byte)(index * 7), (byte)(255 - index)))]);

    // ---- GraphColor ----

    [Theory]
    [InlineData("#1F77B4", 0x1F, 0x77, 0xB4)]
    [InlineData("#1f77b4", 0x1F, 0x77, 0xB4)]
    [InlineData("1F77B4", 0x1F, 0x77, 0xB4)]
    [InlineData("  #000000 ", 0, 0, 0)]
    [InlineData("#FFFFFF", 255, 255, 255)]
    public void AColorIsReadFromItsHexText(string text, byte r, byte g, byte b)
    {
        Assert.True(GraphColor.TryParse(text, out var color));
        Assert.Equal(new GraphColor(r, g, b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#12345G")]
    [InlineData("#12 456")]
    [InlineData("##123456")]
    [InlineData("#+12345")]
    [InlineData("#1F77B4FF")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("#１２３４５６")]
    public void TextThatIsNotRrggbbIsNoColor(string? text)
    {
        Assert.False(GraphColor.TryParse(text, out var color));
        Assert.Equal(default, color);
    }

    [Fact]
    public void AColorIsWrittenOneWay()
    {
        Assert.Equal("#1F77B4", new GraphColor(0x1F, 0x77, 0xB4).ToString());
        Assert.Equal("#000000", new GraphColor(0, 0, 0).ToString());
        Assert.True(GraphColor.TryParse("#0a0b0c", out var color));
        Assert.Equal("#0A0B0C", color.ToString());
        Assert.True(GraphColor.TryParse(color.ToString(), out var again));
        Assert.Equal(color, again);
    }

    [Fact]
    public void ColorsCompareByValue()
    {
        Assert.Equal(new GraphColor(1, 2, 3), new GraphColor(1, 2, 3));
        Assert.NotEqual(new GraphColor(1, 2, 3), new GraphColor(3, 2, 1));
        Assert.Equal(new GraphColor(1, 2, 3).GetHashCode(), new GraphColor(1, 2, 3).GetHashCode());
    }

    // ---- GraphPalette ----

    [Fact]
    public void PalettesCompareByTheirColorsInOrder()
    {
        GraphColor[] colors = [new(1, 2, 3), new(4, 5, 6)];

        Assert.Equal(new GraphPalette(colors), new GraphPalette([.. colors]));
        Assert.Equal(new GraphPalette(colors).GetHashCode(), new GraphPalette([.. colors]).GetHashCode());
        Assert.NotEqual(new GraphPalette(colors), new GraphPalette([colors[1], colors[0]]));
        Assert.NotEqual(new GraphPalette(colors), new GraphPalette([colors[0]]));
    }

    [Fact]
    public void APaletteKeepsItsOwnCopyInOrder()
    {
        var colors = new List<GraphColor> { new(1, 2, 3), new(4, 5, 6) };
        var palette = new GraphPalette(colors);
        colors.Add(new GraphColor(7, 8, 9));

        Assert.Equal([new GraphColor(1, 2, 3), new GraphColor(4, 5, 6)], palette.Colors);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void APaletteHasOneToSixteenColors(int colors, bool valid)
    {
        Assert.Equal(valid, Palette(colors).IsValid);
        Assert.Equal(valid, new GraphAppearanceOptions(Palette(colors)).IsValid);
        Assert.Equal(
            valid ? [] : [GraphValidationReason.AppearancePaletteInvalid],
            Reasons(Histogram(new GraphAppearanceOptions(Palette(colors)))));
    }

    // ---- GraphAppearanceOptions ----

    [Fact]
    public void TheDefaultChoosesNothing()
    {
        var appearance = GraphAppearanceOptions.Default;

        Assert.Null(appearance.Palette);
        Assert.Equal(GraphGridMode.Auto, appearance.GridMode);
        Assert.Null(appearance.GridColor);
        Assert.Null(appearance.PlotBackground);
        Assert.Null(appearance.GraphBackground);
        Assert.True(appearance.IsDefault);
        Assert.True(appearance.IsValid);
        Assert.Equal(new GraphAppearanceOptions(), appearance);
        Assert.Same(GraphAppearanceOptions.Default, Histogram(GraphAppearanceOptions.Default).AppearanceOptions);
        Assert.Same(GraphAppearanceOptions.Default, new GraphConfiguration(GraphType.ScatterPlot, WorksheetId, []).AppearanceOptions);
    }

    [Fact]
    public void AnythingChosenIsNotTheDefault()
    {
        var color = new GraphColor(0x12, 0x34, 0x56);

        Assert.False(new GraphAppearanceOptions(Palette(3)).IsDefault);
        Assert.False(new GraphAppearanceOptions(GridMode: GraphGridMode.Show).IsDefault);
        Assert.False(new GraphAppearanceOptions(GridMode: GraphGridMode.Hide).IsDefault);
        Assert.False(new GraphAppearanceOptions(GridColor: color).IsDefault);
        Assert.False(new GraphAppearanceOptions(PlotBackground: color).IsDefault);
        Assert.False(new GraphAppearanceOptions(GraphBackground: color).IsDefault);
    }

    [Fact]
    public void AppearancesCompareByValueTheirPalettesToo()
    {
        var one = new GraphAppearanceOptions(Palette(4), GraphGridMode.Hide, new GraphColor(1, 1, 1));
        var other = new GraphAppearanceOptions(Palette(4), GraphGridMode.Hide, new GraphColor(1, 1, 1));

        Assert.Equal(one, other);
        Assert.NotEqual(one, other with { Palette = Palette(5) });
        Assert.NotEqual(one, other with { GridMode = GraphGridMode.Show });
    }

    [Theory]
    [InlineData(GraphGridMode.Auto)]
    [InlineData(GraphGridMode.Show)]
    [InlineData(GraphGridMode.Hide)]
    public void EveryGridModeIsValid(GraphGridMode mode)
    {
        Assert.Empty(Reasons(Histogram(new GraphAppearanceOptions(GridMode: mode))));
    }

    [Fact]
    public void AGridModeThatIsNotAChoiceIsRefused()
    {
        var appearance = new GraphAppearanceOptions(GridMode: (GraphGridMode)7);

        Assert.False(appearance.IsValid);
        Assert.Equal([GraphValidationReason.AppearanceGridModeInvalid], Reasons(Histogram(appearance)));
        Assert.Equal(
            [GraphValidationReason.AppearanceGridModeInvalid, GraphValidationReason.AppearancePaletteInvalid],
            GraphConfigurationValidator.AppearanceErrors(new GraphAppearanceOptions(Palette(0), (GraphGridMode)(-1))).Select(error => error.Reason));
    }

    // Every graph type has an appearance, so every one checks it.
    [Fact]
    public void EveryGraphTypeHasAnAppearance()
    {
        Assert.All(GraphTypeDefinitions.All, definition => Assert.True(definition.Supports(GraphCapability.Appearance)));
    }

    [Fact]
    public void AnAppearanceChangesNothingTheDataIsReadBy()
    {
        var configuration = Histogram(GraphAppearanceOptions.Default);
        var styled = configuration with { AppearanceOptions = new GraphAppearanceOptions(Palette(2), GraphGridMode.Hide) };

        Assert.Equal(configuration.GraphType, styled.GraphType);
        Assert.Equal(configuration.WorksheetId, styled.WorksheetId);
        Assert.Equal(configuration.Assignments, styled.Assignments);
    }
}
