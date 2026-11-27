using DuckDB.NET.Data;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Infrastructure.Persistence.DuckDb;

// Test-only fault-injection phases for DuckDbAtomicWorksheetCreator. Always null in production.
internal enum DuckDbAtomicWorksheetPhase
{
    AfterGateAcquired,
    AfterWorksheetMetadataInsert,
    AfterColumnMetadataInsert,
    AfterRawTableCreate,
    BeforeAppenderClose,
    AfterAppenderClose,
    BeforeAppenderCleanupOnFailure,
    AfterRawCatalogUpdate,
    AfterWorksheetFinalize,
    BeforeCommit,
    AfterCommit,
    BeforeRollback
}

// Single-transaction atomic creator of a new worksheet (worksheet metadata, column metadata, raw block table,
// streamed rows through DuckDB Appender, raw catalog pointers and finalized row/column counts) over a shared
// DuckDbProjectDatabase connection and gate.
public sealed class DuckDbAtomicWorksheetCreator : IAtomicWorksheetCreator
{
    public const string AppenderCleanupExceptionDataKey = "AppenderCleanupException";

    private readonly DuckDbProjectDatabase _database;
    private readonly TimeProvider _timeProvider;

    public DuckDbAtomicWorksheetCreator(DuckDbProjectDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _timeProvider = timeProvider;
    }

    // Test-only fault-injection hook between phases. Always null in production.
    internal Action<DuckDbAtomicWorksheetPhase>? PhaseHook { get; set; }

    public Task<CreatedWorksheetResult> CreateWorksheetAsync(
        AtomicWorksheetCreateRequest request,
        Action<IRawDataRowWriter> writeRows,
        CancellationToken cancellationToken = default)
    {
        var committed = false;
        try
        {
            var (worksheetName, definitions) = ValidateRequest(request, writeRows);
            cancellationToken.ThrowIfCancellationRequested();

            var result = _database.Execute(
                connection => CreateWorksheetCore(
                    connection,
                    request.ProjectId,
                    worksheetName,
                    definitions,
                    writeRows,
                    cancellationToken,
                    out committed),
                cancellationToken);

            return Task.FromResult(result);
        }
        catch (OperationCanceledException) when (!committed && cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<CreatedWorksheetResult>(cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<CreatedWorksheetResult>(exception);
        }
    }

    private CreatedWorksheetResult CreateWorksheetCore(
        DuckDBConnection connection,
        Guid projectId,
        string worksheetName,
        WorksheetColumnDefinition[] definitions,
        Action<IRawDataRowWriter> writeRows,
        CancellationToken cancellationToken,
        out bool committed)
    {
        committed = false;

        PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.AfterGateAcquired);
        cancellationToken.ThrowIfCancellationRequested();

        using var transaction = connection.BeginTransaction();
        try
        {
            // 1. Validate project existence and case-insensitive worksheet name uniqueness inside the transaction.
            if (!ProjectExists(connection, projectId))
            {
                throw new EntityNotFoundException(nameof(Project), projectId);
            }

            if (DuckDbWorksheetRepository.ExistsByNameIgnoreCase(connection, projectId, worksheetName))
            {
                throw new ValidationException($"A worksheet named '{worksheetName}' already exists in this project.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 2. Insert initial worksheet metadata (order assigned via COALESCE(MAX(position) + 1, 0)).
            var createdAt = DuckDbMetadata.ToStoredTimestamp(_timeProvider.GetUtcNow());
            var worksheet = new Worksheet
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Name = worksheetName,
                RowCount = 0,
                ColumnCount = definitions.Length,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            };
            DuckDbWorksheetRepository.Insert(connection, worksheet);
            PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.AfterWorksheetMetadataInsert);
            cancellationToken.ThrowIfCancellationRequested();

            // 3. Allocate and insert column metadata in contiguous 0..N-1 order.
            var worksheetColumns = new WorksheetColumn[definitions.Length];
            var rawSchema = new RawColumnSchema[definitions.Length];
            for (var index = 0; index < definitions.Length; index++)
            {
                var def = definitions[index];
                var column = new WorksheetColumn
                {
                    Id = Guid.NewGuid(),
                    WorksheetId = worksheet.Id,
                    Index = index,
                    Name = def.Name,
                    DataType = def.DataType,
                    SemanticType = def.SemanticType,
                    Unit = def.Unit
                };
                worksheetColumns[index] = column;
                rawSchema[index] = new RawColumnSchema(column.Id, column.DataType);
            }

            DuckDbWorksheetColumnRepository.InsertColumns(connection, worksheetColumns, createdAt);
            PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.AfterColumnMetadataInsert);
            cancellationToken.ThrowIfCancellationRequested();

            // 4. Create the physical raw block table inside the same transaction.
            var blockId = Guid.NewGuid();
            var tableName = DuckDbIdentifiers.BlockTable(blockId);
            DuckDbWorksheetRawDataStore.CreateBlockTable(connection, tableName, rawSchema);
            PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.AfterRawTableCreate);
            cancellationToken.ThrowIfCancellationRequested();

            // 5. Stream rows through DuckDB Appender participating in the active transaction.
            var rowCount = AppendStreamingRows(connection, tableName, rawSchema, writeRows, cancellationToken);

