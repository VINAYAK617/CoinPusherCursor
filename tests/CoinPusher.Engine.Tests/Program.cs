using CoinPusher.Engine;
using System.Text.Json;

if (args.Contains("--trace-demo", StringComparer.Ordinal))
{
    RunTraceDemo();
    return;
}

if (args.Contains("--load", StringComparer.Ordinal))
{
    RunLoadTest(iterations: 100, printSummary: true);
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("planner creates exact verified outcome", PlannerCreatesExactVerifiedOutcome),
    ("planner uses clockwise rotation and paced timeline", PlannerUsesClockwiseRotationAndPacedTimeline),
    ("backward reconstruction produces continuous boards", BackwardReconstructionProducesContinuousBoards),
    ("non-target board fill is visually varied", NonTargetBoardFillIsVisuallyVaried),
    ("normal planner does not create unearned stacks", NormalPlannerDoesNotCreateUnearnedStacks),
    ("timeline planner requests extra spin capacity", TimelinePlannerRequestsExtraSpinCapacity),
    ("console trace prints board formation and spin states", ConsoleTracePrintsBoardFormationAndSpinStates),
    ("json exporter emits requested protocol shape", JsonExporterEmitsRequestedProtocolShape),
    ("manual feature mechanics verify", ManualFeatureMechanicsVerify),
    ("feature demo visibly includes wheel and flush", FeatureDemoVisiblyIncludesWheelAndFlush),
    ("deterministic load scenarios verify", DeterministicLoadScenariosVerify),
    ("wheel uses documented stack increment formula", WheelUsesDocumentedStackIncrementFormula),
    ("wheel caps stacks and harvests potential", WheelCapsStacksAndHarvestsPotential),
    ("non-target symbols count but stay below threshold", NonTargetSymbolsCountButStayBelowThreshold),
    ("verifier rejects accidental non-target win", VerifierRejectsAccidentalNonTargetWin),
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
        }),
        symbolThresholds: Thresholds(("A", 30), ("B", 20), ("C", 10)));

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
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry> { ["A"] = new(10, 10, 10) }),
        symbolThresholds: Thresholds(("A", 30)));

    var plan = new OutcomePlanner().Generate(request);
    var report = new GamePlanVerifier().Verify(plan);

    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.True(plan.Spins.All(spin => spin.Rotation == BoardRotation.Clockwise), "Generated spins should use official clockwise rotation.");
    Assert.Equal(5, report.SimulationResult!.Spins.Count(spin => spin.Collections.Count > 0));
}

static void BackwardReconstructionProducesContinuousBoards()
{
    var request = OutcomeRequest.Create(
        100,
        new[]
        {
            new ObjectiveRequirement("A", 30),
            new ObjectiveRequirement("B", 30),
            new ObjectiveRequirement("C", 20),
            new ObjectiveRequirement("D", 15)
        },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry>
        {
            ["A"] = new(25, 25, 25),
            ["B"] = new(25, 25, 25),
            ["C"] = new(25, 25, 25),
            ["D"] = new(25, 25, 25)
        }),
        symbolThresholds: Thresholds(("A", 30), ("B", 30), ("C", 20), ("D", 15)));

    var plan = new OutcomePlanner().Generate(request);
    var report = new GamePlanVerifier().Verify(plan);

    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.True(plan.Spins.Any(spin => spin.Spawns.Count > 0), "Backward reconstruction should produce explicit spawns.");
    Assert.True(plan.BoardStates.Count == plan.Spins.Count + 1, "Every spin should have start and end snapshots.");

    for (var spinIndex = 0; spinIndex < plan.Spins.Count; spinIndex++)
    {
        Assert.True(
            report.SimulationResult!.BoardTimeline[spinIndex].ValueEquals(plan.BoardStates[spinIndex]),
            $"Spin {spinIndex} start snapshot should match replay.");
        Assert.True(
            report.SimulationResult.BoardTimeline[spinIndex + 1].ValueEquals(plan.BoardStates[spinIndex + 1]),
            $"Spin {spinIndex} end snapshot should match replay.");
    }
}

