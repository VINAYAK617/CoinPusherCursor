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

            for (var count = 0; count < objective.TargetCount; count++)
            {
                units.Add(new ContributionUnit(objective.Id, 1, ContributionSource.Normal));
            }
        }

        return new ContributionPlan(allocations, units);
    }
}
