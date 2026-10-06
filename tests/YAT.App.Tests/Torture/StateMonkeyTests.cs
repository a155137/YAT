using System.Globalization;
using System.Text;
using SkiaSharp;
using YAT.App.Tests.Robustness;
using YAT.Application.Filtering;
using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.app.Graphs;
using YAT.app.ViewModels;

namespace YAT.App.Tests.Torture;

// Task #063, Phase 2: the view-model state monkey. A sequence is a run of episodes; each opens the graph setup of a
// graph type from the Graph menu, does a seeded series of what a user can do in it - Select All, Clear Selection,
// picking and unpicking variables, Together / Separate, a group, a panel, a filter, the statistics, the legend, the
// labels, the axis ranges, the specification, the histogram bins, a scatter plot's X and Y - and then confirms or
// cancels. A confirmed graph is drawn and checked like every torture graph.
//
// After every action the setup is checked:
//   * nothing throws;
//   * OK is available exactly when the setup has a configuration, and a message says why when it has none;
//   * a configuration passes the validator against the worksheet's columns;
//   * it assigns exactly the columns the roles show, in worksheet order - so nothing hidden, a column picked and
//     unpicked or a role set and cleared, reaches the graph - and at most one column to a single-column role;
//   * it carries exactly the options the editors show;
//   * the selection count is what "Selected: n / 50" says, and more than 50 cannot be confirmed.
//
// Everything is decided by (seed, sequence) through RobustnessRandom (SplitMix64). A failure names the seed, the
// episode, the step, the actions that led there and the state, and the same seed replays the same trace exactly.
public sealed class StateMonkeyTests
{
    public static readonly ulong[] Seeds = [0x0630_3001, 0x0630_3002, 0x0630_3003, 0x0630_3004];

    private static readonly GraphType[] Types =
        [GraphType.Histogram, GraphType.BoxPlot, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf, GraphType.ScatterPlot];

    // 55 numeric columns - more than the 50 a setup takes - a text lot column and a text site column.
    private static readonly TortureDataset Data = MaximumCombinationTests.Dataset(55, rows: 90);

    public static TheoryData<ulong> NormalSeeds => [.. Seeds];

    [Theory]
    [MemberData(nameof(NormalSeeds))]
    public async Task ARandomSequenceOfSetupActionsKeepsTheSetupConsistent(ulong seed)
    {
        await RunAsync(seed, actions: 300);
    }

    // The same seed is the same sequence: every step's state, every episode's outcome.
    [Fact]
    public async Task TheSameSeedReplaysTheSameSequence()
    {
        var first = await RunAsync(Seeds[0], actions: 250);
        var second = await RunAsync(Seeds[0], actions: 250);

        Assert.Equal(first.Count, second.Count);
        for (var index = 0; index < first.Count; index++)
        {
            Assert.True(first[index] == second[index], $"replay diverged at trace line {index}:\n  {first[index]}\n  {second[index]}");
        }
    }

    // The explicit torture: several seeds (YAT_TORTURE_SEED overrides the first) of thousands of actions each.
    [Fact(Explicit = true)]
    public async Task LongRandomSequencesKeepTheSetupConsistent()
    {
        ulong[] seeds = [TortureFuzzer.SweepSeed() == TortureFuzzer.Seed ? 0x0630_3101UL : TortureFuzzer.SweepSeed(), 0x0630_3102, 0x0630_3103];
        foreach (var seed in seeds)
        {
            await RunAsync(seed, actions: TortureFuzzer.SweepCases(3000));
        }
    }

    // ---- The monkey ----

    private sealed class Monkey(ulong seed, IReadOnlyList<WorksheetColumn> columns)
    {
        private readonly RobustnessRandom _random = new(RobustnessRandom.Mix(seed, 0));
        private readonly List<string> _actions = [];

        public List<string> Trace { get; } = [];

        public int Episode { get; set; }

        public int Step { get; private set; }

        public RobustnessRandom Random => _random;

        public string Failure(string violated, GraphSetupViewModel setup) =>
            $"STATE MONKEY seed=0x{seed:X} episode={Episode} step={Step}: {violated}\n" +
            $"  state: {Summary(setup)}\n" +
            $"  last actions ({_actions.Count} in all, replay with the same seed):\n    " +
            string.Join("\n    ", _actions.TakeLast(40));

