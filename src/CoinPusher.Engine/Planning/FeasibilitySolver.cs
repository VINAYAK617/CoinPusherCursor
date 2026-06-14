namespace CoinPusher.Engine;

public sealed class FeasibilitySolver : IFeasibilitySolver
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
