namespace CoinPusherEngine;

internal static class Verifier
{
    internal static void Check(GamePlan plan)
    {
        var got = Sim.Run(plan);

        foreach (var (sym, target) in plan.Targets)
        {
            got.TryGetValue(sym, out int g);
            if (g != target)
                throw new InvalidOperationException(
                    $"VERIFY FAIL sym={sym} got={g} want={target}");
        }

        foreach (var (sym, target) in plan.NonWinTargets)
        {
            got.TryGetValue(sym, out int g);
            if (g < target || g >= K.FILL_CAP)
                throw new InvalidOperationException(
                    $"VERIFY FAIL nonwin sym={sym} got={g} want>={target} and <{K.FILL_CAP}");
        }

        foreach (var (sym, count) in got)
        {
            if (count == 0 || plan.Targets.ContainsKey(sym) || K.IsFeat(sym)) continue;
            if (count >= K.FILL_CAP)
                throw new InvalidOperationException(
                    $"VERIFY FAIL filler sym={sym} count={count} >= cap={K.FILL_CAP}");
        }
    }
}
