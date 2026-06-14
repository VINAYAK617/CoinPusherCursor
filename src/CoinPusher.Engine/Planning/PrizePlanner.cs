namespace CoinPusher.Engine;

public sealed class PrizePlanner : IPrizePlanner
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
