namespace CoinPusher.Engine;

public sealed class TimelinePlanner : ITimelinePlanner
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
            .Select(_ => new List<ContributionUnit>())
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
            spins.Select((spin, index) => new TimelineSpinContributions(index, spin)).ToArray(),
            contributionPlan.SymbolThresholds,
            contributionPlan.ObjectiveIds);
    }
}
