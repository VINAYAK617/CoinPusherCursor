namespace CoinPusher.Engine;

public sealed class CoinPusherSimulator : IGameSimulator
{
    private readonly IEngineTraceSink _trace;

    public CoinPusherSimulator(IEngineTraceSink? trace = null)
    {
        _trace = trace ?? NullEngineTraceSink.Instance;
    }

    public SimulationResult Replay(GamePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlanEnvelope(plan);

        var objectiveTargets = plan.Objectives.ToDictionary(
            objective => objective.Id,
            objective => objective.TargetCount,
            StringComparer.Ordinal);
        var collectionCounts = plan.SymbolThresholds.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var prizeLevels = objectiveTargets.Keys.ToDictionary(id => id, _ => PrizeLevel.Base, StringComparer.Ordinal);

        var board = plan.InitialBoard.Clone();
        board.Validate();

        var availableSpins = plan.FeatureConfiguration.InitialSpinCount;
        var timeline = new List<BoardState> { board.Clone() };
        var spinResults = new List<SpinSimulationResult>();

        Trace($"[simulator] Replay start. initialSpins={availableSpins}, plannedSpins={plan.Spins.Count}");
        TraceBoard("replay initial board", board);

        for (var spinIndex = 0; spinIndex < plan.Spins.Count; spinIndex++)
        {
            var displaySpin = spinIndex + 1;
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

            Trace("");
            Trace($"[forward spin {displaySpin}] START");
            Trace($"[forward spin {displaySpin}] Where this board came from: {(spinIndex == 0 ? "initial board produced by backward reconstruction" : $"previous spin {displaySpin - 1} after spawn/end")}");
            Trace($"[forward spin {displaySpin}] Pushers: [{string.Join("] [", spin.PushValues)}]");
            Trace($"[forward spin {displaySpin}] Planned rotation after collection: {spin.Rotation}");
            TraceBoard($"forward spin {displaySpin} - board at start", board);

            ApplyFeatureLandings(board, spin, objectiveTargets);
            TraceBoard($"forward spin {displaySpin} - after feature landing", board);

            ApplyFeatureActions(board, spin, prizeLevels, collectionCounts, plan.SymbolThresholds, objectiveTargets, spinCollections, ref availableSpins);
            Trace($"[forward spin {displaySpin}] After feature activation: availableSpins={availableSpins}, prizes={BoardFormatter.FormatPrizeLevels(prizeLevels)}");
            TraceBoard($"forward spin {displaySpin} - after feature activation", board);

            ApplyFeatureConversions(board, spin);
            TraceBoard($"forward spin {displaySpin} - after feature conversion", board);

            Trace($"[forward spin {displaySpin}] Planned harvest from bottom rows before push: {FormatProjectedHarvest(board, spin, plan.SymbolThresholds)}");
            Trace($"[forward spin {displaySpin}] Collection step: each pusher removes that many cells from the bottom of its column.");
            ApplyPushes(board, spin, collectionCounts, plan.SymbolThresholds, objectiveTargets, spinCollections);
            Trace($"[forward spin {displaySpin}] Actually collected: {FormatCollections(spinCollections)}");
            Trace($"[forward spin {displaySpin}] Cumulative counts now: {BoardFormatter.FormatCounts(collectionCounts)}");
            TraceBoard($"forward spin {displaySpin} - after collection / before rotation", board);

            Trace($"[forward spin {displaySpin}] Rotation step: rotate clockwise to create empty spawn space.");
            board.Rotate(spin.Rotation);
            board.Validate();
            TraceBoard($"forward spin {displaySpin} - after clockwise rotation", board);

            Trace($"[forward spin {displaySpin}] Spawn step: fill the empty cells using the spawn plan created during backward reconstruction.");
            Trace($"[forward spin {displaySpin}] Spawn plan: {FormatSpawns(spin.Spawns)}");
            ApplySpawns(board, spin);
            board.Validate();
            TraceBoard($"forward spin {displaySpin} - after spawn / next spin start", board);

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
        Trace($"[simulator] Replay end. counts={BoardFormatter.FormatCounts(collectionCounts)}, prizes={BoardFormatter.FormatPrizeLevels(prizeLevels)}, payout={finalPayout}");
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
            if (!plan.SymbolThresholds.TryGetValue(objective.Id, out var threshold))
            {
                throw new SimulationException($"Objective '{objective.Id}' is missing from symbol thresholds.");
            }

            if (threshold != objective.TargetCount)
            {
                throw new SimulationException($"Objective '{objective.Id}' target must equal its symbol threshold.");
            }

            if (!plan.PlannedPrizeLevels.ContainsKey(objective.Id))
            {
                throw new SimulationException($"Plan is missing prize level for objective '{objective.Id}'.");
            }
        }
    }

