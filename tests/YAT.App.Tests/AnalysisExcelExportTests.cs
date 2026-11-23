using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using YAT.Application.Analyses;
using YAT.app.Analyses;
using YAT.app.Analyses.Export;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

public sealed class AnalysisExcelExportTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"yat-excel-export-tests-{Guid.NewGuid():N}");

    public AnalysisExcelExportTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static AnalysisColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static AnalysisVariableData Variable(string name, params double?[] values) => new(Column(name), values);

    private string TempPath(string fileName) => Path.Combine(_tempDirectory, fileName);

    [Fact]
    public void DescriptiveStatisticsExportsValidXlsxWithNumericPrecisionAndFormatting()
    {
        var data = new AnalysisData(
            Guid.NewGuid(),
            4,
            [
                Variable("Vth_Linear", 1.0 / 3.0, 2.0 / 3.0, 1.0, null),
                Variable("Idsat_High", 10.1234567890123, 10.2234567890123, 10.3234567890123, 10.4234567890123)
            ],
            new StringAnalysisGroupData(Column("SITE", WorksheetDataType.String), new string?[] { "S1", "S1", "S2", "S2" }));

        var table = new DescriptiveStatisticsBuilder().Build(data, Token) with
        {
            Note = "Filter: SITE is not empty"
        };

        var path = TempPath("descriptive.xlsx");
        var exporter = new OpenXmlAnalysisExcelExporter();
        exporter.Save(path, table);

        Assert.True(File.Exists(path));

        using var document = SpreadsheetDocument.Open(path, false);
        var validationErrors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document, Token).ToList();
        Assert.Empty(validationErrors);
        var workbookPart = document.WorkbookPart!;
        var sheets = workbookPart.Workbook!.Sheets!.Elements<Sheet>().ToList();
        Assert.Equal(["Results", "Analysis Info"], sheets.Select(s => s.Name!.Value));

        var resultsPart = (WorksheetPart)workbookPart.GetPartById(sheets[0].Id!);
        var worksheet = resultsPart.Worksheet!;

        // Frozen header row and AutoFilter
        var pane = worksheet.SheetViews!.GetFirstChild<SheetView>()!.GetFirstChild<Pane>()!;
        Assert.Equal(1D, pane.VerticalSplit!.Value);
        Assert.Equal("A2", pane.TopLeftCell!.Value);
        Assert.Equal(PaneStateValues.Frozen, pane.State!.Value);

        var autoFilter = worksheet.GetFirstChild<AutoFilter>()!;
        Assert.Equal("A1:K5", autoFilter.Reference!.Value);

        // Column widths configured
        var columns = worksheet.GetFirstChild<Columns>()!.Elements<Column>().ToList();
        Assert.Equal(11, columns.Count);
        Assert.All(columns, col => Assert.True(col.CustomWidth!.Value));

        // 1 header + 4 data rows (2 variables x 2 groups)
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToList();
        Assert.Equal(5, rows.Count);

        // First data row: Vth_Linear, S1 -> Mean = 0.5, N = 2, Missing = 0
        var firstDataCells = rows[1].Elements<Cell>().ToDictionary(c => c.CellReference!.Value!);
        Assert.Equal(CellValues.InlineString, firstDataCells["A2"].DataType!.Value);
        Assert.Equal("Vth_Linear", firstDataCells["A2"].InlineString!.Text!.Text);
        Assert.Equal(CellValues.Number, firstDataCells["C2"].DataType!.Value);
        Assert.Equal("2", firstDataCells["C2"].CellValue!.Text);
        Assert.Equal(CellValues.Number, firstDataCells["E2"].DataType!.Value);
        Assert.Equal(0.5, double.Parse(firstDataCells["E2"].CellValue!.Text, CultureInfo.InvariantCulture), 12);

        // Vth_Linear, S2 has only 1 observation (1.0) and 1 missing -> StDev must be omitted (not 0)
        var secondDataCells = rows[2].Elements<Cell>().ToDictionary(c => c.CellReference!.Value!);
        Assert.False(secondDataCells.ContainsKey("F3"));
        Assert.Equal("1", secondDataCells["D3"].CellValue!.Text); // Missing = 1

        // High-precision check: IdSat_High S1 Mean = 10.1734567890123 (beyond G8 display precision)
        var thirdDataCells = rows[3].Elements<Cell>().ToDictionary(c => c.CellReference!.Value!);
        var exportedMean = double.Parse(thirdDataCells["E4"].CellValue!.Text, CultureInfo.InvariantCulture);
        Assert.Equal(table.Rows[2].RawNumericValues![4]!.Value, exportedMean, 12);

        // Analysis Info sheet preserves filter note
        var infoPart = (WorksheetPart)workbookPart.GetPartById(sheets[1].Id!);
        var infoText = infoPart.Worksheet!.InnerText;
        Assert.Contains("Descriptive Statistics", infoText);
        Assert.Contains("Filter: SITE is not empty", infoText);
    }

    [Fact]
    public void CapabilityAnalysisExportsValidXlsxWithSpecificationsAndIndices()
    {
        var v1 = Variable("Reg1", 100, 101, 104, 102);
        var v2 = Variable("Reg2", 50.1, 50.2, 50.15, 50.25);
        var data = new AnalysisData(Guid.NewGuid(), 4, [v1, v2], null);
        var config = new CapabilityAnalysisConfiguration(
            data.WorksheetId,
            [
                new CapabilityVariable(v1.Column.ColumnId, 95.0, 110.0),
                new CapabilityVariable(v2.Column.ColumnId, 49.5, 50.5)
            ],
            null,
            CapabilityAnalysisConfiguration.SelectableStatistics);

        var table = new CapabilityAnalysisBuilder().Build(data, config, Token);
        var path = TempPath("capability.xlsx");
        new OpenXmlAnalysisExcelExporter().Save(path, table);

        using var document = SpreadsheetDocument.Open(path, false);
        var validationErrors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document, Token).ToList();
        Assert.Empty(validationErrors);
        var workbookPart = document.WorkbookPart!;
        var sheets = workbookPart.Workbook!.Sheets!.Elements<Sheet>().ToList();
        var resultsPart = (WorksheetPart)workbookPart.GetPartById(sheets[0].Id!);
        var rows = resultsPart.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().ToList();

        Assert.Equal(3, rows.Count); // Header + 2 variables
        var headerTexts = rows[0].Elements<Cell>().Select(c => c.InlineString!.Text!.Text).ToList();
        Assert.Equal(table.Columns.Select(c => c.Name).ToList(), headerTexts);
        Assert.Contains("LSL", headerTexts);
        Assert.Contains("USL", headerTexts);
        Assert.Contains("Cp", headerTexts);
        Assert.Contains("Cpk", headerTexts);

        // Verify numeric cells match unrounded raw values
        var cpIndex = headerTexts.IndexOf("Cp");
        var cpColumnLetter = ((char)('A' + cpIndex)).ToString();
        var reg1Cells = rows[1].Elements<Cell>().ToList();
        var cpCell = reg1Cells.First(c => c.CellReference!.Value!.StartsWith(cpColumnLetter, StringComparison.Ordinal));
        Assert.Equal(CellValues.Number, cpCell.DataType!.Value);
        var exportedCp = double.Parse(cpCell.CellValue!.Text, CultureInfo.InvariantCulture);
        Assert.Equal(table.Rows[0].RawNumericValues![cpIndex]!.Value, exportedCp, 12);

        // Verify Analysis Info includes variable specifications
        var infoPart = (WorksheetPart)workbookPart.GetPartById(sheets[1].Id!);
        var infoRows = infoPart.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().ToList();
        Assert.Contains(infoRows, r => r.InnerText.Contains("Reg1") && r.InnerText.Contains("95") && r.InnerText.Contains("110"));
        Assert.Contains(infoRows, r => r.InnerText.Contains("Reg2") && r.InnerText.Contains("49.5") && r.InnerText.Contains("50.5"));
    }

    [Fact]
    public void HandlesSpecialCharactersLongNamesAndNullValuesWithoutError()
    {
        var longName = new string('X', 120) + " <&> \"quoted\" 'single' ";
        var data = new AnalysisData(
            Guid.NewGuid(),
            2,
            [Variable(longName, null, null)],
            null);

        var table = new DescriptiveStatisticsBuilder().Build(data, Token);
        var path = TempPath("special-chars.xlsx");
        new OpenXmlAnalysisExcelExporter().Save(path, table);

        using var document = SpreadsheetDocument.Open(path, false);
        var sheets = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().ToList();
        var resultsPart = (WorksheetPart)document.WorkbookPart!.GetPartById(sheets[0].Id!);
        var rows = resultsPart.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().ToList();
        var firstCell = rows[1].Elements<Cell>().First();
        Assert.Equal(longName, firstCell.InlineString!.Text!.Text);
    }

    [Fact]
    public async Task ControllerSuggestsDefaultNamesAndHandlesCancelEmptyAndFailure()
    {
        Assert.Equal("YAT_DescriptiveStatistics.xlsx", AnalysisExportController.SuggestedFileName(AnalysisExportKind.DescriptiveStatistics));
        Assert.Equal("YAT_CapabilityAnalysis.xlsx", AnalysisExportController.SuggestedFileName(AnalysisExportKind.CapabilityAnalysis));

        var emptyTable = new AnalysisResultTable("Empty", [new AnalysisResultColumn("Variable", AnalysisResultAlignment.Left)], []);
        var dialogs = new FakeExportDialogs();
        var exporter = new OpenXmlAnalysisExcelExporter();
        var controller = new AnalysisExportController(dialogs, exporter);

        // Empty table -> shows error, never opens file picker
        Assert.False(await controller.ExportExcelAsync(emptyTable, Token));
        Assert.Equal(0, dialogs.PickCount);
        Assert.Single(dialogs.Errors);

        // Cancel save dialog -> no error, no file written
        var validTable = new AnalysisResultTable(
            "Descriptive Statistics: V1",
            [new AnalysisResultColumn("Variable", AnalysisResultAlignment.Left), new AnalysisResultColumn("N", AnalysisResultAlignment.Right)],
            [new AnalysisResultRow(["V1", "5"], [null, 5])])
        {
            ExportKind = AnalysisExportKind.DescriptiveStatistics
        };

        dialogs.NextPath = null;
        Assert.False(await controller.ExportExcelAsync(validTable, Token));
        Assert.Equal("YAT_DescriptiveStatistics.xlsx", dialogs.LastSuggestedFileName);
        Assert.Single(dialogs.Errors); // Still only the earlier error

        // Valid export -> writes file and shows info message
        var targetPath = TempPath("exported.xlsx");
        dialogs.NextPath = targetPath;
        Assert.True(await controller.ExportExcelAsync(validTable, Token));
        Assert.True(File.Exists(targetPath));
        Assert.Equal(targetPath, controller.LastExportedPath);
        Assert.Single(dialogs.Infos);

        // Overwrite existing file cleanly without residual data
        var overwrittenTable = new AnalysisResultTable(
            "Descriptive Statistics: V2",
            [new AnalysisResultColumn("Variable", AnalysisResultAlignment.Left), new AnalysisResultColumn("N", AnalysisResultAlignment.Right)],
            [new AnalysisResultRow(["V2", "9"], [null, 9])])
        {
            ExportKind = AnalysisExportKind.DescriptiveStatistics
        };
        dialogs.NextPath = targetPath;
        Assert.True(await controller.ExportExcelAsync(overwrittenTable, Token));
        using (var overwrittenDoc = SpreadsheetDocument.Open(targetPath, false))
        {
            Assert.Empty(new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(overwrittenDoc, Token));
            var overwrittenSheets = overwrittenDoc.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().ToList();
            var overwrittenResults = (WorksheetPart)overwrittenDoc.WorkbookPart.GetPartById(overwrittenSheets[0].Id!);
            var overwrittenRows = overwrittenResults.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().ToList();
            Assert.Equal("V2", overwrittenRows[1].Elements<Cell>().First().InlineString!.Text!.Text);
        }

        // Failing exporter -> shows error without throwing
        var failingController = new AnalysisExportController(dialogs, new ThrowingExporter());
        dialogs.NextPath = TempPath("fail.xlsx");
        Assert.False(await failingController.ExportExcelAsync(validTable, Token));
        Assert.Equal(2, dialogs.Errors.Count);
    }

    private sealed class FakeExportDialogs : IAnalysisExportDialogs
    {
        public string? NextPath { get; set; }
        public int PickCount { get; private set; }
        public string? LastSuggestedFileName { get; private set; }
        public List<string> Infos { get; } = [];
        public List<string> Errors { get; } = [];

        public Task<string?> PickExcelAsync(string suggestedFileName)
        {
            PickCount++;
            LastSuggestedFileName = suggestedFileName;
            return Task.FromResult(NextPath);
        }

        public Task ShowInformationAsync(string message)
        {
            Infos.Add(message);
            return Task.CompletedTask;
        }

        public Task ShowErrorAsync(string message)
        {
            Errors.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingExporter : IAnalysisExcelExporter
    {
        public void Save(string path, AnalysisResultTable table) => throw new IOException("Disk full");
    }
}
