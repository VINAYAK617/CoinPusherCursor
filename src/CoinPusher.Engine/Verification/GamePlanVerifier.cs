namespace CoinPusher.Engine;

public sealed class GamePlanVerifier : IGamePlanVerifier
{
    private readonly IGameSimulator _simulator;

    public GamePlanVerifier(IGameSimulator? simulator = null)
    {
        _simulator = simulator ?? new CoinPusherSimulator();
    }

    public VerificationReport Verify(GamePlan plan)
    {
        var issues = new List<VerificationIssue>();
        SimulationResult? result = null;

        try
        {
            result = _simulator.Replay(plan);
            VerifyObjectiveCompletion(plan, result, issues);
            VerifyPrizeLevels(plan, result, issues);
            VerifyPayout(plan, result, issues);
        }
        catch (OutcomeEngineException exception)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Error, "replay_failed", exception.Message));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Error, "invalid_plan", exception.Message));
        }

        return new VerificationReport(issues.Count == 0, issues, result);
    }

    private static void VerifyObjectiveCompletion(GamePlan plan, SimulationResult result, ICollection<VerificationIssue> issues)
    {
        foreach (var objective in plan.Objectives)
        {
            var actual = result.CollectionCounts[objective.Id];
            if (actual != objective.TargetCount)
            {
                issues.Add(new VerificationIssue(
                    VerificationSeverity.Error,
                    "objective_mismatch",
                    $"Objective '{objective.Id}' expected {objective.TargetCount}, collected {actual}."));
            }
        }
    }

    private static void VerifyPrizeLevels(GamePlan plan, SimulationResult result, ICollection<VerificationIssue> issues)
    {
        foreach (var objective in plan.Objectives)
        {
            var expected = plan.PlannedPrizeLevels[objective.Id];
            var actual = result.PrizeLevels[objective.Id];
            if (actual != expected)
            {
                issues.Add(new VerificationIssue(
                    VerificationSeverity.Error,
                    "prize_level_mismatch",
                    $"Objective '{objective.Id}' expected prize level {expected}, replay produced {actual}."));
            }
        }
    }

    private static void VerifyPayout(GamePlan plan, SimulationResult result, ICollection<VerificationIssue> issues)
    {
        if (result.FinalPayout != plan.TargetWin)
        {
            issues.Add(new VerificationIssue(
                VerificationSeverity.Error,
                "payout_mismatch",
                $"Target win {plan.TargetWin}, replay payout {result.FinalPayout}."));
        }
    }
}
