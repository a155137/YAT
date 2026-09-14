using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Features.Worksheets.DeleteWorksheetColumns;

// Deletes one or more worksheet columns as a whole, in a single batch: all columns are validated first, their raw
// values are retired with one raw store call, their metadata is deleted, and the surviving columns are reindexed
// contiguously (0..N-1) once, in their existing order. Survivors keep their Id and all other properties.
//
// Known limitation: column metadata and raw data are not persisted in one transaction. Raw storage is retired
// first (all or nothing), so a raw failure leaves the metadata untouched; a metadata failure after that point
// surfaces as an exception with the raw values already retired. Deleting the columns that still exist then
// completes the metadata part.
public sealed class DeleteWorksheetColumnsHandler
{
    private readonly IWorksheetRepository _worksheets;
    private readonly IWorksheetColumnRepository _columns;
    private readonly IWorksheetRawDataStore _rawDataStore;

    public DeleteWorksheetColumnsHandler(
        IWorksheetRepository worksheets,
        IWorksheetColumnRepository columns,
        IWorksheetRawDataStore rawDataStore)
    {
        _worksheets = worksheets;
        _columns = columns;
        _rawDataStore = rawDataStore;
    }

    // Returns the worksheet's remaining column metadata ordered by Index.
    public async Task<IReadOnlyList<WorksheetColumn>> HandleAsync(
        DeleteWorksheetColumnsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.ColumnIds);
        if (command.ColumnIds.Count == 0)
        {
            throw new ArgumentException("At least one column id is required.", nameof(command));
        }

        if (command.ColumnIds.Distinct().Count() != command.ColumnIds.Count)
        {
            throw new ArgumentException("Column ids must not contain duplicates.", nameof(command));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Validate everything before any change.
        if (await _worksheets.GetByIdAsync(command.WorksheetId, cancellationToken) is null)
        {
            throw new EntityNotFoundException(nameof(Worksheet), command.WorksheetId);
        }

        var columns = await _columns.GetByWorksheetIdAsync(command.WorksheetId, cancellationToken);
        var worksheetColumnIds = columns.Select(column => column.Id).ToHashSet();
        foreach (var columnId in command.ColumnIds)
        {
            if (!worksheetColumnIds.Contains(columnId))
            {
                throw new EntityNotFoundException(nameof(WorksheetColumn), columnId);
            }
        }

        // Columns that exist only as metadata have no raw storage to retire.
        var storedColumnIds = await _rawDataStore.GetStoredColumnIdsAsync(command.WorksheetId, cancellationToken);
        var rawColumnIds = command.ColumnIds.Where(storedColumnIds.Contains).ToArray();

        // 2. Raw storage first, in one call: if this fails (or is cancelled), nothing has changed.
        if (rawColumnIds.Length > 0)
        {
            await _rawDataStore.DeleteColumnsAsync(command.WorksheetId, rawColumnIds, cancellationToken);
        }

        // 3. Metadata. Raw values are already gone, so these steps are not cancellable.
        foreach (var columnId in command.ColumnIds)
        {
            await _columns.DeleteAsync(columnId, CancellationToken.None);
        }

        // 4. One reindex pass over the survivors.
        var deleted = command.ColumnIds.ToHashSet();
        var remaining = columns.Where(column => !deleted.Contains(column.Id)).ToArray();
        var reindexed = new WorksheetColumn[remaining.Length];
        for (var position = 0; position < remaining.Length; position++)
        {
            var column = remaining[position];
            if (column.Index == position)
            {
                reindexed[position] = column;
                continue;
            }

            // A new instance: repositories may hand out their stored objects, which are never mutated in place.
            reindexed[position] = new WorksheetColumn
            {
                Id = column.Id,
                WorksheetId = column.WorksheetId,
                Index = position,
                Name = column.Name,
                DataType = column.DataType,
                SemanticType = column.SemanticType,
                Unit = column.Unit
            };
            await _columns.UpdateAsync(reindexed[position], CancellationToken.None);
        }

        return Array.AsReadOnly(reindexed);
    }
}
