using System.Text;
using DuckDB.NET.Data;
using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Enums;

namespace YAT.Infrastructure.Persistence.DuckDb;

// The IRawDataRowWriter of a streaming raw write: appends each row (row_index, then one value per schema column) to a
// block table through a DuckDB appender, so no per-column value arrays exist. String values are encoded as UTF-8 into
// one reusable scratch buffer with Encoding.UTF8 - the same encoder (and the same U+FFFD replacement of unpaired
// surrogates) the string overload of the appender uses - and appended as bytes, so no string is allocated per cell.
//
// Protocol violations throw InvalidOperationException; a non-finite number throws ArgumentException. Cancellation is
// checked every CancellationCheckInterval written values (row starts included), so a single very wide row is still
// checked at bounded intervals. Detach ends the writer's lifetime: any later call throws ObjectDisposedException.
internal sealed class DuckDbRawRowWriter : IRawDataRowWriter
{
    internal const int CancellationCheckInterval = 16_384;

    private const int InitialScratchBytes = 4096;

    private readonly DuckDBAppender _appender;
    private readonly WorksheetDataType[] _types;
    private readonly CancellationToken _cancellationToken;
    private IDuckDBAppenderRow? _row;
    private int _nextColumn;
    private int _workSinceCheck;
    private byte[]? _scratch;
    private bool _detached;

    public DuckDbRawRowWriter(DuckDBAppender appender, IReadOnlyList<RawColumnSchema> columns, CancellationToken cancellationToken)
    {
        _appender = appender;
        _types = columns.Select(column => column.DataType).ToArray();
        _cancellationToken = cancellationToken;
    }

    // Rows begun and ended so far.
    public long RowCount { get; private set; }

    // Test probes: how often the scratch buffer was (re)allocated, and its current size.
    internal int ScratchAllocations { get; private set; }

    internal int ScratchCapacity => _scratch?.Length ?? 0;

    public bool IsRowOpen => _row is not null;

    public void BeginRow()
    {
        ThrowIfDetached();
        if (_row is not null)
        {
            throw new InvalidOperationException("The previous row has not been ended.");
        }

        CountWork();
        _row = _appender.CreateRow().AppendValue(RowCount);
        _nextColumn = 0;
    }

    public void WriteNull()
    {
        NextColumn();
        _row = _row!.AppendNullValue();
    }

    public void WriteDouble(double value)
    {
        var type = NextColumnType();
        if (type != WorksheetDataType.Numeric)
        {
            throw new InvalidOperationException($"Column {_nextColumn} is {type}; a number cannot be written to it.");
        }

        if (!double.IsFinite(value))
        {
            throw new ArgumentException($"Row {RowCount} contains a non-finite numeric value ({value}).", nameof(value));
        }

        _nextColumn++;
        _row = _row!.AppendValue(value);
    }

    public void WriteString(ReadOnlySpan<char> value)
    {
        var type = NextColumnType();
        if (type != WorksheetDataType.String)
        {
            throw new InvalidOperationException($"Column {_nextColumn} is {type}; text cannot be written to it.");
        }

        // Legacy AppendValue(string) passes a null-terminated C string to DuckDB, which stops at the first NUL.
        var nulIndex = value.IndexOf('\0');
        if (nulIndex >= 0)
        {
            value = value[..nulIndex];
        }

        var bytes = EnsureScratch(value);
        var length = Encoding.UTF8.GetBytes(value, bytes);
        _nextColumn++;
        _row = _row!.AppendValue(bytes.AsSpan(0, length));
    }

    public void EndRow()
    {
        ThrowIfDetached();
        if (_row is null)
        {
            throw new InvalidOperationException("No row has been begun.");
        }

        if (_nextColumn != _types.Length)
        {
            throw new InvalidOperationException($"The row has {_nextColumn} of {_types.Length} values.");
        }

        _row.EndRow();
        _row = null;
        RowCount++;
    }

    // Ends the writer's lifetime; called by the store when the callback has returned or thrown.
    public void Detach() => _detached = true;

    private void NextColumn()
    {
        NextColumnType();
        _nextColumn++;
    }

    private WorksheetDataType NextColumnType()
    {
        ThrowIfDetached();
        if (_row is null)
        {
            throw new InvalidOperationException("No row has been begun.");
        }

        if (_nextColumn >= _types.Length)
        {
            throw new InvalidOperationException($"The row already has all {_types.Length} values.");
        }

        CountWork();
        return _types[_nextColumn];
    }

    private void CountWork()
    {
        if (++_workSinceCheck >= CancellationCheckInterval)
        {
            _workSinceCheck = 0;
            _cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private byte[] EnsureScratch(ReadOnlySpan<char> value)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (_scratch is not null && _scratch.Length >= maxBytes)
        {
            return _scratch;
        }

        // The worst case is not always needed: grow only to what this value takes (at least doubling).
        var needed = maxBytes <= InitialScratchBytes ? maxBytes : Encoding.UTF8.GetByteCount(value);
        if (_scratch is not null && _scratch.Length >= needed)
        {
            return _scratch;
        }

        var capacity = Math.Max(Math.Max(needed, InitialScratchBytes), (_scratch?.Length ?? 0) * 2);
        _scratch = new byte[capacity];
        ScratchAllocations++;
        return _scratch;
    }

    private void ThrowIfDetached() => ObjectDisposedException.ThrowIf(_detached, this);
}
