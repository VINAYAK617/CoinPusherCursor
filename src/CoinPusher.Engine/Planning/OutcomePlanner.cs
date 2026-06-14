namespace CoinPusher.Engine;

public sealed class OutcomePlanner : IOutcomePlanner
{
    private const string EngineVersion = "1.0.0";
    private readonly IGameSimulator _simulator;
    private readonly IGamePlanVerifier _verifier;
    private readonly IOutcomeSolver _outcomeSolver;
    private readonly IPrizePlanner _prizePlanner;
    private readonly IContributionPlanner _contributionPlanner;
    private readonly IFeasibilitySolver _feasibilitySolver;
    private readonly ITimelinePlanner _timelinePlanner;
    private readonly IBackwardBoardPlanner _backwardBoardPlanner;
    private readonly IEngineTraceSink _trace;

    public OutcomePlanner(
        IGameSimulator? simulator = null,
        IGamePlanVerifier? verifier = null,
        IOutcomeSolver? outcomeSolver = null,
        IPrizePlanner? prizePlanner = null,
        IContributionPlanner? contributionPlanner = null,
        IFeasibilitySolver? feasibilitySolver = null,
        ITimelinePlanner? timelinePlanner = null,
        IBackwardBoardPlanner? backwardBoardPlanner = null,
        IEngineTraceSink? trace = null)
    {
        _trace = trace ?? NullEngineTraceSink.Instance;
        _simulator = simulator ?? new CoinPusherSimulator(_trace);
        _verifier = verifier ?? new GamePlanVerifier(_simulator);
        _outcomeSolver = outcomeSolver ?? new OutcomeSolver();
        _prizePlanner = prizePlanner ?? new PrizePlanner();
        _contributionPlanner = contributionPlanner ?? new ContributionPlanner();
        _feasibilitySolver = feasibilitySolver ?? new FeasibilitySolver();
        _timelinePlanner = timelinePlanner ?? new TimelinePlanner();
        _backwardBoardPlanner = backwardBoardPlanner ?? new BackwardBoardPlanner(_trace);
    }

    public GamePlan Generate(OutcomeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        Trace($"[planner] Target win: {request.TargetWin}");
        var objectives = _outcomeSolver.Solve(request);
        Trace($"[planner] Objectives: {string.Join(", ", objectives.Select(objective => $"{objective.Id}={objective.TargetCount}"))}");

        var plannedPrizeLevels = _prizePlanner.Solve(request);
        Trace($"[planner] Prize levels: {BoardFormatter.FormatPrizeLevels(plannedPrizeLevels)}");

        var contributionPlan = _contributionPlanner.Plan(objectives);
        Trace($"[planner] Contribution units: {contributionPlan.Units.Count}");

        var feasibility = _feasibilitySolver.Evaluate(contributionPlan, request.FeatureConfiguration);
        Trace($"[planner] Feasibility: requiredCells={feasibility.RequiredContributionCells}, requiredSpins={feasibility.RequiredSpins}, initialSpins={feasibility.AvailableInitialSpins}");
        if (!feasibility.IsFeasible)
        {
            throw new PlanningException(feasibility.Reason ?? "Requested outcome is not feasible.");
        }

        var timelinePlan = _timelinePlanner.Plan(contributionPlan, request.FeatureConfiguration);
        Trace($"[planner] Timeline: spins={timelinePlan.SpinCount}, extraSpins={timelinePlan.ExtraSpinsRequired}");

        var collectionBatches = _backwardBoardPlanner.Build(timelinePlan);
        var spinBuilders = new List<SpinPlanBuilder>(timelinePlan.SpinCount);
        for (var spinIndex = 0; spinIndex < timelinePlan.SpinCount; spinIndex++)
        {
            var batch = spinIndex < collectionBatches.Count
                ? collectionBatches[spinIndex]
                : new PlannedCollectionBatch(BoardState.Empty(), DefaultPushValues(), Array.Empty<SpawnInstruction>());
            spinBuilders.Add(new SpinPlanBuilder(spinIndex, batch.Board, batch.PushValues, batch.Spawns));
        }

        SchedulePrizeAndSpinFeatures(request, plannedPrizeLevels, timelinePlan.ExtraSpinsRequired, spinBuilders);

        var initialBoard = spinBuilders.Count > 0 ? spinBuilders[0].StartBoard.Clone() : BoardState.Empty();
        TraceBoard("initial board before replay", initialBoard);

        var planWithoutSnapshots = CreatePlan(request, plannedPrizeLevels, initialBoard, spinBuilders, Array.Empty<BoardState>());
        var simulation = _simulator.Replay(planWithoutSnapshots);
        var plan = CreatePlan(request, plannedPrizeLevels, initialBoard, spinBuilders, simulation.BoardTimeline);
        var verification = _verifier.Verify(plan);

        if (!verification.IsValid)
        {
            var detail = string.Join("; ", verification.Issues.Select(issue => issue.Message));
            throw new PlanningException($"Generated plan failed verification: {detail}");
        }

        Trace("[planner] Verification passed. Generated plan is deterministic and exact.");
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

                var replacement = builder.StartBoard.Get(position);
                builder.ReservedFeaturePositions.Add(position);
                builder.FeatureLandings.Add(new FeatureLanding(position, new FeatureToken(pendingFeature.Kind)));
                builder.FeatureActions.Add(pendingFeature.CreateAction(position));
                builder.FeatureConversions.Add(new FeatureConversion(position, replacement));
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
                var cell = board.Get(position);
                if (cell.Kind == CellKind.Empty || (cell.Kind == CellKind.Symbol && !cell.Symbol!.ContributesToObjective))
                {
                    yield return position;
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

    private sealed class SpinPlanBuilder
    {
        public SpinPlanBuilder(
            int spinIndex,
            BoardState startBoard,
            IReadOnlyList<int> pushValues,
            IReadOnlyList<SpawnInstruction> spawns)
        {
            SpinIndex = spinIndex;
            StartBoard = startBoard.Clone();
            PushValues = pushValues.ToArray();
            Spawns.AddRange(spawns);
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
