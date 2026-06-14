using CoinPusher.Engine;

if (args.Contains("--trace-demo", StringComparer.Ordinal))
{
    RunTraceDemo();
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("planner creates exact verified outcome", PlannerCreatesExactVerifiedOutcome),
    ("planner uses clockwise rotation and paced timeline", PlannerUsesClockwiseRotationAndPacedTimeline),
    ("timeline planner requests extra spin capacity", TimelinePlannerRequestsExtraSpinCapacity),
    ("console trace prints board formation and spin states", ConsoleTracePrintsBoardFormationAndSpinStates),
    ("wheel uses documented stack increment formula", WheelUsesDocumentedStackIncrementFormula),
    ("wheel caps stacks and harvests potential", WheelCapsStacksAndHarvestsPotential),
    ("filler symbols do not contribute to objectives", FillerSymbolsDoNotContributeToObjectives),
    ("verifier rejects over collection", VerifierRejectsOverCollection),
    ("verifier rejects invalid feature chain", VerifierRejectsInvalidFeatureChain)
};

var failures = new List<string>();
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.WriteLine($"FAIL {name}");
        Console.WriteLine(exception);
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failures:");
    foreach (var failure in failures)
    {
        Console.WriteLine($"- {failure}");
    }

    Environment.Exit(1);
}

static void PlannerCreatesExactVerifiedOutcome()
{
    var request = OutcomeRequest.Create(
        500,
        new[]
        {
            new ObjectiveRequirement("A", 30),
            new ObjectiveRequirement("B", 20),
            new ObjectiveRequirement("C", 10)
        },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry>
        {
            ["A"] = new(10, 20, 30),
            ["B"] = new(20, 40, 60),
            ["C"] = new(410, 420, 430)
        }));

    var plan = new OutcomePlanner().Generate(request);
    var report = new GamePlanVerifier().Verify(plan);

    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.Equal(500, report.SimulationResult!.FinalPayout);
    Assert.Equal(30, report.SimulationResult.CollectionCounts["A"]);
    Assert.Equal(20, report.SimulationResult.CollectionCounts["B"]);
    Assert.Equal(10, report.SimulationResult.CollectionCounts["C"]);
    Assert.Equal(PrizeLevel.Upgrade2, report.SimulationResult.PrizeLevels["A"]);
    Assert.Equal(PrizeLevel.Upgrade2, report.SimulationResult.PrizeLevels["B"]);
    Assert.Equal(PrizeLevel.Base, report.SimulationResult.PrizeLevels["C"]);
    Assert.True(plan.BoardStates.Count == plan.Spins.Count + 1, "Plan should include deterministic board snapshots.");
}

static void PlannerUsesClockwiseRotationAndPacedTimeline()
{
    var request = OutcomeRequest.Create(
        10,
        new[] { new ObjectiveRequirement("A", 30) },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry> { ["A"] = new(10, 10, 10) }));

    var plan = new OutcomePlanner().Generate(request);
    var report = new GamePlanVerifier().Verify(plan);

    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.True(plan.Spins.All(spin => spin.Rotation == BoardRotation.Clockwise), "Generated spins should use official clockwise rotation.");
    Assert.Equal(5, report.SimulationResult!.Spins.Count(spin => spin.Collections.Count > 0));
}

static void TimelinePlannerRequestsExtraSpinCapacity()
{
    var objectives = new[] { new ObjectiveRequirement("A", 600) };
    var contributionPlan = new ContributionPlanner().Plan(objectives);
    var timeline = new TimelinePlanner().Plan(contributionPlan, new FeatureConfiguration());

    Assert.Equal(6, timeline.SpinCount);
    Assert.Equal(1, timeline.ExtraSpinsRequired);
    Assert.True(timeline.Spins.All(spin => spin.Contributions.Count <= 15), "No spin should exceed pusher collection capacity.");
}

static void ConsoleTracePrintsBoardFormationAndSpinStates()
{
    var originalOut = Console.Out;
    using var writer = new StringWriter();
    try
    {
        Console.SetOut(writer);
        var request = SimpleTraceRequest();
        var plan = new OutcomePlanner(trace: new ConsoleEngineTraceSink()).Generate(request);

        Assert.True(plan.Spins.Count > 0, "Trace test should produce a plan.");
    }
    finally
    {
        Console.SetOut(originalOut);
    }

    var output = writer.ToString();
    Assert.Contains(output, "[board-planner] Building");
    Assert.Contains(output, "=== planned start board for spin 0 ===");
    Assert.Contains(output, "=== spin 0 start ===");
    Assert.Contains(output, "=== spin 0 after rotation ===");
    Assert.Contains(output, "[simulator] Replay end.");
}

static void WheelUsesDocumentedStackIncrementFormula()
{
    var board = BoardState.Empty();
    board.Set(new BoardPosition(4, 0), BoardCell.FromSymbol("A", 1));
    board.Set(new BoardPosition(0, 1), BoardCell.FromFeature(FeatureKind.Wheel));

    var spin = new SpinPlan(
        0,
        Array.Empty<FeatureLanding>(),
        new FeatureAction[] { new WheelAction(new BoardPosition(0, 1), "A", 2) },
        new[] { new FeatureConversion(new BoardPosition(0, 1), BoardCell.Empty) },
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        Array.Empty<SpawnInstruction>());

    var plan = SingleObjectivePlan(4, 10, board, spin);
    var report = new GamePlanVerifier().Verify(plan);

    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.Equal(4, report.SimulationResult!.CollectionCounts["A"]);
}

