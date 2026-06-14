namespace CoinPusher.Engine;

public sealed class OutcomePlanner
{
    private const string EngineVersion = "1.0.0";
    private readonly CoinPusherSimulator _simulator;
    private readonly GamePlanVerifier _verifier;

    public OutcomePlanner(CoinPusherSimulator? simulator = null, GamePlanVerifier? verifier = null)
    {
        _simulator = simulator ?? new CoinPusherSimulator();
        _verifier = verifier ?? new GamePlanVerifier(_simulator);
    }

    public GamePlan Generate(OutcomeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var plannedPrizeLevels = SolvePrizeLevels(request);
        var collectionBatches = BuildCollectionBatches(request.Objectives);
        var requiredCollectionSpins = Math.Max(1, collectionBatches.Count);
        var spinCount = Math.Max(request.FeatureConfiguration.InitialSpinCount, requiredCollectionSpins);
        var extraSpinsRequired = Math.Max(0, requiredCollectionSpins - request.FeatureConfiguration.InitialSpinCount);

        var spinBuilders = new List<SpinPlanBuilder>(spinCount);
        for (var spinIndex = 0; spinIndex < spinCount; spinIndex++)
        {
            var batch = spinIndex < collectionBatches.Count ? collectionBatches[spinIndex] : new CollectionBatch(BoardState.Empty(), DefaultPushValues());
            spinBuilders.Add(new SpinPlanBuilder(spinIndex, batch.Board, batch.PushValues));
        }

        SchedulePrizeAndSpinFeatures(request, plannedPrizeLevels, extraSpinsRequired, spinBuilders);
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

    private static IReadOnlyDictionary<string, PrizeLevel> SolvePrizeLevels(OutcomeRequest request)
    {
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

    private static IReadOnlyList<CollectionBatch> BuildCollectionBatches(IReadOnlyList<ObjectiveRequirement> objectives)
    {
        var cells = new Queue<BoardCell>();
        foreach (var objective in objectives)
        {
            var remaining = objective.TargetCount;
            while (remaining > 0)
            {
                var stackSize = Math.Min(EngineConstants.MaximumStackSize, remaining);
                cells.Enqueue(BoardCell.FromSymbol(objective.Id, stackSize));
                remaining -= stackSize;
            }
        }

        var batches = new List<CollectionBatch>();
        while (cells.Count > 0)
        {
            var board = BoardState.Empty();
            var perColumnCounts = new int[EngineConstants.BoardColumns];
            var batchCount = Math.Min(EngineConstants.BoardColumns * EngineConstants.MaximumPushValue, cells.Count);

            for (var index = 0; index < batchCount; index++)
            {
                var column = index % EngineConstants.BoardColumns;
                var depth = index / EngineConstants.BoardColumns;
                var row = EngineConstants.BoardRows - 1 - depth;
                board.Set(new BoardPosition(row, column), cells.Dequeue());
                perColumnCounts[column]++;
            }

            var pushValues = perColumnCounts
                .Select(count => Math.Max(EngineConstants.MinimumPushValue, count))
                .ToArray();
            batches.Add(new CollectionBatch(board, pushValues));
        }

        return batches;
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

    private static void ScheduleSpawns(IReadOnlyList<CollectionBatch> collectionBatches, IReadOnlyList<SpinPlanBuilder> spinBuilders)
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

    private sealed record CollectionBatch(BoardState Board, IReadOnlyList<int> PushValues);

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
                BoardRotation.None,
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
