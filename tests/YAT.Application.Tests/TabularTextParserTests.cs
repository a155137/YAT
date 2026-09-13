using YAT.Application.Exceptions;
using YAT.Application.Ingestion;

namespace YAT.Application.Tests;

public class TabularTextParserTests
{
    [Fact]
    public void ParsesStandardTsvPreservingOrder()
    {
        var parser = new TabularTextParser();

        var data = parser.Parse("A\tB\tC\n1\t2\t3\n4\t5\t6");

        Assert.Equal(["A", "B", "C"], data.Headers);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(["1", "2", "3"], data.Rows[0]);
        Assert.Equal(["4", "5", "6"], data.Rows[1]);
    }

    [Fact]
    public void ParsesCrlfLineEndings()
    {
        var parser = new TabularTextParser();

        var data = parser.Parse("No\tBin\tSITE\r\n1\t1\t1\r\n2\t2\t2");

        Assert.Equal(["No", "Bin", "SITE"], data.Headers);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(["1", "1", "1"], data.Rows[0]);
        Assert.Equal(["2", "2", "2"], data.Rows[1]);
    }

    [Fact]
    public void ParsesLfLineEndings()
    {
        var parser = new TabularTextParser();

        var data = parser.Parse("No\tBin\tSITE\n1\t1\t1\n2\t2\t2");

        Assert.Equal(["No", "Bin", "SITE"], data.Headers);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(["1", "1", "1"], data.Rows[0]);
        Assert.Equal(["2", "2", "2"], data.Rows[1]);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void IgnoresSingleTrailingLineTerminator(string newline)
    {
        var parser = new TabularTextParser();

        var data = parser.Parse($"A\tB{newline}1\t2{newline}");

        Assert.Equal(["A", "B"], data.Headers);
        var row = Assert.Single(data.Rows);
        Assert.Equal(["1", "2"], row);
    }

    [Fact]
    public void ParsesSpreadsheetSample()
    {
        var parser = new TabularTextParser();

        var data = parser.Parse(
            "No\tBin\tSITE\tReg1\tReg2\tReg3\tReg4\r\n" +
            "1\t1\t1\t5\t0.132\t500\t2\r\n" +
            "2\t2\t2\t7\t0.157\t2350\t2\r\n");

        Assert.Equal(["No", "Bin", "SITE", "Reg1", "Reg2", "Reg3", "Reg4"], data.Headers);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(["1", "1", "1", "5", "0.132", "500", "2"], data.Rows[0]);
        Assert.Equal(["2", "2", "2", "7", "0.157", "2350", "2"], data.Rows[1]);
    }

    [Fact]
    public void PreservesEmptyMiddleCell()
    {
        var parser = new TabularTextParser();

        var data = parser.Parse("A\tB\tC\n1\t\t3");

        var row = Assert.Single(data.Rows);
        Assert.Equal(["1", "", "3"], row);
    }

    [Fact]
    public void PreservesTrailingEmptyCell()
    {
        var parser = new TabularTextParser();

        var data = parser.Parse("A\tB\tC\n1\t2\t");

        var row = Assert.Single(data.Rows);
        Assert.Equal(3, row.Count);
        Assert.Equal(["1", "2", ""], row);
    }

    [Fact]
    public void PreservesEmptyRowsInSingleColumnData()
    {
        var parser = new TabularTextParser();

        var data = parser.Parse("A\r\n1\r\n\r\n3\r\n");

        Assert.Equal(["A"], data.Headers);
        Assert.Equal(3, data.Rows.Count);
        Assert.Equal(["1"], data.Rows[0]);
        Assert.Equal([""], data.Rows[1]);
        Assert.Equal(["3"], data.Rows[2]);
    }

    [Fact]
    public void PreservesCellTextVerbatim()
    {
        var parser = new TabularTextParser();

        var data = parser.Parse(" No \tLot A\n 1 \tN123.45");

        Assert.Equal([" No ", "Lot A"], data.Headers);
        Assert.Equal([" 1 ", "N123.45"], Assert.Single(data.Rows));
    }

    [Theory]
    [InlineData("No\tBin\tSITE")]
    [InlineData("No\tBin\tSITE\r\n")]
    [InlineData("No\tBin\tSITE\n")]
    public void AcceptsHeaderOnlyInput(string text)
    {
        var parser = new TabularTextParser();

        var data = parser.Parse(text);

        Assert.Equal(["No", "Bin", "SITE"], data.Headers);
        Assert.Empty(data.Rows);
    }

    [Fact]
    public void KeepsDuplicateHeaderNames()
    {
        var parser = new TabularTextParser();

        var data = parser.Parse("Value\tValue\n1\t2");

        Assert.Equal(["Value", "Value"], data.Headers);
    }

    [Fact]
    public void KeepsEmptyHeaderCells()
    {
        var parser = new TabularTextParser();

        var data = parser.Parse("No\t\tReg1\n1\t2\t3");

        Assert.Equal(["No", "", "Reg1"], data.Headers);
        Assert.Equal(["1", "2", "3"], Assert.Single(data.Rows));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData(" \t \n ")]
    public void RejectsBlankInput(string? text)
    {
        var parser = new TabularTextParser();

        Assert.Throws<ValidationException>(() => parser.Parse(text!));
    }

    [Fact]
    public void RejectsRowWithTooFewCells()
    {
        var parser = new TabularTextParser();

        var exception = Assert.Throws<ValidationException>(() => parser.Parse("A\tB\tC\n1\t2"));

        Assert.Equal("Line 2 has 2 cells, but the header has 3 columns.", exception.Message);
    }

    [Fact]
    public void RejectsRowWithTooManyCells()
    {
        var parser = new TabularTextParser();

        var exception = Assert.Throws<ValidationException>(() => parser.Parse("A\tB\n1\t2\t3"));

        Assert.Equal("Line 2 has 3 cells, but the header has 2 columns.", exception.Message);
    }

    [Fact]
    public void RejectsWidthMismatchAfterValidRows()
    {
        var parser = new TabularTextParser();

        var exception = Assert.Throws<ValidationException>(() => parser.Parse("A\tB\r\n1\t2\r\n\r\n3\t4\r\n"));

        Assert.Equal("Line 3 has 1 cell, but the header has 2 columns.", exception.Message);
    }
}
