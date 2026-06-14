namespace CoinPusher.Engine;

public sealed class OutcomeSolver : IOutcomeSolver
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
