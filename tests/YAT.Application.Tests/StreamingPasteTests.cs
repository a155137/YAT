using System.Globalization;
using System.Text;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Ingestion;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

public class StreamingPasteTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("A\tB\tC\n1\t2\t3\n4\t5\t6")]
    [InlineData("No\tBin\tSITE\r\n1\t1\t1\r\n2\t2\t2\r\n")]
    [InlineData("No\tBin\tSITE\r1\t1\t1\r2\t2\t2\r")]
    [InlineData("A\tB\n1\t2\n\t\n")]
    [InlineData("A\r\n1\r\n\r\n3\r\n")]
    [InlineData("A\tB\tC\n1\t\t3\n4\t5\t")]
    [InlineData(" No \tLot A\t \n 1 \t N123 \t\u00A0\n")]
    [InlineData("Empty\tNo\n\t1\n \t2\n")]
    [InlineData("No\tBin\tSITE")]
    [InlineData("No\tBin\tSITE\r\n")]
    [InlineData("Value\tValue\tvalue\n1\t2\t3\n")]
    [InlineData("Reg\n123\n-23.5\n.132\n1.57E-6\n-2.5e3\n5e-324\n1e-310\n-0.0\n")]
    [InlineData("Mixed\n1\nNaN\nInfinity\n-Infinity\n1E400\n1,234\n0x1F\n5%\n")]
    [InlineData("Text\n半導體_Vth_µm_Ω_🚀\na\0b\n")]
    public async Task StreamingPasteMatchesLegacyPasteSemantically(string text)
    {
        var worksheetId = Guid.NewGuid();
        var existing = new[]
        {
            new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = worksheetId, Index = 0, Name = "Existing", DataType = WorksheetDataType.Numeric },
            new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = worksheetId, Index = 1, Name = "SITE", DataType = WorksheetDataType.Numeric }
        };

        var legacyResult = await ExecuteLegacyAsync(worksheetId, existing, 1, text);
        var streamResult = await ExecuteStreamingAsync(worksheetId, existing, 1, text);

        Assert.Equal(legacyResult.Plan.StartColumnIndex, streamResult.Plan.StartColumnIndex);
        Assert.Equal(legacyResult.Plan.ColumnCount, streamResult.Plan.ColumnCount);
        Assert.Equal(legacyResult.Plan.DataRowCount, streamResult.Plan.DataRowCount);
        Assert.Equal(legacyResult.Plan.Columns, streamResult.Plan.Columns);

        Assert.Equal(legacyResult.Result.RowCount, streamResult.Result.RowCount);
        Assert.Equal(
            legacyResult.Result.Columns.Select(c => (c.Index, c.Name, c.DataType, c.IsNew)),
            streamResult.Result.Columns.Select(c => (c.Index, c.Name, c.DataType, c.IsNew)));

        Assert.Equal(legacyResult.Block.RowCount, streamResult.Block.RowCount);
        Assert.Equal(legacyResult.Block.Columns.Count, streamResult.Block.Columns.Count);
        for (var i = 0; i < legacyResult.Block.Columns.Count; i++)
        {
            var expected = legacyResult.Block.Columns[i];
            var actual = streamResult.Block.Columns[i];
            Assert.Equal(expected.DataType, actual.DataType);
            if (expected is NumericRawDataColumn expectedNum)
            {
                var actualNum = Assert.IsType<NumericRawDataColumn>(actual);
                Assert.Equal(expectedNum.Values, actualNum.Values);
            }
            else
            {
                var expectedStr = Assert.IsType<StringRawDataColumn>(expected);
                var actualStr = Assert.IsType<StringRawDataColumn>(actual);
                Assert.Equal(expectedStr.Values, actualStr.Values);
            }
        }
    }

    [Fact]
    public void StreamingPlannerMatchesLegacyUnderCommaDecimalCulture()
    {
        var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaCulture.NumberFormat.NumberDecimalSeparator = ",";
        commaCulture.NumberFormat.NumberGroupSeparator = ".";

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = commaCulture;
            var text = "A\tB\n1.25\t1,25\n";
            var planner = new WorksheetPastePlanner(new ColumnDataTypeDetector());
            var legacy = planner.Plan(Guid.NewGuid(), [], 0, new TabularTextParser().Parse(text));
            var streaming = planner.PlanFromText(Guid.NewGuid(), [], 0, text, Token);

            Assert.Equal(legacy.Columns, streaming.Columns);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData(" \t \n ")]
    [InlineData("A\tB\tC\n1\t2")]
    [InlineData("A\tB\n1\t2\t3")]
    [InlineData("A\tB\r\n1\t2\r\n\r\n3\t4\r\n")]
    [InlineData("A\tB\n1\t2\n3\t4\t5\t6")]
    public void StreamingPlannerThrowsIdenticalValidationExceptionsAsLegacy(string? text)
    {
        var parser = new TabularTextParser();
        var planner = new WorksheetPastePlanner(new ColumnDataTypeDetector());

        var legacyEx = Assert.Throws<ValidationException>(() => parser.Parse(text!));
        var streamEx = Assert.Throws<ValidationException>(() => planner.PlanFromText(Guid.NewGuid(), [], 0, text!, Token));

        Assert.Equal(legacyEx.Message, streamEx.Message);
    }

    [Theory]
    [InlineData("No\tBin\n1\t1\n", "No\tBin\tSITE\n1\t1\t1\n")]
    [InlineData("No\tBin\n1\t1\n", "No\n1\n")]
    [InlineData("No\tBin\n1\t1\n", "No\tSITE\n1\t1\n")]
    [InlineData("No\tBin\n1\t1\n", "No\tBin\n1\t1\n2\t2\n")]
    [InlineData("No\tBin\n1\t1\n2\t2\n", "No\tBin\n1\t1\n")]
    [InlineData("No\tReg1\n1\t5\n2\t7\n", "No\tReg1\n1\t5\n2\tabc\n")]
    public async Task MismatchedPlanAndTextFailsWithoutPersisting(string plannedText, string executedText)
    {
        var worksheet = new Worksheet { Id = Guid.NewGuid(), Name = "WAT" };
        var worksheets = new FakeWorksheetRepository();
        worksheets.Seed(worksheet);
        var columns = new FakeWorksheetColumnRepository();
        var rawStore = new FakeWorksheetRawDataStore();

        var planner = new WorksheetPastePlanner(new ColumnDataTypeDetector());
        var plan = planner.PlanFromText(worksheet.Id, [], 0, plannedText, Token);
        var service = new PasteExecutionService(worksheets, columns, rawStore);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteFromTextAsync(plan, executedText, Token));
        Assert.Empty(rawStore.Writes);
        Assert.Empty(columns.Added);
        Assert.Empty(columns.Updated);
    }

    [Fact]
    public async Task MidStreamCancellationInPass1AndPass2LeavesNothingPersisted()
    {
        var sb = new StringBuilder("A\tB\n");
        for (var i = 0; i < 20_000; i++)
        {
            sb.Append(i).Append('\t').Append(i * 0.5).Append('\n');
        }

        var largeText = sb.ToString();
        var planner = new WorksheetPastePlanner(new ColumnDataTypeDetector());

        using (var pass1Cts = new CancellationTokenSource())
        {
            await pass1Cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Task.Run(() => planner.PlanFromText(Guid.NewGuid(), [], 0, largeText, pass1Cts.Token), Token));
        }

        var worksheet = new Worksheet { Id = Guid.NewGuid(), Name = "WAT" };
        var worksheets = new FakeWorksheetRepository();
        worksheets.Seed(worksheet);
        var columns = new FakeWorksheetColumnRepository();
        var rawStore = new FakeWorksheetRawDataStore();
        var plan = planner.PlanFromText(worksheet.Id, [], 0, largeText, Token);
        var service = new PasteExecutionService(worksheets, columns, rawStore);

        using var pass2Cts = new CancellationTokenSource();
        rawStore.OnWrite = () => pass2Cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteFromTextAsync(plan, largeText, pass2Cts.Token));
        Assert.Empty(rawStore.Writes);
        Assert.Empty(columns.Added);
    }

    [Fact]
    public async Task StreamingMetadataFailurePreservesDocumentedPartialSemantics()
    {
        var worksheet = new Worksheet { Id = Guid.NewGuid(), Name = "WAT" };
        var worksheets = new FakeWorksheetRepository();
        worksheets.Seed(worksheet);
        var columns = new FakeWorksheetColumnRepository();
        var existing = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = worksheet.Id, Index = 0, Name = "No", DataType = WorksheetDataType.Numeric };
        columns.Seed(existing);
        var failure = new InvalidOperationException("Simulated metadata failure.");
        columns.FailingWrite = (2, failure);
        var rawStore = new FakeWorksheetRawDataStore();

        var plan = new WorksheetPastePlanner(new ColumnDataTypeDetector()).PlanFromText(worksheet.Id, [existing], 0, "Lot\tReg1\nN1\t5\n", Token);
        var service = new PasteExecutionService(worksheets, columns, rawStore);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteFromTextAsync(plan, "Lot\tReg1\nN1\t5\n", Token));
        Assert.Same(failure, thrown);
        Assert.Single(rawStore.Writes);
        Assert.Single(columns.Updated);
        Assert.Empty(columns.Added);
    }

    private static async Task<(WorksheetPastePlan Plan, PasteExecutionResult Result, RawDataBlock Block)> ExecuteLegacyAsync(
        Guid worksheetId,
        IReadOnlyList<WorksheetColumn> existing,
        int startColumnIndex,
        string text)
    {
        var worksheets = new FakeWorksheetRepository();
        worksheets.Seed(new Worksheet { Id = worksheetId, Name = "WAT" });
        var columns = new FakeWorksheetColumnRepository();
        foreach (var col in existing)
        {
            columns.Seed(col);
        }

        var rawStore = new FakeWorksheetRawDataStore();
        var data = new TabularTextParser().Parse(text);
        var plan = new WorksheetPastePlanner(new ColumnDataTypeDetector()).Plan(worksheetId, existing, startColumnIndex, data);
        var result = await new PasteExecutionService(worksheets, columns, rawStore).ExecuteAsync(plan, data, Token);
        return (plan, result, Assert.Single(rawStore.Writes).Block);
    }

    private static async Task<(WorksheetPastePlan Plan, PasteExecutionResult Result, RawDataBlock Block)> ExecuteStreamingAsync(
        Guid worksheetId,
        IReadOnlyList<WorksheetColumn> existing,
        int startColumnIndex,
        string text)
    {
        var worksheets = new FakeWorksheetRepository();
        worksheets.Seed(new Worksheet { Id = worksheetId, Name = "WAT" });
        var columns = new FakeWorksheetColumnRepository();
        foreach (var col in existing)
        {
            columns.Seed(col);
        }

        var rawStore = new FakeWorksheetRawDataStore();
        var plan = new WorksheetPastePlanner(new ColumnDataTypeDetector()).PlanFromText(worksheetId, existing, startColumnIndex, text, Token);
        var result = await new PasteExecutionService(worksheets, columns, rawStore).ExecuteFromTextAsync(plan, text, Token);
        return (plan, result, Assert.Single(rawStore.Writes).Block);
    }
}