    private static void ApplyFeatureLandings(
        BoardState board,
        SpinPlan spin,
        IReadOnlyDictionary<string, int> objectiveTargets)
    {
        foreach (var landing in spin.FeatureLandings)
        {
            var currentCell = board.Get(landing.Position);
            var canLand = currentCell.Kind == CellKind.Empty
                || (currentCell.Kind == CellKind.Symbol && !objectiveTargets.ContainsKey(currentCell.Symbol!.SymbolId));
            if (!canLand)
            {
                throw new SimulationException($"Feature landing at {landing.Position} can only replace empty or filler cells.");
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
        IReadOnlyDictionary<string, int> symbolThresholds,
        IReadOnlyDictionary<string, int> objectiveTargets,
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
                    ApplyFlush(board, spin.SpinIndex, flush, collectionCounts, symbolThresholds, objectiveTargets, spinCollections);
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
        IReadOnlyDictionary<string, int> symbolThresholds,
        IReadOnlyDictionary<string, int> objectiveTargets,
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

            CollectSymbol(spinIndex, cell.Symbol!, CollectionSource.Flush, position, collectionCounts, symbolThresholds, objectiveTargets, spinCollections);
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
        IReadOnlyDictionary<string, int> symbolThresholds,
        IReadOnlyDictionary<string, int> objectiveTargets,
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

                CollectSymbol(spin.SpinIndex, cell.Symbol!, CollectionSource.Push, position, collectionCounts, symbolThresholds, objectiveTargets, spinCollections);
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
        IReadOnlyDictionary<string, int> symbolThresholds,
        IReadOnlyDictionary<string, int> objectiveTargets,
        ICollection<CollectionEvent> spinCollections)
    {
        if (!symbolThresholds.TryGetValue(symbol.SymbolId, out var threshold))
        {
            throw new SimulationException($"Collected unknown symbol '{symbol.SymbolId}' at {position}.");
        }

        var nextCount = collectionCounts[symbol.SymbolId] + symbol.StackSize;
        if (objectiveTargets.TryGetValue(symbol.SymbolId, out var target))
        {
            if (nextCount > target)
            {
                throw new SimulationException($"Over-collection for objective '{symbol.SymbolId}'. Target {target}, attempted {nextCount}.");
            }
        }
        else if (nextCount >= threshold)
        {
            throw new SimulationException($"Accidental non-target win for symbol '{symbol.SymbolId}'. Threshold {threshold}, attempted {nextCount}.");
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

    private static string FormatCollections(IReadOnlyList<CollectionEvent> collections)
    {
        if (collections.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", collections.Select(collection => $"{collection.ObjectiveId}+={collection.Amount} ({collection.Source} {collection.Position})"));
    }

    private static string FormatProjectedHarvest(
        BoardState board,
        SpinPlan spin,
        IReadOnlyDictionary<string, int> symbolThresholds)
    {
        var projected = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var column = 0; column < EngineConstants.BoardColumns; column++)
        {
            var pushValue = spin.PushValues[column];
            for (var row = EngineConstants.BoardRows - pushValue; row < EngineConstants.BoardRows; row++)
            {
                var cell = board.Get(new BoardPosition(row, column));
                if (cell.Kind != CellKind.Symbol || !symbolThresholds.ContainsKey(cell.Symbol!.SymbolId))
                {
                    continue;
                }

                projected[cell.Symbol.SymbolId] = projected.TryGetValue(cell.Symbol.SymbolId, out var current)
                    ? current + cell.Symbol.StackSize
                    : cell.Symbol.StackSize;
            }
        }

        return projected.Count == 0 ? "none" : BoardFormatter.FormatCounts(projected);
    }

    private static string FormatSpawns(IReadOnlyList<SpawnInstruction> spawns)
    {
        if (spawns.Count == 0)
        {
            return "none";
        }

        return string.Join(
            ", ",
            spawns
                .OrderBy(spawn => spawn.Position.Row)
                .ThenBy(spawn => spawn.Position.Column)
                .Select(spawn => $"{spawn.Position}={spawn.Cell}"));
    }

    private void Trace(string message)
    {
        if (_trace.IsEnabled)
        {
            _trace.Write(message);
        }
    }

    private void TraceBoard(string title, BoardState board)
    {
        if (_trace.IsEnabled)
        {
            _trace.WriteBoard(title, board);
        }
    }
}
