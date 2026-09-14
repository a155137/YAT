using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Features.Worksheets.DeleteWorksheetColumns;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

public class DeleteWorksheetColumnsHandlerTests
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

        public DeleteWorksheetColumnsHandler Handler => new(Worksheets, Columns, RawStore);

        public WorksheetColumn Column(int index, string name, bool stored = true)
        {
            var column = new WorksheetColumn
            {
                Id = Guid.NewGuid(),
                WorksheetId = Worksheet.Id,
                Index = index,
                Name = name,
                DataType = WorksheetDataType.Numeric,
                SemanticType = ColumnSemanticType.TestParameter,
                Unit = "V"
            };
            Columns.Seed(column);
            if (stored)
            {
                RawStore.Seed(Worksheet.Id, new NumericRawDataColumn(column.Id, [index]));
            }

            return column;
        }

        public Task<IReadOnlyList<WorksheetColumn>> DeleteAsync(params Guid[] columnIds) =>
            Handler.HandleAsync(new DeleteWorksheetColumnsCommand(Worksheet.Id, columnIds), Token);

        public Task<IReadOnlyList<WorksheetColumn>> StoredAsync() => Columns.GetByWorksheetIdAsync(Worksheet.Id, Token);

        public void AssertNothingChanged()
        {
            Assert.Empty(RawStore.Deletes);
            Assert.Empty(Columns.Deleted);
            Assert.Empty(Columns.Updated);
        }
    }

    // 1
    [Fact]
    public async Task DeletesTheColumnsRawValuesAndMetadata()
    {
        var fixture = new Fixture();
        var no = fixture.Column(0, "No");
        var site = fixture.Column(1, "SITE");

        await fixture.DeleteAsync(site.Id);

        var delete = Assert.Single(fixture.RawStore.Deletes);
        Assert.Equal(fixture.Worksheet.Id, delete.WorksheetId);
        Assert.Equal([site.Id], delete.ColumnIds);
        Assert.Equal([site.Id], fixture.Columns.Deleted);
        Assert.Equal([no.Id], (await fixture.StoredAsync()).Select(column => column.Id));
        Assert.DoesNotContain(site.Id, await fixture.RawStore.GetStoredColumnIdsAsync(fixture.Worksheet.Id, Token));
    }

    // 2
    [Fact]
    public async Task UnknownColumnFailsBeforeAnyChange()
    {
        var fixture = new Fixture();
        fixture.Column(0, "No");

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.DeleteAsync(Guid.NewGuid()));

        Assert.Equal(nameof(WorksheetColumn), exception.EntityName);
        fixture.AssertNothingChanged();
    }

    [Fact]
    public async Task ColumnOfAnotherWorksheetIsNotFound()
    {
        var fixture = new Fixture();
        fixture.Column(0, "No");
        var foreign = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = Guid.NewGuid(), Index = 0, Name = "No" };
        fixture.Columns.Seed(foreign);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.DeleteAsync(foreign.Id));

        fixture.AssertNothingChanged();
    }

    [Fact]
    public async Task UnknownWorksheetFailsBeforeAnyChange()
    {
        var fixture = new Fixture();
        var no = fixture.Column(0, "No");

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => fixture.Handler.HandleAsync(new DeleteWorksheetColumnsCommand(Guid.NewGuid(), [no.Id]), Token));

        Assert.Equal(nameof(Worksheet), exception.EntityName);
        fixture.AssertNothingChanged();
    }

    // 3, 4
    [Fact]
    public async Task RemainingColumnsAreReindexedContiguouslyKeepingIdsAndProperties()
    {
        var fixture = new Fixture();
        var a = fixture.Column(0, "A");
        var b = fixture.Column(1, "B");
        var c = fixture.Column(2, "C");
        var d = fixture.Column(3, "D");

        var remaining = await fixture.DeleteAsync(b.Id);

        var stored = await fixture.StoredAsync();
        Assert.Equal([a.Id, c.Id, d.Id], stored.Select(column => column.Id));
        Assert.Equal([0, 1, 2], stored.Select(column => column.Index));
        Assert.Equal(["A", "C", "D"], stored.Select(column => column.Name));
        Assert.All(stored, column =>
        {
            Assert.Equal(fixture.Worksheet.Id, column.WorksheetId);
            Assert.Equal(WorksheetDataType.Numeric, column.DataType);
            Assert.Equal(ColumnSemanticType.TestParameter, column.SemanticType);
            Assert.Equal("V", column.Unit);
        });
        Assert.Equal(stored.Select(column => column.Id), remaining.Select(column => column.Id));
        Assert.Equal([0, 1, 2], remaining.Select(column => column.Index));

        // Only columns whose position changed are updated, as new instances; loaded objects are never mutated.
        Assert.Equal([c.Id, d.Id], fixture.Columns.Updated.Select(column => column.Id));
        Assert.Same(a, stored[0]);
        Assert.Equal(2, c.Index);
        Assert.Equal(3, d.Index);
    }

    [Fact]
    public async Task ExistingIndexGapsAreClosedToo()
    {
        var fixture = new Fixture();
        var a = fixture.Column(0, "A");
        fixture.Column(2, "B");
        fixture.Column(5, "C");

        await fixture.DeleteAsync(a.Id);

        Assert.Equal([0, 1], (await fixture.StoredAsync()).Select(column => column.Index));
    }

    [Fact]
    public async Task DeletingTheLastColumnLeavesAnEmptyWorksheet()
    {
        var fixture = new Fixture();
        var only = fixture.Column(0, "No");

        var remaining = await fixture.DeleteAsync(only.Id);

        Assert.Empty(remaining);
        Assert.Empty(await fixture.StoredAsync());
        Assert.Equal(0, await fixture.RawStore.GetWorksheetRowCountAsync(fixture.Worksheet.Id, Token));
    }

    [Fact]
    public async Task MetadataOnlyColumnIsDeletedWithoutARawDelete()
    {
        var fixture = new Fixture();
        fixture.Column(0, "No");
        var vth = fixture.Column(1, "Vth", stored: false);

        await fixture.DeleteAsync(vth.Id);

        Assert.Empty(fixture.RawStore.Deletes);
        Assert.Equal([vth.Id], fixture.Columns.Deleted);
    }

    [Fact]
    public async Task RawStorageIsRetiredBeforeMetadataChanges()
    {
        var fixture = new Fixture();
        fixture.Column(0, "A");
        var b = fixture.Column(1, "B");
        fixture.Column(2, "C");
        var metadataChangesAtRawDelete = -1;
        fixture.RawStore.OnDelete = () => metadataChangesAtRawDelete = fixture.Columns.Deleted.Count + fixture.Columns.Updated.Count;

        await fixture.DeleteAsync(b.Id);

        Assert.Equal(0, metadataChangesAtRawDelete);
    }

    [Fact]
    public async Task RawStorageFailureLeavesMetadataUntouched()
    {
        var fixture = new Fixture();
        fixture.Column(0, "A");
        var b = fixture.Column(1, "B");
        fixture.Column(2, "C");
        var failure = new RawDataStorageException("Simulated storage failure.");
        fixture.RawStore.DeleteFailure = failure;

        var thrown = await Assert.ThrowsAsync<RawDataStorageException>(() => fixture.DeleteAsync(b.Id));

        Assert.Same(failure, thrown);
        Assert.Empty(fixture.Columns.Deleted);
        Assert.Empty(fixture.Columns.Updated);
        Assert.Equal(["A", "B", "C"], (await fixture.StoredAsync()).Select(column => column.Name));
    }

    // Known limitation: metadata and raw data do not share a transaction.
    [Fact]
    public async Task MetadataFailureAfterRawDeletionSurfacesAndARetryCompletesTheDelete()
    {
        var fixture = new Fixture();
        fixture.Column(0, "A");
        var b = fixture.Column(1, "B");
        var c = fixture.Column(2, "C");
        var failure = new InvalidOperationException("Simulated metadata failure.");
        fixture.Columns.FailingWrite = (1, failure);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DeleteAsync(b.Id));

        Assert.Same(failure, thrown);
        Assert.Single(fixture.RawStore.Deletes);
        Assert.Equal(["A", "B", "C"], (await fixture.StoredAsync()).Select(column => column.Name));

        fixture.Columns.FailingWrite = null;
        await fixture.DeleteAsync(b.Id);

        Assert.Single(fixture.RawStore.Deletes);
        Assert.Equal([0, 1], (await fixture.StoredAsync()).Select(column => column.Index));
        Assert.Equal(c.Id, (await fixture.StoredAsync())[1].Id);
    }

    [Fact]
    public async Task CancellationBeforeDeletionChangesNothing()
    {
        var fixture = new Fixture();
        var no = fixture.Column(0, "No");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Handler.HandleAsync(new DeleteWorksheetColumnsCommand(fixture.Worksheet.Id, [no.Id]), cancellation.Token));

        fixture.AssertNothingChanged();
    }

    // Batch (Task #019): 6, 7, 8
    [Fact]
    public async Task BatchDeletesSeveralColumnsWithOneRawCallAndOneReindexPass()
    {
        var fixture = new Fixture();
        var a = fixture.Column(0, "A");
        var b = fixture.Column(1, "B");
        var c = fixture.Column(2, "C");
        var d = fixture.Column(3, "D");
        var e = fixture.Column(4, "E");

        var remaining = await fixture.DeleteAsync(d.Id, b.Id);

        var rawDelete = Assert.Single(fixture.RawStore.Deletes);
        Assert.Equal([d.Id, b.Id], rawDelete.ColumnIds);
        Assert.Equal([d.Id, b.Id], fixture.Columns.Deleted);

        var stored = await fixture.StoredAsync();
        Assert.Equal([a.Id, c.Id, e.Id], stored.Select(column => column.Id));
        Assert.Equal([0, 1, 2], stored.Select(column => column.Index));
        Assert.Equal(stored.Select(column => column.Id), remaining.Select(column => column.Id));

        // Each survivor whose position changed is updated exactly once.
        Assert.Equal([c.Id, e.Id], fixture.Columns.Updated.Select(column => column.Id));
    }

    [Fact]
    public async Task BatchRetiresOnlyStoredColumnsInItsSingleRawCall()
    {
        var fixture = new Fixture();
        var no = fixture.Column(0, "No");
        var vth = fixture.Column(1, "Vth", stored: false);
        var site = fixture.Column(2, "SITE");

        await fixture.DeleteAsync(no.Id, vth.Id, site.Id);

        Assert.Equal([no.Id, site.Id], Assert.Single(fixture.RawStore.Deletes).ColumnIds);
        Assert.Equal([no.Id, vth.Id, site.Id], fixture.Columns.Deleted);
    }

    [Fact]
    public async Task BatchOfOnlyMetadataColumnsMakesNoRawCall()
    {
        var fixture = new Fixture();
        var vth = fixture.Column(0, "Vth", stored: false);
        var idsat = fixture.Column(1, "Idsat", stored: false);

        await fixture.DeleteAsync(vth.Id, idsat.Id);

        Assert.Empty(fixture.RawStore.Deletes);
        Assert.Empty(await fixture.StoredAsync());
    }

    [Fact]
    public async Task BatchValidatesEveryColumnBeforeAnyChange()
    {
        var fixture = new Fixture();
        var a = fixture.Column(0, "A");
        fixture.Column(1, "B");

        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.DeleteAsync(a.Id, Guid.NewGuid()));

        fixture.AssertNothingChanged();
        Assert.Equal(2, (await fixture.StoredAsync()).Count);
    }

    [Fact]
    public async Task BatchRejectsEmptyAndDuplicateIds()
    {
        var fixture = new Fixture();
        var a = fixture.Column(0, "A");

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.DeleteAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.DeleteAsync(a.Id, a.Id));

        fixture.AssertNothingChanged();
    }

    // 9
    [Fact]
    public async Task BatchDeletingAllColumnsLeavesZeroColumnsAndZeroRows()
    {
        var fixture = new Fixture();
        var a = fixture.Column(0, "A");
        var b = fixture.Column(1, "B");
        var c = fixture.Column(2, "C", stored: false);

        var remaining = await fixture.DeleteAsync(a.Id, b.Id, c.Id);

        Assert.Empty(remaining);
        Assert.Empty(await fixture.StoredAsync());
        Assert.Empty(fixture.Columns.Updated);
        Assert.Equal(0, await fixture.RawStore.GetWorksheetRowCountAsync(fixture.Worksheet.Id, Token));
    }

    [Fact]
    public async Task BatchRawFailureLeavesAllMetadataUntouched()
    {
        var fixture = new Fixture();
        var a = fixture.Column(0, "A");
        var b = fixture.Column(1, "B");
        fixture.Column(2, "C");
        fixture.RawStore.DeleteFailure = new RawDataStorageException("Simulated storage failure.");

        await Assert.ThrowsAsync<RawDataStorageException>(() => fixture.DeleteAsync(a.Id, b.Id));

        Assert.Empty(fixture.Columns.Deleted);
        Assert.Empty(fixture.Columns.Updated);
        Assert.Equal(["A", "B", "C"], (await fixture.StoredAsync()).Select(column => column.Name));
    }
}
