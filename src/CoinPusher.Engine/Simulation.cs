namespace CoinPusher.Engine;

public sealed record CollectionEvent(
    int SpinIndex,
    string ObjectiveId,
    int Amount,
    CollectionSource Source,
    BoardPosition Position);

public sealed record SpinSimulationResult(
    int SpinIndex,
    BoardState StartBoard,
    BoardState EndBoard,
    IReadOnlyList<CollectionEvent> Collections);

public sealed record SimulationResult(
    IReadOnlyDictionary<string, int> CollectionCounts,
    IReadOnlyDictionary<string, PrizeLevel> PrizeLevels,
    int FinalPayout,
    IReadOnlyList<SpinSimulationResult> Spins,
    IReadOnlyList<BoardState> BoardTimeline);

public sealed class CoinPusherSimulator
{
    public SimulationResult Replay(GamePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlanEnvelope(plan);

        var targetCounts = plan.Objectives.ToDictionary(
            objective => objective.Id,
            objective => objective.TargetCount,
            StringComparer.Ordinal);
        var collectionCounts = targetCounts.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var prizeLevels = targetCounts.Keys.ToDictionary(id => id, _ => PrizeLevel.Base, StringComparer.Ordinal);

        var board = plan.InitialBoard.Clone();
        board.Validate();

        var availableSpins = plan.FeatureConfiguration.InitialSpinCount;
        var timeline = new List<BoardState> { board.Clone() };
        var spinResults = new List<SpinSimulationResult>();

        for (var spinIndex = 0; spinIndex < plan.Spins.Count; spinIndex++)
        {
            if (spinIndex >= availableSpins)
            {
                throw new SimulationException($"Spin {spinIndex} is not available. Add extra spins before planning beyond {availableSpins} spins.");
            }

            var spin = plan.Spins[spinIndex];
            if (spin.SpinIndex != spinIndex)
            {
                throw new SimulationException($"Spin plan index mismatch. Expected {spinIndex}, got {spin.SpinIndex}.");
            }

            if (plan.BoardStates.Count > 0 && !board.ValueEquals(plan.BoardStates[spinIndex]))
            {
                throw new SimulationException($"Board snapshot mismatch at start of spin {spinIndex}.");
            }

            var startBoard = board.Clone();
            var spinCollections = new List<CollectionEvent>();

            ApplyFeatureLandings(board, spin);
            ApplyFeatureActions(board, spin, prizeLevels, collectionCounts, targetCounts, spinCollections, ref availableSpins);
            ApplyFeatureConversions(board, spin);
            ApplyPushes(board, spin, collectionCounts, targetCounts, spinCollections);
            board.Rotate(spin.Rotation);
            board.Validate();
            ApplySpawns(board, spin);
            board.Validate();

            spinResults.Add(new SpinSimulationResult(spinIndex, startBoard, board.Clone(), spinCollections));
            timeline.Add(board.Clone());
        }

        if (plan.BoardStates.Count > 0 && plan.BoardStates.Count != timeline.Count)
        {
            throw new SimulationException($"Plan contains {plan.BoardStates.Count} board snapshots, but replay produced {timeline.Count}.");
        }

        if (plan.BoardStates.Count > 0 && !timeline[^1].ValueEquals(plan.BoardStates[^1]))
        {
            throw new SimulationException("Board snapshot mismatch at final board state.");
        }

        var finalPayout = CalculatePayout(plan, prizeLevels);
        return new SimulationResult(collectionCounts, prizeLevels, finalPayout, spinResults, timeline);
    }

