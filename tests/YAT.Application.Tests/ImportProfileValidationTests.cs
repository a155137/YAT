using YAT.Application.ImportProfiles;

namespace YAT.Application.Tests;

public sealed class ImportProfileValidationTests
{
    private readonly ImportProfileValidator _validator = new();

    internal static ImportProfile CreateRepresentativeProfile(
        Guid? id = null,
        string name = "OSAT_FT_Sample",
        MissingValuePolicy missingPolicy = MissingValuePolicy.UseMissing,
        ColumnSelector? siteColumn = null) => new()
    {
        Id = id ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Name = name,
        SchemaVersion = ImportProfile.CurrentSchemaVersion,
        FilenameRules = new FilenameRuleSet
        {
            AcceptedExtensions = [".Reg.zip", ".csv"],
            Mode = FilenameRuleMode.Structured,
            SegmentDelimiters = ['.', '_'],
            LotId = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 4 },
            TestStage = new FilenameFieldRule { Kind = FilenameFieldKind.FixedRange, SegmentIndex = 5, StartIndex = 0, Length = 2 },
            Retest = new FilenameRetestRule
            {
                RetestMarker = "R",
                RetestRoundField = new FilenameFieldRule { Kind = FilenameFieldKind.MarkerAfter, SegmentIndex = 5, Marker = "R" }
            },
            FilePart = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 6 }
        },
        StructureRules = new DatalogStructureRuleSet
        {
            Delimiter = ",",
            CompositeFields =
            [
                new CompositeFieldRule(
                    new ColumnSelector { Kind = ColumnSelectorKind.Index, ColumnIndex = 0 },
                    "/",
                    ["Lot_Part", "Wafer"])
            ],
            HeaderRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Test_Name", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            UnitRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Unit", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            TargetRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Target", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            LslRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "LSL", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            UslRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "USL", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            DataStartRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 10 },
            BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "HARD_BIN" },
            SiteColumn = siteColumn,
            IgnoredColumns =
            [
                new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "TEST_TIME" }
            ]
        },
        BinRules = new BinRuleSet
        {
            GoodBins = ["1", "01", "001"],
            NonRetestFailBins = ["2", "02", "99"]
        },
        MissingValueRules = new MissingValueRuleSet
        {
            Markers = ["", "N/A", "NULL", "*****"],
            Policy = missingPolicy
        }
    };

    [Fact]
    public void RepresentativeProfileIsValidAndPreservesLeadingZeroBins()
    {
        var profile = CreateRepresentativeProfile(
            siteColumn: new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" });

        var result = _validator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Equal(["1", "01", "001"], profile.BinRules.GoodBins);
        Assert.Equal(["2", "02", "99"], profile.BinRules.NonRetestFailBins);
    }

    [Theory]
    [InlineData(MissingValuePolicy.StopImport)]
    [InlineData(MissingValuePolicy.UseMissing)]
    [InlineData(MissingValuePolicy.ReplaceWithZero)]
    public void AllThreeMissingValuePoliciesAreValid(MissingValuePolicy policy)
    {
        var profile = CreateRepresentativeProfile(missingPolicy: policy);
        Assert.True(_validator.Validate(profile).IsValid);
    }

    [Fact]
    public void OptionalSiteColumnMayBeNull()
    {
        var profile = CreateRepresentativeProfile(siteColumn: null);
        Assert.True(_validator.Validate(profile).IsValid);
    }

    [Fact]
    public void MissingRequiredFieldsProduceActionableErrors()
    {
        var profile = CreateRepresentativeProfile() with
        {
            Id = Guid.Empty,
            Name = "   ",
            FilenameRules = CreateRepresentativeProfile().FilenameRules with { AcceptedExtensions = [] },
            StructureRules = CreateRepresentativeProfile().StructureRules with
            {
                Delimiter = string.Empty,
                HeaderRow = null!,
                DataStartRow = null!,
                BinColumn = null!
            },
            BinRules = new BinRuleSet { GoodBins = [], NonRetestFailBins = [] }
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == ImportProfileValidationCode.InvalidIdentity && e.Field == nameof(ImportProfile.Id));
        Assert.Contains(result.Errors, e => e.Code == ImportProfileValidationCode.InvalidIdentity && e.Field == nameof(ImportProfile.Name));
        Assert.Contains(result.Errors, e => e.Code == ImportProfileValidationCode.InvalidFilenameRules);
        Assert.Contains(result.Errors, e => e.Code == ImportProfileValidationCode.InvalidStructureRules);
        Assert.Contains(result.Errors, e => e.Code == ImportProfileValidationCode.InvalidColumnSelector);
        Assert.Contains(result.Errors, e => e.Code == ImportProfileValidationCode.InvalidBinRules);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void InvalidSchemaVersionIsRejected(int schemaVersion)
    {
        var profile = CreateRepresentativeProfile() with { SchemaVersion = schemaVersion };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == ImportProfileValidationCode.UnsupportedSchemaVersion);
    }

    [Fact]
    public void ConflictingRowAndColumnRulesAreRejected()
    {
        var baseProfile = CreateRepresentativeProfile();
        var profile = baseProfile with
        {
            StructureRules = baseProfile.StructureRules with
            {
                HeaderRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 5 },
                UnitRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 5 },
                DataStartRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 4 },
                BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "BIN" },
                SiteColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "BIN" },
                IgnoredColumns = [new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "BIN" }]
            }
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == ImportProfileValidationCode.ConflictingRowRules);
        Assert.Contains(result.Errors, e => e.Code == ImportProfileValidationCode.ConflictingColumnRules);
    }

    [Fact]
    public void OverlappingGoodAndFailBinsAreRejectedWhileDistinctLeadingZerosAreAllowed()
    {
        var profile = CreateRepresentativeProfile() with
        {
            BinRules = new BinRuleSet
            {
                GoodBins = ["01"],
                NonRetestFailBins = ["01", "1"]
            }
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        var conflict = Assert.Single(result.Errors, e => e.Code == ImportProfileValidationCode.ConflictingBinRules);
        Assert.Contains("01", conflict.Message);
    }
}
