using CoinPusher.Engine;

var tests = new (string Name, Action Run)[]
{
    ("planner creates exact verified outcome", PlannerCreatesExactVerifiedOutcome),
    ("wheel caps stacks and harvests potential", WheelCapsStacksAndHarvestsPotential),
    ("verifier rejects over collection", VerifierRejectsOverCollection),
    ("verifier rejects invalid feature chain", VerifierRejectsInvalidFeatureChain),
    ("v23 planner creates exact verified ticket", V23PlannerCreatesExactVerifiedTicket),
    ("v23 prize upgrade counts from symbol globally", V23PrizeUpgradeCountsFromSymbolGlobally),
    ("v23 wheel stacks reserved next-spin zone cells", V23WheelStacksReservedNextSpinZoneCells),
    ("v23 extra spin and flush are represented in ticket", V23ExtraSpinAndFlushAreRepresentedInTicket),
    ("v23 verifier rejects tampered target", V23VerifierRejectsTamperedTarget)
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

static void V23PlannerCreatesExactVerifiedTicket()
{
    var input = new MathInput(
        new Dictionary<string, int>
        {
            ["DIAMOND"] = 12,
            ["GOLD"] = 8
        },
        TotalBaseSpins: 4,
        RngSeed: 9301);

    var plan = new CoinPusherV23Planner().Generate(input);
    var replay = new CoinPusherV23Verifier().Replay(plan);

    Assert.True(plan.Verified, "Planner should return a verified v23 ticket.");
    Assert.Equal(4, plan.TotalSpins);
    Assert.Equal(12, replay.CollectionCounts[SymbolTable.IdFor("DIAMOND")]);
    Assert.Equal(8, replay.CollectionCounts[SymbolTable.IdFor("GOLD")]);
    Assert.True(plan.SpinPlans.All(spin => spin.BoardAtStart.GetLength(0) == 5 && spin.BoardAtStart.GetLength(1) == 5), "Every v23 board must be 5x5.");
    Assert.True(plan.PlanLog.Any(line => line == "verification=passed"), "Plan log should record verification.");
}

static void V23PrizeUpgradeCountsFromSymbolGlobally()
{
    var input = new MathInput(
        new Dictionary<string, int> { ["DIAMOND"] = 6 },
        TotalBaseSpins: 3,
        RequiredFeatures: new Dictionary<string, int> { ["PRIZE_UPGRADE"] = 1 },
        PrizeUpgradeMap: new Dictionary<string, string> { ["SEVEN"] = "DIAMOND" },
        RngSeed: 9301);

    var plan = new CoinPusherV23Planner().Generate(input);
    var replay = new CoinPusherV23Verifier().Replay(plan);
    var diamond = SymbolTable.IdFor("DIAMOND");
    var seven = SymbolTable.IdFor("SEVEN");

    Assert.Equal(diamond, plan.PrizeUpgradeMap[seven]);
    Assert.Equal(6, replay.CollectionCounts[diamond]);
    Assert.True(PlanCells(plan).Any(cell => cell is { IsFeature: true, FeatureId: CoinPusherV23Constants.PrizeUpgrade }), "PRUP token should be present in planned spawns.");
    Assert.True(PlanCells(plan).Any(cell => cell is { IsFeature: false, SymId: 4 }), "Board should represent upgraded DIAMOND wins as SEVEN cells.");
}

static void V23WheelStacksReservedNextSpinZoneCells()
{
    var input = new MathInput(
        new Dictionary<string, int> { ["DIAMOND"] = 12 },
        TotalBaseSpins: 4,
        RequiredFeatures: new Dictionary<string, int> { ["WHEEL"] = 1 },
        WheelSymbolOrder: new[] { "DIAMOND" },
        RngSeed: 9301);

    var plan = new CoinPusherV23Planner().Generate(input);
    var replay = new CoinPusherV23Verifier().Replay(plan);
    var diamond = SymbolTable.IdFor("DIAMOND");
    var wheelLock = plan.WheelLocks.Single();
    var postWheelSpin = plan.SpinPlans[wheelLock.WheelSpin + 1];
    var stackedZoneCells = CountCells(
        postWheelSpin.BoardAtStart,
        cell => cell is { SymId: 1, StackCount: CoinPusherV23Constants.WheelStackMultiplier });

    Assert.Equal(diamond, wheelLock.SymId);
    Assert.Equal(12, replay.CollectionCounts[diamond]);
    Assert.Equal(3, stackedZoneCells);
    Assert.True(plan.SpinPlans[wheelLock.WheelSpin].Pushers.All(pusher => pusher.PushValue == CoinPusherV23Constants.MinPush), "WHEEL turn should use min push on every column.");
}

static void V23ExtraSpinAndFlushAreRepresentedInTicket()
{
    var input = new MathInput(
        new Dictionary<string, int> { ["DIAMOND"] = 20 },
        TotalBaseSpins: 2,
        RequiredFeatures: new Dictionary<string, int>
        {
            ["EXTRA_SPIN"] = 1,
            ["FLUSH"] = 1
        },
        RngSeed: 9301);

    var plan = new CoinPusherV23Planner().Generate(input);
    var replay = new CoinPusherV23Verifier().Replay(plan);

    Assert.Equal(3, plan.TotalSpins);
    Assert.Equal(20, replay.CollectionCounts[SymbolTable.IdFor("DIAMOND")]);
    Assert.True(plan.SpinPlans.Any(spin => spin.Pushers.Any(pusher => pusher.FeatureId == CoinPusherV23Constants.Flush && pusher.PushValue == CoinPusherV23Constants.FlushPush)), "FLUSH should be encoded as a pusher feature.");
    Assert.True(plan.SpinPlans.Any(spin => spin.PlannedSpawns.Values.Any(cell => cell.IsFeature && cell.FeatureId == CoinPusherV23Constants.ExtraSpin)), "EXTRA_SPIN token should be present in planned spawns.");
    Assert.True(plan.SpinPlans[2].IsExtraSpin, "Turn after base spins should be marked as an extra spin.");
    Assert.Equal(0, plan.SpinPlans[2].ParentTurnIndex!.Value);
}

static void V23VerifierRejectsTamperedTarget()
{
    var input = new MathInput(
        new Dictionary<string, int> { ["DIAMOND"] = 5 },
        TotalBaseSpins: 2,
        RngSeed: 9301);
    var plan = new CoinPusherV23Planner().Generate(input);
    var tamperedTargets = plan.TargetCollection.ToDictionary(entry => entry.Key, entry => entry.Value);
    tamperedTargets[SymbolTable.IdFor("DIAMOND")] = 6;
    var tamperedPlan = new GameMasterPlan(
        plan.TotalSpins,
        plan.SpinPlans,
        tamperedTargets,
        plan.WinSymIds,
        plan.FillerSymIds,
        plan.PrizeUpgradeMap,
        plan.WheelLocks,
        false,
        plan.PlanLog,
        plan.RngSeed,
        plan.TotalBaseSpins,
        plan.FeatureRegistry);

    Assert.Throws<InvalidOperationException>(() => new CoinPusherV23Verifier().VerifyPlan(tamperedPlan));
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

static IEnumerable<CellState> PlanCells(GameMasterPlan plan)
{
    foreach (var spin in plan.SpinPlans)
    {
        foreach (var cell in spin.PlannedSpawns.Values)
        {
            yield return cell;
        }

        for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
        {
            for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
            {
                var cell = spin.BoardAtStart[row, column];
                if (cell is not null)
                {
                    yield return cell;
                }
            }
        }
    }
}

static int CountCells(CellState?[,] board, Func<CellState, bool> predicate)
{
    var count = 0;
    for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
    {
        for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
        {
            var cell = board[row, column];
            if (cell is not null && predicate(cell))
            {
                count++;
            }
        }
    }

    return count;
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

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception of type {typeof(TException).Name}.");
    }
}
