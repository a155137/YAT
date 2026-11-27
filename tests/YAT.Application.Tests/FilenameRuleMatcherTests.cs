using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;

namespace YAT.Application.Tests;

public sealed class FilenameRuleMatcherTests
{
    private readonly FilenameRuleMatcher _matcher = new();

    // Representative Structured Profile resolving all three OSAT filenames without Regex.
    public static ImportProfile CreateStructuredRepresentativeProfile() => new()
    {
        Id = Guid.Parse("22222222-3333-4444-5555-666666666666"),
        Name = "OSAT_FT_Structured",
        SchemaVersion = ImportProfile.CurrentSchemaVersion,
        FilenameRules = new FilenameRuleSet
        {
            AcceptedExtensions = [".Reg.zip", ".csv"],
            Mode = FilenameRuleMode.Structured,
            SegmentDelimiters = ['_'],
            LotId = new FilenameFieldRule
            {
                Kind = FilenameFieldKind.MarkerAfter,
                SegmentIndex = 0,
                Marker = ".",
                MarkerOccurrence = 2
            },
            TestStage = new FilenameFieldRule
            {
                Kind = FilenameFieldKind.FixedRange,
                SegmentIndex = 2,
                StartIndex = 0,
                Length = 2
            },
            Retest = new FilenameRetestRule
            {
                RoleField = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 2 },
                RetestMarker = "R",
                RetestRoundField = new FilenameFieldRule
                {
                    Kind = FilenameFieldKind.MarkerAfter,
                    SegmentIndex = 2,
                    Marker = "R",
                    MarkerOccurrence = 1
                }
            },
            FilePart = new FilenameFieldRule
            {
                Kind = FilenameFieldKind.WholeSegment,
                SegmentIndex = 3
            }
        },
        StructureRules = new DatalogStructureRuleSet
        {
            Delimiter = ",",
            HeaderRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 1 },
            DataStartRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 2 },
            BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "BIN" }
        },
        BinRules = new BinRuleSet
        {
            GoodBins = ["1", "01"],
            NonRetestFailBins = ["2"]
        },
        MissingValueRules = new MissingValueRuleSet
        {
            Markers = ["", "N/A"],
            Policy = MissingValuePolicy.UseMissing
        }
    };

    [Theory]
    [InlineData(
        "E320BD-262.20261123111219.XXX123.1-6846_TQB01RXAA1_F1_1_20261122171516_20261123110911.Reg.zip",
        "XXX123.1-6846", "F1", FtTestRole.Original, null, 1)]
    [InlineData(
        "E320BD-262.20261124094509.XXX123.1-6846_TQB01RXAA1_F1_2_20261123111608_20261124094143.Reg.zip",
        "XXX123.1-6846", "F1", FtTestRole.Original, null, 2)]
    [InlineData(
        "E320BD-262.20261124104655.XXX123.1-6846_TQB01RXAA1_F1B45678R1_1_20261124095542_20261124104352.Reg.zip",
        "XXX123.1-6846", "F1", FtTestRole.Retest, 1, 1)]
    [InlineData(
        "E320BD-262.20261124104655.XXX123.1-6846_TQB01RXAA1_F1B45678R12_3_20261124095542_20261124104352.Reg.zip",
        "XXX123.1-6846", "F1", FtTestRole.Retest, 12, 3)]
    public void StructuredModeResolvesRepresentativeAndMultiRoundFilenames(
        string fileName,
        string expectedLot,
        string expectedStage,
        FtTestRole expectedRole,
        int? expectedRound,
        int? expectedPart)
    {
        var profile = CreateStructuredRepresentativeProfile();
        var result = _matcher.Match(profile, $@"C:\Datalogs\{fileName}");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.NotNull(result.Value);
        Assert.Equal(".Reg.zip", result.Value.MatchedExtension);
        Assert.Equal(expectedLot, result.Value.LotId);
        Assert.Equal(expectedStage, result.Value.TestStage);
        Assert.Equal(expectedRole, result.Value.TestRole);
        Assert.Equal(expectedRound, result.Value.RetestRound);
        Assert.Equal(expectedPart, result.Value.FilePart);
    }

    [Fact]
    public void CompoundExtensionWinsOverShorterExtension()
    {
        var baseProfile = CreateStructuredRepresentativeProfile();
        var profile = baseProfile with
        {
            FilenameRules = baseProfile.FilenameRules with
            {
                AcceptedExtensions = [".zip", ".Reg.zip"]
            }
        };

        var result = _matcher.Match(profile, "A.B.LOT1_DEV_F1_1.Reg.zip");

        Assert.True(result.IsSuccess);
        Assert.Equal(".Reg.zip", result.Value!.MatchedExtension);
        Assert.Equal(1, result.Value.FilePart);
    }

    [Fact]
    public void InvalidUnmatchedAndOverflowFilenamesAreRejected()
    {
        var profile = CreateStructuredRepresentativeProfile();

        // Wrong extension
        var wrongExt = _matcher.Match(profile, "A.B.LOT1_DEV_F1_1.txt");
        Assert.False(wrongExt.IsSuccess);
        Assert.Equal(FilenameMatchErrorCode.ExtensionNotAccepted, wrongExt.ErrorCode);

        // Missing segments
        var shortName = _matcher.Match(profile, "A.B.LOT1_DEV.Reg.zip");
        Assert.False(shortName.IsSuccess);
        Assert.Equal(FilenameMatchErrorCode.SegmentOutOfRange, shortName.ErrorCode);

        // Overflow FilePart
        var overflow = _matcher.Match(profile, "A.B.LOT1_DEV_F1_99999999999999999999.Reg.zip");
        Assert.False(overflow.IsSuccess);
        Assert.Equal(FilenameMatchErrorCode.InvalidNumericField, overflow.ErrorCode);

        // Explicit OriginalToken mismatch (must not silently classify as Original)
        var strictOriginal = profile with
        {
            FilenameRules = profile.FilenameRules with
            {
                Retest = profile.FilenameRules.Retest with { OriginalToken = "F1" }
            }
        };
        var unmatchedRole = _matcher.Match(strictOriginal, "A.B.LOT1_DEV_XX_1.Reg.zip");
        Assert.False(unmatchedRole.IsSuccess);
        Assert.Equal(FilenameMatchErrorCode.UnmatchedRetestRule, unmatchedRole.ErrorCode);
    }

    [Fact]
    public void AmbiguousOriginalAndRetestTokensAreRejected()
    {
        var baseProfile = CreateStructuredRepresentativeProfile();
        var ambiguous = baseProfile with
        {
            FilenameRules = baseProfile.FilenameRules with
            {
                Retest = new FilenameRetestRule
                {
                    RoleField = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 2 },
                    OriginalToken = "FR1",
                    RetestMarker = "R"
                }
            }
        };

        var result = _matcher.Match(ambiguous, "A.B.LOT1_DEV_FR1_1.Reg.zip");
        Assert.False(result.IsSuccess);
        Assert.Equal(FilenameMatchErrorCode.AmbiguousMatch, result.ErrorCode);
    }

    [Fact]
    public void AdvancedRegexModeAndTimeoutWork()
    {
        var baseProfile = CreateStructuredRepresentativeProfile();
        var regexProfile = baseProfile with
        {
            FilenameRules = baseProfile.FilenameRules with
            {
                Mode = FilenameRuleMode.Regex,
                AdvancedRegexPattern = @"^[^.]+\.[^.]+\.(?<lot_id>[^_]+)_[^_]+_(?<test_stage>F\d+)(?:[^R]*R(?<retest_round>\d+))?_(?<file_part>\d+)(?:_\d+)*\.Reg\.zip$"
            }
        };

        var f1 = _matcher.Match(regexProfile, "E320BD-262.20261123111219.XXX123.1-6846_TQB01RXAA1_F1_1_20261122171516_20261123110911.Reg.zip");
        Assert.True(f1.IsSuccess);
        Assert.Equal("XXX123.1-6846", f1.Value!.LotId);
        Assert.Equal(FtTestRole.Original, f1.Value.TestRole);

        var f3 = _matcher.Match(regexProfile, "E320BD-262.20261124104655.XXX123.1-6846_TQB01RXAA1_F1B45678R1_1_20261124095542_20261124104352.Reg.zip");
        Assert.True(f3.IsSuccess);
        Assert.Equal(FtTestRole.Retest, f3.Value!.TestRole);
        Assert.Equal(1, f3.Value.RetestRound);

        // Catastrophic backtracking regex with 1ms timeout -> RegexTimeout
        var slowMatcher = new FilenameRuleMatcher(TimeSpan.FromMilliseconds(1));
        var slowProfile = baseProfile with
        {
            FilenameRules = baseProfile.FilenameRules with
            {
                Mode = FilenameRuleMode.Regex,
                AdvancedRegexPattern = @"^(?<lot_id>(a+)+)_(?<test_stage>b)$"
            }
        };

        var timeoutResult = slowMatcher.Match(slowProfile, new string('a', 35) + "_c.csv");
        Assert.False(timeoutResult.IsSuccess);
        Assert.Equal(FilenameMatchErrorCode.RegexTimeout, timeoutResult.ErrorCode);
    }
}