            // 6. Register raw block and raw column catalog entries inside the same transaction.
            DuckDbRawCatalog.InsertBlock(connection, blockId, worksheet.Id, tableName, rowCount);
            DuckDbRawCatalog.UpsertColumns(connection, worksheet.Id, blockId, rawSchema);
            PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.AfterRawCatalogUpdate);
            cancellationToken.ThrowIfCancellationRequested();

            // 7. Finalize worksheet row/column counts and UpdatedAt timestamp.
            var updatedAt = DuckDbMetadata.ToStoredTimestamp(_timeProvider.GetUtcNow());
            if (updatedAt < createdAt)
            {
                updatedAt = createdAt;
            }

            worksheet.RowCount = rowCount;
            worksheet.ColumnCount = worksheetColumns.Length;
            worksheet.UpdatedAt = updatedAt;
            DuckDbWorksheetRepository.Update(connection, worksheet);
            PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.AfterWorksheetFinalize);

            // 8. Final pre-commit cancellation check.
            PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.BeforeCommit);
            cancellationToken.ThrowIfCancellationRequested();

            // 9. Commit boundary: once Commit() returns, never roll back and never throw cancellation.
            transaction.Commit();
            committed = true;

            PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.AfterCommit);
            return new CreatedWorksheetResult(worksheet, Array.AsReadOnly(worksheetColumns));
        }
        catch (Exception primary) when (!committed)
        {
            try
            {
                PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.BeforeRollback);
                transaction.Rollback();
            }
            catch (Exception rollbackException)
            {
                _database.MarkFaulted(
                    $"The project database is unavailable because transaction rollback failed: {rollbackException.Message}");
                throw new ProjectStorageException(
                    ProjectStorageError.StorageFailure,
                    $"Atomic worksheet creation failed and rollback could not be confirmed ({rollbackException.Message}). Original error: {primary.Message}",
                    new AggregateException("Atomic worksheet creation failed and rollback also failed.", primary, rollbackException));
            }

            if (primary is DuckDBException duckDbException)
            {
                var translated = new ProjectStorageException(
                    ProjectStorageError.StorageFailure,
                    $"The atomic worksheet storage operation failed: {duckDbException.Message}",
                    duckDbException);
                if (duckDbException.Data.Contains(AppenderCleanupExceptionDataKey))
                {
                    translated.Data[AppenderCleanupExceptionDataKey] = duckDbException.Data[AppenderCleanupExceptionDataKey];
                }

                throw translated;
            }

            throw;
        }
    }

    private long AppendStreamingRows(
        DuckDBConnection connection,
        string tableName,
        IReadOnlyList<RawColumnSchema> columns,
        Action<IRawDataRowWriter> writeRows,
        CancellationToken cancellationToken)
    {
        DuckDBAppender? appender = null;
        DuckDbRawRowWriter? writer = null;
        Exception? primaryException = null;
        try
        {
            appender = connection.CreateAppender(tableName);
            writer = new DuckDbRawRowWriter(appender, columns, cancellationToken);
            writeRows(writer);
            if (writer.IsRowOpen)
            {
                throw new InvalidOperationException("The streaming write left an open row.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.BeforeAppenderClose);
            appender.Close();
            PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.AfterAppenderClose);
            return writer.RowCount;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            writer?.Detach();
            if (appender is not null)
            {
                try
                {
                    if (primaryException is not null)
                    {
                        PhaseHook?.Invoke(DuckDbAtomicWorksheetPhase.BeforeAppenderCleanupOnFailure);
                        // Discard partially written rows/chunks before Dispose() flushes.
                        appender.Clear();
                    }

                    appender.Dispose();
                }
                catch (Exception cleanupException)
                {
                    if (primaryException is not null)
                    {
                        // Preserve the primary failure while recording the secondary cleanup failure clearly.
                        primaryException.Data[AppenderCleanupExceptionDataKey] = cleanupException;
                    }
                    else
                    {
                        // No prior failure occurred; propagate so the outer transaction rolls back.
                        throw;
                    }
                }
            }
        }
    }

    private static bool ProjectExists(DuckDBConnection connection, Guid projectId) =>
        DuckDbRawCatalog.Scalar(connection, "SELECT 1 FROM project WHERE id = $id LIMIT 1", ("id", projectId)) is not null;

    private static (string WorksheetName, WorksheetColumnDefinition[] Columns) ValidateRequest(
        AtomicWorksheetCreateRequest request,
        Action<IRawDataRowWriter> writeRows)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writeRows);

        if (string.IsNullOrWhiteSpace(request.WorksheetName))
        {
            throw new ValidationException("Worksheet name must not be empty.");
        }

        if (request.Columns is null || request.Columns.Count == 0)
        {
            throw new ValidationException("At least one column definition is required to create a worksheet.");
        }

        var normalizedColumns = new WorksheetColumnDefinition[request.Columns.Count];
        var seenColumnNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < request.Columns.Count; i++)
        {
            var col = request.Columns[i];
            if (col is null)
            {
                throw new ValidationException($"Column definition at index {i} must not be null.");
            }

            if (string.IsNullOrWhiteSpace(col.Name))
            {
                throw new ValidationException($"Column name at index {i} must not be empty.");
            }

            if (col.DataType is not (WorksheetDataType.Numeric or WorksheetDataType.String))
            {
                throw new ValidationException($"Column '{col.Name}' has unsupported data type '{col.DataType}'.");
            }

            var trimmedName = col.Name.Trim();
            if (!seenColumnNames.Add(trimmedName))
            {
                throw new ValidationException($"Duplicate column name '{trimmedName}' is not allowed in the same worksheet.");
            }

            normalizedColumns[i] = col with { Name = trimmedName };
        }

        return (request.WorksheetName.Trim(), normalizedColumns);
    }
}
