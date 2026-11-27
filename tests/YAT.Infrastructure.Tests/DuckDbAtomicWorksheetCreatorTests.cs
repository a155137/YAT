using System.Globalization;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.Application.Queries;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;

namespace YAT.Infrastructure.Tests;

public sealed class DuckDbAtomicWorksheetCreatorTests : IDisposable
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 27, 16, 0, 0, TimeSpan.Zero);

    private readonly string _tempRoot;
    private readonly DuckDbProjectStorage _storage;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DuckDbAtomicWorksheetCreatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "yat-ft02a-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _storage = new DuckDbProjectStorage(Path.Combine(_tempRoot, "storage"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed record DatabaseSnapshot(
        long ProjectCount,
        long WorksheetCount,
        long ColumnCount,
        long RawBlockCount,
        long RawColumnCount,
        long BlockTableCount);

    private static DatabaseSnapshot CaptureSnapshot(DuckDbProjectDatabase db) =>
        db.Execute(connection => new DatabaseSnapshot(
            Count(connection, "SELECT count(*) FROM project"),
            Count(connection, "SELECT count(*) FROM worksheet"),
            Count(connection, "SELECT count(*) FROM worksheet_column"),
            Count(connection, "SELECT count(*) FROM raw_block"),
            Count(connection, "SELECT count(*) FROM raw_column"),
            Count(connection, "SELECT count(*) FROM duckdb_tables() WHERE database_name = current_database() AND schema_name = 'main' AND table_name LIKE 'blk_%'")));

    private static long Count(DuckDB.NET.Data.DuckDBConnection connection, string sql) =>
        Convert.ToInt64(DuckDbRawCatalog.Scalar(connection, sql), CultureInfo.InvariantCulture);

    private async Task<(DuckDbProjectDatabase Db, Project Project, Worksheet ExistingSheet)> CreateProjectWithSeedWorksheetAsync(
        string? filePath = null,
        TimeProvider? timeProvider = null)
    {
        var tp = timeProvider ?? new MutableTimeProvider(BaseTime);
        var db = filePath is null ? _storage.CreateTemporary() : _storage.Create(filePath);
        var projectRepo = new DuckDbProjectRepository(db);
        var worksheetRepo = new DuckDbWorksheetRepository(db);
        var columnRepo = new DuckDbWorksheetColumnRepository(db, tp);
        var rawStore = new DuckDbWorksheetRawDataStore(db);

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "FT02A_Project",
            CreatedAt = BaseTime,
            UpdatedAt = BaseTime
        };
        await projectRepo.AddAsync(project, Token);

        var existing = new Worksheet
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = "Sheet1",
            RowCount = 2,
            ColumnCount = 1,
            CreatedAt = BaseTime,
            UpdatedAt = BaseTime
        };
        await worksheetRepo.AddAsync(existing, Token);

        var seedCol = new WorksheetColumn
        {
            Id = Guid.NewGuid(),
            WorksheetId = existing.Id,
            Index = 0,
            Name = "SeedVal",
            DataType = WorksheetDataType.Numeric
        };
        await columnRepo.AddAsync(seedCol, Token);
        await rawStore.WriteColumnsAsync(
            existing.Id,
            new RawDataBlock([new NumericRawDataColumn(seedCol.Id, [10.0, 20.0])]),
            Token);

        return (db, project, existing);
    }

    private static IReadOnlyList<WorksheetColumnDefinition> MixedRepresentativeColumns() =>
    [
        new("Lot_ID", WorksheetDataType.String, ColumnSemanticType.Lot),
        new("Bin", WorksheetDataType.String, ColumnSemanticType.Bin),
        new("Site", WorksheetDataType.String, ColumnSemanticType.Site),
        new("Test_Role", WorksheetDataType.String),
        new("VDD", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter, "V"),
        new("IDD", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter, "mA")
    ];

    // ---- A. Successful Atomic Creation ----

    [Fact]
    public async Task SuccessfulAtomicCreationPreservesTypesZerosMissingLeadingZerosOrderBorrowedBuffersAndExistingSheets()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, existingSheet) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var creator = new DuckDbAtomicWorksheetCreator(db, tp);
            var columns = MixedRepresentativeColumns();

            // Mutable borrowed char buffer mutated immediately after WriteString returns.
            var borrowedLot = "LOT001".ToCharArray();
            var borrowedBin = "001".ToCharArray();

            tp.Current = BaseTime.AddMinutes(5);
            var result = await creator.CreateWorksheetAsync(
                new AtomicWorksheetCreateRequest(project.Id, "  FT_Import_01  ", columns),
                writer =>
                {
                    // Segment 1 (File 1): leading zeros, zero numeric value, valid Site
                    writer.BeginRow();
                    writer.WriteString(borrowedLot);
                    borrowedLot.AsSpan().Fill('X'); // mutate borrowed buffer immediately
                    writer.WriteString(borrowedBin);
                    borrowedBin.AsSpan().Fill('9');
                    writer.WriteString("01");
                    writer.WriteString("Initial");
                    writer.WriteDouble(0.0);
                    writer.WriteDouble(1.234567890123);
                    writer.EndRow();

                    // Segment 1: missing Site (null), missing Numeric (null) distinct from 0.0
                    writer.BeginRow();
                    writer.WriteString("LOT001");
                    writer.WriteString("0002");
                    writer.WriteNull();
                    writer.WriteString("Initial");
                    writer.WriteNull();
                    writer.WriteDouble(-0.0);
                    writer.EndRow();

                    // Segment 2 (File 2 streamed into the same Worksheet)
                    writer.BeginRow();
                    writer.WriteString("LOT002");
                    writer.WriteString(" 001 ");
                    writer.WriteString("02");
                    writer.WriteString("Retest");
                    writer.WriteDouble(3.3);
                    writer.WriteNull();
                    writer.EndRow();
                },
                Token);

            Assert.Equal("FT_Import_01", result.Worksheet.Name);
            Assert.Equal(project.Id, result.Worksheet.ProjectId);
            Assert.Equal(3, result.Worksheet.RowCount);
            Assert.Equal(6, result.Worksheet.ColumnCount);
            Assert.Equal(6, result.Columns.Count);
            for (var i = 0; i < columns.Count; i++)
            {
                Assert.Equal(i, result.Columns[i].Index);
                Assert.Equal(columns[i].Name, result.Columns[i].Name);
                Assert.Equal(columns[i].DataType, result.Columns[i].DataType);
                Assert.Equal(columns[i].SemanticType, result.Columns[i].SemanticType);
                Assert.Equal(columns[i].Unit, result.Columns[i].Unit);
                Assert.Equal(result.Worksheet.Id, result.Columns[i].WorksheetId);
            }

            // Verify persisted metadata ordering (existing Sheet1 stays first, imported sheet second).
            var worksheetRepo = new DuckDbWorksheetRepository(db);
            var allSheets = await worksheetRepo.GetByProjectIdAsync(project.Id, Token);
            Assert.Equal(2, allSheets.Count);
            Assert.Equal(existingSheet.Id, allSheets[0].Id);
            Assert.Equal(2, allSheets[0].RowCount);
            Assert.Equal(result.Worksheet.Id, allSheets[1].Id);
            Assert.Equal(3, allSheets[1].RowCount);
            Assert.Equal(6, allSheets[1].ColumnCount);

            // Verify raw values round-trip accurately (leading zeros, borrowed buffer integrity, 0 vs null, precision).
            var rawStore = new DuckDbWorksheetRawDataStore(db);
            Assert.Equal(3, await rawStore.GetWorksheetRowCountAsync(result.Worksheet.Id, Token));
            var block = await rawStore.ReadColumnsAsync(
                result.Worksheet.Id,
                result.Columns.Select(c => c.Id).ToArray(),
                rowOffset: 0,
                rowCount: 10,
                Token);

            var lotCol = Assert.IsType<StringRawDataColumn>(block.Columns[0]);
            var binCol = Assert.IsType<StringRawDataColumn>(block.Columns[1]);
            var siteCol = Assert.IsType<StringRawDataColumn>(block.Columns[2]);
            var roleCol = Assert.IsType<StringRawDataColumn>(block.Columns[3]);
            var vddCol = Assert.IsType<NumericRawDataColumn>(block.Columns[4]);
            var iddCol = Assert.IsType<NumericRawDataColumn>(block.Columns[5]);

            Assert.Equal(["LOT001", "LOT001", "LOT002"], lotCol.Values);
            Assert.Equal(["001", "0002", " 001 "], binCol.Values);
            Assert.Equal(["01", null, "02"], siteCol.Values);
            Assert.Equal(["Initial", "Initial", "Retest"], roleCol.Values);
            Assert.Equal([0.0, null, 3.3], vddCol.Values);
            Assert.Equal(1.234567890123, iddCol.Values[0]);
            Assert.Equal(0.0, iddCol.Values[1]);
            Assert.Null(iddCol.Values[2]);

            // Existing worksheet raw data remains intact.
            var existingCols = await new DuckDbWorksheetColumnRepository(db, tp).GetByWorksheetIdAsync(existingSheet.Id, Token);
            var existingBlock = await rawStore.ReadColumnsAsync(existingSheet.Id, [existingCols[0].Id], 0, 10, Token);
            Assert.Equal([10.0, 20.0], Assert.IsType<NumericRawDataColumn>(existingBlock.Columns[0]).Values);
        }
    }

    [Fact]
    public async Task ZeroRowAtomicCreationCommitsValidEmptyWorksheet()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var creator = new DuckDbAtomicWorksheetCreator(db, tp);
            var result = await creator.CreateWorksheetAsync(
                new AtomicWorksheetCreateRequest(project.Id, "EmptyImport", [new("X", WorksheetDataType.Numeric)]),
                _ => { },
                Token);

            Assert.Equal(0, result.Worksheet.RowCount);
            Assert.Equal(1, result.Worksheet.ColumnCount);
            var rawStore = new DuckDbWorksheetRawDataStore(db);
            Assert.Equal(0, await rawStore.GetWorksheetRowCountAsync(result.Worksheet.Id, Token));
            Assert.Contains(result.Columns[0].Id, await rawStore.GetStoredColumnIdsAsync(result.Worksheet.Id, Token));
        }
    }

    [Fact]
    public async Task ValidationErrorsRejectInvalidRequestsWithoutLeavingResidualObjects()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var before = CaptureSnapshot(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, tp);
            var validCols = new[] { new WorksheetColumnDefinition("A", WorksheetDataType.Numeric) };

            await Assert.ThrowsAsync<ValidationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "   ", validCols), _ => { }, Token));
            await Assert.ThrowsAsync<ValidationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "sheet1", validCols), _ => { }, Token));
            await Assert.ThrowsAsync<EntityNotFoundException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(Guid.NewGuid(), "NewSheet", validCols), _ => { }, Token));
            await Assert.ThrowsAsync<ValidationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "NewSheet", []), _ => { }, Token));
            await Assert.ThrowsAsync<ValidationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "NewSheet", [new(" ", WorksheetDataType.String)]), _ => { }, Token));

            Assert.Equal(before, CaptureSnapshot(db));
        }
    }

    // ---- B. Rollback and Fault Injection ----

    [Fact]
    public async Task ProducerExceptionsAndProtocolViolationsRollBackCompletelyAndLeaveDatabaseUsable()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var before = CaptureSnapshot(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, tp);
            var cols = new[]
            {
                new WorksheetColumnDefinition("Tag", WorksheetDataType.String),
                new WorksheetColumnDefinition("Val", WorksheetDataType.Numeric)
            };

            // 1. Producer throws before first row.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "FailBeforeFirst", cols),
                    _ => throw new InvalidOperationException("before-first"), Token));
            Assert.Equal(before, CaptureSnapshot(db));

            // 2. Producer throws mid-stream (after 5,000 rows).
            await Assert.ThrowsAsync<ApplicationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "FailMidStream", cols),
                    writer =>
                    {
                        for (var i = 0; i < 5_000; i++)
                        {
                            writer.BeginRow();
                            writer.WriteString("A");
                            writer.WriteDouble(i);
                            writer.EndRow();
                        }

                        throw new ApplicationException("mid-stream");
                    }, Token));
            Assert.Equal(before, CaptureSnapshot(db));

            // 3. Invalid row width (too few values before EndRow).
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "FailShortRow", cols),
                    writer =>
                    {
                        writer.BeginRow();
                        writer.WriteString("A");
                        writer.EndRow();
                    }, Token));
            Assert.Equal(before, CaptureSnapshot(db));

            // 4. Invalid row width (too many values).
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "FailWideRow", cols),
                    writer =>
                    {
                        writer.BeginRow();
                        writer.WriteString("A");
                        writer.WriteDouble(1.0);
                        writer.WriteNull();
                        writer.EndRow();
                    }, Token));
            Assert.Equal(before, CaptureSnapshot(db));

            // 5. Open row left at callback return.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "FailOpenRow", cols),
                    writer =>
                    {
                        writer.BeginRow();
                        writer.WriteString("A");
                    }, Token));
            Assert.Equal(before, CaptureSnapshot(db));

            // 6. Wrong column type (number into String column).
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "FailWrongType", cols),
                    writer =>
                    {
                        writer.BeginRow();
                        writer.WriteDouble(1.0);
                        writer.WriteDouble(2.0);
                        writer.EndRow();
                    }, Token));
            Assert.Equal(before, CaptureSnapshot(db));

            // 7. Non-finite double (NaN).
            await Assert.ThrowsAsync<ArgumentException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "FailNaN", cols),
                    writer =>
                    {
                        writer.BeginRow();
                        writer.WriteString("A");
                        writer.WriteDouble(double.NaN);
                        writer.EndRow();
                    }, Token));
            Assert.Equal(before, CaptureSnapshot(db));

            // 8. Writer detached after failure cannot be reused.
            IRawDataRowWriter? escaped = null;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "FailEscape", cols),
                    writer =>
                    {
                        escaped = writer;
                        throw new InvalidOperationException("escape");
                    }, Token));
            Assert.NotNull(escaped);
            Assert.Throws<ObjectDisposedException>(() => escaped!.BeginRow());
            Assert.Equal(before, CaptureSnapshot(db));

            // 9. Subsequent valid import succeeds cleanly with the previously failed name.
            var recovered = await creator.CreateWorksheetAsync(
                new AtomicWorksheetCreateRequest(project.Id, "FailMidStream", cols),
                writer =>
                {
                    writer.BeginRow();
                    writer.WriteString("OK");
                    writer.WriteDouble(42.0);
                    writer.EndRow();
                }, Token);
            Assert.Equal(1, recovered.Worksheet.RowCount);
        }
    }

    [Theory]
    [InlineData(DuckDbAtomicWorksheetPhase.AfterWorksheetMetadataInsert)]
    [InlineData(DuckDbAtomicWorksheetPhase.AfterColumnMetadataInsert)]
    [InlineData(DuckDbAtomicWorksheetPhase.AfterRawTableCreate)]
    [InlineData(DuckDbAtomicWorksheetPhase.BeforeAppenderClose)]
    [InlineData(DuckDbAtomicWorksheetPhase.AfterAppenderClose)]
    [InlineData(DuckDbAtomicWorksheetPhase.AfterRawCatalogUpdate)]
    [InlineData(DuckDbAtomicWorksheetPhase.AfterWorksheetFinalize)]
    [InlineData(DuckDbAtomicWorksheetPhase.BeforeCommit)]
    internal async Task FaultAtEveryTransactionPhaseRollsBackCompletelyAndAllowsSubsequentImport(DuckDbAtomicWorksheetPhase faultPhase)
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var before = CaptureSnapshot(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, tp)
            {
                PhaseHook = phase =>
                {
                    if (phase == faultPhase)
                    {
                        throw new InvalidOperationException($"Injected fault at {phase}");
                    }
                }
            };

            var cols = MixedRepresentativeColumns();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                creator.CreateWorksheetAsync(
                    new AtomicWorksheetCreateRequest(project.Id, $"Fault_{faultPhase}", cols),
                    writer =>
                    {
                        for (var r = 0; r < 100; r++)
                        {
                            writer.BeginRow();
                            writer.WriteString("L1");
                            writer.WriteString("01");
                            writer.WriteString("S1");
                            writer.WriteString("Initial");
                            writer.WriteDouble(1.0 + r);
                            writer.WriteDouble(2.0 + r);
                            writer.EndRow();
                        }
                    },
                    Token));

            Assert.Contains(faultPhase.ToString(), ex.Message, StringComparison.Ordinal);
            Assert.Equal(before, CaptureSnapshot(db));

            // Clear hook and confirm a subsequent import with the same name succeeds.
            creator.PhaseHook = null;
            var ok = await creator.CreateWorksheetAsync(
                new AtomicWorksheetCreateRequest(project.Id, $"Fault_{faultPhase}", cols),
                writer =>
                {
                    writer.BeginRow();
                    writer.WriteString("L1");
                    writer.WriteString("01");
                    writer.WriteString("S1");
                    writer.WriteString("Initial");
                    writer.WriteDouble(3.3);
                    writer.WriteDouble(12.5);
                    writer.EndRow();
                },
                Token);
            Assert.Equal(1, ok.Worksheet.RowCount);
        }
    }

    [Fact]
    public async Task SecondaryAppenderCleanupFailurePreservesPrimaryExceptionAndRollsBack()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var before = CaptureSnapshot(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, tp)
            {
                PhaseHook = phase =>
                {
                    if (phase == DuckDbAtomicWorksheetPhase.BeforeAppenderCleanupOnFailure)
                    {
                        throw new IOException("simulated-cleanup-failure");
                    }
                }
            };

            var primary = await Assert.ThrowsAsync<ApplicationException>(() =>
                creator.CreateWorksheetAsync(
                    new AtomicWorksheetCreateRequest(project.Id, "CleanupFail", [new("V", WorksheetDataType.Numeric)]),
                    writer =>
                    {
                        writer.BeginRow();
                        writer.WriteDouble(1.0);
                        writer.EndRow();
                        throw new ApplicationException("primary-producer-failure");
                    },
                    Token));

            Assert.Equal("primary-producer-failure", primary.Message);
            var secondary = Assert.IsType<IOException>(primary.Data[DuckDbAtomicWorksheetCreator.AppenderCleanupExceptionDataKey]);
            Assert.Equal("simulated-cleanup-failure", secondary.Message);
            Assert.Equal(before, CaptureSnapshot(db));
        }
    }

    [Fact]
    public async Task FailedRollbackPreservesBothExceptionsAndPreventsFurtherUnsafeDatabaseUse()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var creator = new DuckDbAtomicWorksheetCreator(db, tp)
            {
                PhaseHook = phase =>
                {
                    if (phase == DuckDbAtomicWorksheetPhase.BeforeRollback)
                    {
                        throw new IOException("simulated-rollback-failure");
                    }
                }
            };

            var ex = await Assert.ThrowsAsync<ProjectStorageException>(() =>
                creator.CreateWorksheetAsync(
                    new AtomicWorksheetCreateRequest(project.Id, "RollbackFail", [new("V", WorksheetDataType.Numeric)]),
                    _ => throw new ApplicationException("primary-failure"),
                    Token));

            Assert.Equal(ProjectStorageError.StorageFailure, ex.Error);
            Assert.Contains("rollback", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("primary-failure", ex.Message, StringComparison.Ordinal);

            var aggregate = Assert.IsType<AggregateException>(ex.InnerException);
            Assert.Contains(aggregate.InnerExceptions, e => e is ApplicationException && e.Message == "primary-failure");
            Assert.Contains(aggregate.InnerExceptions, e => e is IOException && e.Message == "simulated-rollback-failure");

            // Further operations on the faulted database must be blocked.
            var worksheetRepo = new DuckDbWorksheetRepository(db);
            await Assert.ThrowsAsync<ProjectStorageException>(() => worksheetRepo.GetByProjectIdAsync(project.Id, Token));
        }
    }

    // ---- C. Cancellation ----

    [Fact]
    public async Task CancellationBeforeGateDuringStreamingAndBeforeCommitRollsBackCompletely()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var before = CaptureSnapshot(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, tp);
            var cols = new[] { new WorksheetColumnDefinition("V", WorksheetDataType.Numeric) };

            // 1. Already cancelled token.
            using (var preCancelled = new CancellationTokenSource())
            {
                preCancelled.Cancel();
                await Assert.ThrowsAsync<TaskCanceledException>(() =>
                    creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "PreCancelled", cols),
                        _ => throw new InvalidOperationException("must not invoke producer"), preCancelled.Token));
            }
            Assert.Equal(before, CaptureSnapshot(db));

            // 2. Cancellation during streaming (across CancellationCheckInterval).
            using (var streamCts = new CancellationTokenSource())
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "MidStreamCancel", cols),
                        writer =>
                        {
                            for (var i = 0; i < 40_000; i++)
                            {
                                if (i == 20_000)
                                {
                                    streamCts.Cancel();
                                }

                                writer.BeginRow();
                                writer.WriteDouble(i);
                                writer.EndRow();
                            }
                        },
                        streamCts.Token));
            }
            Assert.Equal(before, CaptureSnapshot(db));

            // 3. Cancellation immediately before commit.
            using (var preCommitCts = new CancellationTokenSource())
            {
                creator.PhaseHook = phase =>
                {
                    if (phase == DuckDbAtomicWorksheetPhase.BeforeCommit)
                    {
                        preCommitCts.Cancel();
                    }
                };

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    creator.CreateWorksheetAsync(new AtomicWorksheetCreateRequest(project.Id, "PreCommitCancel", cols),
                        writer =>
                        {
                            writer.BeginRow();
                            writer.WriteDouble(99.0);
                            writer.EndRow();
                        },
                        preCommitCts.Token));
            }
            Assert.Equal(before, CaptureSnapshot(db));

            // 4. Cancellation after successful commit MUST return committed result, NOT throw.
            using (var postCommitCts = new CancellationTokenSource())
            {
                creator.PhaseHook = phase =>
                {
                    if (phase == DuckDbAtomicWorksheetPhase.AfterCommit)
                    {
                        postCommitCts.Cancel();
                    }
                };

                var committed = await creator.CreateWorksheetAsync(
                    new AtomicWorksheetCreateRequest(project.Id, "PostCommitCancel", cols),
                    writer =>
                    {
                        writer.BeginRow();
                        writer.WriteDouble(123.45);
                        writer.EndRow();
                    },
                    postCommitCts.Token);

                Assert.Equal("PostCommitCancel", committed.Worksheet.Name);
                Assert.Equal(1, committed.Worksheet.RowCount);
                Assert.NotNull(await new DuckDbWorksheetRepository(db).GetByIdAsync(committed.Worksheet.Id, Token));
            }
        }
    }

    [Fact]
    public async Task CancellationWhileWaitingForDatabaseGateCancelsPromptly()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            using var gateHeld = new ManualResetEventSlim(false);
            using var releaseGate = new ManualResetEventSlim(false);

            var holder = new DuckDbAtomicWorksheetCreator(db, tp)
            {
                PhaseHook = phase =>
                {
                    if (phase == DuckDbAtomicWorksheetPhase.AfterGateAcquired)
                    {
                        gateHeld.Set();
                        releaseGate.Wait(TimeSpan.FromSeconds(10), Token);
                    }
                }
            };

            var cols = new[] { new WorksheetColumnDefinition("V", WorksheetDataType.Numeric) };
            var firstTask = Task.Run(() =>
                holder.CreateWorksheetAsync(
                    new AtomicWorksheetCreateRequest(project.Id, "HolderSheet", cols),
                    writer =>
                    {
                        writer.BeginRow();
                        writer.WriteDouble(1.0);
                        writer.EndRow();
                    },
                    Token), Token);

            Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(5), Token));

            var waiter = new DuckDbAtomicWorksheetCreator(db, tp);
            using var waitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Task.Run(() =>
                    waiter.CreateWorksheetAsync(
                        new AtomicWorksheetCreateRequest(project.Id, "WaiterSheet", cols),
                        _ => { },
                        waitCts.Token),
                    Token));

            releaseGate.Set();
            var firstResult = await firstTask;
            Assert.Equal("HolderSheet", firstResult.Worksheet.Name);

            var sheets = await new DuckDbWorksheetRepository(db).GetByProjectIdAsync(project.Id, Token);
            Assert.DoesNotContain(sheets, w => w.Name == "WaiterSheet");
        }
    }

    // ---- D. Concurrency ----

    [Fact]
    public async Task SameThreadReEntryInsideProducerThrowsImmediatelyWithoutDeadlockAndRollsBack()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var before = CaptureSnapshot(db);
            var worksheetRepo = new DuckDbWorksheetRepository(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, tp);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                creator.CreateWorksheetAsync(
                    new AtomicWorksheetCreateRequest(project.Id, "Reentrant", [new("V", WorksheetDataType.Numeric)]),
                    writer =>
                    {
                        writer.BeginRow();
                        writer.WriteDouble(1.0);
                        writer.EndRow();
                        // Forbidden same-thread re-entry into the project database while holding the gate.
                        worksheetRepo.GetByProjectIdAsync(project.Id, Token).GetAwaiter().GetResult();
                    },
                    Token));

            Assert.Contains("Nested project database access", ex.Message, StringComparison.Ordinal);
            Assert.Equal(before, CaptureSnapshot(db));
        }
    }

    [Fact]
    public async Task ConcurrentReadersAndSaveCopyNeverObserveIncompleteWorksheetState()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            using var midTransaction = new ManualResetEventSlim(false);
            using var allowFinish = new ManualResetEventSlim(false);

            var creator = new DuckDbAtomicWorksheetCreator(db, tp)
            {
                PhaseHook = phase =>
                {
                    if (phase == DuckDbAtomicWorksheetPhase.AfterRawCatalogUpdate)
                    {
                        midTransaction.Set();
                        allowFinish.Wait(TimeSpan.FromSeconds(10), Token);
                        throw new InvalidOperationException("abort-after-wait");
                    }
                }
            };

            var importTask = Task.Run(() =>
                creator.CreateWorksheetAsync(
                    new AtomicWorksheetCreateRequest(project.Id, "NeverVisible", [new("V", WorksheetDataType.Numeric)]),
                    writer =>
                    {
                        writer.BeginRow();
                        writer.WriteDouble(77.0);
                        writer.EndRow();
                    },
                    Token), Token);

            Assert.True(midTransaction.Wait(TimeSpan.FromSeconds(5), Token));

            // Launch concurrent reader + SaveCopy while the import transaction is still open (and will roll back).
            var copyPath = Path.Combine(_tempRoot, "concurrent_copy.yat");
            var readTask = Task.Run(() => new DuckDbWorksheetRepository(db).GetByProjectIdAsync(project.Id, Token), Token);
            var saveCopyTask = Task.Run(() => db.SaveCopyAsync(copyPath, Token), Token);

            // Brief yield so both concurrent callers are blocked waiting on the gate.
            await Task.Delay(50, Token);
            Assert.False(readTask.IsCompleted);
            Assert.False(saveCopyTask.IsCompleted);

            allowFinish.Set();

            await Assert.ThrowsAsync<InvalidOperationException>(() => importTask);
            var sheets = await readTask;
            await saveCopyTask;

            Assert.DoesNotContain(sheets, w => w.Name == "NeverVisible");

            // Verify the SaveCopy snapshot also contains zero trace of the rolled-back worksheet.
            using var reopened = _storage.Open(copyPath);
            var copySheets = await new DuckDbWorksheetRepository(reopened).GetByProjectIdAsync(project.Id, Token);
            Assert.Single(copySheets);
            Assert.Equal("Sheet1", copySheets[0].Name);
        }
    }

    // ---- E. Persistence, Save As, Reopen, Virtualized Paging & Graph Queries ----

    [Fact]
    public async Task CommittedWorksheetSurvivesCheckpointReopenSaveAsVirtualizedPagingAndGraphQueries()
    {
        var projectPath = Path.Combine(_tempRoot, "imported_project.yat");
        var copyPath = Path.Combine(_tempRoot, "imported_project_copy.yat");
        var tp = new MutableTimeProvider(BaseTime);

        Guid projectId;
        CreatedWorksheetResult created;
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(filePath: projectPath, timeProvider: tp);
        projectId = project.Id;
        using (db)
        {
            var creator = new DuckDbAtomicWorksheetCreator(db, tp);
            created = await creator.CreateWorksheetAsync(
                new AtomicWorksheetCreateRequest(projectId, "FT_LotA", MixedRepresentativeColumns()),
                writer =>
                {
                    for (var i = 0; i < 650; i++)
                    {
                        writer.BeginRow();
                        writer.WriteString(i % 2 == 0 ? "LOT01" : "LOT02");
                        writer.WriteString(i % 5 == 0 ? "002" : "001");
                        if (i % 7 == 0)
                        {
                            writer.WriteNull();
                        }
                        else
                        {
                            writer.WriteString($"S{(i % 4) + 1}");
                        }

                        writer.WriteString("Initial");
                        writer.WriteDouble(i == 0 ? 0.0 : 1.0 + i * 0.01);
                        if (i % 10 == 0)
                        {
                            writer.WriteNull();
                        }
                        else
                        {
                            writer.WriteDouble(10.0 + i * 0.1);
                        }

                        writer.EndRow();
                    }
                },
                Token);

            await db.CheckpointAsync(Token);
            await db.SaveCopyAsync(copyPath, Token);
        }

        // Verify both reopened original .yat and Save As copy.
        foreach (var path in new[] { projectPath, copyPath })
        {
            using var reopened = _storage.Open(path);
            var projects = new DuckDbProjectRepository(reopened);
            var worksheets = new DuckDbWorksheetRepository(reopened);
            var columns = new DuckDbWorksheetColumnRepository(reopened, tp);
            var rawStore = new DuckDbWorksheetRawDataStore(reopened);

            // Project metadata query
            var metadataService = new ProjectMetadataQueryService(projects, worksheets, columns);
            var meta = await metadataService.LoadAsync(projectId, Token);
            Assert.Equal(2, meta.Worksheets.Count);
            var importedMeta = meta.Worksheets[1];
            Assert.Equal(created.Worksheet.Id, importedMeta.Worksheet.Id);
            Assert.Equal(650, importedMeta.Worksheet.RowCount);
            Assert.Equal(6, importedMeta.Columns.Count);

            // Virtualized paging (page 2: offset 500, size 500 -> 150 rows)
            var dataQuery = new WorksheetDataQueryService(worksheets, columns, rawStore);
            var pageMeta = await dataQuery.GetPageMetadataAsync(created.Worksheet.Id, rowOffset: 500, rowCount: 500, Token);
            Assert.Equal(650, pageMeta.TotalRowCount);
            Assert.Equal(150, pageMeta.RowCount);

            var window = await dataQuery.GetColumnWindowAsync(
                created.Worksheet.Id,
                rowOffset: 500,
                rowCount: 500,
                [created.Columns[1].Id, created.Columns[4].Id],
                Token);
            Assert.Equal(150, window.RowCount);
            Assert.Equal("002", Assert.IsType<StringRawDataColumn>(window.Columns[0].Values).Values[0]);
            Assert.Equal(6.0, Assert.IsType<NumericRawDataColumn>(window.Columns[1].Values).Values[0]!.Value, 6);

            // GraphDataQueryService (Histogram over Numeric VDD grouped by String Lot_ID)
            var graphService = new GraphDataQueryService(worksheets, columns, rawStore);
            var graphConfig = new GraphConfiguration(
                GraphType.Histogram,
                created.Worksheet.Id,
                [
                    new GraphColumnAssignment(GraphVariableRole.Variable, created.Columns[4].Id),
                    new GraphColumnAssignment(GraphVariableRole.Group, created.Columns[0].Id)
                ]);
            var graphData = await graphService.LoadAsync(graphConfig, Token);
            Assert.Equal(650, graphData.Count);
            var group = graphData switch
            {
                MultiVariableGraphData multi => Assert.IsType<StringGroupData>(multi.Variables[0].Group),
                UnivariateGraphData uni => Assert.IsType<StringGroupData>(uni.Group),
                _ => throw new InvalidOperationException($"Unexpected GraphData type: {graphData.GetType().Name}")
            };
            Assert.Equal(650, group.Count);
        }
    }

    [Fact]
    public async Task DisposeBlocksUntilActiveTransactionCompletesAndQueuedOperationsFailSafely()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);

        using var txActive = new ManualResetEventSlim(false);
        using var allowCommit = new ManualResetEventSlim(false);
        using var disposeStarted = new ManualResetEventSlim(false);
        using var queuedStarted = new ManualResetEventSlim(false);

        var creator = new DuckDbAtomicWorksheetCreator(db, tp)
        {
            PhaseHook = phase =>
            {
                if (phase == DuckDbAtomicWorksheetPhase.AfterRawTableCreate)
                {
                    txActive.Set();
                    allowCommit.Wait(TimeSpan.FromSeconds(10), Token);
                }
            }
        };

        var importTask = Task.Run(() =>
            creator.CreateWorksheetAsync(
                new AtomicWorksheetCreateRequest(project.Id, "CommitBeforeDispose", [new("V", WorksheetDataType.Numeric)]),
                writer =>
                {
                    writer.BeginRow();
                    writer.WriteDouble(5.0);
                    writer.EndRow();
                },
                Token), Token);

        Assert.True(txActive.Wait(TimeSpan.FromSeconds(5), Token));

        var disposeTask = Task.Run(() =>
        {
            disposeStarted.Set();
            db.Dispose();
        }, Token);
        Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(5), Token));

        var queuedReadTask = Task.Run(() =>
        {
            queuedStarted.Set();
            return new DuckDbWorksheetRepository(db).GetByProjectIdAsync(project.Id, Token);
        }, Token);
        Assert.True(queuedStarted.Wait(TimeSpan.FromSeconds(5), Token));

        // Neither Dispose nor the queued read may complete while the active transaction holds the gate.
        await Task.Delay(50, Token);
        Assert.False(disposeTask.IsCompleted);
        Assert.False(queuedReadTask.IsCompleted);

        allowCommit.Set();

        var created = await importTask;
        Assert.Equal("CommitBeforeDispose", created.Worksheet.Name);
        await disposeTask;

        // Queued read may either run before Dispose (returning the committed sheet) or after Dispose (throwing ObjectDisposedException);
        // it must never crash with NullReferenceException or use a disposed connection.
        try
        {
            var sheets = await queuedReadTask;
            Assert.Contains(sheets, w => w.Id == created.Worksheet.Id);
        }
        catch (ObjectDisposedException)
        {
            // Expected when Dispose acquired the gate ahead of the queued reader.
        }

        // Subsequent operation after Dispose deterministically throws ObjectDisposedException, and repeated Dispose is safe.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => new DuckDbWorksheetRepository(db).GetByProjectIdAsync(project.Id, Token));
        db.Dispose();
    }

    [Fact]
    public async Task QueuedOperationWakesUpSafelyWhenDatabaseBecomesFaultedAfterRollbackFailure()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            using var gateEntered = new ManualResetEventSlim(false);
            using var allowFail = new ManualResetEventSlim(false);
            using var waiterStarted = new ManualResetEventSlim(false);

            var creator = new DuckDbAtomicWorksheetCreator(db, tp)
            {
                PhaseHook = phase =>
                {
                    if (phase == DuckDbAtomicWorksheetPhase.AfterGateAcquired)
                    {
                        gateEntered.Set();
                        allowFail.Wait(TimeSpan.FromSeconds(10), Token);
                    }
                    else if (phase == DuckDbAtomicWorksheetPhase.BeforeRollback)
                    {
                        throw new IOException("simulated-rollback-failure-for-queued");
                    }
                }
            };

            var failingImport = Task.Run(() =>
                creator.CreateWorksheetAsync(
                    new AtomicWorksheetCreateRequest(project.Id, "FaultedSheet", [new("V", WorksheetDataType.Numeric)]),
                    _ => throw new InvalidOperationException("primary-boom"),
                    Token), Token);

            Assert.True(gateEntered.Wait(TimeSpan.FromSeconds(5), Token));

            var queuedReader = Task.Run(() =>
            {
                waiterStarted.Set();
                return new DuckDbWorksheetRepository(db).GetByProjectIdAsync(project.Id, Token);
            }, Token);
            Assert.True(waiterStarted.Wait(TimeSpan.FromSeconds(5), Token));

            await Task.Delay(50, Token);
            Assert.False(queuedReader.IsCompleted);

            allowFail.Set();

            await Assert.ThrowsAsync<ProjectStorageException>(() => failingImport);
            var readerEx = await Assert.ThrowsAsync<ProjectStorageException>(() => queuedReader);
            Assert.Equal(ProjectStorageError.StorageFailure, readerEx.Error);
        }
    }

    [Fact]
    public async Task DefensiveColumnSchemaSnapshotIgnoresCallerListMutationDuringStreamingAndRejectsDuplicateNames()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var before = CaptureSnapshot(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, tp);

            // Duplicate exact column names are rejected before opening a transaction.
            await Assert.ThrowsAsync<ValidationException>(() =>
                creator.CreateWorksheetAsync(
                    new AtomicWorksheetCreateRequest(
                        project.Id,
                        "DupCols",
                        [new("VDD", WorksheetDataType.Numeric), new("VDD", WorksheetDataType.Numeric)]),
                    _ => { },
                    Token));
            Assert.Equal(before, CaptureSnapshot(db));

            // Mutating caller's mutable List<WorksheetColumnDefinition> during streaming cannot alter the committed schema.
            var mutableCols = new List<WorksheetColumnDefinition>
            {
                new("Lot_ID", WorksheetDataType.String),
                new("VDD", WorksheetDataType.Numeric)
            };

            var result = await creator.CreateWorksheetAsync(
                new AtomicWorksheetCreateRequest(project.Id, "SnapshotSchema", mutableCols),
                writer =>
                {
                    mutableCols.Clear();
                    mutableCols.Add(new("Injected", WorksheetDataType.String));

                    writer.BeginRow();
                    writer.WriteString("LOT1");
                    writer.WriteDouble(1.8);
                    writer.EndRow();
                },
                Token);

            Assert.Equal(2, result.Columns.Count);
            Assert.Equal("Lot_ID", result.Columns[0].Name);
            Assert.Equal("VDD", result.Columns[1].Name);
        }
    }

    [Fact]
    public async Task CaseSensitiveLeadingZeroAndInternalSpaceHeadersRemainSeparatelyAddressable()
    {
        var tp = new MutableTimeProvider(BaseTime);
        var (db, project, _) = await CreateProjectWithSeedWorksheetAsync(timeProvider: tp);
        using (db)
        {
            var creator = new DuckDbAtomicWorksheetCreator(db, tp);
            var cols = new WorksheetColumnDefinition[]
            {
                new("Lot_ID", WorksheetDataType.String, ColumnSemanticType.Lot),
                new("Bin", WorksheetDataType.String, ColumnSemanticType.Bin),
                new("Site", WorksheetDataType.String, ColumnSemanticType.Site),
                new("Test_Role", WorksheetDataType.String),
                new("1001_IDD", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter),
                new("1002_IDD", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter),
                new("IDD", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter),
                new("idd", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter),
                new("001_VTH", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter),
                new("1_VTH", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter),
                new("VTH (mV)", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter)
            };

            var created = await creator.CreateWorksheetAsync(
                new AtomicWorksheetCreateRequest(project.Id, "FtCaseSensitiveHeaders", cols),
                writer =>
                {
                    writer.BeginRow();
                    writer.WriteString("LOT1");
                    writer.WriteString("01");
                    writer.WriteNull();
                    writer.WriteString("Original");
                    writer.WriteDouble(10.1);
                    writer.WriteDouble(10.2);
                    writer.WriteDouble(1.0);
                    writer.WriteDouble(2.0);
                    writer.WriteDouble(0.35);
                    writer.WriteDouble(0.45);
                    writer.WriteDouble(350.0);
                    writer.EndRow();
                },
                Token);

            var colRepo = new DuckDbWorksheetColumnRepository(db, tp);
            var storedCols = await colRepo.GetByWorksheetIdAsync(created.Worksheet.Id, Token);
            Assert.Equal(cols.Select(c => c.Name).ToArray(), storedCols.Select(c => c.Name).ToArray());

            var queryService = new WorksheetDataQueryService(
                new DuckDbWorksheetRepository(db),
                colRepo,
                new DuckDbWorksheetRawDataStore(db));
            var page = await queryService.GetPageAsync(created.Worksheet.Id, 0, 10, Token);

            Assert.Equal(1, page.RowCount);
            Assert.Equal(11, page.Columns.Count);
            var iddUpperIdx = storedCols.Single(c => string.Equals(c.Name, "IDD", StringComparison.Ordinal)).Index;
            var iddLowerIdx = storedCols.Single(c => string.Equals(c.Name, "idd", StringComparison.Ordinal)).Index;
            var vthZeroIdx = storedCols.Single(c => string.Equals(c.Name, "001_VTH", StringComparison.Ordinal)).Index;
            var vthOneIdx = storedCols.Single(c => string.Equals(c.Name, "1_VTH", StringComparison.Ordinal)).Index;
            var vthSpaceIdx = storedCols.Single(c => string.Equals(c.Name, "VTH (mV)", StringComparison.Ordinal)).Index;

            Assert.Equal(1.0, Assert.IsType<NumericRawDataColumn>(page.Columns[iddUpperIdx].Values).Values[0]);
            Assert.Equal(2.0, Assert.IsType<NumericRawDataColumn>(page.Columns[iddLowerIdx].Values).Values[0]);
            Assert.Equal(0.35, Assert.IsType<NumericRawDataColumn>(page.Columns[vthZeroIdx].Values).Values[0]);
            Assert.Equal(0.45, Assert.IsType<NumericRawDataColumn>(page.Columns[vthOneIdx].Values).Values[0]);
            Assert.Equal(350.0, Assert.IsType<NumericRawDataColumn>(page.Columns[vthSpaceIdx].Values).Values[0]);
        }
    }
}