        // Does one action and checks the setup after it.
        public void Act(GraphSetupViewModel setup)
        {
            Step++;
            var action = Do(setup);
            _actions.Add($"#{Step} e{Episode} {setup.GraphType}: {action}");
            Check(setup);
            Trace.Add($"#{Step} {action} => {Summary(setup)}");
        }

        // What a user does to get past the setup's messages before OK, so that most confirming episodes draw a graph:
        // every typed number and title back to blank (Auto), a variable picked when none is, a scatter plot's X and Y
        // set apart, the panel apart from the group. One recorded, checked step like any other.
        public void Tidy(GraphSetupViewModel setup)
        {
            Step++;
            setup.Axes.XMinimumText = setup.Axes.XMaximumText = setup.Axes.YMinimumText = setup.Axes.YMaximumText = string.Empty;
            setup.LowerLimitText = setup.TargetText = setup.UpperLimitText = string.Empty;
            setup.BinCountText = "10";
            setup.BinWidthText = "1";
            setup.BinStartText = "0";
            if (setup.SupportsHistogramControls)
            {
                setup.SelectedBinning = setup.BinningChoices[0];
            }

            setup.Labels.SelectedGraphTitleMode = setup.Labels.SelectedXAxisTitleMode = setup.Labels.SelectedYAxisTitleMode = setup.Labels.LabelModeChoices[0];
            var variables = setup.Roles.FirstOrDefault(role => role.AllowsMultiple);
            if (variables is not null && variables.SelectedOptions.Count == 0)
            {
                variables.SelectedOptions.Add(variables.Options.First(option => !option.IsNone));
            }

            while (variables is not null && variables.SelectedOptions.Count > GraphRoleDefinition.MaximumColumns)
            {
                variables.SelectedOptions.RemoveAt(variables.SelectedOptions.Count - 1);
            }

            var numeric = setup.AvailableColumns.Where(option => option.Name.StartsWith("Reg", StringComparison.Ordinal)).ToArray();
            foreach (var (role, index) in new[] { (GraphVariableRole.X, 0), (GraphVariableRole.Y, 1) })
            {
                if (setup.Roles.FirstOrDefault(candidate => candidate.Role == role) is { } axis)
                {
                    axis.SelectedOption = axis.Options.First(option => option.Name == numeric[index].Name);
                }
            }

            foreach (var role in setup.Roles.Where(role => role.Role is GraphVariableRole.Group or GraphVariableRole.Panel))
            {
                role.SelectedOption = role.Options.FirstOrDefault(option => option.IsNone);
            }

            _actions.Add($"#{Step} e{Episode} {setup.GraphType}: tidy before OK");
            Check(setup);
            Trace.Add($"#{Step} tidy => {Summary(setup)}");
        }

