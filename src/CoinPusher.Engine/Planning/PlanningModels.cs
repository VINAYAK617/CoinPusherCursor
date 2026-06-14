namespace CoinPusher.Engine;

public enum ContributionSource
{
    Normal,
    Wheel,
    Flush
}

public sealed record ContributionUnit(string ObjectiveId, int Amount, ContributionSource Source);

public sealed record ObjectiveContributionAllocation(string ObjectiveId, int Normal, int Wheel, int Flush)
{
    public int Total => Normal + Wheel + Flush;
}

public sealed record ContributionPlan(
    IReadOnlyList<ObjectiveContributionAllocation> Allocations,
    IReadOnlyList<ContributionUnit> Units);

public sealed record FeasibilityReport(
    bool IsFeasible,
    int RequiredContributionCells,
    int RequiredSpins,
    int AvailableInitialSpins,
    string? Reason);

public sealed record TimelineSpinContributions(int SpinIndex, IReadOnlyList<ContributionUnit> Contributions);

public sealed record TimelinePlan(
    int SpinCount,
    int ExtraSpinsRequired,
    IReadOnlyList<TimelineSpinContributions> Spins);

public sealed record PlannedCollectionBatch(BoardState Board, IReadOnlyList<int> PushValues);