    private static void ValidatePlanEnvelope(GamePlan plan)
    {
        if (plan.TargetWin < 0)
        {
            throw new SimulationException("Target win cannot be negative.");
        }

        plan.FeatureConfiguration.Validate();

        if (plan.Objectives.Count == 0)
        {
            throw new SimulationException("Plan must contain at least one objective.");
        }

        var seenObjectives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var objective in plan.Objectives)
        {
            if (!seenObjectives.Add(objective.Id))
            {
                throw new SimulationException($"Duplicate objective '{objective.Id}'.");
            }

            plan.Paytable.GetEntry(objective.Id);

            if (!plan.PlannedPrizeLevels.ContainsKey(objective.Id))
            {
                throw new SimulationException($"Plan is missing prize level for objective '{objective.Id}'.");
            }
        }
    }

    private static void ApplyFeatureLandings(BoardState board, SpinPlan spin)
    {
        foreach (var landing in spin.FeatureLandings)
        {
            if (board.Get(landing.Position).Kind != CellKind.Empty)
            {
                throw new SimulationException($"Feature landing at {landing.Position} would overwrite an occupied cell.");
            }

            if (landing.Feature.ChainDepth > EngineConstants.MaximumFeatureChainDepth)
            {
                throw new SimulationException($"Feature landing at {landing.Position} has invalid chain depth {landing.Feature.ChainDepth}.");
            }

            board.Set(landing.Position, BoardCell.FromFeature(landing.Feature));
        }

        board.Validate();
    }

    private static void ApplyFeatureActions(
        BoardState board,
        SpinPlan spin,
        IDictionary<string, PrizeLevel> prizeLevels,
        IDictionary<string, int> collectionCounts,
        IReadOnlyDictionary<string, int> targetCounts,
        ICollection<CollectionEvent> spinCollections,
        ref int availableSpins)
    {
        var activatedPositions = new HashSet<BoardPosition>();
        foreach (var action in spin.FeatureActions)
        {
            if (!activatedPositions.Add(action.SourcePosition))
            {
                throw new SimulationException($"Feature at {action.SourcePosition} cannot be activated more than once in a spin.");
            }

            switch (action)
            {
                case PrizeUpgradeAction prizeUpgrade:
                    EnsureFeature(board, prizeUpgrade.SourcePosition, FeatureKind.PrizeUpgrade);
                    ApplyPrizeUpgrade(prizeUpgrade, prizeLevels);
                    break;
                case WheelAction wheel:
                    EnsureFeature(board, wheel.SourcePosition, FeatureKind.Wheel);
                    ApplyWheel(board, wheel);
                    break;
                case FlushAction flush:
                    EnsureFeature(board, flush.SourcePosition, FeatureKind.Flush);
                    ApplyFlush(board, spin.SpinIndex, flush, collectionCounts, targetCounts, spinCollections);
                    break;
                case ExtraSpinAction extraSpin:
                    EnsureFeature(board, extraSpin.SourcePosition, FeatureKind.ExtraSpin);
                    if (extraSpin.SpinCount <= 0)
                    {
                        throw new SimulationException("Extra spin feature must award at least one spin.");
                    }

                    availableSpins += extraSpin.SpinCount;
                    break;
                default:
                    throw new SimulationException($"Unsupported feature action '{action.GetType().Name}'.");
            }
        }

        board.Validate();
    }

    private static void ApplyPrizeUpgrade(PrizeUpgradeAction action, IDictionary<string, PrizeLevel> prizeLevels)
    {
        var objectiveId = ObjectiveRequirement.NormalizeObjectiveId(action.ObjectiveId);
        if (!prizeLevels.TryGetValue(objectiveId, out var currentLevel))
        {
            throw new SimulationException($"Prize upgrade targets unknown objective '{objectiveId}'.");
        }

        if (currentLevel == PrizeLevel.Upgrade2)
        {
            throw new SimulationException($"Objective '{objectiveId}' cannot be upgraded beyond Upgrade2.");
        }

        prizeLevels[objectiveId] = (PrizeLevel)((int)currentLevel + 1);
    }

    private static void ApplyWheel(BoardState board, WheelAction action)
    {
        if (action.WheelValue is < EngineConstants.MinimumWheelValue or > EngineConstants.MaximumWheelValue)
        {
            throw new SimulationException($"Wheel value must be between 1 and 3. Got {action.WheelValue}.");
        }

        var targetObjectiveId = ObjectiveRequirement.NormalizeObjectiveId(action.TargetObjectiveId);
        var matchedSymbols = 0;
        var increasedSymbols = 0;
        var stackIncrement = 1 + action.WheelValue;

        foreach (var (position, cell) in board.Cells().ToArray())
        {
            if (cell.Kind != CellKind.Symbol || cell.Symbol!.SymbolId != targetObjectiveId)
            {
                continue;
            }

            matchedSymbols++;
            var upgraded = cell.Symbol.AddStacks(stackIncrement);
            if (upgraded.StackSize > cell.Symbol.StackSize)
            {
                increasedSymbols++;
            }

            board.Set(position, BoardCell.FromSymbol(upgraded));
        }

        if (matchedSymbols == 0 || increasedSymbols == 0)
        {
            throw new SimulationException($"Dead wheel is invalid. Target '{targetObjectiveId}' had no symbols that could receive stacks.");
        }
    }

    private static void ApplyFlush(
        BoardState board,
        int spinIndex,
        FlushAction action,
        IDictionary<string, int> collectionCounts,
        IReadOnlyDictionary<string, int> targetCounts,
        ICollection<CollectionEvent> spinCollections)
    {
        if (action.Column < 0 || action.Column >= EngineConstants.BoardColumns)
        {
            throw new SimulationException($"Flush column must be between 0 and 4. Got {action.Column}.");
        }

        var collectedAmount = 0;
        foreach (var (position, cell) in board.ClearColumn(action.Column))
        {
            if (cell.Kind != CellKind.Symbol)
            {
                continue;
            }

            CollectSymbol(spinIndex, cell.Symbol!, CollectionSource.Flush, position, collectionCounts, targetCounts, spinCollections);
            collectedAmount += cell.Symbol!.StackSize;
        }

        if (collectedAmount == 0)
        {
            throw new SimulationException($"Dead flush is invalid. Column {action.Column} had no collection value.");
        }
    }

    private static void ApplyFeatureConversions(BoardState board, SpinPlan spin)
    {
        var convertedPositions = new HashSet<BoardPosition>();
        foreach (var conversion in spin.FeatureConversions)
        {
            if (!convertedPositions.Add(conversion.SourcePosition))
            {
                throw new SimulationException($"Feature at {conversion.SourcePosition} cannot be converted more than once in a spin.");
            }

            var currentCell = board.Get(conversion.SourcePosition);
            if (currentCell.Kind != CellKind.Feature)
            {
                throw new SimulationException($"Feature conversion at {conversion.SourcePosition} requires a feature cell.");
            }

            if (conversion.Replacement.Kind == CellKind.Feature)
            {
                var expectedDepth = currentCell.Feature!.ChainDepth + 1;
                var actualDepth = conversion.Replacement.Feature!.ChainDepth;
                if (expectedDepth > EngineConstants.MaximumFeatureChainDepth || actualDepth != expectedDepth)
                {
                    throw new SimulationException($"Invalid feature chain at {conversion.SourcePosition}. Expected depth {expectedDepth}, got {actualDepth}.");
                }
            }

            board.Set(conversion.SourcePosition, conversion.Replacement);
        }

        board.Validate();
    }

    private static void ApplyPushes(
        BoardState board,
        SpinPlan spin,
        IDictionary<string, int> collectionCounts,
        IReadOnlyDictionary<string, int> targetCounts,
        ICollection<CollectionEvent> spinCollections)
    {
        for (var column = 0; column < EngineConstants.BoardColumns; column++)
        {
            var pushValue = spin.PushValues[column];
            foreach (var (position, cell) in board.PushColumn(column, pushValue))
            {
                if (cell.Kind == CellKind.Empty)
                {
                    continue;
                }

                if (cell.Kind == CellKind.Feature)
                {
                    throw new SimulationException($"Unconverted feature at {position} cannot be pushed into the collection edge.");
                }

                CollectSymbol(spin.SpinIndex, cell.Symbol!, CollectionSource.Push, position, collectionCounts, targetCounts, spinCollections);
            }
        }

        board.Validate();
    }

    private static void ApplySpawns(BoardState board, SpinPlan spin)
    {
        foreach (var spawn in spin.Spawns)
        {
            if (spawn.Cell.Kind == CellKind.Empty)
            {
                throw new SimulationException($"Spawn at {spawn.Position} cannot be empty.");
            }

            if (board.Get(spawn.Position).Kind != CellKind.Empty)
            {
                throw new SimulationException($"Spawn at {spawn.Position} would overwrite an occupied cell.");
            }

            if (spawn.Cell.Kind == CellKind.Feature && spawn.Cell.Feature!.ChainDepth > EngineConstants.MaximumFeatureChainDepth)
            {
                throw new SimulationException($"Spawned feature at {spawn.Position} has invalid chain depth {spawn.Cell.Feature.ChainDepth}.");
            }

            board.Set(spawn.Position, spawn.Cell);
        }
    }

    private static void CollectSymbol(
        int spinIndex,
        SymbolToken symbol,
        CollectionSource source,
        BoardPosition position,
        IDictionary<string, int> collectionCounts,
        IReadOnlyDictionary<string, int> targetCounts,
        ICollection<CollectionEvent> spinCollections)
    {
        if (!symbol.ContributesToObjective)
        {
            return;
        }

        if (!targetCounts.TryGetValue(symbol.SymbolId, out var target))
        {
            throw new SimulationException($"Collected unknown objective symbol '{symbol.SymbolId}' at {position}.");
        }

        var nextCount = collectionCounts[symbol.SymbolId] + symbol.StackSize;
        if (nextCount > target)
        {
            throw new SimulationException($"Over-collection for objective '{symbol.SymbolId}'. Target {target}, attempted {nextCount}.");
        }

        collectionCounts[symbol.SymbolId] = nextCount;
        spinCollections.Add(new CollectionEvent(spinIndex, symbol.SymbolId, symbol.StackSize, source, position));
    }

    private static void EnsureFeature(BoardState board, BoardPosition position, FeatureKind expectedKind)
    {
        var cell = board.Get(position);
        if (cell.Kind != CellKind.Feature || cell.Feature!.Kind != expectedKind)
        {
            throw new SimulationException($"Expected {expectedKind} feature at {position}.");
        }
    }

    private static int CalculatePayout(GamePlan plan, IReadOnlyDictionary<string, PrizeLevel> prizeLevels)
    {
        var payout = 0;
        foreach (var objective in plan.Objectives)
        {
            payout += plan.Paytable.GetEntry(objective.Id).GetValue(prizeLevels[objective.Id]);
        }

        return payout;
    }
}

