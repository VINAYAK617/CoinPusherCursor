namespace CoinPusher.Engine;

public sealed class BackwardBoardPlanner : IBackwardBoardPlanner
{
    private readonly IEngineTraceSink _trace;

    public BackwardBoardPlanner(IEngineTraceSink? trace = null)
    {
        _trace = trace ?? NullEngineTraceSink.Instance;
    }

    public IReadOnlyList<PlannedCollectionBatch> Build(TimelinePlan timelinePlan)
    {
        ArgumentNullException.ThrowIfNull(timelinePlan);
        Trace("[board-planner] BACKWARD BOARD RECONSTRUCTION");
        Trace("[board-planner] Forward runtime is: start board -> collect -> rotate clockwise -> spawn -> next board.");
        Trace("[board-planner] Reverse planner does: next board -> remove spawns -> rotate anti-clockwise -> restore collected rows -> previous start board.");
        Trace($"[board-planner] Reconstructing {timelinePlan.SpinCount} spin start boards from the end of the game back to spin 1.");

        var reversedBatches = new Stack<PlannedCollectionBatch>();
        var nonTargetSelector = new NonTargetSymbolSelector(timelinePlan);
        var nextStartBoard = BuildFinalNonWinBoard(timelinePlan, nonTargetSelector);
        TraceBoard(
            "seed board after the last spin finishes (non-target symbols below threshold)",
            nextStartBoard);

        for (var spinIndex = timelinePlan.Spins.Count - 1; spinIndex >= 0; spinIndex--)
        {
            var spin = timelinePlan.Spins[spinIndex];
            var displaySpin = spin.SpinIndex + 1;
            var columnContributions = AssignContributionsToColumns(spin);
            var pushValues = columnContributions
                .Select(contributions => Math.Max(EngineConstants.MinimumPushValue, contributions.Count))
                .ToArray();
            Trace("");
            Trace($"[reverse spin {displaySpin}] Goal: build the board that the player will see at START of forward spin {displaySpin}.");
            Trace($"[reverse spin {displaySpin}] Known later board: this is the board after forward spin {displaySpin} already collected, rotated, and spawned.");
            Trace($"[reverse spin {displaySpin}] Planned forward harvest: {FormatContributions(spin.Contributions)}");
            Trace($"[reverse spin {displaySpin}] Chosen pushers: [{string.Join("] [", pushValues)}]");
            TraceBoard(
                $"reverse spin {displaySpin} input - later board / next spin start",
                nextStartBoard);

            var (afterRotation, spawns) = RemoveSpawns(nextStartBoard, pushValues);
            Trace($"[reverse spin {displaySpin}] Step 1: remove cells that must have been spawned at the END of forward spin {displaySpin}.");
            Trace($"[reverse spin {displaySpin}] Spawn plan created for forward replay: {FormatSpawns(spawns)}");
            TraceBoard(
                $"reverse spin {displaySpin} step 1 - after removing spawned cells",
                afterRotation);

            var afterCollection = afterRotation.Clone();
            afterCollection.Rotate(BoardRotation.CounterClockwise);
            Trace($"[reverse spin {displaySpin}] Step 2: rotate anti-clockwise to undo the forward clockwise rotation.");
            TraceBoard(
                $"reverse spin {displaySpin} step 2 - after anti-clockwise undo rotation",
                afterCollection);

            var startBoard = RestoreCollections(
                spin.SpinIndex,
                afterCollection,
                pushValues,
                columnContributions,
                timelinePlan,
                nonTargetSelector);
            Trace($"[reverse spin {displaySpin}] Step 3: restore the bottom collected rows so forward play collects exactly: {FormatContributions(spin.Contributions)}");
            Trace($"[reverse spin {displaySpin}] Result: this is the START board for forward spin {displaySpin}.");
            TraceBoard(
                $"forward spin {displaySpin} start board produced by reverse reconstruction",
                startBoard);

            var batch = new PlannedCollectionBatch(startBoard, pushValues, spawns);
            reversedBatches.Push(batch);
            nextStartBoard = startBoard;
        }

        return reversedBatches.ToArray();
    }

