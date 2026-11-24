using YAT.Domain.Enums;

namespace YAT.Application.Abstractions.Persistence;

// Identity and data type of one raw column of a streaming write (IWorksheetRawDataStore.WriteColumnsAsync with an
// IRawDataRowWriter). Carries no values. Only the data types raw storage supports are accepted.
public sealed class RawColumnSchema
{
    public RawColumnSchema(Guid columnId, WorksheetDataType dataType)
    {
        if (dataType is not (WorksheetDataType.Numeric or WorksheetDataType.String))
        {
            throw new ArgumentException($"Raw data type '{dataType}' is not supported.", nameof(dataType));
        }

        ColumnId = columnId;
        DataType = dataType;
    }

    public Guid ColumnId { get; }

    public WorksheetDataType DataType { get; }
}
