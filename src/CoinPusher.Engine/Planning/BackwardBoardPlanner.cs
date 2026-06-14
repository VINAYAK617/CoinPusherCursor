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
        Trace($"[board-planner] Building {timelinePlan.SpinCount} spin board states from planned contributions.");

        var batches = new List<PlannedCollectionBatch>(timelinePlan.Spins.Count);
        foreach (var spin in timelinePlan.Spins)
        {
            var board = BoardState.Empty();
            var perColumnCounts = new int[EngineConstants.BoardColumns];

            for (var index = 0; index < spin.Contributions.Count; index++)
            {
                var contribution = spin.Contributions[index];
                var column = index % EngineConstants.BoardColumns;
                var depth = index / EngineConstants.BoardColumns;
                if (depth >= EngineConstants.MaximumPushValue)
                {
                    throw new PlanningException($"Spin {spin.SpinIndex} has more contributions than pushers can collect.");
                }

                var row = EngineConstants.BoardRows - 1 - depth;
                board.Set(new BoardPosition(row, column), BoardCell.FromSymbol(contribution.ObjectiveId, contribution.Amount));
                perColumnCounts[column]++;
            }

            var pushValues = perColumnCounts
                .Select(count => Math.Max(EngineConstants.MinimumPushValue, count))
                .ToArray();
            var batch = new PlannedCollectionBatch(board, pushValues);
            batches.Add(batch);

            Trace($"[board-planner] Spin {spin.SpinIndex}: contributions={spin.Contributions.Count}, pushers=[{string.Join(", ", pushValues)}]");
            TraceBoard($"planned start board for spin {spin.SpinIndex}", board);
        }

        return batches;
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