static void WheelCapsStacksAndHarvestsPotential()
{
    var board = BoardState.Empty();
    board.Set(new BoardPosition(4, 0), BoardCell.FromSymbol("A", 5));
    board.Set(new BoardPosition(0, 1), BoardCell.FromFeature(FeatureKind.Wheel));

    var spin = new SpinPlan(
        0,
        Array.Empty<FeatureLanding>(),
        new FeatureAction[] { new WheelAction(new BoardPosition(0, 1), "A", 3) },
        new[] { new FeatureConversion(new BoardPosition(0, 1), BoardCell.Empty) },
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        Array.Empty<SpawnInstruction>());

    var plan = SingleObjectivePlan(7, 10, board, spin);
    var report = new GamePlanVerifier().Verify(plan);

    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.Equal(7, report.SimulationResult!.CollectionCounts["A"]);
}

static void FillerSymbolsDoNotContributeToObjectives()
{
    var board = BoardState.Empty();
    board.Set(new BoardPosition(4, 0), BoardCell.FromSymbol("A", 1));
    board.Set(new BoardPosition(4, 1), BoardCell.FromFillerSymbol("Bell", 7));

    var spin = new SpinPlan(
        0,
        Array.Empty<FeatureLanding>(),
        Array.Empty<FeatureAction>(),
        Array.Empty<FeatureConversion>(),
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        Array.Empty<SpawnInstruction>());

    var plan = SingleObjectivePlan(1, 10, board, spin);
    var report = new GamePlanVerifier().Verify(plan);

    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.Equal(1, report.SimulationResult!.CollectionCounts["A"]);
}

static void VerifierRejectsOverCollection()
{
    var board = BoardState.Empty();
    board.Set(new BoardPosition(4, 0), BoardCell.FromSymbol("A", 2));

    var spin = new SpinPlan(
        0,
        Array.Empty<FeatureLanding>(),
        Array.Empty<FeatureAction>(),
        Array.Empty<FeatureConversion>(),
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        Array.Empty<SpawnInstruction>());

    var plan = SingleObjectivePlan(1, 10, board, spin);
    var report = new GamePlanVerifier().Verify(plan);

    Assert.False(report.IsValid, "Over-collection should invalidate the plan.");
    Assert.Contains(report.Issues, issue => issue.Code == "replay_failed" && issue.Message.Contains("Over-collection", StringComparison.Ordinal));
}

static void VerifierRejectsInvalidFeatureChain()
{
    var board = BoardState.Empty();
    board.Set(new BoardPosition(0, 0), BoardCell.FromFeature(FeatureKind.Wheel, 1));

    var spin = new SpinPlan(
        0,
        Array.Empty<FeatureLanding>(),
        Array.Empty<FeatureAction>(),
        new[] { new FeatureConversion(new BoardPosition(0, 0), BoardCell.FromFeature(FeatureKind.Flush, 2)) },
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        Array.Empty<SpawnInstruction>());

    var plan = SingleObjectivePlan(0, 10, board, spin);
    var report = new GamePlanVerifier().Verify(plan);

    Assert.False(report.IsValid, "Invalid feature chain should invalidate the plan.");
    Assert.Contains(report.Issues, issue => issue.Code == "replay_failed" && issue.Message.Contains("Invalid feature chain", StringComparison.Ordinal));
}

static GamePlan SingleObjectivePlan(int targetCount, int targetWin, BoardState initialBoard, SpinPlan spin) =>
    new(
        targetWin,
        new[] { new ObjectiveRequirement("A", targetCount) },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry> { ["A"] = new(targetWin, targetWin, targetWin) }),
        new FeatureConfiguration(),
        new Dictionary<string, PrizeLevel> { ["A"] = PrizeLevel.Base },
        initialBoard,
        new[] { spin },
        Array.Empty<BoardState>(),
        new VerificationMetadata("test", 5, "test", DateTimeOffset.UnixEpoch));

static OutcomeRequest SimpleTraceRequest() =>
    OutcomeRequest.Create(
        30,
        new[]
        {
            new ObjectiveRequirement("Gold", 8),
            new ObjectiveRequirement("Star", 6)
        },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry>
        {
            ["Gold"] = new(10, 20, 20),
            ["Star"] = new(10, 10, 10)
        }));

static void RunTraceDemo()
{
    Console.WriteLine("Coin Pusher Outcome Engine trace demo");
    Console.WriteLine("-------------------------------------");
    var request = SimpleTraceRequest();
    var plan = new OutcomePlanner(trace: new ConsoleEngineTraceSink()).Generate(request);
    var report = new GamePlanVerifier(new CoinPusherSimulator(new ConsoleEngineTraceSink())).Verify(plan);

    Console.WriteLine();
    Console.WriteLine($"Verifier: {(report.IsValid ? "PASS" : "FAIL")}");
    if (report.SimulationResult is not null)
    {
        Console.WriteLine($"Final counts: {BoardFormatter.FormatCounts(report.SimulationResult.CollectionCounts)}");
        Console.WriteLine($"Final prizes: {BoardFormatter.FormatPrizeLevels(report.SimulationResult.PrizeLevels)}");
        Console.WriteLine($"Final payout: {report.SimulationResult.FinalPayout}");
    }
}

static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    public static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (!values.Any(predicate))
        {
            throw new InvalidOperationException("Expected sequence to contain a matching item.");
        }
    }

    public static void Contains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected output to contain '{expected}'.");
        }
    }
}
