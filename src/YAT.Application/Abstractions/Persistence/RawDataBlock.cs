namespace YAT.Application.Abstractions.Persistence;

// A rectangular set of raw columns: at least one column, distinct column ids, equal row counts.
// It is an in-memory transfer/write unit for one operation (today: a paste). It is not an invariant that a
// worksheet's whole dataset, or an arbitrarily large stored column, must always fit in a single block.
public sealed class RawDataBlock
{
    public RawDataBlock(IReadOnlyList<RawDataColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var copy = columns.ToArray();
        if (copy.Length == 0)
        {
            throw new ArgumentException("A raw data block must contain at least one column.", nameof(columns));
        }

        if (copy.Any(column => column is null))
        {
            throw new ArgumentException("A raw data block must not contain null columns.", nameof(columns));
        }

        if (copy.Select(column => column.ColumnId).Distinct().Count() != copy.Length)
        {
            throw new ArgumentException("A raw data block must not contain the same column id twice.", nameof(columns));
        }

        var rowCount = copy[0].RowCount;
        if (copy.Any(column => column.RowCount != rowCount))
        {
            throw new ArgumentException("All columns in a raw data block must have the same row count.", nameof(columns));
        }

        Columns = Array.AsReadOnly(copy);
        RowCount = rowCount;
    }

    public IReadOnlyList<RawDataColumn> Columns { get; }

    public int RowCount { get; }
}