static void NonTargetBoardFillIsVisuallyVaried()
{
    var request = OutcomeRequest.Create(
        100,
        new[]
        {
            new ObjectiveRequirement("A", 30),
            new ObjectiveRequirement("B", 30),
            new ObjectiveRequirement("C", 20),
            new ObjectiveRequirement("D", 15)
        },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry>
        {
            ["A"] = new(25, 25, 25),
            ["B"] = new(25, 25, 25),
            ["C"] = new(25, 25, 25),
            ["D"] = new(25, 25, 25)
        }),
        symbolThresholds: Thresholds(("A", 30), ("B", 30), ("C", 20), ("D", 15)));

    var plan = new OutcomePlanner().Generate(request);
    var nonTargetSymbolsOnFinalBoard = plan.BoardStates[^1]
        .Cells()
        .Where(item => item.Cell.Kind == CellKind.Symbol && !request.Objectives.Any(objective => objective.Id == item.Cell.Symbol!.SymbolId))
        .Select(item => item.Cell.Symbol!.SymbolId)
        .Distinct(StringComparer.Ordinal)
        .Count();

    Assert.True(nonTargetSymbolsOnFinalBoard >= 3, "Final visible board should vary non-target symbols.");
}

static void NormalPlannerDoesNotCreateUnearnedStacks()
{
    var request = OutcomeRequest.Create(
        100,
        new[]
        {
            new ObjectiveRequirement("A", 30),
            new ObjectiveRequirement("B", 30),
            new ObjectiveRequirement("C", 20),
            new ObjectiveRequirement("D", 15)
        },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry>
        {
            ["A"] = new(25, 25, 25),
            ["B"] = new(25, 25, 25),
            ["C"] = new(25, 25, 25),
            ["D"] = new(25, 25, 25)
        }),
        symbolThresholds: Thresholds(("A", 30), ("B", 30), ("C", 20), ("D", 15)));

    var plan = new OutcomePlanner().Generate(request);

    foreach (var board in plan.BoardStates)
    {
        foreach (var (_, cell) in board.Cells())
        {
            if (cell.Kind == CellKind.Symbol && cell.Symbol!.ContributesToObjective)
            {
                Assert.Equal(1, cell.Symbol.StackSize);
            }
        }
    }
}

static void TimelinePlannerRequestsExtraSpinCapacity()
{
    var objectives = new[] { new ObjectiveRequirement("A", 600) };
    var contributionPlan = new ContributionPlanner().Plan(objectives);
    var timeline = new TimelinePlanner().Plan(contributionPlan, new FeatureConfiguration());

    Assert.Equal(40, timeline.SpinCount);
    Assert.Equal(35, timeline.ExtraSpinsRequired);
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
    Assert.Contains(output, "[board-planner] BACKWARD BOARD RECONSTRUCTION");
    Assert.Contains(output, "Where this board came from:");
    Assert.Contains(output, "=== forward spin 1 start board produced by reverse reconstruction ===");
    Assert.Contains(output, "=== forward spin 1 - board at start ===");
    Assert.Contains(output, "Planned harvest from bottom rows before push:");
    Assert.Contains(output, "=== forward spin 1 - after clockwise rotation ===");
    Assert.Contains(output, "[simulator] Replay end.");
}

static void JsonExporterEmitsRequestedProtocolShape()
{
    var request = SimpleTraceRequest();
    var plan = new OutcomePlanner().Generate(request);
    var json = new GamePlanJsonExporter().Export(plan);
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;

    Assert.True(root.TryGetProperty("startingBoard", out var startingBoard), "JSON should include startingBoard.");
    Assert.Equal(5, startingBoard.GetArrayLength());
    Assert.Equal(5, startingBoard[0].GetArrayLength());
    Assert.True(startingBoard[0][0].TryGetProperty("id", out _), "Board cells should include id.");

    Assert.True(root.TryGetProperty("turns", out var turns), "JSON should include turns.");
    Assert.Equal(plan.Spins.Count, turns.GetArrayLength());
    Assert.True(turns[0].TryGetProperty("pushers", out var pushers), "Turn should include pushers.");
    Assert.Equal(5, pushers.GetArrayLength());
    Assert.True(pushers[0].TryGetProperty("pushValue", out _), "Pusher should include pushValue.");
    Assert.True(turns[0].TryGetProperty("spawns", out var spawns), "Turn should include spawns.");
    if (spawns.GetArrayLength() > 0)
    {
        Assert.True(spawns[0].TryGetProperty("Pos", out _), "Spawn should include Pos.");
        Assert.True(spawns[0].TryGetProperty("id", out _), "Spawn should include id.");
    }
}