        private string Do(GraphSetupViewModel setup)
        {
            var variables = setup.Roles.FirstOrDefault(role => role.AllowsMultiple);
            switch (_random.Next(16))
            {
                case 0 when variables is not null:
                    variables.SelectAll();
                    return "Select All";

                case 1 when variables is not null:
                    variables.ClearSelection();
                    return "Clear Selection";

                case 2 or 3 when variables is not null:
                {
                    // Picking one column more or one less, beyond the 50 Select All stops at.
                    var option = variables.Options.Where(candidate => !candidate.IsNone).ToArray()[_random.Next(variables.Options.Count(candidate => !candidate.IsNone))];
                    if (variables.SelectedOptions.Contains(option))
                    {
                        variables.SelectedOptions.Remove(option);
                        return $"unpick {option.Name}";
                    }

                    variables.SelectedOptions.Add(option);
                    return $"pick {option.Name}";
                }

                case 4 when setup.SupportsVariableLayout:
                    setup.Layout = setup.Layout == GraphVariableLayout.Together ? GraphVariableLayout.Separate : GraphVariableLayout.Together;
                    return $"layout {setup.Layout}";

                case 5:
                    return Choose(setup, GraphVariableRole.Group);

                case 6:
                    return Choose(setup, GraphVariableRole.Panel);

                case 7 when setup.Roles.Any(role => role.Role == GraphVariableRole.X):
                    return _random.Next(2) == 0 ? Choose(setup, GraphVariableRole.X) : Choose(setup, GraphVariableRole.Y);

                case 8:
                    return Filter(setup);

                case 9 when setup.SupportsStatisticsPanel:
                {
                    var statistics = setup.Statistics;
                    if (_random.Next(3) == 0)
                    {
                        statistics.SelectedMode = _random.Pick(statistics.ModeChoices);
                        return $"statistics {statistics.SelectedMode.Value}";
                    }

                    switch (_random.Next(8))
                    {
                        case 0: statistics.ShowMean = !statistics.ShowMean; break;
                        case 1: statistics.ShowStandardDeviation = !statistics.ShowStandardDeviation; break;
                        case 2: statistics.ShowCount = !statistics.ShowCount; break;
                        case 3: statistics.ShowMinimum = !statistics.ShowMinimum; break;
                        case 4: statistics.ShowFirstQuartile = !statistics.ShowFirstQuartile; break;
                        case 5: statistics.ShowMedian = !statistics.ShowMedian; break;
                        case 6: statistics.ShowThirdQuartile = !statistics.ShowThirdQuartile; break;
                        default: statistics.ShowMaximum = !statistics.ShowMaximum; break;
                    }

                    return $"statistics items {statistics.Options}";
                }

                case 10 when setup.SupportsLegend:
                    setup.Legend.SelectedMode = _random.Pick(setup.Legend.ModeChoices);
                    setup.Legend.SelectedPosition = _random.Pick(setup.Legend.PositionChoices);
                    return $"legend {setup.Legend.Options}";

                case 11 when setup.SupportsLabels:
                {
                    var labels = setup.Labels;
                    var mode = _random.Pick(labels.LabelModeChoices);
                    var text = _random.Pick(LabelTexts);
                    switch (_random.Next(3))
                    {
                        case 0: labels.SelectedGraphTitleMode = mode; labels.GraphTitleText = text; break;
                        case 1: labels.SelectedXAxisTitleMode = mode; labels.XAxisTitleText = text; break;
                        default: labels.SelectedYAxisTitleMode = mode; labels.YAxisTitleText = text; break;
                    }

                    return $"label {mode.Value} \"{text}\"";
                }

                case 12 when setup.SupportsAxisRanges:
                {
                    var text = _random.Pick(NumberTexts);
                    switch (_random.Next(4))
                    {
                        case 0: setup.Axes.XMinimumText = text; break;
                        case 1: setup.Axes.XMaximumText = text; break;
                        case 2: setup.Axes.YMinimumText = text; break;
                        default: setup.Axes.YMaximumText = text; break;
                    }

                    return $"axis range text \"{text}\"";
                }

                case 13 when setup.SupportsSpecificationLines:
                {
                    var text = _random.Pick(NumberTexts);
                    switch (_random.Next(3))
                    {
                        case 0: setup.LowerLimitText = text; break;
                        case 1: setup.TargetText = text; break;
                        default: setup.UpperLimitText = text; break;
                    }

                    return $"specification text \"{text}\"";
                }

                case 14 when setup.SupportsHistogramControls:
                    setup.SelectedBinning = _random.Pick(setup.BinningChoices);
                    setup.SelectedYScale = _random.Pick(setup.YScaleChoices);
                    setup.BinCountText = _random.Pick(NumberTexts);
                    setup.BinWidthText = _random.Pick(NumberTexts);
                    setup.BinStartText = _random.Pick(NumberTexts);
                    return $"bins {setup.SelectedBinning.Value} {setup.SelectedYScale.Value} count=\"{setup.BinCountText}\" width=\"{setup.BinWidthText}\" start=\"{setup.BinStartText}\"";

                default:
                    return "nothing";
            }
        }

        private string Choose(GraphSetupViewModel setup, GraphVariableRole roleName)
        {
            if (setup.Roles.FirstOrDefault(role => role.Role == roleName) is not { } role)
            {
                return $"no {roleName} role";
            }

            var option = _random.Next(3) == 0 && !role.IsRequired ? role.Options.FirstOrDefault(candidate => candidate.IsNone) : _random.Pick(role.Options);
            role.SelectedOption = option;
            return $"{roleName} = {option?.Name ?? "nothing"}";
        }

