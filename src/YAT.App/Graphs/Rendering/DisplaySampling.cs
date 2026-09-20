namespace YAT.app.Graphs.Rendering;

// How many of a graph's points are actually drawn, and which ones.
//
// Some graphs have far more observations than a screen can show one by one. They draw a deterministic sample instead:
// never more than the cap in total, every group still represented while the budget allows it, and the first and last
// observation of every series kept - index arithmetic only, so the same data always draws the same points at every
// window size, on every redraw and in every theme.
//
// This is a display policy, not a statistical one: whatever a graph computes from its observations is computed from all
// of them, before any of this.
public static class DisplaySampling
{
    // The most points a graph draws. A hard cap: the quotas below always add up to at most this.
    public const int DefaultMaximumRenderedPoints = 100_000;

    // How many points each series may draw, spending a fixed budget: as even a share of it as the series sizes allow.
    // counts: how many points each series has, in the order the series are drawn.
    public static int[] Quotas(IReadOnlyList<int> counts, int pointCount, int maximumRenderedPoints)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRenderedPoints, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(pointCount);

        var quotas = new int[counts.Count];

        if (pointCount <= maximumRenderedPoints)
        {
            for (var index = 0; index < counts.Count; index++)
            {
                quotas[index] = counts[index];
            }

            return quotas;
        }

        // More groups than the cap allows points: a categorical case no graph can show anyway. The first groups in
        // first-observed order get one point each, and the cap still holds.
        if (counts.Count >= maximumRenderedPoints)
        {
            for (var index = 0; index < maximumRenderedPoints; index++)
            {
                quotas[index] = 1;
            }

            return quotas;
        }

        // Every series keeps one point first, so no group disappears; the rest of the budget follows the sizes of the
        // series, and what rounding leaves over goes to the largest remainders (ties in first-observed order).
        var budget = maximumRenderedPoints - counts.Count;
        var pool = pointCount - counts.Count;
        var remainders = new (double Fraction, int Index)[counts.Count];
        var granted = 0;

        for (var index = 0; index < counts.Count; index++)
        {
            var exact = budget * (double)(counts[index] - 1) / pool;
            var whole = (int)exact;
            quotas[index] = 1 + whole;
            granted += whole;
            remainders[index] = (exact - whole, index);
        }

        var leftover = budget - granted;
        if (leftover > 0)
        {
            Array.Sort(remainders, (first, second) =>
            {
                var byFraction = second.Fraction.CompareTo(first.Fraction);
                return byFraction != 0 ? byFraction : first.Index.CompareTo(second.Index);
            });

            foreach (var (_, index) in remainders)
            {
                if (leftover == 0)
                {
                    break;
                }

                if (quotas[index] >= counts[index])
                {
                    continue;
                }

                quotas[index]++;
                leftover--;
            }
        }

        return quotas;
    }

    // Which point of a series the index-th drawn point is: evenly spread over the series, keeping its first and its
    // last. A series drawing all of its points asks for index itself.
    public static int SampleIndex(int index, int count, int quota)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(quota, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quota, count);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, quota);

        return quota == 1 ? 0 : (int)((long)index * (count - 1) / (quota - 1));
    }
}
