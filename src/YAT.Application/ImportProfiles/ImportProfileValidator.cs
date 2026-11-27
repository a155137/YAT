using System.Text.RegularExpressions;

namespace YAT.Application.ImportProfiles;

public enum ImportProfileValidationCode
{
    InvalidIdentity,
    UnsupportedSchemaVersion,
    InvalidFilenameRules,
    InvalidStructureRules,
    ConflictingRowRules,
    InvalidColumnSelector,
    ConflictingColumnRules,
    InvalidBinRules,
    ConflictingBinRules,
    InvalidMissingValueRules
}

public sealed record ImportProfileValidationError(
    ImportProfileValidationCode Code,
    string Field,
    string Message);

public sealed record ImportProfileValidationResult(IReadOnlyList<ImportProfileValidationError> Errors)
{
    public static ImportProfileValidationResult Valid { get; } = new([]);

    public bool IsValid => Errors.Count == 0;
}

// Deterministic, UI-ready validator for ImportProfile configuration (FT-01A).
// Validates configuration consistency only; does not inspect datalog files.
public sealed class ImportProfileValidator
{
    private static readonly TimeSpan RegexValidationTimeout = TimeSpan.FromSeconds(1);

    public ImportProfileValidationResult Validate(ImportProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<ImportProfileValidationError>();

        ValidateIdentity(profile, errors);
        ValidateFilenameRules(profile.FilenameRules, errors);
        ValidateStructureRules(profile.StructureRules, errors);
        ValidateBinRules(profile.BinRules, errors);
        ValidateMissingValueRules(profile.MissingValueRules, errors);

        return errors.Count == 0 ? ImportProfileValidationResult.Valid : new ImportProfileValidationResult(errors);
    }