        private string Filter(GraphSetupViewModel setup)
        {
            if (!setup.SupportsFilter)
            {
                return "no filter";
            }

            Guid Column(string name) => setup.AvailableColumns.Single(option => option.Name == name).WorksheetColumnId!.Value;
            switch (_random.Next(4))
            {
                case 0:
                    setup.Filter = null;
                    return "filter none";
                case 1:
                {
                    string[] values = [.. Enumerable.Range(0, 1 + _random.Next(3)).Select(_ => $"LOT-{_random.Next(6)}").Distinct()];
                    setup.Filter = new RowFilter(new TextValueSetCondition(Column("Lot"), values, includeMissing: _random.Next(2) == 0));
                    return $"filter Lot in [{string.Join(",", values)}]";
                }

                case 2:
                {
                    string[] values = [$"Site {1 + _random.Next(12)}"];
                    setup.Filter = new RowFilter(new TextValueSetCondition(Column("Site"), values, exclude: _random.Next(2) == 0));
                    return $"filter Site {values[0]}";
                }

                default:
                {
                    var value = Math.Round(15000 + (_random.NextGaussian() * 3000), 1);
                    setup.Filter = new RowFilter(new NumericComparisonCondition(Column("Reg1"), _random.Pick(Enum.GetValues<NumericComparison>()), value));
                    return $"filter Reg1 vs {value.ToString(CultureInfo.InvariantCulture)}";
                }
            }
        }

        private void Check(GraphSetupViewModel setup)
        {
            void That(bool condition, string violated)
            {
                if (!condition)
                {
                    throw new TortureFailure(Failure(violated, setup));
                }
            }

            var configuration = setup.Confirm();
            That((configuration is not null) == setup.CanConfirm, $"CanConfirm={setup.CanConfirm} but Confirm() gave {(configuration is null ? "nothing" : "a configuration")}");
            That((setup.ValidationMessage is null) == setup.CanConfirm, $"CanConfirm={setup.CanConfirm} but the message is \"{setup.ValidationMessage}\"");

            foreach (var role in setup.Roles.Where(role => role.AllowsMultiple))
            {
                That(role.SelectedOptions.Distinct().Count() == role.SelectedOptions.Count, $"{role.DisplayName} has a column picked twice");
                That(role.SelectionSummary == $"Selected: {role.SelectedOptions.Count} / {GraphRoleDefinition.MaximumColumns}", $"summary \"{role.SelectionSummary}\" for {role.SelectedOptions.Count}");
                That(role.SelectedOptions.Count <= GraphRoleDefinition.MaximumColumns || !setup.CanConfirm, $"{role.SelectedOptions.Count} columns can be confirmed");
                That(role.SelectedOptions.All(option => !option.IsNone), $"{role.DisplayName} has (None) picked");
            }

            if (configuration is null)
            {
                return;
            }

            var validation = new GraphConfigurationValidator().Validate(configuration, columns);
            That(validation.IsValid, $"a confirmed configuration is not valid: {string.Join("; ", validation.Errors.Select(error => error.ToString()))}");

            foreach (var role in setup.Roles)
            {
                var assigned = configuration.FindColumnIds(role.Role);
                That(assigned.SequenceEqual(role.SelectedColumnIds), $"{role.DisplayName} assigns [{Names(assigned)}] but shows [{Names(role.SelectedColumnIds)}]");
                That(role.AllowsMultiple || assigned.Count <= 1, $"{role.DisplayName} assigns {assigned.Count} columns");
            }

            That(configuration.Assignments.Count == setup.Roles.Sum(role => role.SelectedColumnIds.Count), "the configuration assigns columns no role shows");
            That(configuration.Filter == setup.Filter, "the configuration's filter is not the setup's");
            That(!setup.SupportsStatisticsPanel || configuration.StatisticsOptions == setup.Statistics.Options, $"statistics {configuration.StatisticsOptions} but the editor shows {setup.Statistics.Options}");
            That(!setup.SupportsLegend || configuration.LegendOptions == setup.Legend.Options, $"legend {configuration.LegendOptions} but the editor shows {setup.Legend.Options}");
            That(!setup.SupportsLabels || configuration.LabelOptions == setup.Labels.Options, $"labels {configuration.LabelOptions} but the editor shows {setup.Labels.Options}");
            That(!setup.SupportsAxisRanges || configuration.AxisRangeOptions == setup.Axes.Options, $"ranges {configuration.AxisRangeOptions} but the editor shows {setup.Axes.Options}");
        }

        private string Names(IEnumerable<Guid> ids) =>
            string.Join(",", ids.Select(id => columns.FirstOrDefault(column => column.Id == id)?.Name ?? id.ToString()));

        // What the setup is now, in names rather than identities, so a replay in another project reads the same.
        public string Summary(GraphSetupViewModel setup)
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"{setup.GraphType} ok={setup.CanConfirm}");
            foreach (var role in setup.Roles)
            {
                var picked = role.SelectedColumnIds;
                text.Append(CultureInfo.InvariantCulture, $" {role.Role}=[{(picked.Count > 4 ? $"{picked.Count} columns" : Names(picked))}]");
            }

