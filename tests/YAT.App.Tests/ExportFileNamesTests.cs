using YAT.app.Graphs.Export;

namespace YAT.App.Tests;

// The file name an export offers, made from the graph's title without changing it.
public class ExportFileNamesTests
{
    // 1
    [Fact]
    public void TheTitlesOfTheGraphsYatDraws()
    {
        Assert.Equal("Scatterplot of Reg2 vs Reg1.png", ExportFileNames.Suggest("Scatterplot of Reg2 vs Reg1", "png"));
        Assert.Equal("Histogram of Reg1.pptx", ExportFileNames.Suggest("Histogram of Reg1", "pptx"));
    }

    // 2
    [Fact]
    public void CharactersAFileNameCannotHoldBecomeUnderscores()
    {
        Assert.Equal("Scatterplot of I_V vs T", ExportFileNames.Sanitize("Scatterplot of I/V vs T"));
        Assert.Equal("Histogram of A_B", ExportFileNames.Sanitize("Histogram of A:B"));
        Assert.Equal("a_b", ExportFileNames.Sanitize("a<>:\"/\\|?*b"));
    }

    // 3
    [Fact]
    public void ATitleThatIsNothingButSuchCharactersFallsBack()
    {
        Assert.Equal(ExportFileNames.Fallback, ExportFileNames.Sanitize("///"));
        Assert.Equal(ExportFileNames.Fallback, ExportFileNames.Sanitize(string.Empty));
        Assert.Equal(ExportFileNames.Fallback, ExportFileNames.Sanitize("   "));
        Assert.Equal(ExportFileNames.Fallback, ExportFileNames.Sanitize(null));
        Assert.Equal($"{ExportFileNames.Fallback}.png", ExportFileNames.Suggest(null, "png"));
    }

    // 4
    [Fact]
    public void TrailingDotsAndSpacesAreNotPartOfAFileName()
    {
        Assert.Equal("Histogram of Reg1", ExportFileNames.Sanitize("  Histogram of Reg1. "));
    }

    // 5
    [Fact]
    public void AVeryLongTitleIsCutToSomethingUsable()
    {
        var name = ExportFileNames.Sanitize(new string('R', 500));

        Assert.Equal(ExportFileNames.MaximumLength, name.Length);
        Assert.True(ExportFileNames.Suggest(new string('R', 500), "pptx").Length <= ExportFileNames.MaximumLength + 5);
    }

    // 6
    [Fact]
    public void NamesWindowsKeepsForItselfAreAvoided()
    {
        Assert.Equal("_CON", ExportFileNames.Sanitize("CON"));
        Assert.Equal("_lpt1", ExportFileNames.Sanitize("lpt1"));
        Assert.Equal("Console", ExportFileNames.Sanitize("Console"));
    }

    // 7
    [Fact]
    public void ControlCharactersAreNotPartOfAFileName()
    {
        Assert.Equal("a_b", ExportFileNames.Sanitize("a\t\nb"));
    }

    // 8
    [Fact]
    public void TheExtensionIsAddedExactlyOnce()
    {
        Assert.Equal("Graph.png", ExportFileNames.Suggest("Graph", "png"));
        Assert.Equal("Graph.png", ExportFileNames.Suggest("Graph", ".png"));
        Assert.Throws<ArgumentException>(() => ExportFileNames.Suggest("Graph", " "));
    }

    // 9
    [Fact]
    public void TheGraphTitleItselfIsNeverChanged()
    {
        const string title = "Scatterplot of I/V vs T";

        ExportFileNames.Suggest(title, "png");

        Assert.Equal("Scatterplot of I/V vs T", title);
    }
}
