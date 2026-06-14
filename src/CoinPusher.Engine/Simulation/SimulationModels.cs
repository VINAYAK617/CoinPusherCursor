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
