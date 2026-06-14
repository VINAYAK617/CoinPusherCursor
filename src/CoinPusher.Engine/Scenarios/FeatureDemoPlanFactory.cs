namespace CoinPusher.Engine;

public static class FeatureDemoPlanFactory
{
    private static readonly string[] NonTargetSymbols = ["E", "F", "G", "H", "I", "J"];

    public static GamePlan Create()
    {
        var initialBoard = BoardState.Empty();
        FillNonTargets(initialBoard, offset: 0);

        initialBoard.Set(new BoardPosition(4, 0), BoardCell.FromSymbol("A"));
        initialBoard.Set(new BoardPosition(0, 1), BoardCell.FromSymbol("B"));
        initialBoard.Set(new BoardPosition(1, 1), BoardCell.FromSymbol("B"));

        var wheelPosition = new BoardPosition(0, 2);
        var flushPosition = new BoardPosition(0, 3);
        var spins = new List<SpinPlan>
        {
            new(
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
                CreateColumnSpawns(0, EngineConstants.BoardColumns - 1))
        };

        for (var spinIndex = 1; spinIndex < 5; spinIndex++)
        {
            spins.Add(new SpinPlan(
                spinIndex,
                Array.Empty<FeatureLanding>(),
                Array.Empty<FeatureAction>(),
                Array.Empty<FeatureConversion>(),
                new[] { 1, 1, 1, 1, 1 },
                BoardRotation.Clockwise,
                CreateColumnSpawns(spinIndex, EngineConstants.BoardColumns - 1)));
        }

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
            spins,
            Array.Empty<BoardState>(),
            new VerificationMetadata("feature-demo", 5, nameof(FeatureDemoPlanFactory), DateTimeOffset.UtcNow));
    }

    private static void FillNonTargets(BoardState board, int offset)
    {
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                var symbol = NonTargetSymbols[(offset + row + column) % NonTargetSymbols.Length];
                board.Set(new BoardPosition(row, column), BoardCell.FromSymbol(symbol));
            }
        }
    }

    private static IReadOnlyList<SpawnInstruction> CreateColumnSpawns(int spinIndex, int column)
    {
        var spawns = new List<SpawnInstruction>();
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            var position = new BoardPosition(row, column);
            var symbol = NonTargetSymbols[(spinIndex + row + column) % NonTargetSymbols.Length];
            spawns.Add(new SpawnInstruction(position, BoardCell.FromSymbol(symbol)));
        }

        return spawns;
    }
}
