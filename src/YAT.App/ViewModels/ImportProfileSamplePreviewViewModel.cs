using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;

namespace YAT.app.ViewModels;

public enum PreviewViewMode
{
    Table,
    Raw
}

public enum PreviewRowRole
{
    None,
    Header,
    Unit,
    Target,
    Lsl,
    Usl,
    DataStart
}

public enum PreviewColumnRole
{
    None,
    Bin,
    Site,
    Ignored,
    Conflict
}

public enum PreviewColumnAction
{
    SetAsBin,
    SetAsSite,
    AddToIgnored,
    RemoveFromIgnored,
    ClearAssignment
}

public sealed record PreviewColumnHeaderModel(
    int Index,
    int Position,
    string DisplayHeader,
    string? ResolvedHeaderName,
    bool IsCompositeExpanded,
    PreviewColumnRole AssignedRole,
    string RoleBadgeText);

public sealed record PreviewTableRowModel(
    int SourceRowNumber,
    PreviewRowRole AssignedRole,
    string RoleBadgeText,
    IReadOnlyList<string> Cells,
    bool IsColumnTruncated);

// Manages bounded sample datalog preview, live filename matching, interactive row/column assignment,
// and cancellable latest-request-wins background refreshes for ImportProfileBuilderViewModel (FT-02.3).
public sealed partial class ImportProfileSamplePreviewViewModel : ObservableObject, IDisposable
{
    private readonly ImportProfileBuilderViewModel _builder;
    private readonly DatalogSamplePreviewService _service;
    private readonly Action<Action> _dispatchToUi;
    private readonly TimeSpan _debounceDelay;
    private readonly Lock _sync = new();

    private CancellationTokenSource? _activeCts;
    private long _requestVersion;
    private long _appliedInspectionVersion;
    private bool _disposed;

    public ImportProfileSamplePreviewViewModel(
        ImportProfileBuilderViewModel builder,
        DatalogSamplePreviewService? service = null,
        Action<Action>? dispatchToUi = null,
        TimeSpan? debounceDelay = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _builder = builder;
        _service = service ?? new DatalogSamplePreviewService();
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(120);

        if (dispatchToUi is not null)
        {
            _dispatchToUi = dispatchToUi;
        }
        else
        {
            var captured = SynchronizationContext.Current;
            _dispatchToUi = action =>
            {
                if (captured is not null)
                {
                    captured.Post(_ => action(), null);
                }
                else
                {
                    action();
                }
            };
        }
    }

