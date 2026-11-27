using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Abstractions.Persistence;

// Schema definition of one column in an atomic streaming worksheet creation.
public sealed record WorksheetColumnDefinition(
    string Name,
    WorksheetDataType DataType,
    ColumnSemanticType? SemanticType = null,
    string? Unit = null);

// Complete, pre-transaction specification of a new worksheet to be created atomically with streamed rows.
public sealed record AtomicWorksheetCreateRequest(
    Guid ProjectId,
    string WorksheetName,
    IReadOnlyList<WorksheetColumnDefinition> Columns);

// Committed metadata returned after an atomic streaming worksheet creation succeeds.
public sealed record CreatedWorksheetResult(
    Worksheet Worksheet,
    IReadOnlyList<WorksheetColumn> Columns);

// Atomically creates a new worksheet (worksheet metadata, column metadata, raw storage table, streamed rows, raw
// catalog pointers and finalized row/column counts) inside a single storage transaction.
//
// Contract:
//   - The complete column schema is determined and defensively snapshotted before the transaction begins.
//   - writeRows is invoked once, synchronously, inside the active transaction while holding the database gate.
//   - Reentrancy restriction: writeRows must NOT re-enter project database, repository or session operations on the
//     active project while running. Same-thread reentrancy is detected and rejected immediately with
//     InvalidOperationException, whereas starting another database operation on a different thread and synchronously
//     waiting on it from inside writeRows will deadlock on the database gate and is strictly prohibited.
//   - Borrowed caller buffers passed into IRawDataRowWriter.WriteString are encoded immediately and never retained
//     after the call returns.
//   - Zero-data-row streaming is supported and commits a valid empty worksheet (RowCount = 0, ColumnCount = N);
//     higher-level workflows (e.g. FT-03) decide whether an import with zero retained rows should create a worksheet.
//   - Any failure or cancellation observed before commit rolls back all metadata and raw storage changes so no
//     partial worksheet remains.
//   - Commit boundary: once the transaction commits, the committed CreatedWorksheetResult is returned even if
//     cancellation is signaled concurrently after commit. Outer session notifications (e.g. ProjectModified) run
//     after commit and must remain lightweight and non-throwing so committed work is never misreported as uncommitted.
public interface IAtomicWorksheetCreator
{
    Task<CreatedWorksheetResult> CreateWorksheetAsync(
        AtomicWorksheetCreateRequest request,
        Action<IRawDataRowWriter> writeRows,
        CancellationToken cancellationToken = default);
}