    private static BoardState BuildFinalNonWinBoard(TimelinePlan timelinePlan, NonTargetSymbolSelector nonTargetSelector)
    {
        var board = BoardState.Empty();
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                var position = new BoardPosition(row, column);
                board.Set(position, nonTargetSelector.CreateDecorativeCell(timelinePlan.SpinCount, position));
            }
        }

        return board;
    }

    private static IReadOnlyList<ContributionUnit>[] AssignContributionsToColumns(TimelineSpinContributions spin)
    {
        var columns = Enumerable
            .Range(0, EngineConstants.BoardColumns)
            .Select(_ => new List<ContributionUnit>())
            .ToArray();

        for (var index = 0; index < spin.Contributions.Count; index++)
        {
            var column = index % EngineConstants.BoardColumns;
            columns[column].Add(spin.Contributions[index]);
            if (columns[column].Count > EngineConstants.MaximumPushValue)
            {
                throw new PlanningException($"Spin {spin.SpinIndex} has more contributions than column {column} can collect.");
            }
        }

        return columns;
    }

    private static (BoardState AfterRotation, IReadOnlyList<SpawnInstruction> Spawns) RemoveSpawns(
        BoardState nextStartBoard,
        IReadOnlyList<int> pushValues)
    {
        var afterRotation = nextStartBoard.Clone();
        var spawns = new List<SpawnInstruction>();

        foreach (var (position, cell) in nextStartBoard.Cells())
        {
            var afterCollectionPosition = RotatePosition(position, BoardRotation.CounterClockwise);
            var pushValue = pushValues[afterCollectionPosition.Column];
            if (afterCollectionPosition.Row >= pushValue)
            {
                continue;
            }

            afterRotation.Set(position, BoardCell.Empty);
            if (cell.Kind != CellKind.Empty)
            {
                spawns.Add(new SpawnInstruction(position, cell));
            }
        }

        return (afterRotation, spawns);
    }

    private BoardState RestoreCollections(
        int spinIndex,
        BoardState afterCollection,
        IReadOnlyList<int> pushValues,
        IReadOnlyList<ContributionUnit>[] columnContributions,
        TimelinePlan timelinePlan,
        NonTargetSymbolSelector nonTargetSelector)
    {
        var startBoard = BoardState.Empty();

        for (var column = 0; column < EngineConstants.BoardColumns; column++)
        {
            var pushValue = pushValues[column];
            for (var row = pushValue; row < EngineConstants.BoardRows; row++)
            {
                var restoredPosition = new BoardPosition(row - pushValue, column);
                startBoard.Set(restoredPosition, afterCollection.Get(new BoardPosition(row, column)));
            }

            var contributionIndex = 0;
            for (var row = EngineConstants.BoardRows - pushValue; row < EngineConstants.BoardRows; row++)
            {
                var position = new BoardPosition(row, column);
                if (contributionIndex < columnContributions[column].Count)
                {
                    var contribution = columnContributions[column][contributionIndex++];
                    startBoard.Set(position, BoardCell.FromSymbol(contribution.ObjectiveId, contribution.Amount));
                }
                else
                {
                    startBoard.Set(position, nonTargetSelector.CreateCollectedCell(spinIndex, position));
                }
            }
        }

        startBoard.Validate();
        return startBoard;
    }

    private static int StableScore(string symbolId, int spinIndex, BoardPosition position)
    {
        var hash = spinIndex * 31 + position.Row * 11 + position.Column * 7;
        foreach (var character in symbolId)
        {
            hash = (hash * 17) + character;
        }

        return Math.Abs(hash);
    }

    private sealed class NonTargetSymbolSelector
    {
        private readonly IReadOnlyDictionary<string, int> _thresholds;
        private readonly IReadOnlyList<string> _symbols;
        private readonly Dictionary<string, int> _plannedCollections;
        private readonly Dictionary<string, int> _visualUsage;

        public NonTargetSymbolSelector(TimelinePlan timelinePlan)
        {
            _thresholds = timelinePlan.SymbolThresholds;
            _symbols = timelinePlan.SymbolThresholds.Keys
                .Where(symbol => !timelinePlan.ObjectiveIds.Contains(symbol))
                .OrderBy(symbol => symbol, StringComparer.Ordinal)
                .ToArray();

            if (_symbols.Count == 0)
            {
                throw new PlanningException("At least one non-target symbol is required to fill non-winning board space.");
            }

            _plannedCollections = _symbols.ToDictionary(symbol => symbol, _ => 0, StringComparer.Ordinal);
            _visualUsage = _symbols.ToDictionary(symbol => symbol, _ => 0, StringComparer.Ordinal);
        }

        public BoardCell CreateDecorativeCell(int spinIndex, BoardPosition position)
        {
            var symbolId = ChooseBalancedSymbol(spinIndex, position, requireCollectionCapacity: false);
            _visualUsage[symbolId]++;
            return BoardCell.FromSymbol(symbolId);
        }

        public BoardCell CreateCollectedCell(int spinIndex, BoardPosition position)
        {
            var symbolId = ChooseBalancedSymbol(spinIndex, position, requireCollectionCapacity: true);
            _visualUsage[symbolId]++;
            _plannedCollections[symbolId]++;
            return BoardCell.FromSymbol(symbolId);
        }

        private string ChooseBalancedSymbol(int spinIndex, BoardPosition position, bool requireCollectionCapacity)
        {
            foreach (var symbolId in _symbols
                .Where(symbol => !requireCollectionCapacity || _plannedCollections[symbol] + 1 < _thresholds[symbol])
                .OrderBy(symbol => _visualUsage[symbol])
                .ThenBy(symbol => _plannedCollections[symbol])
                .ThenBy(symbol => StableScore(symbol, spinIndex, position))
                .ThenBy(symbol => symbol, StringComparer.Ordinal))
            {
                return symbolId;
            }

            throw new PlanningException("Unable to select a safe non-target symbol without causing an accidental threshold win.");
        }
    }

    private static BoardPosition RotatePosition(BoardPosition position, BoardRotation rotation) =>
        rotation switch
        {
            BoardRotation.Clockwise => new BoardPosition(position.Column, EngineConstants.BoardColumns - 1 - position.Row),
            BoardRotation.CounterClockwise => new BoardPosition(EngineConstants.BoardRows - 1 - position.Column, position.Row),
            BoardRotation.HalfTurn => new BoardPosition(EngineConstants.BoardRows - 1 - position.Row, EngineConstants.BoardColumns - 1 - position.Column),
            BoardRotation.None => position,
            _ => throw new PlanningException($"Unsupported board rotation '{rotation}'.")
        };

    private static string FormatContributions(IReadOnlyList<ContributionUnit> contributions)
    {
        if (contributions.Count == 0)
        {
            return "none (only non-target symbols may be collected, still below threshold)";
        }

        return string.Join(
            ", ",
            contributions
                .GroupBy(contribution => contribution.ObjectiveId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}+={group.Sum(contribution => contribution.Amount)}"));
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
