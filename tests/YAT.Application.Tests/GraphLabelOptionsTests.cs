using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// The graph labels (Task #040): every label Auto unless the user chooses, Custom text normalized to one trimmed line and
// refused when nothing is left, Hidden as no label at all.
public class GraphLabelOptionsTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly WorksheetColumn Reg1 = Column("Reg1", 0);
    private static readonly WorksheetColumn Reg2 = Column("Reg2", 1);

    private static WorksheetColumn Column(string name, int index) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = WorksheetId,
        Index = index,
        Name = name,
        DataType = WorksheetDataType.Numeric
    };

    private static GraphConfiguration Configuration(GraphType graphType, GraphLabelOptions options) =>
        new(graphType, WorksheetId, graphType == GraphType.ScatterPlot
            ? [new GraphColumnAssignment(GraphVariableRole.X, Reg1.Id), new GraphColumnAssignment(GraphVariableRole.Y, Reg2.Id)]
            : [new GraphColumnAssignment(GraphVariableRole.Variable, Reg1.Id)])
        {
            LabelOptions = options
        };

    private static GraphValidationResult Validate(GraphType graphType, GraphLabelOptions options) =>
        new GraphConfigurationValidator().Validate(Configuration(graphType, options), [Reg1, Reg2]);

    public static TheoryData<GraphType> GraphTypes => [.. Enum.GetValues<GraphType>()];

    // ---- Model ----

    [Fact]
    public void TheDefaultIsEveryLabelAuto()
    {
        Assert.Equal(GraphLabelOption.Auto, GraphLabelOptions.Default.Title);
        Assert.Equal(GraphLabelOption.Auto, GraphLabelOptions.Default.XAxisTitle);
        Assert.Equal(GraphLabelOption.Auto, GraphLabelOptions.Default.YAxisTitle);
        Assert.Equal(new GraphLabelOption(GraphLabelMode.Auto, null), GraphLabelOption.Auto);
        Assert.Equal(GraphLabelMode.Auto, new GraphLabelOption().Mode);
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void EveryConfigurationStartsWithTheDefault(GraphType graphType) =>
        Assert.Same(GraphLabelOptions.Default, new GraphConfiguration(graphType, WorksheetId, []).LabelOptions);

    [Fact]
    public void EachFieldReadsItsOwnOption()
    {
        var options = new GraphLabelOptions(GraphLabelOption.Custom("T"), GraphLabelOption.Hidden, GraphLabelOption.Custom("Y"));

        Assert.Same(options.Title, options.For(GraphLabelField.Title));
        Assert.Same(options.XAxisTitle, options.For(GraphLabelField.XAxisTitle));
        Assert.Same(options.YAxisTitle, options.For(GraphLabelField.YAxisTitle));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.For((GraphLabelField)9));
        Assert.Equal([GraphLabelField.Title, GraphLabelField.XAxisTitle, GraphLabelField.YAxisTitle], GraphLabelRules.Fields);
    }

    // ---- Normalization ----

    [Theory]
    [InlineData("Wafer thickness", "Wafer thickness")]
    [InlineData("  Wafer thickness  ", "Wafer thickness")]
    [InlineData("Wafer\r\nthickness", "Wafer thickness")]
    [InlineData("Wafer\nthickness", "Wafer thickness")]
    [InlineData("Wafer\rthickness", "Wafer thickness")]
    [InlineData("Wafer\tthickness", "Wafer thickness")]
    [InlineData("Wafer\u0085thickness", "Wafer thickness")]
    [InlineData("Wafer\u2028thickness", "Wafer thickness")]
    [InlineData("Wafer\u2029thickness", "Wafer thickness")]
    [InlineData("a\r\n\r\nb", "a  b")]
    [InlineData("\t Lot  A \r\n", "Lot  A")]
    [InlineData("μm ± 3σ", "μm ± 3σ")]
    [InlineData("CASE kept", "CASE kept")]
    public void CustomTextBecomesOneTrimmedLine(string typed, string expected) =>
        Assert.Equal(expected, GraphLabelRules.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData(" \r\n\t \n ")]
    [InlineData("\u2028\u2029")]
    public void WhitespaceOnlyTextNormalizesToNothing(string? typed) =>
        Assert.Equal(string.Empty, GraphLabelRules.Normalize(typed));

    [Fact]
    public void LongTextIsKeptWhole()
    {
        var typed = new string('x', 5000);

        Assert.Equal(typed, GraphLabelRules.Normalize(typed));
        Assert.Empty(GraphLabelRules.Check(new GraphLabelOptions(GraphLabelOption.Custom(typed), GraphLabelOption.Auto, GraphLabelOption.Auto)));
    }

    // ---- Rules ----

    public static TheoryData<GraphLabelOption> ValidOptions => new()
    {
        GraphLabelOption.Auto,
        GraphLabelOption.Hidden,
        GraphLabelOption.Custom("Title"),
        GraphLabelOption.Custom("  padded  "),
        new GraphLabelOption(GraphLabelMode.Auto, "   "),
        new GraphLabelOption(GraphLabelMode.Hidden, "   "),
        new GraphLabelOption(GraphLabelMode.Auto, "typed, then Auto chosen")
    };

    [Theory]
    [MemberData(nameof(ValidOptions))]
    public void EveryFieldAcceptsAValidOption(GraphLabelOption option)
    {
        Assert.Empty(GraphLabelRules.Check(new GraphLabelOptions(option, GraphLabelOption.Auto, GraphLabelOption.Auto)));
        Assert.Empty(GraphLabelRules.Check(new GraphLabelOptions(GraphLabelOption.Auto, option, GraphLabelOption.Auto)));
        Assert.Empty(GraphLabelRules.Check(new GraphLabelOptions(GraphLabelOption.Auto, GraphLabelOption.Auto, option)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void CustomNeedsTextThatSurvivesNormalization(string? typed)
    {
        var custom = new GraphLabelOption(GraphLabelMode.Custom, typed);

        Assert.Equal(
            [new GraphLabelProblem(GraphLabelField.Title, GraphLabelProblemKind.CustomTextMissing)],
            GraphLabelRules.Check(new GraphLabelOptions(custom, GraphLabelOption.Auto, GraphLabelOption.Auto)));
        Assert.Equal(
            [new GraphLabelProblem(GraphLabelField.XAxisTitle, GraphLabelProblemKind.CustomTextMissing)],
            GraphLabelRules.Check(new GraphLabelOptions(GraphLabelOption.Auto, custom, GraphLabelOption.Auto)));
        Assert.Equal(
            [new GraphLabelProblem(GraphLabelField.YAxisTitle, GraphLabelProblemKind.CustomTextMissing)],
            GraphLabelRules.Check(new GraphLabelOptions(GraphLabelOption.Auto, GraphLabelOption.Auto, custom)));
    }

    [Fact]
    public void EveryProblemIsReportedInFieldOrder()
    {
        var options = new GraphLabelOptions(
            GraphLabelOption.Custom(" "),
            new GraphLabelOption((GraphLabelMode)7),
            GraphLabelOption.Custom("\t"));

        Assert.Equal(
            [
                new GraphLabelProblem(GraphLabelField.Title, GraphLabelProblemKind.CustomTextMissing),
                new GraphLabelProblem(GraphLabelField.XAxisTitle, GraphLabelProblemKind.UnknownMode),
                new GraphLabelProblem(GraphLabelField.YAxisTitle, GraphLabelProblemKind.CustomTextMissing)
            ],
            GraphLabelRules.Check(options));
    }

    [Fact]
    public void MissingOptionsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => GraphLabelRules.Check(null!));
        Assert.Throws<ArgumentNullException>(() => GraphLabelRules.Check(new GraphLabelOptions(null!, GraphLabelOption.Auto, GraphLabelOption.Auto)));
        Assert.Throws<ArgumentNullException>(() => GraphLabelRules.Resolve(null!, "Auto"));
    }

    // ---- Resolution ----

    [Theory]
    [InlineData("Histogram of Reg1")]
    [InlineData(null)]
    [InlineData("")]
    public void AutoIsTheGraphTypesOwnLabelUnchanged(string? automatic) =>
        Assert.Equal(automatic, GraphLabelRules.Resolve(GraphLabelOption.Auto, automatic));

    [Fact]
    public void AutoIgnoresAnyTypedText() =>
        Assert.Equal("Reg1", GraphLabelRules.Resolve(new GraphLabelOption(GraphLabelMode.Auto, "typed"), "Reg1"));

    [Theory]
    [InlineData("Histogram of Reg1")]
    [InlineData(null)]
    public void HiddenIsNoLabel(string? automatic)
    {
        Assert.Null(GraphLabelRules.Resolve(GraphLabelOption.Hidden, automatic));
        Assert.Null(GraphLabelRules.Resolve(new GraphLabelOption(GraphLabelMode.Hidden, "typed"), automatic));
    }

    [Theory]
    [InlineData("Wafer thickness", "Wafer thickness")]
    [InlineData("  Thickness\r\n(um)\t", "Thickness (um)")]
    public void CustomIsTheNormalizedText(string typed, string expected)
    {
        Assert.Equal(expected, GraphLabelRules.Resolve(GraphLabelOption.Custom(typed), "Reg1"));
        Assert.Equal(expected, GraphLabelRules.Resolve(GraphLabelOption.Custom(typed), null));
    }

    [Fact]
    public void OptionsTheRulesRefuseCannotBeResolved()
    {
        Assert.Throws<ArgumentException>(() => GraphLabelRules.Resolve(GraphLabelOption.Custom("  "), "Reg1"));
        Assert.Throws<ArgumentException>(() => GraphLabelRules.Resolve(new GraphLabelOption(GraphLabelMode.Custom), "Reg1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphLabelRules.Resolve(new GraphLabelOption((GraphLabelMode)7), "Reg1"));
    }

    // ---- Validation ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheDefaultLabelsAreValidForEveryGraph(GraphType graphType) =>
        Assert.True(Validate(graphType, GraphLabelOptions.Default).IsValid);

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void CustomAndHiddenLabelsAreValidForEveryGraph(GraphType graphType) =>
        Assert.True(Validate(graphType, new GraphLabelOptions(GraphLabelOption.Custom("T"), GraphLabelOption.Hidden, GraphLabelOption.Custom("Y"))).IsValid);

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void AnEmptyCustomLabelIsReportedForTheFieldItBelongsTo(GraphType graphType)
    {
        var result = Validate(graphType, new GraphLabelOptions(GraphLabelOption.Auto, GraphLabelOption.Custom(" \t "), GraphLabelOption.Auto));

        var error = Assert.Single(result.Errors);
        Assert.Equal(GraphValidationReason.LabelTextMissing, error.Reason);
        Assert.Equal(GraphLabelField.XAxisTitle, error.LabelField);
        Assert.Null(error.Field);
        Assert.Null(error.Role);
    }

    [Fact]
    public void AnUnknownModeIsReportedForTheFieldItBelongsTo()
    {
        var result = Validate(GraphType.Histogram, new GraphLabelOptions(GraphLabelOption.Auto, GraphLabelOption.Auto, new GraphLabelOption((GraphLabelMode)7)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(GraphValidationReason.LabelModeInvalid, error.Reason);
        Assert.Equal(GraphLabelField.YAxisTitle, error.LabelField);
    }

    [Fact]
    public void LabelProblemsFollowTheOtherProblems()
    {
        var configuration = Configuration(GraphType.Histogram, new GraphLabelOptions(GraphLabelOption.Custom(""), GraphLabelOption.Auto, GraphLabelOption.Auto))
            with { HistogramOptions = new HistogramOptions(BinningMode: HistogramBinningMode.Count) };

        var result = new GraphConfigurationValidator().Validate(configuration, [Reg1, Reg2]);

        Assert.Equal(
            [GraphValidationReason.HistogramBinCountInvalid, GraphValidationReason.LabelTextMissing],
            result.Errors.Select(error => error.Reason));
    }
}
