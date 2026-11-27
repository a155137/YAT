using System.Collections.Frozen;
using YAT.Application.ImportProfiles;

namespace YAT.Application.FtImport;

public enum FtBinRetentionMode
{
    RetainAllRows,
    RetainGoodAndNonRetestFailBinsOnly
}

// Immutable per-source FT/RT Bin retention policy with a reusable, zero-allocation DatalogRowParser predicate.
// Uses exact ordinal string matching (preserving leading zeros and casing; never converts Bin values to numbers).
public sealed class FtSourceBinRetentionPolicy
{
    private readonly FrozenSet<string>? _retainedBins;

    internal FtSourceBinRetentionPolicy(
        FtBinRetentionMode mode,
        IReadOnlyList<string> retainedBins,
        FrozenSet<string>? retainedBinLookup,
        Func<string, bool>? includeBinPredicate)
    {
        Mode = mode;
        RetainedBins = retainedBins;
        _retainedBins = retainedBinLookup;
        IncludeBinPredicate = includeBinPredicate;
    }

    public FtBinRetentionMode Mode { get; }

    // Empty when Mode == RetainAllRows; otherwise the exact ordinal union of GoodBins and NonRetestFailBins.
    public IReadOnlyList<string> RetainedBins { get; }

    // Reusable predicate compatible with DatalogRowParser.ParseFile / ParseReader (null when all valid rows are retained).
    public Func<string, bool>? IncludeBinPredicate { get; }

    // Evaluates whether a Bin value is retained under this policy.
    // Empty/whitespace Bin values are always rejected (consistent with DatalogRowParser's MissingRequiredBin rule).
    public bool ShouldRetainBin(string? binValue)
    {
        if (string.IsNullOrWhiteSpace(binValue))
        {
            return false;
        }

        if (Mode == FtBinRetentionMode.RetainAllRows)
        {
            return true;
        }

        return _retainedBins!.Contains(binValue);
    }

    public static FtSourceBinRetentionPolicy CreateRetainAll() =>
        new(FtBinRetentionMode.RetainAllRows, Array.AsReadOnly(Array.Empty<string>()), null, null);

    public static FtSourceBinRetentionPolicy CreateFiltered(BinRuleSet binRules)
    {
        ArgumentNullException.ThrowIfNull(binRules);
        var orderedBins = new List<string>(
            (binRules.GoodBins?.Count ?? 0) + (binRules.NonRetestFailBins?.Count ?? 0));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (binRules.GoodBins is not null)
        {
            foreach (var bin in binRules.GoodBins)
            {
                if (!string.IsNullOrWhiteSpace(bin) && seen.Add(bin))
                {
                    orderedBins.Add(bin);
                }
            }
        }

        if (binRules.NonRetestFailBins is not null)
        {
            foreach (var bin in binRules.NonRetestFailBins)
            {
                if (!string.IsNullOrWhiteSpace(bin) && seen.Add(bin))
                {
                    orderedBins.Add(bin);
                }
            }
        }

        var frozen = seen.ToFrozenSet(StringComparer.Ordinal);
        return new FtSourceBinRetentionPolicy(
            FtBinRetentionMode.RetainGoodAndNonRetestFailBinsOnly,
            Array.AsReadOnly(orderedBins.ToArray()),
            frozen,
            frozen.Contains);
    }
}

// Resolves deterministic per-source FT/RT Bin retention policies independently per Lot and Test Stage.
public static class FtBinRetentionPolicyResolver
{
    public static FtSourceBinRetentionPolicy ResolveForFile(
        FtPlannedLot lot,
        FtPlannedSourceFile file,
        FtSourceBinRetentionPolicy retainAllPolicy,
        FtSourceBinRetentionPolicy filteredPolicy)
    {
        ArgumentNullException.ThrowIfNull(lot);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(retainAllPolicy);
        ArgumentNullException.ThrowIfNull(filteredPolicy);

        if (!string.Equals(lot.LotId, file.LotId, StringComparison.Ordinal)
            || !string.Equals(lot.TestStage, file.TestStage, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"File '{file.FileName}' (Lot '{file.LotId}', Stage '{file.TestStage}') does not belong to Lot '{lot.LotId}' ({lot.TestStage}).",
                nameof(file));
        }

        return lot.LatestRetestKind switch
        {
            FtLatestRetestKind.None => file.TestRole == FtTestRole.Original
                ? retainAllPolicy
                : throw new InvalidOperationException(
                    $"Lot '{lot.LotId}' ({lot.TestStage}) declares LatestRetestKind=None but contains Retest file '{file.FileName}'."),

            FtLatestRetestKind.NumberedRound => ResolveNumberedRound(lot, file, retainAllPolicy, filteredPolicy),

            FtLatestRetestKind.UnnumberedGroup => ResolveUnnumberedGroup(lot, file, retainAllPolicy, filteredPolicy),

            _ => throw new ArgumentOutOfRangeException(nameof(lot), $"Unsupported FtLatestRetestKind '{lot.LatestRetestKind}'.")
        };
    }

    public static FtSourceBinRetentionPolicy ResolveForFile(
        FtPlannedLot lot,
        FtPlannedSourceFile file,
        BinRuleSet binRules)
    {
        ArgumentNullException.ThrowIfNull(binRules);
        var retainAll = FtSourceBinRetentionPolicy.CreateRetainAll();
        var filtered = FtSourceBinRetentionPolicy.CreateFiltered(binRules);
        return ResolveForFile(lot, file, retainAll, filtered);
    }

    private static FtSourceBinRetentionPolicy ResolveNumberedRound(
        FtPlannedLot lot,
        FtPlannedSourceFile file,
        FtSourceBinRetentionPolicy retainAllPolicy,
        FtSourceBinRetentionPolicy filteredPolicy)
    {
        if (!lot.LatestSelectedRetestRound.HasValue)
        {
            throw new InvalidOperationException(
                $"Lot '{lot.LotId}' ({lot.TestStage}) declares LatestRetestKind=NumberedRound but LatestSelectedRetestRound is null.");
        }

        if (file.TestRole == FtTestRole.Original)
        {
            return filteredPolicy;
        }

        if (!file.RetestRound.HasValue)
        {
            throw new InvalidOperationException(
                $"Lot '{lot.LotId}' ({lot.TestStage}) mixes numbered Retest rounds with unnumbered Retest file '{file.FileName}'.");
        }

        return file.RetestRound.Value == lot.LatestSelectedRetestRound.Value
            ? retainAllPolicy
            : filteredPolicy;
    }

    private static FtSourceBinRetentionPolicy ResolveUnnumberedGroup(
        FtPlannedLot lot,
        FtPlannedSourceFile file,
        FtSourceBinRetentionPolicy retainAllPolicy,
        FtSourceBinRetentionPolicy filteredPolicy)
    {
        if (file.TestRole == FtTestRole.Original)
        {
            return filteredPolicy;
        }

        if (file.RetestRound.HasValue)
        {
            throw new InvalidOperationException(
                $"Lot '{lot.LotId}' ({lot.TestStage}) mixes unnumbered Retest group with numbered Retest file '{file.FileName}' (R{file.RetestRound.Value}).");
        }

        return retainAllPolicy;
    }
}
