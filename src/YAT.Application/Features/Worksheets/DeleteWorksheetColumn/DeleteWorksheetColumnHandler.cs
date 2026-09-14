using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Features.Worksheets.DeleteWorksheetColumn;

// Deletes one worksheet column as a whole: its raw values, then its metadata, then the remaining columns are
// reindexed contiguously (0..N-1) in their existing order. Remaining columns keep their Id and all other properties.
//
// Known limitation: column metadata and raw data are not persisted in one transaction. Raw storage is retired
// first, so a raw failure leaves the metadata untouched; a metadata failure after that point surfaces as an
// exception with the raw values already retired. Retrying the delete then completes the metadata part.
public sealed class DeleteWorksheetColumnHandler
{
    private readonly IWorksheetRepository _worksheets;
    private readonly IWorksheetColumnRepository _columns;
    private readonly IWorksheetRawDataStore _rawDataStore;

    public DeleteWorksheetColumnHandler(
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
        DeleteWorksheetColumnCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Validate everything before any change.
        if (await _worksheets.GetByIdAsync(command.WorksheetId, cancellationToken) is null)
        {
            throw new EntityNotFoundException(nameof(Worksheet), command.WorksheetId);
        }

        var columns = await _columns.GetByWorksheetIdAsync(command.WorksheetId, cancellationToken);
        if (columns.All(column => column.Id != command.ColumnId))
        {
            throw new EntityNotFoundException(nameof(WorksheetColumn), command.ColumnId);
        }

        // A column that exists only as metadata has no raw storage to retire.
        var storedColumnIds = await _rawDataStore.GetStoredColumnIdsAsync(command.WorksheetId, cancellationToken);

        // 2. Raw storage first: if this fails (or is cancelled), nothing has changed.
        if (storedColumnIds.Contains(command.ColumnId))
        {
            await _rawDataStore.DeleteColumnsAsync(command.WorksheetId, [command.ColumnId], cancellationToken);
        }

        // 3–4. Metadata. Raw values are already gone, so these steps are not cancellable.
        await _columns.DeleteAsync(command.ColumnId, CancellationToken.None);

        var remaining = columns.Where(column => column.Id != command.ColumnId).ToArray();
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