    public Task CurrentRefreshTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSampleFile))]
    [NotifyPropertyChangedFor(nameof(SampleFileName))]
    public partial string? SampleFilePath { get; private set; }

    public bool HasSampleFile => !string.IsNullOrEmpty(SampleFilePath);

    public string SampleFileName => string.IsNullOrEmpty(SampleFilePath) ? "No sample selected" : Path.GetFileName(SampleFilePath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTableView))]
    [NotifyPropertyChangedFor(nameof(IsRawView))]
    public partial PreviewViewMode ViewMode { get; set; } = PreviewViewMode.Table;

    public bool IsTableView => ViewMode == PreviewViewMode.Table;

    public bool IsRawView => ViewMode == PreviewViewMode.Raw;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenError))]
    public partial string? OpenError { get; private set; }

    public bool HasOpenError => !string.IsNullOrEmpty(OpenError);

    // ---- Filename Preview ----

    [ObservableProperty]
    public partial bool IsFilenameMatched { get; private set; }

    [ObservableProperty]
    public partial string FilenameSummaryText { get; private set; } = "Select a sample datalog file to preview filename matching and table structure.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilenameDiagnostic))]
    public partial string? FilenameDiagnosticText { get; private set; }

    public bool HasFilenameDiagnostic => !string.IsNullOrEmpty(FilenameDiagnosticText);

    // ---- Structure & Bounded Sample Validation ----

    [ObservableProperty]
    public partial bool IsStructureResolved { get; private set; }

    [ObservableProperty]
    public partial string StructureStatusText { get; private set; } = "No sample loaded";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStructureDiagnostics))]
    public partial string? StructureDiagnosticsText { get; private set; }

    public bool HasStructureDiagnostics => !string.IsNullOrEmpty(StructureDiagnosticsText);

    [ObservableProperty]
    public partial bool IsMeasurementValidationSuccess { get; private set; }

    [ObservableProperty]
    public partial string MeasurementValidationText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTruncation))]
    public partial string? TruncationText { get; private set; }

    public bool HasTruncation => !string.IsNullOrEmpty(TruncationText);

    // ---- Bounded Table & Raw Data ----

    public ObservableCollection<PreviewColumnHeaderModel> PreviewColumns { get; } = [];

    public ObservableCollection<PreviewTableRowModel> PreviewTableRows { get; } = [];

    [ObservableProperty]
    public partial string RawPreviewText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial long TableRevision { get; private set; }

    public DatalogSampleInspectionResult? LastInspection { get; private set; }

    public void SetSampleFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        SampleFilePath = filePath;
        RequestRefresh(immediate: true);
    }

    public void ClearSampleFile()
    {
        CancelActive();
        _appliedInspectionVersion = 0;
        SampleFilePath = null;
        LastInspection = null;
        IsLoading = false;
        OpenError = null;
        IsFilenameMatched = false;
        FilenameSummaryText = "Select a sample datalog file to preview filename matching and table structure.";
        FilenameDiagnosticText = null;
        IsStructureResolved = false;
        StructureStatusText = "No sample loaded";
        StructureDiagnosticsText = null;
        IsMeasurementValidationSuccess = false;
        MeasurementValidationText = string.Empty;
        TruncationText = null;
        PreviewColumns.Clear();
        PreviewTableRows.Clear();
        RawPreviewText = string.Empty;
        TableRevision++;
    }

    public void RequestRefresh(bool immediate = false)
    {
        if (_disposed || string.IsNullOrWhiteSpace(SampleFilePath))
        {
            return;
        }

        var path = SampleFilePath;
        var profileSnapshot = _builder.BuildProfile();

        CancellationTokenSource cts;
        long version;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _activeCts?.Cancel();
            _activeCts?.Dispose();
            cts = new CancellationTokenSource();
            _activeCts = cts;
            version = ++_requestVersion;
        }

        IsLoading = true;
        var delay = immediate ? TimeSpan.Zero : _debounceDelay;
        var task = RunRefreshAsync(profileSnapshot, path, version, delay, cts.Token);
        CurrentRefreshTask = task;
    }

    public bool WillReplaceKeywordRule(PreviewRowRole role)
    {
        var draft = GetRowDraft(role);
        return draft is not null && draft.IsEnabled && draft.Kind == RowMatchKind.KeywordMatch;
    }

    public bool CanClearRowRole(int sourceRowNumber)
    {
        foreach (var draft in new[] { _builder.UnitRow, _builder.TargetRow, _builder.LslRow, _builder.UslRow })
        {
            if (draft.IsEnabled && draft.Kind == RowMatchKind.FixedRowIndex && draft.RowNumber == sourceRowNumber)
            {
                return true;
            }
        }

        if (LastInspection?.Preview?.Structure?.RowPositions is { } pos)
        {
            if (pos.UnitRowNumber == sourceRowNumber || pos.TargetRowNumber == sourceRowNumber
                || pos.LslRowNumber == sourceRowNumber || pos.UslRowNumber == sourceRowNumber)
            {
                return true;
            }
        }

        return false;
    }

    public void AssignRowRole(int sourceRowNumber, PreviewRowRole role)
    {
        if (sourceRowNumber < 1 || role == PreviewRowRole.None)
        {
            return;
        }

        var draft = GetRowDraft(role);
        if (draft is null)
        {
            return;
        }

        _builder.RunSuppressed(() =>
        {
            draft.IsEnabled = true;
            draft.Kind = RowMatchKind.FixedRowIndex;
            draft.RowNumber = sourceRowNumber;
        });

        _builder.Validate();
        RequestRefresh(immediate: true);
    }

    public void ClearRowRole(int sourceRowNumber)
    {
        var changed = false;
        var pos = LastInspection?.Preview?.Structure?.RowPositions;

        _builder.RunSuppressed(() =>
        {
            if (_builder.UnitRow.IsEnabled && ((_builder.UnitRow.Kind == RowMatchKind.FixedRowIndex && _builder.UnitRow.RowNumber == sourceRowNumber) || pos?.UnitRowNumber == sourceRowNumber))
            {
                _builder.UnitRow.IsEnabled = false;
                changed = true;
            }

            if (_builder.TargetRow.IsEnabled && ((_builder.TargetRow.Kind == RowMatchKind.FixedRowIndex && _builder.TargetRow.RowNumber == sourceRowNumber) || pos?.TargetRowNumber == sourceRowNumber))
            {
                _builder.TargetRow.IsEnabled = false;
                changed = true;
            }

            if (_builder.LslRow.IsEnabled && ((_builder.LslRow.Kind == RowMatchKind.FixedRowIndex && _builder.LslRow.RowNumber == sourceRowNumber) || pos?.LslRowNumber == sourceRowNumber))
            {
                _builder.LslRow.IsEnabled = false;
                changed = true;
            }

            if (_builder.UslRow.IsEnabled && ((_builder.UslRow.Kind == RowMatchKind.FixedRowIndex && _builder.UslRow.RowNumber == sourceRowNumber) || pos?.UslRowNumber == sourceRowNumber))
            {
                _builder.UslRow.IsEnabled = false;
                changed = true;
            }
        });

        if (changed)
        {
            _builder.Validate();
            RequestRefresh(immediate: true);
        }
    }

    public void AssignColumnRole(PreviewColumnHeaderModel column, PreviewColumnAction action)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (IsLoading || _appliedInspectionVersion != GetCurrentVersion())
        {
            return;
        }

        var useHeaderName = !string.IsNullOrEmpty(column.ResolvedHeaderName);

        _builder.RunSuppressed(() =>
        {
            switch (action)
            {
                case PreviewColumnAction.SetAsBin:
                    ApplySelector(_builder.BinColumn, column, useHeaderName);
                    break;

                case PreviewColumnAction.SetAsSite:
                    _builder.SiteColumn.IsEnabled = true;
                    ApplySelector(_builder.SiteColumn, column, useHeaderName);
                    break;

                case PreviewColumnAction.AddToIgnored:
                    if (!IsColumnIgnored(column))
                    {
                        var draft = new ColumnSelectorDraft("Ignored Column", isRequired: true);
                        ApplySelector(draft, column, useHeaderName);
                        _builder.IgnoredColumns.Add(draft);
                    }
                    break;

                case PreviewColumnAction.RemoveFromIgnored:
                    RemoveMatchingIgnored(column);
                    break;

                case PreviewColumnAction.ClearAssignment:
                    if (_builder.SiteColumn.IsEnabled && SelectorMatchesColumn(_builder.SiteColumn, column))
                    {
                        _builder.SiteColumn.IsEnabled = false;
                    }
                    RemoveMatchingIgnored(column);
                    break;
            }
        });

        _builder.Validate();
        RequestRefresh(immediate: true);
    }

    public void Dispose()
    {
        CancelActive();
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private void CancelActive()
    {
        lock (_sync)
        {
            _requestVersion++;
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    private async Task RunRefreshAsync(
        ImportProfile profileSnapshot,
        string path,
        long version,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var result = await Task.Run(
                () => _service.Inspect(profileSnapshot, path, cancellationToken: cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (IsStale(version) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _dispatchToUi(() =>
            {
                if (IsStale(version) || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ApplyInspectionResult(result);
                _appliedInspectionVersion = version;
                IsLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded or closed — leave newer request in charge.
        }
        catch (Exception ex)
        {
            if (IsStale(version))
            {
                return;
            }

            _dispatchToUi(() =>
            {
                if (IsStale(version))
                {
                    return;
                }

                OpenError = ex.Message;
                IsLoading = false;
            });
        }
    }

    private long GetCurrentVersion()
    {
        lock (_sync)
        {
            return _requestVersion;
        }
    }

    private bool IsStale(long version)
    {
        lock (_sync)
        {
            return _disposed || version != _requestVersion;
        }
    }

    private void ApplyInspectionResult(DatalogSampleInspectionResult result)
    {
        LastInspection = result;
        OpenError = result.OpenError;

        // 1. Filename Preview
        if (result.FilenameMatch.IsSuccess && result.FilenameMatch.Value is { } matched)
        {
            IsFilenameMatched = true;
            FilenameDiagnosticText = null;
            var round = matched.RetestRound is { } r ? $" | Round: {r}" : string.Empty;
            var part = matched.FilePart is { } p ? $" | Part: {p}" : string.Empty;
            FilenameSummaryText = $"Lot: {matched.LotId} | Stage: {matched.TestStage} | {matched.TestRole}{round}{part}";
        }
        else
        {
            IsFilenameMatched = false;
            FilenameSummaryText = $"Filename did not match ({result.FileName})";
            FilenameDiagnosticText = result.FilenameMatch.ErrorMessage ?? "Filename rules did not match the selected sample.";
        }

        // 2. Preview Data (Table + Raw)
        PreviewColumns.Clear();
        PreviewTableRows.Clear();

        if (result.Preview is null)
        {
            RawPreviewText = string.Empty;
            IsStructureResolved = false;
            StructureStatusText = "Sample could not be opened";
            StructureDiagnosticsText = result.OpenError;
            IsMeasurementValidationSuccess = false;
            MeasurementValidationText = string.Empty;
            TruncationText = null;
            return;
        }

        var preview = result.Preview;
        var structure = preview.Structure;

        // Raw view text (bounded, 1-based row numbers)
        var sb = new StringBuilder();
        foreach (var line in preview.RawLines)
        {
            sb.Append(line.SourceRowNumber.ToString().PadLeft(4));
            sb.Append(" | ");
            sb.Append(line.Text);
            if (line.IsTruncated)
            {
                sb.Append(" ... [truncated]");
            }

            sb.AppendLine();
        }
        RawPreviewText = sb.ToString();

        // Truncation banner
        var truncParts = new List<string>();
        if (preview.WasTruncatedByRowLimit)
        {
            truncParts.Add($"showing first {preview.RawLines.Count} rows (limit {DatalogPreviewReader.DefaultMaxRows})");
        }
        if (preview.WasTruncatedBySizeLimit)
        {
            truncParts.Add("preview character cap reached (512 KB)");
        }
        if (preview.TableRows.Any(r => r.IsColumnTruncated))
        {
            truncParts.Add($"wide rows truncated to {DatalogPreviewReader.DefaultMaxColumnsPerRow} columns");
        }
        TruncationText = truncParts.Count == 0 ? null : "Bounded preview: " + string.Join("; ", truncParts) + ".";

        // Structure status
        if (structure is { IsSuccess: true })
        {
            IsStructureResolved = true;
            StructureDiagnosticsText = null;
            var siteText = structure.SiteColumn is { } s ? s.Name : "None";
            StructureStatusText = $"Structure resolved | Bin: {structure.BinColumn!.Name} | Site: {siteText} | Measurements: {structure.AvailableTestHeaders.Count}";
        }
        else
        {
            IsStructureResolved = false;
            StructureStatusText = "Structure unresolved (preview still available)";
            StructureDiagnosticsText = preview.Diagnostics.Count > 0
                ? string.Join(Environment.NewLine, preview.Diagnostics.Take(3).Select(d => d.Message))
                : "Configure delimiter, Header Row, Data Start Row, and Bin column.";
        }

        // Bounded measurement validation status
        if (result.MeasurementValidation is { } mv)
        {
            IsMeasurementValidationSuccess = mv.IsSuccess;
            MeasurementValidationText = mv.Summary;
        }
        else
        {
            IsMeasurementValidationSuccess = false;
            MeasurementValidationText = string.Empty;
        }

        // Build Table Columns + Rows
        BuildTableModels(preview, structure);
        TableRevision++;
    }

    private void BuildTableModels(DatalogPreviewResult preview, DatalogStructureScanResult? structure)
    {
        var hasResolvedHeader = structure is { IsSuccess: true } && structure.AllHeaders.Count > 0;
        var expandedIndices = ComputeCompositeExpandedIndices(structure);

        var colCount = hasResolvedHeader
            ? Math.Min(structure!.AllHeaders.Count, DatalogPreviewReader.DefaultMaxColumnsPerRow)
            : (preview.TableRows.Count > 0 ? preview.TableRows.Max(r => r.Cells.Count) : 0);

        for (var i = 0; i < colCount; i++)
        {
            var resolvedName = hasResolvedHeader ? structure!.AllHeaders[i] : null;
            var isExpanded = expandedIndices.Contains(i);
            var display = !string.IsNullOrEmpty(resolvedName) ? resolvedName : $"C{i + 1}";
            var (role, badge) = ResolveColumnRole(i, resolvedName, structure);
            if (isExpanded)
            {
                badge = string.IsNullOrEmpty(badge) ? "[EXP]" : $"{badge} [EXP]";
            }

            PreviewColumns.Add(new PreviewColumnHeaderModel(i, i + 1, display, resolvedName, isExpanded, role, badge));
        }

        var compositePlans = structure is { IsSuccess: true } ? structure.CompositePlans : [];
        var headerRowNum = structure?.RowPositions?.HeaderRowNumber;

        foreach (var row in preview.TableRows)
        {
            IReadOnlyList<string> cells = row.Cells;
            if (compositePlans.Count > 0 && headerRowNum is { } hdr && row.SourceRowNumber >= hdr)
            {
                if (DatalogRecordTokenizer.TryExpandCompositeRow(row.Cells.ToArray(), compositePlans, row.SourceRowNumber, out var expanded, out _))
                {
                    cells = expanded.Length > DatalogPreviewReader.DefaultMaxColumnsPerRow
                        ? expanded[..DatalogPreviewReader.DefaultMaxColumnsPerRow]
                        : expanded;
                }
            }

            var (rowRole, rowBadge) = ResolveRowRole(row.SourceRowNumber, structure);
            PreviewTableRows.Add(new PreviewTableRowModel(row.SourceRowNumber, rowRole, rowBadge, cells, row.IsColumnTruncated));
        }
    }

    private static HashSet<int> ComputeCompositeExpandedIndices(DatalogStructureScanResult? structure)
    {
        var set = new HashSet<int>();
        if (structure is not { IsSuccess: true } || structure.CompositePlans.Count == 0)
        {
            return set;
        }

        var offset = 0;
        foreach (var plan in structure.CompositePlans.OrderBy(p => p.RawColumnIndex))
        {
            var start = plan.RawColumnIndex + offset;
            for (var k = 0; k < plan.OutputNames.Count; k++)
            {
                set.Add(start + k);
            }

            offset += plan.OutputNames.Count - 1;
        }

        return set;
    }

    private (PreviewRowRole Role, string Badge) ResolveRowRole(int rowNumber, DatalogStructureScanResult? structure)
    {
        if (structure is { IsSuccess: true, RowPositions: { } pos })
        {
            if (pos.HeaderRowNumber == rowNumber) return (PreviewRowRole.Header, "HDR");
            if (pos.UnitRowNumber == rowNumber) return (PreviewRowRole.Unit, "UNIT");
            if (pos.TargetRowNumber == rowNumber) return (PreviewRowRole.Target, "TGT");
            if (pos.LslRowNumber == rowNumber) return (PreviewRowRole.Lsl, "LSL");
            if (pos.UslRowNumber == rowNumber) return (PreviewRowRole.Usl, "USL");
            if (pos.DataStartRowNumber == rowNumber) return (PreviewRowRole.DataStart, "DATA");
            return (PreviewRowRole.None, string.Empty);
        }

        // Before full structure resolution, reflect fixed-row draft assignments directly.
        if (_builder.HeaderRow.Kind == RowMatchKind.FixedRowIndex && _builder.HeaderRow.RowNumber == rowNumber) return (PreviewRowRole.Header, "HDR");
        if (_builder.UnitRow.IsEnabled && _builder.UnitRow.Kind == RowMatchKind.FixedRowIndex && _builder.UnitRow.RowNumber == rowNumber) return (PreviewRowRole.Unit, "UNIT");
        if (_builder.TargetRow.IsEnabled && _builder.TargetRow.Kind == RowMatchKind.FixedRowIndex && _builder.TargetRow.RowNumber == rowNumber) return (PreviewRowRole.Target, "TGT");
        if (_builder.LslRow.IsEnabled && _builder.LslRow.Kind == RowMatchKind.FixedRowIndex && _builder.LslRow.RowNumber == rowNumber) return (PreviewRowRole.Lsl, "LSL");
        if (_builder.UslRow.IsEnabled && _builder.UslRow.Kind == RowMatchKind.FixedRowIndex && _builder.UslRow.RowNumber == rowNumber) return (PreviewRowRole.Usl, "USL");
        if (_builder.DataStartRow.Kind == RowMatchKind.FixedRowIndex && _builder.DataStartRow.RowNumber == rowNumber) return (PreviewRowRole.DataStart, "DATA");
        return (PreviewRowRole.None, string.Empty);
    }

    private (PreviewColumnRole Role, string Badge) ResolveColumnRole(int index, string? headerName, DatalogStructureScanResult? structure)
    {
        if (structure is { IsSuccess: true })
        {
            if (structure.BinColumn?.ColumnIndex == index) return (PreviewColumnRole.Bin, "[BIN]");
            if (structure.SiteColumn?.ColumnIndex == index) return (PreviewColumnRole.Site, "[SITE]");
            if (structure.IgnoredColumns.Any(c => c.ColumnIndex == index)) return (PreviewColumnRole.Ignored, "[IGN]");
            return (PreviewColumnRole.None, string.Empty);
        }

        var isBin = SelectorMatchesIndexOrName(_builder.BinColumn, index, headerName);
        var isSite = _builder.SiteColumn.IsEnabled && SelectorMatchesIndexOrName(_builder.SiteColumn, index, headerName);
        var isIgnored = _builder.IgnoredColumns.Any(c => SelectorMatchesIndexOrName(c, index, headerName));
        var count = (isBin ? 1 : 0) + (isSite ? 1 : 0) + (isIgnored ? 1 : 0);
        if (count > 1) return (PreviewColumnRole.Conflict, "[CONFLICT]");
        if (isBin) return (PreviewColumnRole.Bin, "[BIN]");
        if (isSite) return (PreviewColumnRole.Site, "[SITE]");
        if (isIgnored) return (PreviewColumnRole.Ignored, "[IGN]");
        return (PreviewColumnRole.None, string.Empty);
    }

    private RowIdentifierDraft? GetRowDraft(PreviewRowRole role) => role switch
    {
        PreviewRowRole.Header => _builder.HeaderRow,
        PreviewRowRole.Unit => _builder.UnitRow,
        PreviewRowRole.Target => _builder.TargetRow,
        PreviewRowRole.Lsl => _builder.LslRow,
        PreviewRowRole.Usl => _builder.UslRow,
        PreviewRowRole.DataStart => _builder.DataStartRow,
        _ => null
    };

    private static void ApplySelector(ColumnSelectorDraft draft, PreviewColumnHeaderModel column, bool useHeaderName)
    {
        if (useHeaderName)
        {
            draft.Kind = ColumnSelectorKind.HeaderName;
            draft.HeaderName = column.ResolvedHeaderName!;
        }
        else
        {
            draft.Kind = ColumnSelectorKind.Index;
            draft.ColumnPosition = column.Position;
        }
    }

    private bool IsColumnIgnored(PreviewColumnHeaderModel column) =>
        _builder.IgnoredColumns.Any(c => SelectorMatchesColumn(c, column));

    private void RemoveMatchingIgnored(PreviewColumnHeaderModel column)
    {
        var matches = _builder.IgnoredColumns.Where(c => SelectorMatchesColumn(c, column)).ToArray();
        foreach (var match in matches)
        {
            _builder.IgnoredColumns.Remove(match);
        }
    }

    private static bool SelectorMatchesColumn(ColumnSelectorDraft draft, PreviewColumnHeaderModel column) =>
        SelectorMatchesIndexOrName(draft, column.Index, column.ResolvedHeaderName);

    private static bool SelectorMatchesIndexOrName(ColumnSelectorDraft draft, int index, string? headerName) =>
        draft.Kind == ColumnSelectorKind.Index
            ? draft.ColumnPosition - 1 == index
            : !string.IsNullOrEmpty(headerName) && string.Equals(draft.HeaderName, headerName, StringComparison.Ordinal);
}
