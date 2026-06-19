namespace CoinPusherEngine;

internal static class WMath
{
    // Smallest n>=1 where Zone(target,2^n)>=1 AND Zone*2^n<=target
    internal static int BestN(int target)
    {
        for (int n = 1; n <= 6; n++)
        {
            int stack = 1 << n;
            int zone  = Zone(target, stack);
            if (zone >= 1 && zone * stack <= target) return n;
        }
        return 3;
    }

    // min(target/stack, COLS-1)  — cap leaves 1 slot for the WHEEL token
    internal static int Zone(int target, int stack) =>
        Math.Min(target / stack, K.COLS - 1);

    internal static WLock MakeLock(int sym, int target, int fireSpin, int n)
    {
        int stack = 1 << n;
        int zone  = Zone(target, stack);
        int post  = zone * stack;
        return new WLock { Sym=sym, FireSpin=fireSpin, Stack=stack,
                           Zone=zone, Pre=Math.Max(0, target-post), Post=post };
    }

    internal static (WLock lk1, WLock lk2) MakeMultiLock(
        int sym, int total, int spin1, int n1, int spin2, int n2, int t1)
    {
        int t2 = total - t1;
        int s1=1<<n1, z1=Zone(t1,s1);
        int s2=1<<n2, z2=Zone(t2,s2);
        var lk1 = new WLock { Sym=sym, FireSpin=spin1, Stack=s1, Zone=z1,
                               Pre=Math.Max(0,t1-z1*s1), Post=z1*s1 };
        var lk2 = new WLock { Sym=sym, FireSpin=spin2, Stack=s2, Zone=z2, Pre=0, Post=z2*s2 };
        return (lk1, lk2);
    }

    // EDF feasibility: can newPre pre-wins fit in spins 1..newSpin-1?
    internal static bool EdfOk(int newPre, int newSpin,
        IEnumerable<PlacedFeat> existing,
        IReadOnlyDictionary<int, int> targets,
        bool isMulti)
    {
        var tasks  = new List<(int demand, int deadline)>();
        var wSpins = new HashSet<int> { newSpin };

        if (!isMulti && newPre > 0)
            tasks.Add((newPre, newSpin - 1));

        foreach (var f in existing.Where(f => f.Id == "WHEEL" && f.WSym != 0))
        {
            wSpins.Add(f.Spin);
            int t   = targets.GetValueOrDefault(f.WSym, 0);
            int st  = 1 << f.WN;
            int z   = Zone(t, st);
            int pre = Math.Max(0, t - z * st);
            if (pre > 0) tasks.Add((pre, f.Spin - 1));
        }

        tasks.Sort((a, b) => a.deadline.CompareTo(b.deadline));
        int cap=0, dem=0, ti=0;
        int maxDl = tasks.Count > 0 ? tasks.Max(x => x.deadline) : 0;
        for (int d = 1; d <= maxDl; d++)
        {
            cap += K.COLS * (wSpins.Contains(d) ? K.MIN_PUSH : K.MAX_PUSH);
            while (ti < tasks.Count && tasks[ti].deadline <= d) dem += tasks[ti++].demand;
            if (dem > cap) return false;
        }
        return true;
    }
}
