using YAT.Application.Abstractions.Persistence;

namespace YAT.Application.Filtering;

// Applies a row filter to the blocks of worksheet rows a graph or analysis reads (Task #053): the one place a filter is
// evaluated, so every graph type, Descriptive Statistics and Capability Analysis keep exactly the same rows.
//
// The filter's columns are read in the same aligned blocks as the reader's own columns (one request per distinct
// column); the reader says where each of them is in a block, or that it is not read at all - a column without stored
// values, which has no value in any row. For each block, Bind looks the columns up once; Keeps then tests one row,
// condition by condition, and stops at the first that fails (AND). Nothing is allocated per row.
public sealed class RowFilterEvaluator
{
    private readonly ConditionTest[] _tests;

    private RowFilterEvaluator(ConditionTest[] tests)
    {
        _tests = tests;
    }

    // The filter over blocks in which its columns are where positionOf says (-1: not read).
    public static RowFilterEvaluator Create(RowFilter filter, Func<Guid, int> positionOf)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(positionOf);

        return new RowFilterEvaluator([.. filter.Conditions.Select(condition => ConditionTest.For(condition, positionOf(condition.ColumnId)))]);
    }

    // Whether a row without a value in any filter column is kept - a row beyond every column read, or a filter whose
    // columns have no stored values at all.
    public bool KeepsRowWithoutValues => _tests.All(test => test.MatchesMissing);

    // The block's columns, looked up once; then any row of it can be tested.
    public BoundBlock Bind(RawDataBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        foreach (var test in _tests)
        {
            test.Bind(block);
        }

        return new BoundBlock(this);
    }

    private bool Keeps(int row)
    {
        foreach (var test in _tests)
        {
            if (!test.Matches(row))
            {
                return false;
            }
        }

        return true;
    }

    // A block the evaluator is bound to: Keeps(row) tests one of its rows. Valid until the next Bind.
    public readonly struct BoundBlock
    {
        private readonly RowFilterEvaluator _evaluator;

        internal BoundBlock(RowFilterEvaluator evaluator)
        {
            _evaluator = evaluator;
        }

        public bool Keeps(int row) => _evaluator.Keeps(row);
    }

    // One condition over the column at a position of each block (or none: always Missing).
    private abstract class ConditionTest(int position)
    {
        protected int Position { get; } = position;

        public abstract bool MatchesMissing { get; }

        public static ConditionTest For(RowFilterCondition condition, int position) => condition switch
        {
            NumericValueSetCondition set => new NumericTest(position, set.MatchesMissing, set.Matches),
            NumericComparisonCondition comparison => new NumericTest(position, false, comparison.Matches),
            NumericBetweenCondition between => new NumericTest(position, false, between.Matches),
            TextValueSetCondition set => new TextTest(position, set.MatchesMissing, set.Matches),
            TextComparisonCondition comparison => new TextTest(position, false, comparison.Matches),
            _ => throw new NotSupportedException($"The condition '{condition.GetType().Name}' is not supported.")
        };

        public abstract void Bind(RawDataBlock block);

        public abstract bool Matches(int row);
    }

    private sealed class NumericTest(int position, bool matchesMissing, Func<double?, bool> matches) : ConditionTest(position)
    {
        private IReadOnlyList<double?>? _values;

        public override bool MatchesMissing => matchesMissing;

        public override void Bind(RawDataBlock block) =>
            _values = Position < 0 ? null : ((NumericRawDataColumn)block.Columns[Position]).Values;

        public override bool Matches(int row) => _values is null ? matchesMissing : matches(_values[row]);
    }

    private sealed class TextTest(int position, bool matchesMissing, Func<string?, bool> matches) : ConditionTest(position)
    {
        private IReadOnlyList<string?>? _values;

        public override bool MatchesMissing => matchesMissing;

        public override void Bind(RawDataBlock block) =>
            _values = Position < 0 ? null : ((StringRawDataColumn)block.Columns[Position]).Values;

        public override bool Matches(int row) => _values is null ? matchesMissing : matches(_values[row]);
    }
}
