namespace CoinPusherEngine;

/// <summary>
/// Pure math model of the physical board-cell capacity.
///
/// The user's exact framing:
///   No extra-spin: max achievable = 5 cols × S spins × 5 rows (flush) = 25S cells
///   Normal push only:                5 cols × S spins × 3 rows (max)  = 15S cells
///   WHEEL compresses win footprint → fewer physical cells for the same target
///
/// THE BINDING CONSTRAINT — filler capacity:
///   total_collected = S×FloorVol + dense_spins×(denseVol − FloorVol)
///   filler_collected = total_collected − physWins
///   filler_collected ≤ fillSymCount × FILL_CAP   (filler budget)
///
///   Solving for max allowable dense spins given S total spins:
///     denseAllowed = floor((physWins + fillerBudget − S×FloorVol) / (denseVol − FloorVol))
///   Solving for min required dense spins:
///     denseNeeded = ceil(max(0, physWins + tokenLoad − S×FloorVol) / (denseVol − FloorVol))
///
/// COUNTER-INTUITIVE: more spins WORSENS feasibility when filler budget is tight.
/// Every extra spin adds FloorVol(=5) cells of minimum filler even at MIN_PUSH.
///
/// FIXED-BASELINE RULE: BaseSpins is always exactly K.BASE_SPINS (=5) for every
/// ticket LadderCombinator produces — never a free variable to compute or clamp.
/// The only way to add capacity beyond the 5-spin baseline is to plan for and award
/// the EXTRA_SPIN feature, which can raise the total spin count up to K.MAX_SPINS
/// (=8): 5 base + up to 3 extra. So feasibility is no longer "what BaseSpins value
/// works?" — it's "how many EXTRA_SPIN awards (0..3) are needed at the fixed
/// baseline, and is the bundle even feasible at the 8-spin ceiling?"
/// </summary>
internal static class CapacityModel
{
    internal const int FloorVol  = K.COLS * K.MIN_PUSH;  // 5   — min cells collected per spin
    internal const int NormalVol = K.COLS * K.MAX_PUSH;  // 15  — max normal push per spin
    internal const int FlushVol  = K.COLS * K.ROWS;      // 25  — full-flush per spin

    // ── Physical win cell count ────────────────────────────────────────────
    /// <summary>
    /// Without WHEEL: every win requires 1 physical collected cell.
    /// With WHEEL on target T (stack = 2^BestN): physical = zone + pre
    ///   where zone = min(T/stack, COLS-1), pre = max(0, T − zone×stack).
    /// Applied greedily to the highest-target symbols first.
    /// </summary>
    internal static int PhysicalWins(IReadOnlyDictionary<int,int> targets, int wheelCount)
    {
        var ordered = targets.OrderByDescending(kv => kv.Value).ToList();
        int total   = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            int tgt = ordered[i].Value;
            if (i < wheelCount)
            {
                int n     = WMath.BestN(tgt);
                int stack = 1 << n;
                int zone  = WMath.Zone(tgt, stack);
                total    += zone + Math.Max(0, tgt - zone * stack);
            }
            else
                total += tgt;
        }
        return total;
    }

    // ── Feasibility ────────────────────────────────────────────────────────
    /// <summary>
    /// True iff physWins can be scheduled across totalSpins while keeping filler
    /// volume within fillSymCount × FILL_CAP.
    /// </summary>
    internal static bool IsFeasible(int physWins, int totalSpins, int fillSymCount,
                                     int denseVol = NormalVol, int tokenLoad = 0)
    {
        int load = physWins + tokenLoad;
        if (load > totalSpins * denseVol) return false;
        // Safety margin: the Verifier rejects count >= FILL_CAP (strict less-than).
        // Round-robin splitting across fillSymCount symbols is nearly even but not
        // perfectly guaranteed, so we require total filler to be safely below
        // fillSymCount × FILL_CAP rather than exactly equal — 2 cells of headroom
        // per filler symbol.
        int fillerBudget = fillSymCount * (K.FILL_CAP - 2);
        int denseNeeded  = DenseSpinsNeeded(load, totalSpins, denseVol);
        return denseNeeded <= DenseSpinsAllowed(physWins, totalSpins, fillerBudget, denseVol);
    }

    // ── Fixed-baseline capacity lever ─────────────────────────────────────
    /// <summary>
    /// Minimum number of EXTRA_SPIN awards needed so physWins fits within the filler
    /// budget at totalSpins = baseSpins + extras, capped at (K.MAX_SPINS - baseSpins)
    /// awards so totalSpins never exceeds K.MAX_SPINS (8). Returns -1 if not feasible
    /// even at that ceiling — a hard infeasibility that EXTRA_SPIN alone cannot fix;
    /// WHEEL/FLUSH compression of physWins (or fewer simultaneous win symbols) is
    /// required instead.
    ///
    /// baseSpins is normally K.BASE_SPINS (5, the engine's fixed default produced by
    /// LadderCombinator), but a hand-authored MathInput may set a different value —
    /// this always checks against whatever baseline is actually in play, never a
    /// hardcoded assumption.
    /// </summary>
    internal static int MinExtraSpins(int physWins, int fillSymCount,
                                      int baseSpins = K.BASE_SPINS, int tokenLoad = 0)
    {
        int maxExtras = Math.Max(0, K.MAX_SPINS - baseSpins);
        for (int extras = 0; extras <= maxExtras; extras++)
            if (IsFeasible(physWins, baseSpins + extras, fillSymCount,
                           tokenLoad: tokenLoad + extras))
                return extras;
        return -1;
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static int DenseSpinsNeeded(int load, int totalSpins, int denseVol)
    {
        int den = denseVol - FloorVol;
        if (den <= 0) return load <= totalSpins * FloorVol ? 0 : int.MaxValue;

        int aboveFloor = load - totalSpins * FloorVol;
        return aboveFloor <= 0 ? 0 : (int)Math.Ceiling((double)aboveFloor / den);
    }

    private static int DenseSpinsAllowed(int physWins, int totalSpins,
                                          int fillerBudget, int denseVol)
    {
        int num = physWins + fillerBudget - totalSpins * FloorVol;
        int den = denseVol - FloorVol;
        return den <= 0 ? 0 : (int)Math.Floor((double)num / den);
    }
}
