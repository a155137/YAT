using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace YAT.app.Analyses.Export;

// Writes an AnalysisResultTable to a genuine .xlsx workbook using the Open XML SDK (no Excel installation required).
// Numeric values are written as true Excel numeric cells with full double precision, headers are styled and frozen,
// auto-filter is enabled, column widths are fitted, and analysis metadata / specifications are preserved in a
// companion "Analysis Info" sheet.
public sealed class OpenXmlAnalysisExcelExporter : IAnalysisExcelExporter
{
    private const string ResultsSheetName = "Results";
    private const string InfoSheetName = "Analysis Info";
    private const double MinColumnWidth = 10;
    private const double MaxColumnWidth = 60;
    private const double HeaderFilterPadding = 5;
    private const double CellPadding = 3;

    private const uint DefaultStyleIndex = 0;
    private const uint HeaderStyleIndex = 1;
    private const uint NumericStyleIndex = 2;

    public void Save(string path, AnalysisResultTable table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(table);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStylesheet();
        stylesPart.Stylesheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());

        var resultsPart = workbookPart.AddNewPart<WorksheetPart>();
        resultsPart.Worksheet = BuildResultsWorksheet(table);
        resultsPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(resultsPart),
            SheetId = 1,
            Name = ResultsSheetName
        });

        var infoPart = workbookPart.AddNewPart<WorksheetPart>();
        infoPart.Worksheet = BuildInfoWorksheet(table);
        infoPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(infoPart),
            SheetId = 2,
            Name = InfoSheetName
        });

        workbookPart.Workbook.Save();
    }

    private static Worksheet BuildResultsWorksheet(AnalysisResultTable table)
    {
        var columnCount = table.Columns.Count;
        var columnWidths = new double[columnCount];
        for (var col = 0; col < columnCount; col++)
        {
            columnWidths[col] = table.Columns[col].Name.Length + HeaderFilterPadding;
        }

        var sheetData = new SheetData();

        var headerRow = new Row { RowIndex = 1 };
        for (var col = 0; col < columnCount; col++)
        {
            headerRow.Append(CreateTextCell(CellReference(col, 1), table.Columns[col].Name, HeaderStyleIndex));
        }
        sheetData.Append(headerRow);

        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var sourceRow = table.Rows[rowIndex];
            var excelRowNumber = (uint)(rowIndex + 2);
            var row = new Row { RowIndex = excelRowNumber };

            for (var col = 0; col < columnCount; col++)
            {
                var displayText = sourceRow.Cells[col] ?? string.Empty;
                columnWidths[col] = Math.Max(columnWidths[col], displayText.Length + CellPadding);

                var cellRef = CellReference(col, excelRowNumber);
                if (TryGetNumericValue(sourceRow, table.Columns[col], col, out var numericValue))
                {
                    row.Append(CreateNumericCell(cellRef, numericValue, NumericStyleIndex));
                }
                else if (!string.IsNullOrEmpty(displayText))
                {
                    row.Append(CreateTextCell(cellRef, displayText, DefaultStyleIndex));
                }
            }

            sheetData.Append(row);
        }

        var columns = new Columns();
        for (var col = 0; col < columnCount; col++)
        {
            columns.Append(new Column
            {
                Min = (uint)(col + 1),
                Max = (uint)(col + 1),
                Width = Math.Clamp(columnWidths[col], MinColumnWidth, MaxColumnWidth),
                CustomWidth = true
            });
        }

        var sheetViews = new SheetViews(
            new SheetView(
                new Pane
                {
                    VerticalSplit = 1,
                    TopLeftCell = "A2",
                    ActivePane = PaneValues.BottomLeft,
                    State = PaneStateValues.Frozen
                })
            {
                TabSelected = true,
                WorkbookViewId = 0
            });

        var lastColumn = ColumnName(columnCount - 1);
        var lastRow = table.Rows.Count + 1;
        var autoFilter = new AutoFilter { Reference = $"A1:{lastColumn}{lastRow}" };

        return new Worksheet(sheetViews, columns, sheetData, autoFilter);
    }

    private static Worksheet BuildInfoWorksheet(AnalysisResultTable table)
    {
        var entries = new List<(string Label, string? TextValue, double? NumericValue)>
        {
            ("Analysis Type", table.ExportMetadata?.AnalysisType ?? "Analysis", null),
            ("Title", table.Title, null)
        };

        if (table.ExportMetadata?.GroupByColumnName is { } groupColumn)
        {
            entries.Add(("Group By", groupColumn, null));
        }

        if (!string.IsNullOrWhiteSpace(table.Note))
        {
            entries.Add(("Filter", table.Note, null));
        }

        entries.Add(("Result Rows", null, table.Rows.Count));
        entries.Add(("Exported At", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), null));

        var sheetData = new SheetData();
        var colWidths = new double[] { 18, 24, 16 };
        uint rowNum = 1;

        var header = new Row { RowIndex = rowNum };
        header.Append(
            CreateTextCell(CellReference(0, rowNum), "Property", HeaderStyleIndex),
            CreateTextCell(CellReference(1, rowNum), "Value", HeaderStyleIndex));
        sheetData.Append(header);
        rowNum++;

        foreach (var (label, textValue, numericValue) in entries)
        {
            var row = new Row { RowIndex = rowNum };
            row.Append(CreateTextCell(CellReference(0, rowNum), label, DefaultStyleIndex));
            colWidths[0] = Math.Max(colWidths[0], label.Length + CellPadding);

            if (numericValue is { } num && double.IsFinite(num))
            {
                row.Append(CreateNumericCell(CellReference(1, rowNum), num, NumericStyleIndex));
            }
            else if (!string.IsNullOrEmpty(textValue))
            {
                row.Append(CreateTextCell(CellReference(1, rowNum), textValue, DefaultStyleIndex));
                colWidths[1] = Math.Max(colWidths[1], textValue.Length + CellPadding);
            }

            sheetData.Append(row);
            rowNum++;
        }

        if (table.ExportMetadata?.Specifications is { Count: > 0 } specs)
        {
            rowNum++; // Blank separator row

            var specHeader = new Row { RowIndex = rowNum };
            specHeader.Append(
                CreateTextCell(CellReference(0, rowNum), "Variable", HeaderStyleIndex),
                CreateTextCell(CellReference(1, rowNum), "LSL", HeaderStyleIndex),
                CreateTextCell(CellReference(2, rowNum), "USL", HeaderStyleIndex));
            sheetData.Append(specHeader);
            rowNum++;

            foreach (var spec in specs)
            {
                var row = new Row { RowIndex = rowNum };
                row.Append(CreateTextCell(CellReference(0, rowNum), spec.VariableName, DefaultStyleIndex));
                colWidths[0] = Math.Max(colWidths[0], spec.VariableName.Length + CellPadding);

                if (spec.LowerSpecificationLimit is { } lsl && double.IsFinite(lsl))
                {
                    row.Append(CreateNumericCell(CellReference(1, rowNum), lsl, NumericStyleIndex));
                }

                if (spec.UpperSpecificationLimit is { } usl && double.IsFinite(usl))
                {
                    row.Append(CreateNumericCell(CellReference(2, rowNum), usl, NumericStyleIndex));
                }

                sheetData.Append(row);
                rowNum++;
            }
        }

        var columns = new Columns();
        for (var col = 0; col < colWidths.Length; col++)
        {
            columns.Append(new Column
            {
                Min = (uint)(col + 1),
                Max = (uint)(col + 1),
                Width = Math.Clamp(colWidths[col], MinColumnWidth, MaxColumnWidth),
                CustomWidth = true
            });
        }

        return new Worksheet(columns, sheetData);
    }

    private static bool TryGetNumericValue(
        AnalysisResultRow row,
        AnalysisResultColumn column,
        int columnIndex,
        out double value)
    {
        if (row.RawNumericValues is { } rawValues)
        {
            if (rawValues[columnIndex] is { } raw && double.IsFinite(raw))
            {
                value = raw;
                return true;
            }

            value = 0;
            return false;
        }

        if (column.Alignment == AnalysisResultAlignment.Right &&
            double.TryParse(row.Cells[columnIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed))
        {
            value = parsed;
            return true;
        }

        value = 0;
        return false;
    }

    private static Cell CreateTextCell(string reference, string text, uint styleIndex) =>
        new()
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            StyleIndex = styleIndex,
            InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve })
        };

    private static Cell CreateNumericCell(string reference, double value, uint styleIndex) =>
        new()
        {
            CellReference = reference,
            DataType = CellValues.Number,
            StyleIndex = styleIndex,
            CellValue = new CellValue(value.ToString("G17", CultureInfo.InvariantCulture))
        };

    private static string CellReference(int zeroBasedColumnIndex, uint oneBasedRowIndex) =>
        $"{ColumnName(zeroBasedColumnIndex)}{oneBasedRowIndex}";

    private static string ColumnName(int zeroBasedIndex)
    {
        var dividend = zeroBasedIndex + 1;
        var name = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            name = (char)('A' + modulo) + name;
            dividend = (dividend - modulo) / 26;
        }

        return name;
    }

    private static Stylesheet CreateStylesheet()
    {
        var fonts = new Fonts(
            new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" }));

        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFE8EEF5" }) { PatternType = PatternValues.Solid }));

        var borders = new Borders(
            new Border(),
            new Border(new BottomBorder(new Color { Rgb = "FFB0BEC5" }) { Style = BorderStyleValues.Thin }));

        var cellFormats = new CellFormats(
            // 0: Default text
            new CellFormat { FontId = 0, FillId = 0, BorderId = 0 },
            // 1: Bold header
            new CellFormat { FontId = 1, FillId = 2, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true },
            // 2: Numeric right-aligned
            new CellFormat
            {
                FontId = 0,
                FillId = 0,
                BorderId = 0,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right },
                ApplyAlignment = true
            });

        return new Stylesheet(fonts, fills, borders, cellFormats);
    }
}