    private static void ValidateIdentity(ImportProfile profile, List<ImportProfileValidationError> errors)
    {
        if (profile.Id == Guid.Empty)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidIdentity, nameof(ImportProfile.Id), "Profile Id must not be empty."));
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add(new(ImportProfileValidationCode.InvalidIdentity, nameof(ImportProfile.Name), "Profile Name is required."));
        }

        if (profile.SchemaVersion != ImportProfile.CurrentSchemaVersion)
        {
            errors.Add(new(
                ImportProfileValidationCode.UnsupportedSchemaVersion,
                nameof(ImportProfile.SchemaVersion),
                $"Profile schema version {profile.SchemaVersion} is not supported; expected {ImportProfile.CurrentSchemaVersion}."));
        }
    }

    private static void ValidateFilenameRules(FilenameRuleSet? rules, List<ImportProfileValidationError> errors)
    {
        if (rules is null)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, nameof(ImportProfile.FilenameRules), "Filename rules are required."));
            return;
        }

        if (rules.AcceptedExtensions is null || rules.AcceptedExtensions.Count == 0)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, nameof(FilenameRuleSet.AcceptedExtensions), "At least one accepted extension is required."));
        }
        else
        {
            var seenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in rules.AcceptedExtensions)
            {
                if (string.IsNullOrWhiteSpace(ext) || !ext.StartsWith('.') || ext.Length < 2
                    || ext.IndexOfAny(['/', '\\', '*', '?', ':', '<', '>', '|']) >= 0)
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, nameof(FilenameRuleSet.AcceptedExtensions), $"Invalid file extension '{ext}'."));
                }
                else if (!seenExtensions.Add(ext))
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, nameof(FilenameRuleSet.AcceptedExtensions), $"Duplicate file extension '{ext}'."));
                }
            }
        }

        if (!Enum.IsDefined(rules.Mode))
        {
            errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, nameof(FilenameRuleSet.Mode), "Invalid filename rule mode."));
            return;
        }

        if (rules.Mode == FilenameRuleMode.Regex)
        {
            if (string.IsNullOrWhiteSpace(rules.AdvancedRegexPattern))
            {
                errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, nameof(FilenameRuleSet.AdvancedRegexPattern), "Regex pattern is required when Mode is Regex."));
            }
            else
            {
                ValidateRegex(rules.AdvancedRegexPattern, nameof(FilenameRuleSet.AdvancedRegexPattern), errors);
            }

            return;
        }

        if (rules.SegmentDelimiters is null || rules.SegmentDelimiters.Count == 0)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, nameof(FilenameRuleSet.SegmentDelimiters), "At least one segment delimiter is required in Structured mode."));
        }

        ValidateFilenameField(rules.LotId, nameof(FilenameRuleSet.LotId), required: true, errors);
        ValidateFilenameField(rules.TestStage, nameof(FilenameRuleSet.TestStage), required: true, errors);
        ValidateFilenameField(rules.FilePart, nameof(FilenameRuleSet.FilePart), required: false, errors);

        if (rules.Retest is null)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, nameof(FilenameRuleSet.Retest), "Retest rule configuration is required."));
        }
        else
        {
            var hasRoleField = rules.Retest.RoleField is not null;
            var hasMarker = !string.IsNullOrEmpty(rules.Retest.RetestMarker) || !string.IsNullOrEmpty(rules.Retest.OriginalToken);
            if (!hasRoleField && !hasMarker)
            {
                errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, nameof(FilenameRuleSet.Retest), "Retest rule must specify a RoleField or Original/Retest marker."));
            }

            ValidateFilenameField(rules.Retest.RoleField, $"{nameof(FilenameRuleSet.Retest)}.{nameof(FilenameRetestRule.RoleField)}", required: false, errors);
            ValidateFilenameField(rules.Retest.RetestRoundField, $"{nameof(FilenameRuleSet.Retest)}.{nameof(FilenameRetestRule.RetestRoundField)}", required: false, errors);
        }
    }

    private static void ValidateFilenameField(
        FilenameFieldRule? field,
        string fieldName,
        bool required,
        List<ImportProfileValidationError> errors)
    {
        if (field is null)
        {
            if (required)
            {
                errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, fieldName, $"{fieldName} rule is required."));
            }

            return;
        }

        if (!Enum.IsDefined(field.Kind))
        {
            errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, fieldName, $"Invalid field kind for {fieldName}."));
            return;
        }

        if (field.Kind != FilenameFieldKind.RegexCapture && field.SegmentIndex < 0)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, fieldName, $"{fieldName} SegmentIndex must be >= 0."));
        }

        switch (field.Kind)
        {
            case FilenameFieldKind.MarkerBefore or FilenameFieldKind.MarkerAfter:
                if (string.IsNullOrEmpty(field.Marker))
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, fieldName, $"{fieldName} Marker is required."));
                }

                if (field.MarkerOccurrence < 1)
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, fieldName, $"{fieldName} MarkerOccurrence must be >= 1."));
                }
                break;

            case FilenameFieldKind.FixedRange when field.StartIndex is null or < 0 || field.Length is null or <= 0:
                errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, fieldName, $"{fieldName} FixedRange requires StartIndex >= 0 and Length > 0."));
                break;

            case FilenameFieldKind.RegexCapture:
                if (string.IsNullOrWhiteSpace(field.Pattern))
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, fieldName, $"{fieldName} Pattern is required."));
                }
                else
                {
                    ValidateRegex(field.Pattern, fieldName, errors);
                }
                break;
        }
    }

    private static void ValidateRegex(string pattern, string fieldName, List<ImportProfileValidationError> errors)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, RegexValidationTimeout);
        }
        catch (ArgumentException ex)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidFilenameRules, fieldName, $"Invalid regex in {fieldName}: {ex.Message}"));
        }
    }

    private static void ValidateStructureRules(DatalogStructureRuleSet? rules, List<ImportProfileValidationError> errors)
    {
        if (rules is null)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, nameof(ImportProfile.StructureRules), "Structure rules are required."));
            return;
        }

        if (string.IsNullOrEmpty(rules.Delimiter))
        {
            errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, nameof(DatalogStructureRuleSet.Delimiter), "Delimiter is required."));
        }

        var rowRoles = new List<(string Name, RowIdentifier? Row)>
        {
            (nameof(DatalogStructureRuleSet.HeaderRow), rules.HeaderRow),
            (nameof(DatalogStructureRuleSet.UnitRow), rules.UnitRow),
            (nameof(DatalogStructureRuleSet.TargetRow), rules.TargetRow),
            (nameof(DatalogStructureRuleSet.LslRow), rules.LslRow),
            (nameof(DatalogStructureRuleSet.UslRow), rules.UslRow),
            (nameof(DatalogStructureRuleSet.DataStartRow), rules.DataStartRow)
        };

        var validRows = new List<(string Name, RowIdentifier Row)>();
        foreach (var (name, row) in rowRoles)
        {
            var required = name is nameof(DatalogStructureRuleSet.HeaderRow) or nameof(DatalogStructureRuleSet.DataStartRow);
            if (ValidateRowIdentifier(row, name, required, errors) && row is not null)
            {
                validRows.Add((name, row));
            }
        }

        // Fixed row index conflicts & ordering.
        var fixedRows = new Dictionary<int, string>();
        foreach (var (name, row) in validRows)
        {
            if (row.Kind == RowMatchKind.FixedRowIndex && row.RowNumber is { } num)
            {
                if (!fixedRows.TryAdd(num, name))
                {
                    errors.Add(new(
                        ImportProfileValidationCode.ConflictingRowRules,
                        name,
                        $"{name} cannot use the same fixed row number ({num}) as {fixedRows[num]}."));
                }
            }
        }

        if (rules.HeaderRow is { Kind: RowMatchKind.FixedRowIndex, RowNumber: { } headerNum }
            && rules.DataStartRow is { Kind: RowMatchKind.FixedRowIndex, RowNumber: { } dataStartNum }
            && dataStartNum <= headerNum)
        {
            errors.Add(new(
                ImportProfileValidationCode.ConflictingRowRules,
                nameof(DatalogStructureRuleSet.DataStartRow),
                $"DataStartRow ({dataStartNum}) must be after HeaderRow ({headerNum})."));
        }

        foreach (var (name, row) in validRows)
        {
            if (name is nameof(DatalogStructureRuleSet.HeaderRow) or nameof(DatalogStructureRuleSet.DataStartRow))
            {
                continue;
            }

            if (row.Kind == RowMatchKind.FixedRowIndex && row.RowNumber is { } metaNum)
            {
                if (rules.HeaderRow is { Kind: RowMatchKind.FixedRowIndex, RowNumber: { } hNum } && metaNum <= hNum)
                {
                    errors.Add(new(ImportProfileValidationCode.ConflictingRowRules, name, $"{name} ({metaNum}) must be after HeaderRow ({hNum})."));
                }

                if (rules.DataStartRow is { Kind: RowMatchKind.FixedRowIndex, RowNumber: { } dNum } && metaNum >= dNum)
                {
                    errors.Add(new(ImportProfileValidationCode.ConflictingRowRules, name, $"{name} ({metaNum}) must be before DataStartRow ({dNum})."));
                }
            }
        }

        // Identical keyword row identifiers across distinct roles.
        var keywordRows = new Dictionary<(string Keyword, int Col, KeywordMatchMode Mode), string>();
        foreach (var (name, row) in validRows)
        {
            if (row.Kind == RowMatchKind.KeywordMatch && row.Keyword is not null)
            {
                var key = (row.Keyword, row.ColumnIndex, row.MatchMode);
                if (!keywordRows.TryAdd(key, name))
                {
                    errors.Add(new(
                        ImportProfileValidationCode.ConflictingRowRules,
                        name,
                        $"{name} uses the same keyword rule as {keywordRows[key]}."));
                }
            }
        }

        // Column selectors.
        var binValid = ValidateColumnSelector(rules.BinColumn, nameof(DatalogStructureRuleSet.BinColumn), required: true, errors);
        var siteValid = ValidateColumnSelector(rules.SiteColumn, nameof(DatalogStructureRuleSet.SiteColumn), required: false, errors);

        if (binValid && siteValid && rules.SiteColumn is not null && ColumnsConflict(rules.BinColumn, rules.SiteColumn))
        {
            errors.Add(new(ImportProfileValidationCode.ConflictingColumnRules, nameof(DatalogStructureRuleSet.SiteColumn), "SiteColumn cannot be the same as BinColumn."));
        }

        if (rules.IgnoredColumns is null)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidColumnSelector, nameof(DatalogStructureRuleSet.IgnoredColumns), "IgnoredColumns must not be null."));
        }
        else
        {
            var seenIgnored = new List<ColumnSelector>();
            for (var i = 0; i < rules.IgnoredColumns.Count; i++)
            {
                var ignored = rules.IgnoredColumns[i];
                var field = $"{nameof(DatalogStructureRuleSet.IgnoredColumns)}[{i}]";
                if (!ValidateColumnSelector(ignored, field, required: true, errors) || ignored is null)
                {
                    continue;
                }

                if (binValid && ColumnsConflict(rules.BinColumn, ignored))
                {
                    errors.Add(new(ImportProfileValidationCode.ConflictingColumnRules, field, "BinColumn cannot be in IgnoredColumns."));
                }

                if (siteValid && rules.SiteColumn is not null && ColumnsConflict(rules.SiteColumn, ignored))
                {
                    errors.Add(new(ImportProfileValidationCode.ConflictingColumnRules, field, "SiteColumn cannot be in IgnoredColumns."));
                }

                if (seenIgnored.Any(prev => ColumnsConflict(prev, ignored)))
                {
                    errors.Add(new(ImportProfileValidationCode.ConflictingColumnRules, field, "Duplicate entry in IgnoredColumns."));
                }
                else
                {
                    seenIgnored.Add(ignored);
                }
            }
        }

        if (rules.CompositeFields is not null)
        {
            for (var i = 0; i < rules.CompositeFields.Count; i++)
            {
                var composite = rules.CompositeFields[i];
                var field = $"{nameof(DatalogStructureRuleSet.CompositeFields)}[{i}]";
                if (composite is null)
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, field, "Composite field rule must not be null."));
                    continue;
                }

                ValidateColumnSelector(composite.Column, $"{field}.Column", required: true, errors);
                if (string.IsNullOrEmpty(composite.Separator))
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, $"{field}.Separator", "Composite field separator is required."));
                }

                if (composite.OutputNames is null || composite.OutputNames.Count < 2 || composite.OutputNames.Any(string.IsNullOrWhiteSpace))
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, $"{field}.OutputNames", "Composite field must define at least 2 non-empty output names."));
                }
            }
        }
    }

    private static bool ValidateRowIdentifier(
        RowIdentifier? row,
        string fieldName,
        bool required,
        List<ImportProfileValidationError> errors)
    {
        if (row is null)
        {
            if (required)
            {
                errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, fieldName, $"{fieldName} is required."));
            }

            return false;
        }

        if (!Enum.IsDefined(row.Kind))
        {
            errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, fieldName, $"Invalid row match kind for {fieldName}."));
            return false;
        }

        if (row.Kind == RowMatchKind.FixedRowIndex)
        {
            if (row.RowNumber is null or < 1)
            {
                errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, fieldName, $"{fieldName} RowNumber must be >= 1."));
                return false;
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(row.Keyword))
        {
            errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, fieldName, $"{fieldName} Keyword is required."));
            return false;
        }

        if (row.ColumnIndex < -1)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, fieldName, $"{fieldName} ColumnIndex must be >= -1."));
            return false;
        }

        if (!Enum.IsDefined(row.MatchMode))
        {
            errors.Add(new(ImportProfileValidationCode.InvalidStructureRules, fieldName, $"Invalid keyword match mode for {fieldName}."));
            return false;
        }

        return true;
    }

    private static bool ValidateColumnSelector(
        ColumnSelector? selector,
        string fieldName,
        bool required,
        List<ImportProfileValidationError> errors)
    {
        if (selector is null)
        {
            if (required)
            {
                errors.Add(new(ImportProfileValidationCode.InvalidColumnSelector, fieldName, $"{fieldName} is required."));
            }

            return false;
        }

        if (!Enum.IsDefined(selector.Kind))
        {
            errors.Add(new(ImportProfileValidationCode.InvalidColumnSelector, fieldName, $"Invalid selector kind for {fieldName}."));
            return false;
        }

        if (selector.Kind == ColumnSelectorKind.Index)
        {
            if (selector.ColumnIndex is null or < 0)
            {
                errors.Add(new(ImportProfileValidationCode.InvalidColumnSelector, fieldName, $"{fieldName} ColumnIndex must be >= 0."));
                return false;
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(selector.HeaderName))
        {
            errors.Add(new(ImportProfileValidationCode.InvalidColumnSelector, fieldName, $"{fieldName} HeaderName is required."));
            return false;
        }

        return true;
    }

    private static bool ColumnsConflict(ColumnSelector left, ColumnSelector right) =>
        (left.Kind, right.Kind) switch
        {
            (ColumnSelectorKind.Index, ColumnSelectorKind.Index) => left.ColumnIndex == right.ColumnIndex,
            (ColumnSelectorKind.HeaderName, ColumnSelectorKind.HeaderName) =>
                string.Equals(left.HeaderName, right.HeaderName, StringComparison.Ordinal),
            _ => false
        };

    private static void ValidateBinRules(BinRuleSet? rules, List<ImportProfileValidationError> errors)
    {
        if (rules is null)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidBinRules, nameof(ImportProfile.BinRules), "Bin rules are required."));
            return;
        }

        var goodSet = new HashSet<string>(StringComparer.Ordinal);
        if (rules.GoodBins is null || rules.GoodBins.Count == 0)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidBinRules, nameof(BinRuleSet.GoodBins), "At least one Good Bin is required."));
        }
        else
        {
            foreach (var bin in rules.GoodBins)
            {
                if (string.IsNullOrWhiteSpace(bin))
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidBinRules, nameof(BinRuleSet.GoodBins), "GoodBins must not contain empty or whitespace entries."));
                }
                else if (!goodSet.Add(bin))
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidBinRules, nameof(BinRuleSet.GoodBins), $"Duplicate Good Bin '{bin}'."));
                }
            }
        }

        var failSet = new HashSet<string>(StringComparer.Ordinal);
        if (rules.NonRetestFailBins is null)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidBinRules, nameof(BinRuleSet.NonRetestFailBins), "NonRetestFailBins must not be null."));
        }
        else
        {
            foreach (var bin in rules.NonRetestFailBins)
            {
                if (string.IsNullOrWhiteSpace(bin))
                {
                    errors.Add(new(ImportProfileValidationCode.InvalidBinRules, nameof(BinRuleSet.NonRetestFailBins), "NonRetestFailBins must not contain empty or whitespace entries."));
                }
                else
                {
                    if (!failSet.Add(bin))
                    {
                        errors.Add(new(ImportProfileValidationCode.InvalidBinRules, nameof(BinRuleSet.NonRetestFailBins), $"Duplicate Non-Retest Fail Bin '{bin}'."));
                    }

                    if (goodSet.Contains(bin))
                    {
                        errors.Add(new(ImportProfileValidationCode.ConflictingBinRules, nameof(BinRuleSet.NonRetestFailBins), $"Bin '{bin}' cannot be in both GoodBins and NonRetestFailBins."));
                    }
                }
            }
        }
    }

    private static void ValidateMissingValueRules(MissingValueRuleSet? rules, List<ImportProfileValidationError> errors)
    {
        if (rules is null)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidMissingValueRules, nameof(ImportProfile.MissingValueRules), "Missing-value rules are required."));
            return;
        }

        if (!Enum.IsDefined(rules.Policy))
        {
            errors.Add(new(ImportProfileValidationCode.InvalidMissingValueRules, nameof(MissingValueRuleSet.Policy), "Invalid missing-value policy."));
        }

        if (rules.Markers is null)
        {
            errors.Add(new(ImportProfileValidationCode.InvalidMissingValueRules, nameof(MissingValueRuleSet.Markers), "Missing-value markers must not be null."));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var marker in rules.Markers)
        {
            if (marker is null)
            {
                errors.Add(new(ImportProfileValidationCode.InvalidMissingValueRules, nameof(MissingValueRuleSet.Markers), "Missing-value markers must not contain null."));
            }
            else if (!seen.Add(marker))
            {
                errors.Add(new(ImportProfileValidationCode.InvalidMissingValueRules, nameof(MissingValueRuleSet.Markers), $"Duplicate missing-value marker '{marker}'."));
            }
        }
    }
}
