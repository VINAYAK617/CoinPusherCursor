namespace CoinPusher.Engine;

public sealed class ContributionPlanner : IContributionPlanner
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