static void ManualFeatureMechanicsVerify()
{
    PrizeUpgradeFeatureAppliesPayoutOnly();
    ExtraSpinFeatureExtendsTimeline();
    FlushFeatureCollectsFullColumn();
    ValidFeatureChainIsAccepted();
}

static void FeatureDemoVisiblyIncludesWheelAndFlush()
{
    var plan = FeatureDemoPlanFactory.Create();
    var report = new GamePlanVerifier().Verify(plan);
    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.Equal(5, plan.Spins.Count);
    Assert.True(plan.Spins.Any(spin => spin.FeatureActions.OfType<WheelAction>().Any()), "Feature demo should include Wheel.");
    Assert.True(plan.Spins.Any(spin => spin.FeatureActions.OfType<FlushAction>().Any()), "Feature demo should include Flush.");

    var json = new GamePlanJsonExporter().Export(plan);
    Assert.Contains(json, "\"featureId\": 11");
    Assert.Contains(json, "\"featureId\": 14");
    Assert.Contains(json, "\"wheelSymbolId\"");
    Assert.Contains(json, "\"column\"");
}

static void PrizeUpgradeFeatureAppliesPayoutOnly()
{
    var board = BoardState.Empty();
    board.Set(new BoardPosition(0, 0), BoardCell.FromFeature(FeatureKind.PrizeUpgrade));
    board.Set(new BoardPosition(4, 0), BoardCell.FromSymbol("A"));

    var spin = new SpinPlan(
        0,
        Array.Empty<FeatureLanding>(),
        new FeatureAction[] { new PrizeUpgradeAction(new BoardPosition(0, 0), "A") },
        new[] { new FeatureConversion(new BoardPosition(0, 0), BoardCell.FromSymbol("E")) },
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        Array.Empty<SpawnInstruction>());

    var plan = new GamePlan(
        20,
        new[] { new ObjectiveRequirement("A", 1) },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry> { ["A"] = new(10, 20, 30) }),
        new FeatureConfiguration(),
        new Dictionary<string, PrizeLevel> { ["A"] = PrizeLevel.Upgrade1 },
        Thresholds(("A", 1), ("E", 30)),
        board,
        new[] { spin },
        Array.Empty<BoardState>(),
        new VerificationMetadata("test", 5, "test", DateTimeOffset.UnixEpoch));

    var report = new GamePlanVerifier().Verify(plan);
    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.Equal(1, report.SimulationResult!.CollectionCounts["A"]);
    Assert.Equal(20, report.SimulationResult.FinalPayout);
}

static void ExtraSpinFeatureExtendsTimeline()
{
    var board = BoardState.Empty();
    board.Set(new BoardPosition(0, 0), BoardCell.FromFeature(FeatureKind.ExtraSpin));

    var spin0 = new SpinPlan(
        0,
        Array.Empty<FeatureLanding>(),
        new FeatureAction[] { new ExtraSpinAction(new BoardPosition(0, 0), 1) },
        new[] { new FeatureConversion(new BoardPosition(0, 0), BoardCell.FromSymbol("E")) },
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        new[] { new SpawnInstruction(new BoardPosition(4, 0), BoardCell.FromSymbol("A")) });

    var spin1 = new SpinPlan(
        1,
        Array.Empty<FeatureLanding>(),
        Array.Empty<FeatureAction>(),
        Array.Empty<FeatureConversion>(),
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        Array.Empty<SpawnInstruction>());

    var plan = new GamePlan(
        10,
        new[] { new ObjectiveRequirement("A", 1) },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry> { ["A"] = new(10, 10, 10) }),
        new FeatureConfiguration(InitialSpinCount: 1),
        new Dictionary<string, PrizeLevel> { ["A"] = PrizeLevel.Base },
        Thresholds(("A", 1), ("E", 30)),
        board,
        new[] { spin0, spin1 },
        Array.Empty<BoardState>(),
        new VerificationMetadata("test", 1, "test", DateTimeOffset.UnixEpoch));

    var report = new GamePlanVerifier().Verify(plan);
    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.Equal(2, report.SimulationResult!.Spins.Count);
    Assert.Equal(1, report.SimulationResult.CollectionCounts["A"]);
}

