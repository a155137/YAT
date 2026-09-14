using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Queries;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

public class WorksheetColumnsTsvExporterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Fixture
    {
        public Fixture()
        {
            Worksheets.Seed(Worksheet);
        }

        public Worksheet Worksheet { get; } = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

        public FakeWorksheetRepository Worksheets { get; } = new();

        public FakeWorksheetColumnRepository Columns { get; } = new();

        public FakeWorksheetRawDataStore RawStore { get; } = new();

        public WorksheetColumnsTsvExporter Exporter => new(Worksheets, Columns, RawStore);

        public WorksheetColumn Numeric(int index, string name, params double?[] values)
        {
            var column = Column(index, name, WorksheetDataType.Numeric);
            RawStore.Seed(Worksheet.Id, new NumericRawDataColumn(column.Id, values));
            return column;
        }

        public WorksheetColumn Text(int index, string name, params string?[] values)
        {
            var column = Column(index, name, WorksheetDataType.String);
            RawStore.Seed(Worksheet.Id, new StringRawDataColumn(column.Id, values));
            return column;
        }

        public WorksheetColumn Column(int index, string name, WorksheetDataType dataType = WorksheetDataType.Numeric)
        {
            var column = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = Worksheet.Id, Index = index, Name = name, DataType = dataType };
            Columns.Seed(column);
            return column;
        }

        public Task<string> ExportAsync(params WorksheetColumn[] columns) =>
            Exporter.ExportAsync(Worksheet.Id, columns.Select(column => column.Id).ToArray(), Token);
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + "\r\n"));

    // 1
    [Fact]
    public async Task OneColumnCopiesItsHeaderAndValues()
    {
        var fixture = new Fixture();
        var site = fixture.Numeric(0, "SITE", 1, 2, 3);

        var text = await fixture.ExportAsync(site);

        Assert.Equal(Lines("SITE", "1", "2", "3"), text);
    }

    // 2
    [Fact]
    public async Task ColumnsAreWrittenInWorksheetIndexOrderWhateverTheRequestOrder()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric(2, "Reg1", 5, 7);
        var no = fixture.Numeric(0, "No", 1, 2);
        var bin = fixture.Numeric(1, "Bin", 1, 2);

        var text = await fixture.ExportAsync(reg1, no, bin);

        Assert.Equal(Lines("No\tBin\tReg1", "1\t1\t5", "2\t2\t7"), text);
    }

    // 3
    [Fact]
    public async Task NonContiguousColumnsKeepTheirIndexOrderWithoutTheColumnsInBetween()
    {
        var fixture = new Fixture();
        var a = fixture.Numeric(0, "A", 1);
        fixture.Numeric(1, "B", 2);
        var c = fixture.Numeric(2, "C", 3);
        fixture.Numeric(3, "D", 4);
        var e = fixture.Numeric(4, "E", 5);

        var text = await fixture.ExportAsync(e, a, c);

        Assert.Equal(Lines("A\tC\tE", "1\t3\t5"), text);
    }

    // 4
    [Fact]
    public async Task NullValuesBecomeEmptyFields()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric(0, "Reg1", 5, null, 7);
        var lot = fixture.Text(1, "Lot", null, "N2", null);

        var text = await fixture.ExportAsync(reg1, lot);

        Assert.Equal(Lines("Reg1\tLot", "5\t", "\tN2", "7\t"), text);
    }

    // 5
    [Fact]
    public async Task NumbersUseInvariantShortestFormAndTextIsWrittenVerbatim()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric(0, "Reg", 0.132, 1.57E-6, -2500, 123456789.25);
        var lot = fixture.Text(1, "Lot", " N 1 ", "5\" wafer", "a,b", "Lot;7");

        var text = await fixture.ExportAsync(reg, lot);

        Assert.Equal(
            Lines("Reg\tLot", "0.132\t N 1 ", "1.57E-06\t5\" wafer", "-2500\ta,b", "123456789.25\tLot;7"),
            text);
    }

    [Fact]
    public async Task FieldsThatSpreadsheetsWouldReadAsQuotedAreQuoted()
    {
        var fixture = new Fixture();
        var lot = fixture.Text(0, "\"Lot\"", "\"N1\"", "plain", "tab\there", "line\nbreak");

        var text = await fixture.ExportAsync(lot);

        Assert.Equal(Lines("\"\"\"Lot\"\"\"", "\"\"\"N1\"\"\"", "plain", "\"tab\there\"", "\"line\nbreak\""), text);
    }

    [Fact]
    public async Task RowCountIsTheLongestSelectedColumnAndShorterOnesArePaddedWithEmptyFields()
    {
        var fixture = new Fixture();
        var a = fixture.Numeric(0, "A", 1, 2, 3);
        var b = fixture.Numeric(1, "B", 9);
        fixture.Numeric(2, "Long", 1, 2, 3, 4, 5, 6);

        var text = await fixture.ExportAsync(a, b);

        Assert.Equal(Lines("A\tB", "1\t9", "2\t", "3\t"), text);
    }

    [Fact]
    public async Task ColumnsWithoutStoredValuesProduceEmptyFields()
    {
        var fixture = new Fixture();
        var no = fixture.Numeric(0, "No", 1, 2);
        var vth = fixture.Column(1, "Vth");

        Assert.Equal(Lines("No\tVth", "1\t", "2\t"), await fixture.ExportAsync(no, vth));
        Assert.Equal(Lines("Vth"), await fixture.ExportAsync(vth));
    }

    [Fact]
    public async Task EmptyColumnsCopyOnlyTheHeaderRow()
    {
        var fixture = new Fixture();
        var empty = fixture.Numeric(0, "Empty");

        Assert.Equal(Lines("Empty"), await fixture.ExportAsync(empty));
    }

    [Fact]
    public async Task LargeColumnsAreReadInBoundedChunks()
    {
        var fixture = new Fixture();
        var rowCount = WorksheetColumnsTsvExporter.ReadChunkRowCount * 2 + 123;
        var reg = fixture.Numeric(0, "Reg", Enumerable.Range(0, rowCount).Select(value => (double?)value).ToArray());

        var text = await fixture.ExportAsync(reg);

        var lines = text.Split("\r\n");
        Assert.Equal(rowCount + 2, lines.Length);
        Assert.Equal("Reg", lines[0]);
        Assert.Equal("0", lines[1]);
        Assert.Equal((rowCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), lines[^2]);
        Assert.Equal(string.Empty, lines[^1]);

        Assert.Equal([0L, WorksheetColumnsTsvExporter.ReadChunkRowCount, WorksheetColumnsTsvExporter.ReadChunkRowCount * 2L],
            fixture.RawStore.Reads.Select(read => read.RowOffset));
        Assert.All(fixture.RawStore.Reads, read => Assert.Equal(WorksheetColumnsTsvExporter.ReadChunkRowCount, read.RowCount));
    }

    [Fact]
    public async Task MoreRowsThanASpreadsheetCanPasteAreRejected()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric(0, "Reg", new double?[WorksheetColumnsTsvExporter.MaxDataRowCount + 1]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => fixture.ExportAsync(reg));

        Assert.Contains("1,048,575", exception.Message);
    }

    [Fact]
    public async Task InvalidRequestsAreRejected()
    {
        var fixture = new Fixture();
        var no = fixture.Numeric(0, "No", 1);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Exporter.ExportAsync(fixture.Worksheet.Id, [], Token));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Exporter.ExportAsync(fixture.Worksheet.Id, [no.Id, no.Id], Token));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.Exporter.ExportAsync(fixture.Worksheet.Id, [no.Id, Guid.NewGuid()], Token));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.Exporter.ExportAsync(Guid.NewGuid(), [no.Id], Token));
        Assert.Empty(fixture.RawStore.Reads);
    }
}
