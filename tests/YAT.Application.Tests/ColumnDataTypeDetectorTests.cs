using System.Globalization;
using YAT.Application.Ingestion;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// WorksheetDataType has a single numeric value: integer and floating-point columns both detect as Numeric.
public class ColumnDataTypeDetectorTests
{
    [Fact]
    public void DetectsIntegerColumnAsNumeric()
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.Numeric, detector.Detect(["1", "2", "3"]));
    }

    [Fact]
    public void DetectsNegativeIntegersAsNumeric()
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.Numeric, detector.Detect(["1", "-2", "3", "0", "-5"]));
    }

    [Fact]
    public void DetectsFloatingPointColumnAsNumeric()
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.Numeric, detector.Detect(["1.1", "2.2", "3.3", "-0.123"]));
    }

    [Fact]
    public void DetectsMixedIntegerAndFloatingPointAsNumeric()
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.Numeric, detector.Detect(["1", "2", "3.5", "4"]));
    }

    [Fact]
    public void DetectsNumericMixedWithTextAsString()
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.String, detector.Detect(["1", "2", "ABC", "4"]));
    }

    [Fact]
    public void IgnoresEmptyCells()
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.Numeric, detector.Detect(["1", "", "2", "", "3"]));
    }

    [Fact]
    public void IgnoresWhitespaceOnlyCells()
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.Numeric, detector.Detect(["1", "   ", "2.5"]));
    }

    [Fact]
    public void FallsBackToStringWhenAllCellsAreEmpty()
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.String, detector.Detect(["", "", " "]));
    }

    [Fact]
    public void FallsBackToStringWhenThereAreNoCells()
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.String, detector.Detect([]));
    }

    [Fact]
    public void DetectsScientificNotationAsNumeric()
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.Numeric, detector.Detect(["1E3", "1e-4", "2.5E+6", "2.5E-2"]));
    }

    [Theory]
    [InlineData("1,234")]
    [InlineData("0x1F")]
    [InlineData("5%")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1E400")]
    public void DetectsNonPlainNumericTextAsString(string cell)
    {
        var detector = new ColumnDataTypeDetector();

        Assert.Equal(WorksheetDataType.String, detector.Detect(["1", cell]));
    }

    [Fact]
    public void ParsesWithInvariantCultureRegardlessOfCurrentCulture()
    {
        var detector = new ColumnDataTypeDetector();
        var commaDecimalCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaDecimalCulture.NumberFormat.NumberDecimalSeparator = ",";
        commaDecimalCulture.NumberFormat.NumberGroupSeparator = ".";

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = commaDecimalCulture;

            Assert.Equal(WorksheetDataType.Numeric, detector.Detect(["1.25", "0.132"]));
            Assert.Equal(WorksheetDataType.String, detector.Detect(["1,25"]));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void DetectsTypePerParsedColumnInOrder()
    {
        var parser = new TabularTextParser();
        var detector = new ColumnDataTypeDetector();
        var data = parser.Parse(
            "No\tLot\tReg2\tEmpty\tSITE\r\n" +
            "1\tN123\t0.132\t\t1\r\n" +
            "2\tN124\t1E-3\t\t\r\n");

        var types = detector.DetectColumnTypes(data);

        Assert.Equal(
            [WorksheetDataType.Numeric, WorksheetDataType.String, WorksheetDataType.Numeric, WorksheetDataType.String, WorksheetDataType.Numeric],
            types);
    }

    [Fact]
    public void DetectsStringForEveryColumnOfHeaderOnlyData()
    {
        var parser = new TabularTextParser();
        var detector = new ColumnDataTypeDetector();
        var data = parser.Parse("No\tBin\tSITE");

        var types = detector.DetectColumnTypes(data);

        Assert.Equal([WorksheetDataType.String, WorksheetDataType.String, WorksheetDataType.String], types);
    }
}