static void FlushFeatureCollectsFullColumn()
{
    var board = BoardState.Empty();
    board.Set(new BoardPosition(0, 0), BoardCell.FromSymbol("A"));
    board.Set(new BoardPosition(1, 0), BoardCell.FromSymbol("A"));
    board.Set(new BoardPosition(2, 0), BoardCell.FromSymbol("B"));
    board.Set(new BoardPosition(3, 0), BoardCell.FromSymbol("E"));
    board.Set(new BoardPosition(4, 0), BoardCell.FromSymbol("F"));
    board.Set(new BoardPosition(0, 4), BoardCell.FromFeature(FeatureKind.Flush));

    var spin = new SpinPlan(
        0,
        Array.Empty<FeatureLanding>(),
        new FeatureAction[] { new FlushAction(new BoardPosition(0, 4), 0) },
        new[] { new FeatureConversion(new BoardPosition(0, 4), BoardCell.FromSymbol("G")) },
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        Array.Empty<SpawnInstruction>());

    var plan = new GamePlan(
        20,
        new[] { new ObjectiveRequirement("A", 2), new ObjectiveRequirement("B", 1) },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry>
        {
            ["A"] = new(10, 10, 10),
            ["B"] = new(10, 10, 10)
        }),
        new FeatureConfiguration(),
        new Dictionary<string, PrizeLevel> { ["A"] = PrizeLevel.Base, ["B"] = PrizeLevel.Base },
        Thresholds(("A", 2), ("B", 1), ("E", 30), ("F", 30), ("G", 30)),
        board,
        new[] { spin },
        Array.Empty<BoardState>(),
        new VerificationMetadata("test", 5, "test", DateTimeOffset.UnixEpoch));

    var report = new GamePlanVerifier().Verify(plan);
    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.Equal(2, report.SimulationResult!.CollectionCounts["A"]);
    Assert.Equal(1, report.SimulationResult.CollectionCounts["B"]);
    Assert.Equal(1, report.SimulationResult.CollectionCounts["E"]);
    Assert.Equal(1, report.SimulationResult.CollectionCounts["F"]);
}

static void ValidFeatureChainIsAccepted()
{
    var board = BoardState.Empty();
    board.Set(new BoardPosition(0, 0), BoardCell.FromFeature(FeatureKind.Wheel));
    board.Set(new BoardPosition(4, 0), BoardCell.FromSymbol("A"));

    var spin = new SpinPlan(
        0,
        Array.Empty<FeatureLanding>(),
        Array.Empty<FeatureAction>(),
        new[] { new FeatureConversion(new BoardPosition(0, 0), BoardCell.FromFeature(FeatureKind.ExtraSpin, 1)) },
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        Array.Empty<SpawnInstruction>());

    var plan = new GamePlan(
        10,
        new[] { new ObjectiveRequirement("A", 1) },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry> { ["A"] = new(10, 10, 10) }),
        new FeatureConfiguration(),
        new Dictionary<string, PrizeLevel> { ["A"] = PrizeLevel.Base },
        Thresholds(("A", 1)),
        board,
        new[] { spin },
        Array.Empty<BoardState>(),
        new VerificationMetadata("test", 5, "test", DateTimeOffset.UnixEpoch));

    var report = new GamePlanVerifier().Verify(plan);
    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
}

static void DeterministicLoadScenariosVerify() => RunLoadTest(iterations: 25, printSummary: false);

static void RunLoadTest(int iterations, bool printSummary)
{
    var totalSpins = 0;
    var maxSpins = 0;
    var minSpins = int.MaxValue;

    for (var scenario = 0; scenario < iterations; scenario++)
    {
        var request = CreateLoadScenario(scenario);
        var plan = new OutcomePlanner().Generate(request);
        var report = new GamePlanVerifier().Verify(plan);
        Assert.True(report.IsValid, $"Scenario {scenario} failed: {string.Join("; ", report.Issues.Select(issue => issue.Message))}");
        Assert.True(plan.BoardStates.Count == plan.Spins.Count + 1, $"Scenario {scenario} board timeline mismatch.");

        foreach (var objective in request.Objectives)
        {
            Assert.Equal(objective.TargetCount, report.SimulationResult!.CollectionCounts[objective.Id]);
        }

        foreach (var (symbolId, threshold) in request.SymbolThresholds)
        {
            if (request.Objectives.Any(objective => objective.Id == symbolId))
            {
                continue;
            }

            Assert.True(report.SimulationResult!.CollectionCounts[symbolId] < threshold, $"Scenario {scenario} accidental win for {symbolId}.");
        }

        var json = new GamePlanJsonExporter().Export(plan);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("turns", out _), $"Scenario {scenario} JSON missing turns.");

        totalSpins += plan.Spins.Count;
        maxSpins = Math.Max(maxSpins, plan.Spins.Count);
        minSpins = Math.Min(minSpins, plan.Spins.Count);
    }

    if (printSummary)
    {
        Console.WriteLine("LOAD TEST PASS");
        Console.WriteLine($"Scenarios : {iterations}");
        Console.WriteLine($"Min spins : {minSpins}");
        Console.WriteLine($"Max spins : {maxSpins}");
        Console.WriteLine($"Avg spins : {totalSpins / (double)iterations:0.00}");
    }
}

