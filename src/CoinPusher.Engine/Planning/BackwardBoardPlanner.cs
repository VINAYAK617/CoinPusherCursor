namespace CoinPusher.Engine;

public sealed class BackwardBoardPlanner : IBackwardBoardPlanner
{
    private readonly IEngineTraceSink _trace;
    private readonly IFillerSymbolProvider _fillerSymbols;

    public BackwardBoardPlanner(IEngineTraceSink? trace = null, IFillerSymbolProvider? fillerSymbols = null)
    {
        _trace = trace ?? NullEngineTraceSink.Instance;
        _fillerSymbols = fillerSymbols ?? new DeterministicFillerSymbolProvider();
    }

    public IReadOnlyList<PlannedCollectionBatch> Build(TimelinePlan timelinePlan)
    {
        ArgumentNullException.ThrowIfNull(timelinePlan);
        Trace($"[board-planner] Backward reconstructing {timelinePlan.SpinCount} spin board states.");

        var reversedBatches = new Stack<PlannedCollectionBatch>();
        var nextStartBoard = BuildFinalNonWinBoard(timelinePlan.SpinCount);
        TraceBoard("final non-win board seed", nextStartBoard);

        for (var spinIndex = timelinePlan.Spins.Count - 1; spinIndex >= 0; spinIndex--)
        {
            var spin = timelinePlan.Spins[spinIndex];
            var columnContributions = AssignContributionsToColumns(spin);
            var pushValues = columnContributions
                .Select(contributions => Math.Max(EngineConstants.MinimumPushValue, contributions.Count))
                .ToArray();
            var (afterRotation, spawns) = RemoveSpawns(nextStartBoard, pushValues);
            var afterCollection = afterRotation.Clone();
            afterCollection.Rotate(BoardRotation.CounterClockwise);
            var startBoard = RestoreCollections(spin.SpinIndex, afterCollection, pushValues, columnContributions);

            var batch = new PlannedCollectionBatch(startBoard, pushValues, spawns);
            reversedBatches.Push(batch);
            nextStartBoard = startBoard;

            Trace($"[board-planner] Spin {spin.SpinIndex}: restored contributions={spin.Contributions.Count}, pushers=[{string.Join(", ", pushValues)}], spawns={spawns.Count}");
            TraceBoard($"spin {spin.SpinIndex} after removing spawns", afterRotation);
            TraceBoard($"spin {spin.SpinIndex} after anticlockwise undo rotation", afterCollection);
            TraceBoard($"planned start board for spin {spin.SpinIndex}", startBoard);
        }

        return reversedBatches.ToArray();
    }

    private BoardState BuildFinalNonWinBoard(int spinCount)
    {
        var board = BoardState.Empty();
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                var position = new BoardPosition(row, column);
                board.Set(position, _fillerSymbols.CreateFillerCell(spinCount, position));
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
        IReadOnlyList<ContributionUnit>[] columnContributions)
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
                    startBoard.Set(position, _fillerSymbols.CreateFillerCell(spinIndex, position));
                }
            }
        }

        startBoard.Validate();
        return startBoard;
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
