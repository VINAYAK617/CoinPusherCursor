namespace CoinPusher.Engine;

public static class FeatureDemoPlanFactory
{
    public static GamePlan Create()
    {
        var initialBoard = BoardState.Empty();
        FillNonTargets(initialBoard);

        initialBoard.Set(new BoardPosition(4, 0), BoardCell.FromSymbol("A"));
        initialBoard.Set(new BoardPosition(0, 1), BoardCell.FromSymbol("B"));
        initialBoard.Set(new BoardPosition(1, 1), BoardCell.FromSymbol("B"));

        var wheelPosition = new BoardPosition(0, 2);
        var flushPosition = new BoardPosition(0, 3);
        var spin = new SpinPlan(
            0,
            new[]
            {
                new FeatureLanding(wheelPosition, new FeatureToken(FeatureKind.Wheel)),
                new FeatureLanding(flushPosition, new FeatureToken(FeatureKind.Flush))
            },
            new FeatureAction[]
            {
                new WheelAction(wheelPosition, "A", 2),
                new FlushAction(flushPosition, 1)
            },
            new[]
            {
                new FeatureConversion(wheelPosition, BoardCell.FromSymbol("E")),
                new FeatureConversion(flushPosition, BoardCell.FromSymbol("F"))
            },
            new[] { 1, 1, 1, 1, 1 },
            BoardRotation.Clockwise,
            CreateFinalSpawns());

        return new GamePlan(
            20,
            new[]
            {
                new ObjectiveRequirement("A", 4),
                new ObjectiveRequirement("B", 2)
            },
            new PaytableConfiguration(new Dictionary<string, PrizeTableEntry>
            {
                ["A"] = new(10, 10, 10),
                ["B"] = new(10, 10, 10)
            }),
            new FeatureConfiguration(),
            new Dictionary<string, PrizeLevel>
            {
                ["A"] = PrizeLevel.Base,
                ["B"] = PrizeLevel.Base
            },
            new Dictionary<string, int>
            {
                ["A"] = 4,
                ["B"] = 2,
                ["C"] = 30,
                ["D"] = 30,
                ["E"] = 30,
                ["F"] = 30,
                ["G"] = 30,
                ["H"] = 30,
                ["I"] = 30,
                ["J"] = 30
            },
            initialBoard,
            new[] { spin },
            Array.Empty<BoardState>(),
            new VerificationMetadata("feature-demo", 5, nameof(FeatureDemoPlanFactory), DateTimeOffset.UtcNow));
    }

    private static void FillNonTargets(BoardState board)
    {
        var symbols = new[] { "E", "F", "G", "H", "I", "J" };
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                var symbol = symbols[(row + column) % symbols.Length];
                board.Set(new BoardPosition(row, column), BoardCell.FromSymbol(symbol));
            }
        }
    }

    private static IReadOnlyList<SpawnInstruction> CreateFinalSpawns()
    {
        var spawns = new List<SpawnInstruction>();
        var symbols = new[] { "E", "F", "G", "H", "I", "J" };
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            var position = new BoardPosition(row, EngineConstants.BoardColumns - 1);
            spawns.Add(new SpawnInstruction(position, BoardCell.FromSymbol(symbols[row % symbols.Length])));
        }

        return spawns;
    }
}