static OutcomeRequest CreateLoadScenario(int scenario)
{
    var symbols = new[] { "A", "B", "C", "D", "E", "F" };
    var objectiveCount = 1 + (scenario % 4);
    var objectives = new List<ObjectiveRequirement>();
    var paytable = new Dictionary<string, PrizeTableEntry>(StringComparer.Ordinal);
    var thresholds = new Dictionary<string, int>(Thresholds(), StringComparer.Ordinal);

    for (var index = 0; index < objectiveCount; index++)
    {
        var symbol = symbols[(scenario + index) % symbols.Length];
        var target = 3 + ((scenario * 5 + index * 7) % 28);
        objectives.Add(new ObjectiveRequirement(symbol, target));
        thresholds[symbol] = target;
        paytable[symbol] = new PrizeTableEntry(10 + index, 20 + index, 30 + index);
    }

    foreach (var symbol in symbols.Except(objectives.Select(objective => objective.Id), StringComparer.Ordinal))
    {
        thresholds[symbol] = 40 + (scenario % 10);
    }

    var targetWin = objectives.Select((_, index) => 30 + index).Sum();
    return OutcomeRequest.Create(
        targetWin,
        objectives,
        new PaytableConfiguration(paytable),
        symbolThresholds: thresholds);
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

static void NonTargetSymbolsCountButStayBelowThreshold()
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
    Assert.Equal(7, report.SimulationResult.CollectionCounts["BELL"]);
}

static void VerifierRejectsAccidentalNonTargetWin()
{
    var board = BoardState.Empty();
    board.Set(new BoardPosition(4, 0), BoardCell.FromSymbol("A", 1));
    board.Set(new BoardPosition(4, 1), BoardCell.FromSymbol("Bell", 7));

    var spin = new SpinPlan(
        0,
        Array.Empty<FeatureLanding>(),
        Array.Empty<FeatureAction>(),
        Array.Empty<FeatureConversion>(),
        new[] { 1, 1, 1, 1, 1 },
        BoardRotation.None,
        Array.Empty<SpawnInstruction>());

    var plan = new GamePlan(
        10,
        new[] { new ObjectiveRequirement("A", 1) },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry> { ["A"] = new(10, 10, 10) }),
        new FeatureConfiguration(),
        new Dictionary<string, PrizeLevel> { ["A"] = PrizeLevel.Base },
        Thresholds(("A", 1), ("Bell", 7)),
        board,
        new[] { spin },
        Array.Empty<BoardState>(),
        new VerificationMetadata("test", 5, "test", DateTimeOffset.UnixEpoch));
    var report = new GamePlanVerifier().Verify(plan);

    Assert.False(report.IsValid, "Non-target threshold hit should invalidate the plan.");
    Assert.Contains(report.Issues, issue => issue.Code == "replay_failed" && issue.Message.Contains("Accidental non-target win", StringComparison.Ordinal));
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
        Thresholds(("A", targetCount)),
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
        }),
        symbolThresholds: Thresholds(("Gold", 8), ("Star", 6)));

static IReadOnlyDictionary<string, int> Thresholds(params (string SymbolId, int Threshold)[] overrides)
{
    var thresholds = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["A"] = 30,
        ["B"] = 30,
        ["C"] = 30,
        ["D"] = 30,
        ["E"] = 30,
        ["F"] = 30,
        ["G"] = 30,
        ["H"] = 30,
        ["I"] = 30,
        ["J"] = 30,
        ["Gold"] = 30,
        ["Star"] = 30,
        ["Bell"] = 30
    };

    foreach (var (symbolId, threshold) in overrides)
    {
        thresholds[symbolId] = threshold;
    }

    return thresholds;
}

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
