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

public sealed class OutcomeSolver
{
    public IReadOnlyList<ObjectiveRequirement> Solve(OutcomeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Objectives.Count == 0)
        {
            throw new PlanningException("At least one objective is required.");
        }

        return request.Objectives;
    }
}

public sealed class PrizePlanner
{
    public IReadOnlyDictionary<string, PrizeLevel> Solve(OutcomeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var objectives = request.Objectives.ToArray();
        var selected = new PrizeLevel[objectives.Length];
        var levelOrder = request.StyleProfile.PreferUpgradedPrizes
            ? new[] { PrizeLevel.Upgrade2, PrizeLevel.Upgrade1, PrizeLevel.Base }
            : new[] { PrizeLevel.Base, PrizeLevel.Upgrade1, PrizeLevel.Upgrade2 };

        if (!Search(0, 0))
        {
            throw new PlanningException("No exact prize-level combination can realize the requested target win.");
        }

        return objectives
            .Select((objective, index) => new KeyValuePair<string, PrizeLevel>(objective.Id, selected[index]))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        bool Search(int objectiveIndex, int payout)
        {
            if (payout > request.TargetWin)
            {
                return false;
            }

            if (objectiveIndex == objectives.Length)
            {
                return payout == request.TargetWin;
            }

            var objective = objectives[objectiveIndex];
            var paytableEntry = request.Paytable.GetEntry(objective.Id);
            foreach (var level in levelOrder)
            {
                selected[objectiveIndex] = level;
                if (Search(objectiveIndex + 1, payout + paytableEntry.GetValue(level)))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

public sealed class ContributionPlanner
{
    public ContributionPlan Plan(IReadOnlyList<ObjectiveRequirement> objectives)
    {
        ArgumentNullException.ThrowIfNull(objectives);

        var allocations = new List<ObjectiveContributionAllocation>();
        var units = new List<ContributionUnit>();

        foreach (var objective in objectives)
        {
            allocations.Add(new ObjectiveContributionAllocation(objective.Id, objective.TargetCount, 0, 0));

            var remaining = objective.TargetCount;
            while (remaining > 0)
            {
                var amount = Math.Min(EngineConstants.MaximumStackSize, remaining);
                units.Add(new ContributionUnit(objective.Id, amount, ContributionSource.Normal));
                remaining -= amount;
            }
        }

        return new ContributionPlan(allocations, units);
    }
}

public sealed class FeasibilitySolver
{
    public FeasibilityReport Evaluate(ContributionPlan contributionPlan, FeatureConfiguration featureConfiguration)
    {
        ArgumentNullException.ThrowIfNull(contributionPlan);
        ArgumentNullException.ThrowIfNull(featureConfiguration);

        featureConfiguration.Validate();

        var requiredCells = contributionPlan.Units.Count;
        var cellsPerSpin = EngineConstants.BoardColumns * EngineConstants.MaximumPushValue;
        var requiredSpins = Math.Max(1, (int)Math.Ceiling(requiredCells / (double)cellsPerSpin));
        var isFeasible = requiredSpins >= 1;
        var reason = isFeasible ? null : "Contribution plan cannot be represented by valid pusher values.";

        return new FeasibilityReport(
            isFeasible,
            requiredCells,
            requiredSpins,
            featureConfiguration.InitialSpinCount,
            reason);
    }
}

public sealed class TimelinePlanner
{
    public TimelinePlan Plan(ContributionPlan contributionPlan, FeatureConfiguration featureConfiguration)
    {
        ArgumentNullException.ThrowIfNull(contributionPlan);
        ArgumentNullException.ThrowIfNull(featureConfiguration);

        var cellsPerSpin = EngineConstants.BoardColumns * EngineConstants.MaximumPushValue;
        var requiredSpins = Math.Max(1, (int)Math.Ceiling(contributionPlan.Units.Count / (double)cellsPerSpin));
        var spinCount = Math.Max(featureConfiguration.InitialSpinCount, requiredSpins);
        var spins = Enumerable
            .Range(0, spinCount)
            .Select(spinIndex => new List<ContributionUnit>())
            .ToArray();

        for (var index = 0; index < contributionPlan.Units.Count; index++)
        {
            spins[index % spinCount].Add(contributionPlan.Units[index]);
        }

        if (spins.Any(spin => spin.Count > cellsPerSpin))
        {
            throw new PlanningException("Timeline planner produced a spin that exceeds pusher collection capacity.");
        }

        return new TimelinePlan(
            spinCount,
            Math.Max(0, spinCount - featureConfiguration.InitialSpinCount),
            spins.Select((spin, index) => new TimelineSpinContributions(index, spin)).ToArray());
    }
}

public sealed class BackwardBoardPlanner
{
    public IReadOnlyList<PlannedCollectionBatch> Build(TimelinePlan timelinePlan)
    {
        ArgumentNullException.ThrowIfNull(timelinePlan);

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
            batches.Add(new PlannedCollectionBatch(board, pushValues));
        }

        return batches;
    }
}

public sealed class OutcomePlanner
{
    private const string EngineVersion = "1.0.0";
    private readonly CoinPusherSimulator _simulator;
    private readonly GamePlanVerifier _verifier;
    private readonly OutcomeSolver _outcomeSolver;
    private readonly PrizePlanner _prizePlanner;
    private readonly ContributionPlanner _contributionPlanner;
    private readonly FeasibilitySolver _feasibilitySolver;
    private readonly TimelinePlanner _timelinePlanner;
    private readonly BackwardBoardPlanner _backwardBoardPlanner;

    public OutcomePlanner(
        CoinPusherSimulator? simulator = null,
        GamePlanVerifier? verifier = null,
        OutcomeSolver? outcomeSolver = null,
        PrizePlanner? prizePlanner = null,
        ContributionPlanner? contributionPlanner = null,
        FeasibilitySolver? feasibilitySolver = null,
        TimelinePlanner? timelinePlanner = null,
        BackwardBoardPlanner? backwardBoardPlanner = null)
    {
        _simulator = simulator ?? new CoinPusherSimulator();
        _verifier = verifier ?? new GamePlanVerifier(_simulator);
        _outcomeSolver = outcomeSolver ?? new OutcomeSolver();
        _prizePlanner = prizePlanner ?? new PrizePlanner();
        _contributionPlanner = contributionPlanner ?? new ContributionPlanner();
        _feasibilitySolver = feasibilitySolver ?? new FeasibilitySolver();
        _timelinePlanner = timelinePlanner ?? new TimelinePlanner();
        _backwardBoardPlanner = backwardBoardPlanner ?? new BackwardBoardPlanner();
    }

    public GamePlan Generate(OutcomeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var objectives = _outcomeSolver.Solve(request);
        var plannedPrizeLevels = _prizePlanner.Solve(request);
        var contributionPlan = _contributionPlanner.Plan(objectives);
        var feasibility = _feasibilitySolver.Evaluate(contributionPlan, request.FeatureConfiguration);
        if (!feasibility.IsFeasible)
        {
            throw new PlanningException(feasibility.Reason ?? "Requested outcome is not feasible.");
        }

        var timelinePlan = _timelinePlanner.Plan(contributionPlan, request.FeatureConfiguration);
        var collectionBatches = _backwardBoardPlanner.Build(timelinePlan);

        var spinBuilders = new List<SpinPlanBuilder>(timelinePlan.SpinCount);
        for (var spinIndex = 0; spinIndex < timelinePlan.SpinCount; spinIndex++)
        {
            var batch = spinIndex < collectionBatches.Count ? collectionBatches[spinIndex] : new PlannedCollectionBatch(BoardState.Empty(), DefaultPushValues());
            spinBuilders.Add(new SpinPlanBuilder(spinIndex, batch.Board, batch.PushValues));
        }

        SchedulePrizeAndSpinFeatures(request, plannedPrizeLevels, timelinePlan.ExtraSpinsRequired, spinBuilders);
        ScheduleSpawns(collectionBatches, spinBuilders);

        var initialBoard = collectionBatches.Count > 0 ? collectionBatches[0].Board.Clone() : BoardState.Empty();
        var planWithoutSnapshots = CreatePlan(request, plannedPrizeLevels, initialBoard, spinBuilders, Array.Empty<BoardState>());
        var simulation = _simulator.Replay(planWithoutSnapshots);
        var plan = CreatePlan(request, plannedPrizeLevels, initialBoard, spinBuilders, simulation.BoardTimeline);
        var verification = _verifier.Verify(plan);

        if (!verification.IsValid)
        {
            var detail = string.Join("; ", verification.Issues.Select(issue => issue.Message));
            throw new PlanningException($"Generated plan failed verification: {detail}");
        }

        return plan;
    }

    private static void ValidateRequest(OutcomeRequest request)
    {
        if (request.TargetWin < 0)
        {
            throw new PlanningException("Target win cannot be negative.");
        }

        request.FeatureConfiguration.Validate();

        if (request.Objectives.Count == 0)
        {
            throw new PlanningException("At least one objective is required.");
        }

        var seenObjectives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var objective in request.Objectives)
        {
            if (!seenObjectives.Add(objective.Id))
            {
                throw new PlanningException($"Duplicate objective '{objective.Id}'.");
            }

            request.Paytable.GetEntry(objective.Id);
        }
    }

    private static void SchedulePrizeAndSpinFeatures(
        OutcomeRequest request,
        IReadOnlyDictionary<string, PrizeLevel> plannedPrizeLevels,
        int extraSpinsRequired,
        IReadOnlyList<SpinPlanBuilder> spinBuilders)
    {
        var pendingFeatures = new List<PendingFeature>();

        if (extraSpinsRequired > 0)
        {
            pendingFeatures.Add(PendingFeature.ExtraSpin(extraSpinsRequired));
        }

        foreach (var objective in request.Objectives)
        {
            var plannedLevel = plannedPrizeLevels[objective.Id];
            for (var upgrade = 0; upgrade < (int)plannedLevel; upgrade++)
            {
                pendingFeatures.Add(PendingFeature.PrizeUpgrade(objective.Id));
            }
        }

        foreach (var pendingFeature in pendingFeatures)
        {
            if (!TryScheduleFeature(pendingFeature, spinBuilders))
            {
                throw new PlanningException("Unable to schedule all feature activations without colliding with planned board values.");
            }
        }
    }

    private static bool TryScheduleFeature(PendingFeature pendingFeature, IReadOnlyList<SpinPlanBuilder> spinBuilders)
    {
        foreach (var builder in spinBuilders)
        {
            foreach (var position in FindFeatureLandingSlots(builder.StartBoard))
            {
                if (builder.ReservedFeaturePositions.Contains(position))
                {
                    continue;
                }

                builder.ReservedFeaturePositions.Add(position);
                builder.FeatureLandings.Add(new FeatureLanding(position, new FeatureToken(pendingFeature.Kind)));
                builder.FeatureActions.Add(pendingFeature.CreateAction(position));
                builder.FeatureConversions.Add(new FeatureConversion(position, BoardCell.Empty));
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<BoardPosition> FindFeatureLandingSlots(BoardState board)
    {
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                var position = new BoardPosition(row, column);
                if (board.Get(position).Kind == CellKind.Empty)
                {
                    yield return position;
                }
            }
        }
    }

    private static void ScheduleSpawns(IReadOnlyList<PlannedCollectionBatch> collectionBatches, IReadOnlyList<SpinPlanBuilder> spinBuilders)
    {
        for (var spinIndex = 0; spinIndex < spinBuilders.Count - 1; spinIndex++)
        {
            if (spinIndex + 1 >= collectionBatches.Count)
            {
                continue;
            }

            foreach (var (position, cell) in collectionBatches[spinIndex + 1].Board.Cells())
            {
                if (cell.Kind != CellKind.Empty)
                {
                    spinBuilders[spinIndex].Spawns.Add(new SpawnInstruction(position, cell));
                }
            }
        }
    }

    private static GamePlan CreatePlan(
        OutcomeRequest request,
        IReadOnlyDictionary<string, PrizeLevel> plannedPrizeLevels,
        BoardState initialBoard,
        IReadOnlyList<SpinPlanBuilder> spinBuilders,
        IReadOnlyList<BoardState> boardStates)
    {
        var spins = spinBuilders.Select(builder => builder.Build()).ToArray();
        var metadata = new VerificationMetadata(
            EngineVersion,
            request.FeatureConfiguration.InitialSpinCount,
            nameof(OutcomePlanner),
            DateTimeOffset.UtcNow);

        return new GamePlan(
            request.TargetWin,
            request.Objectives,
            request.Paytable,
            request.FeatureConfiguration,
            plannedPrizeLevels,
            initialBoard,
            spins,
            boardStates,
            metadata);
    }

    private static int[] DefaultPushValues() =>
        Enumerable.Repeat(EngineConstants.MinimumPushValue, EngineConstants.BoardColumns).ToArray();

    private sealed class SpinPlanBuilder
    {
        public SpinPlanBuilder(int spinIndex, BoardState startBoard, IReadOnlyList<int> pushValues)
        {
            SpinIndex = spinIndex;
            StartBoard = startBoard.Clone();
            PushValues = pushValues.ToArray();
        }

        public int SpinIndex { get; }

        public BoardState StartBoard { get; }

        public IReadOnlyList<int> PushValues { get; }

        public List<FeatureLanding> FeatureLandings { get; } = [];

        public List<FeatureAction> FeatureActions { get; } = [];

        public List<FeatureConversion> FeatureConversions { get; } = [];

        public List<SpawnInstruction> Spawns { get; } = [];

        public HashSet<BoardPosition> ReservedFeaturePositions { get; } = [];

        public SpinPlan Build() =>
            new(
                SpinIndex,
                FeatureLandings,
                FeatureActions,
                FeatureConversions,
                PushValues,
                BoardRotation.Clockwise,
                Spawns);
    }

    private sealed record PendingFeature
    {
        private PendingFeature(FeatureKind kind, string? objectiveId, int spinCount)
        {
            Kind = kind;
            ObjectiveId = objectiveId;
            SpinCount = spinCount;
        }

        public FeatureKind Kind { get; }

        public string? ObjectiveId { get; }

        public int SpinCount { get; }

        public static PendingFeature PrizeUpgrade(string objectiveId) =>
            new(FeatureKind.PrizeUpgrade, objectiveId, 0);

        public static PendingFeature ExtraSpin(int spinCount) =>
            new(FeatureKind.ExtraSpin, null, spinCount);

        public FeatureAction CreateAction(BoardPosition sourcePosition) =>
            Kind switch
            {
                FeatureKind.PrizeUpgrade => new PrizeUpgradeAction(sourcePosition, ObjectiveId!),
                FeatureKind.ExtraSpin => new ExtraSpinAction(sourcePosition, SpinCount),
                _ => throw new PlanningException($"Planner cannot create feature action for '{Kind}'.")
            };
    }
}