            text.Append(CultureInfo.InvariantCulture, $" layout={setup.Layout} filter={(setup.Filter is null ? "-" : RowFilterEditorViewModel.Describe(setup.Filter))}");
            if (setup.ValidationMessage is { } message)
            {
                text.Append(" says \"").Append(message.Split('\n')[0]).Append('"');
            }

            return text.ToString();
        }
    }

    private static readonly string[] LabelTexts = ["", " ", "Title", "晶圓 良率 😀", "<&>\"'", new string('W', 300), "a\tb"];

    private static readonly string[] NumberTexts =
        ["", " ", "0", "1", "-1", "15000", "1e6", "-1e300", "1.7976931348623157E+308", "0.0001", "abc", "1,5", "NaN", "∞", "12 ", "2e", "50", "200"];

    private static async Task<List<string>> RunAsync(ulong seed, int actions)
    {
        using var session = await TortureSession.StartAsync(Data);
        var worksheet = session.Project.SelectedWorksheet!.Id;
        var columns = await session.Workspace.CurrentSession!.WorksheetColumns.GetByWorksheetIdAsync(worksheet, TestContext.Current.CancellationToken);
        var monkey = new Monkey(seed, columns);
        TortureFailure? failure = null;

        while (monkey.Step < actions && failure is null)
        {
            monkey.Episode++;
            var type = monkey.Random.Pick(Types);
            var length = 10 + monkey.Random.Next(70);
            var confirm = monkey.Random.Next(4) != 0;
            var shown = session.GraphWindows.Shown.Count;
            var errors = session.GraphDialogs.Errors.Count;
            var confirmed = false;
            var request = string.Empty;
            session.GraphDialogs.AnswerAsync = null;
            session.GraphDialogs.Answer = setup =>
            {
                try
                {
                    for (var step = 0; step < length && monkey.Step < actions; step++)
                    {
                        monkey.Act(setup);
                    }
                }
                catch (TortureFailure caught)
                {
                    failure = caught;
                    return null;
                }
                catch (Exception exception)
                {
                    failure = new TortureFailure(monkey.Failure($"the setup threw {exception}", setup));
                    return null;
                }

                if (confirm && !setup.CanConfirm)
                {
                    try
                    {
                        monkey.Tidy(setup);
                    }
                    catch (TortureFailure caught)
                    {
                        failure = caught;
                        return null;
                    }
                }

                session.GraphDialogs.Layout = setup.Layout;
                confirmed = confirm && setup.CanConfirm;
                request = monkey.Summary(setup);
                return confirmed ? setup.Confirm() : null;
            };

            var traces = TortureTraceListener.Begin();
            await Command(session, type).ExecuteAsync(null);
            if (failure is not null)
            {
                break;
            }

            var opened = session.GraphWindows.Shown.Count - shown;
            var said = session.GraphDialogs.Errors.Skip(errors).ToList();
            monkey.Trace.Add($"episode {monkey.Episode} {type} confirmed={confirmed} windows={opened} errors=[{string.Join(" | ", said.Select(error => error.ReplaceLineEndings(" / ")))}]");
            if (confirmed)
            {
                List<string> traced;
                lock (traces)
                {
                    traced = [.. traces];
                }

                var graphs = Enumerable.Range(shown, opened)
                    .Select(index => new TortureGraph(session.GraphWindows.Graphs[index], session.GraphWindows.Shown[index].Plot, session.GraphWindows.Panels[index]))
                    .ToList();
                var context = $"STATE MONKEY seed=0x{seed:X} episode={monkey.Episode} step={monkey.Step}\n  confirmed: {request}";
                TortureInvariants.Verify(context, new TortureOutcome(graphs.Take(3).ToList(), said, traced), [new SKSize(640, 480)], export: false);
            }
        }

        if (failure is not null)
        {
            throw failure;
        }

        return monkey.Trace;
    }

    private static CommunityToolkit.Mvvm.Input.IAsyncRelayCommand Command(TortureSession session, GraphType type) => type switch
    {
        GraphType.ScatterPlot => session.Shell.ScatterPlotCommand,
        GraphType.Histogram => session.Shell.HistogramCommand,
        GraphType.BoxPlot => session.Shell.BoxPlotCommand,
        GraphType.ProbabilityPlot => session.Shell.ProbabilityPlotCommand,
        _ => session.Shell.EmpiricalCdfCommand
    };
}
