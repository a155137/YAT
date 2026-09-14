namespace YAT.Application.Abstractions.Persistence;

// Raw measurement values of worksheets, keyed by WorksheetColumn.Id. Column metadata is owned by
// IWorksheetColumnRepository. Implementations keep the data out of the UI and the Domain.
public interface IWorksheetRawDataStore
{
    // Whole-column replacement: once this completes, each column in the block contains exactly the
    // block's values. Rows previously stored beyond the block's row count no longer belong to that column.
    // The block is a transfer unit; storing datasets too large for one block is left to a future contract.
    Task WriteColumnsAsync(Guid worksheetId, RawDataBlock block, CancellationToken cancellationToken);

    // Bounded row window starting at rowOffset, returning at most rowCount rows for the requested columns.
    // Reading columns whose stored row counts differ is not yet defined; that belongs to the storage implementation task.
    Task<RawDataBlock> ReadColumnsAsync(
        Guid worksheetId,
        IReadOnlyList<Guid> columnIds,
        long rowOffset,
        int rowCount,
        CancellationToken cancellationToken);

    // Logical row count of the worksheet: the length of its longest live raw column; 0 when nothing is stored.
    Task<long> GetWorksheetRowCountAsync(Guid worksheetId, CancellationToken cancellationToken);

    // Ids of the worksheet's columns that have stored raw values. Column metadata without raw values is not included.
    Task<IReadOnlySet<Guid>> GetStoredColumnIdsAsync(Guid worksheetId, CancellationToken cancellationToken);

    // Retires the given live raw columns of the worksheet as a whole: afterwards they cannot be read, and the
    // worksheet row count reflects only the remaining columns. Values of other columns are unchanged.
    // All ids must be distinct live columns of this worksheet (EntityNotFoundException otherwise); nothing is
    // retired unless all of them are.
    Task DeleteColumnsAsync(Guid worksheetId, IReadOnlyList<Guid> columnIds, CancellationToken cancellationToken);
}