public enum VerificationSeverity
{
    Error
}

public sealed record VerificationIssue(VerificationSeverity Severity, string Code, string Message);

public sealed record VerificationReport(bool IsValid, IReadOnlyList<VerificationIssue> Issues, SimulationResult? SimulationResult);

public sealed class GamePlanVerifier
{
    private readonly CoinPusherSimulator _simulator;

    public GamePlanVerifier(CoinPusherSimulator? simulator = null)
    {
        _simulator = simulator ?? new CoinPusherSimulator();
    }

    public VerificationReport Verify(GamePlan plan)
    {
        var issues = new List<VerificationIssue>();
        SimulationResult? result = null;

        try
        {
            result = _simulator.Replay(plan);
            VerifyObjectiveCompletion(plan, result, issues);
            VerifyPrizeLevels(plan, result, issues);
            VerifyPayout(plan, result, issues);
        }
        catch (OutcomeEngineException exception)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Error, "replay_failed", exception.Message));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Error, "invalid_plan", exception.Message));
        }

        return new VerificationReport(issues.Count == 0, issues, result);
    }

    private static void VerifyObjectiveCompletion(GamePlan plan, SimulationResult result, ICollection<VerificationIssue> issues)
    {
        foreach (var objective in plan.Objectives)
        {
            var actual = result.CollectionCounts[objective.Id];
            if (actual != objective.TargetCount)
            {
                issues.Add(new VerificationIssue(
                    VerificationSeverity.Error,
                    "objective_mismatch",
                    $"Objective '{objective.Id}' expected {objective.TargetCount}, collected {actual}."));
            }
        }
    }

    private static void VerifyPrizeLevels(GamePlan plan, SimulationResult result, ICollection<VerificationIssue> issues)
    {
        foreach (var objective in plan.Objectives)
        {
            var expected = plan.PlannedPrizeLevels[objective.Id];
            var actual = result.PrizeLevels[objective.Id];
            if (actual != expected)
            {
                issues.Add(new VerificationIssue(
                    VerificationSeverity.Error,
                    "prize_level_mismatch",
                    $"Objective '{objective.Id}' expected prize level {expected}, replay produced {actual}."));
            }
        }
    }

    private static void VerifyPayout(GamePlan plan, SimulationResult result, ICollection<VerificationIssue> issues)
    {
        if (result.FinalPayout != plan.TargetWin)
        {
            issues.Add(new VerificationIssue(
                VerificationSeverity.Error,
                "payout_mismatch",
                $"Target win {plan.TargetWin}, replay payout {result.FinalPayout}."));
        }
    }
}
