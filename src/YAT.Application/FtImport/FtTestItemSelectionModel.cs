namespace YAT.Application.FtImport;

// Application-level Test Item selection model over an immutable FtUnionSchema for the future FT-04 dual-list UI.
// Enforces exact ordinal measurement header identity and retains deterministic union first-seen ordering.
public sealed class FtTestItemSelectionModel
{
    private readonly HashSet<string> _availableSet;
    private readonly HashSet<string> _selectedSet;

    public FtTestItemSelectionModel(FtUnionSchema unionSchema, bool selectAllByDefault = false)
    {
        ArgumentNullException.ThrowIfNull(unionSchema);
        UnionSchema = unionSchema;
        _availableSet = new HashSet<string>(unionSchema.UnionMeasurementHeaders, StringComparer.Ordinal);
        _selectedSet = selectAllByDefault
            ? new HashSet<string>(unionSchema.UnionMeasurementHeaders, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public FtUnionSchema UnionSchema { get; }

    public IReadOnlyList<string> AvailableHeaders => UnionSchema.UnionMeasurementHeaders;

    public int AvailableCount => UnionSchema.UnionMeasurementHeaders.Count;

    public int SelectedCount => _selectedSet.Count;

    public bool IsSelected(string header) =>
        header is not null && _selectedSet.Contains(header);

    public void Add(string header)
    {
        ValidateKnownHeader(header, nameof(header));
        if (_selectedSet.Contains(header))
        {
            throw new ArgumentException($"Measurement header '{header}' is already selected.", nameof(header));
        }

        _selectedSet.Add(header);
    }

    public void AddRange(IEnumerable<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var validated = new List<string>();
        var batchSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            ValidateKnownHeader(header, nameof(headers));
            if (!batchSeen.Add(header))
            {
                throw new ArgumentException($"Duplicate measurement header '{header}' in selection batch.", nameof(headers));
            }

            if (_selectedSet.Contains(header))
            {
                throw new ArgumentException($"Measurement header '{header}' is already selected.", nameof(headers));
            }

            validated.Add(header);
        }

        foreach (var header in validated)
        {
            _selectedSet.Add(header);
        }
    }

    public void Remove(string header)
    {
        ValidateKnownHeader(header, nameof(header));
        _selectedSet.Remove(header);
    }

    public void RemoveRange(IEnumerable<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var validated = new List<string>();
        foreach (var header in headers)
        {
            ValidateKnownHeader(header, nameof(headers));
            validated.Add(header);
        }

        foreach (var header in validated)
        {
            _selectedSet.Remove(header);
        }
    }

    public void SelectAll()
    {
        _selectedSet.Clear();
        foreach (var header in UnionSchema.UnionMeasurementHeaders)
        {
            _selectedSet.Add(header);
        }
    }

    public void ClearAll() => _selectedSet.Clear();

    // Returns an independent immutable snapshot of selected headers in deterministic union first-seen order.
    public IReadOnlyList<string> GetSelectedHeaders()
    {
        if (_selectedSet.Count == 0)
        {
            return Array.AsReadOnly(Array.Empty<string>());
        }

        var ordered = new string[_selectedSet.Count];
        var idx = 0;
        foreach (var header in UnionSchema.UnionMeasurementHeaders)
        {
            if (_selectedSet.Contains(header))
            {
                ordered[idx++] = header;
            }
        }

        return Array.AsReadOnly(ordered);
    }

    // Returns an independent immutable snapshot of unselected headers in deterministic union first-seen order.
    public IReadOnlyList<string> GetUnselectedHeaders()
    {
        var remainingCount = UnionSchema.UnionMeasurementHeaders.Count - _selectedSet.Count;
        if (remainingCount == 0)
        {
            return Array.AsReadOnly(Array.Empty<string>());
        }

        var ordered = new string[remainingCount];
        var idx = 0;
        foreach (var header in UnionSchema.UnionMeasurementHeaders)
        {
            if (!_selectedSet.Contains(header))
            {
                ordered[idx++] = header;
            }
        }

        return Array.AsReadOnly(ordered);
    }

    private void ValidateKnownHeader(string header, string paramName)
    {
        if (header is null || !_availableSet.Contains(header))
        {
            throw new ArgumentException(
                $"Measurement header '{header ?? "<null>"}' does not exist in the Union Schema.",
                paramName);
        }
    }
}
