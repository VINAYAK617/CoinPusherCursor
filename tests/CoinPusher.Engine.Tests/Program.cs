using CoinPusher.Engine;

var tests = new (string Name, Action Run)[]
{
    ("planner creates exact verified outcome", PlannerCreatesExactVerifiedOutcome),
    ("wheel caps stacks and harvests potential", WheelCapsStacksAndHarvestsPotential),
    ("verifier rejects over collection", VerifierRejectsOverCollection),
    ("verifier rejects invalid feature chain", VerifierRejectsInvalidFeatureChain),
    ("ticket replay follows GDD pusher rotation spawn pipeline", TicketReplayFollowsGddPipeline),
    ("ticket replay fires wheel and isolates next zone", TicketReplayFiresWheelAndIsolatesNextZone),
    ("ticket verifier rejects filler cap", TicketVerifierRejectsFillerCap)
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

static void TicketReplayFollowsGddPipeline()
{
    var ticket = new CoinPusherTicket(
        Board(
            new[] { 2, 3, 4, 5, 6 },
            new[] { 3, 4, 5, 6, 7 },
            new[] { 4, 5, 6, 7, 8 },
            new[] { 5, 6, 7, 8, 9 },
            new[] { 1, 1, 2, 3, 4 }),
        new TicketWinInfo(1, new[] { new TicketWinSymbol(1, 2) }, Array.Empty<TicketPrizeTier>()),
        new[]
        {
            new TicketTurn(Pushers(1), Cells(2, 3, 4, 5, 6))
        });

    var report = new CoinPusherTicketVerifier().Verify(ticket);

    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.Equal(2, report.ReplayResult!.WinSymbolCounts[1]);
    Assert.Equal(5, report.ReplayResult.Turns[0].Collections.Count);
    Assert.True(report.ReplayResult.Turns[0].AfterRotation.Get(new BoardPosition(0, 4)) is null, "Rotation should move the pushed-away nulls to the rightmost column.");
    Assert.Equal(2, report.ReplayResult.Turns[0].EndBoard.Get(new BoardPosition(0, 4))!.Id);
    Assert.True(report.ReplayResult.BoardTimeline.Count == 2, "Replay should include initial and final board snapshots.");
}

static void TicketReplayFiresWheelAndIsolatesNextZone()
{
    var wheel = new TicketCell(
        TicketSymbolIds.Wheel,
        feature: new TicketFeature(
            TicketSymbolIds.Wheel,
            convertToId: 2,
            wheelSymbolId: 1,
            wheelStackMultiplier: 4));
    var ticket = new CoinPusherTicket(
        Board(
            new[] { 2, 3, 4, 5, 6 },
            new[] { 3, 4, 5, 6, 7 },
            new[] { 4, 5, 6, 7, 8 },
            new[] { 5, 6, 7, 8, 9 },
            new[] { 6, 7, 8, 9, 10 }),
        new TicketWinInfo(2, new[] { new TicketWinSymbol(1, 4) }, Array.Empty<TicketPrizeTier>()),
        new[]
        {
            new TicketTurn(Pushers(1), new[] { new TicketCell(2), new TicketCell(3), new TicketCell(4), wheel, new TicketCell(1) }),
            new TicketTurn(Pushers(1), Cells(2, 3, 4, 5, 6))
        });

    var report = new CoinPusherTicketVerifier().Verify(ticket);

    Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    Assert.Equal(4, report.ReplayResult!.WinSymbolCounts[1]);
    Assert.Equal(1, report.ReplayResult.Turns[0].FiredFeatures.Count);
    Assert.Equal(2, report.ReplayResult.Turns[0].EndBoard.Get(new BoardPosition(3, 4))!.Id);
    Assert.Equal(4, report.ReplayResult.Turns[0].EndBoard.Get(new BoardPosition(4, 4))!.StackValue);
}

static void TicketVerifierRejectsFillerCap()
{
    var ticket = new CoinPusherTicket(
        Board(
            new[] { 2, 2, 2, 2, 2 },
            new[] { 2, 2, 2, 2, 2 },
            new[] { 2, 2, 2, 2, 2 },
            new[] { 2, 2, 2, 2, 2 },
            new[] { 2, 2, 2, 2, 2 }),
        new TicketWinInfo(4, new[] { new TicketWinSymbol(1, 0) }, Array.Empty<TicketPrizeTier>()),
        new[]
        {
            new TicketTurn(Pushers(1), Cells(2, 2, 2, 2, 2)),
            new TicketTurn(Pushers(1), Cells(2, 2, 2, 2, 2)),
            new TicketTurn(Pushers(1), Cells(2, 2, 2, 2, 2)),
            new TicketTurn(Pushers(1), Cells(2, 2, 2, 2, 2))
        });

    var report = new CoinPusherTicketVerifier().Verify(ticket);

    Assert.False(report.IsValid, "Filler collection cap should invalidate the ticket.");
    Assert.Contains(report.Issues, issue => issue.Code == "ticket_replay_failed" && issue.Message.Contains("collection cap", StringComparison.Ordinal));
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

static IReadOnlyList<IReadOnlyList<TicketCell>> Board(params int[][] rows) =>
    rows.Select(row => (IReadOnlyList<TicketCell>)row.Select(id => new TicketCell(id)).ToArray()).ToArray();

static IReadOnlyList<TicketPusher> Pushers(int pushValue) =>
    Enumerable.Repeat(new TicketPusher(pushValue), EngineConstants.BoardColumns).ToArray();

static IReadOnlyList<TicketCell> Cells(params int[] ids) =>
    ids.Select(id => new TicketCell(id)).ToArray();

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
}
