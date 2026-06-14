namespace CoinPusher.Engine;

public interface IOutcomeSolver
{
    IReadOnlyList<ObjectiveRequirement> Solve(OutcomeRequest request);
}

public interface IPrizePlanner
{
    IReadOnlyDictionary<string, PrizeLevel> Solve(OutcomeRequest request);
}

public interface IContributionPlanner
{
    ContributionPlan Plan(IReadOnlyList<ObjectiveRequirement> objectives);
}

public interface IFeasibilitySolver
{
    FeasibilityReport Evaluate(ContributionPlan contributionPlan, FeatureConfiguration featureConfiguration);
}

public interface ITimelinePlanner
{
    TimelinePlan Plan(ContributionPlan contributionPlan, FeatureConfiguration featureConfiguration);
}

public interface IBackwardBoardPlanner
{
    IReadOnlyList<PlannedCollectionBatch> Build(TimelinePlan timelinePlan);
}

public interface IOutcomePlanner
{
    GamePlan Generate(OutcomeRequest request);
}

public interface IGameSimulator
{
    SimulationResult Replay(GamePlan plan);
}

public interface IGamePlanVerifier
{
    VerificationReport Verify(GamePlan plan);
}

public interface IEngineTraceSink
{
    bool IsEnabled { get; }

    void Write(string message);

    void WriteBoard(string title, BoardState board);
}
