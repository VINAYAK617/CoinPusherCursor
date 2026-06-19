namespace CoinPusherEngine;

/// <summary>
/// Distributes per-symbol win counts across spins using Earliest-Deadline-First (EDF).
///
/// ZONE CAPACITY — the key invariant:
/// Cap() allocates at most `maxZone = freeCols*MAX_PUSH + flushCols*ROWS - reserved` wins
/// to any spin. Builder.MakePushers computes:
///   needed = clamp(alloc_total, freeCols*MIN_PUSH, freeCols*MAX_PUSH)
/// Since alloc_total <= maxZone (by construction here), the clamp can never truncate below
/// alloc_total, so the actual zone the Builder creates is always >= alloc_total.
/// Zone overflow is therefore structurally impossible — this is the central correctness
/// guarantee of the whole pipeline.
///
/// TOKEN RESERVATION:
/// A token firing at spin F needs one filler slot in spin F's Spawns dict, covering
/// positions in spin (F+1)'s zone. So slot index F (0-based, = spin F+1's alloc) has its
/// capacity reduced by 1 per token firing at spin F.
/// </summary>
internal sealed class Scheduler
{
    private readonly IReadOnlyDictionary<int, int> _targets;
    private readonly List<PlacedFeat>              _placed;
    private readonly IReadOnlyList<WLock>          _locks;
    private readonly List<string>                  _log;

    internal Scheduler(IReadOnlyDictionary<int, int> targets,
                       List<PlacedFeat> placed, IReadOnlyList<WLock> locks, List<string> log)
    { _targets=targets; _placed=placed; _locks=locks; _log=log; }

    internal List<Dictionary<int, int>> Schedule(int totalSpins)
    {
        var tokenReserve = new Dictionary<int, int>();
        foreach (var f in _placed.Where(f => FeatReg.Has(f.Id) && FeatReg.Get(f.Id).HasToken))
            tokenReserve[f.Spin] = tokenReserve.GetValueOrDefault(f.Spin, 0) + 1;

        var slots = Enumerable.Range(0, totalSpins).Select(_ => new Dictionary<int, int>()).ToList();

        // Place WHEEL zone cells into slot[FireSpin], reduced by token reservation
        foreach (var lk in _locks)
        {
            if (lk.FireSpin >= totalSpins) continue;
            int reserve   = tokenReserve.GetValueOrDefault(lk.FireSpin, 0);
            int zoneCells = Math.Max(0, lk.Zone - reserve);
            Add(slots[lk.FireSpin], lk.Sym, zoneCells);
        }

        // EDF: distribute remaining wins, respecting exact zone capacity ceiling
        foreach (var (sym, target) in _targets)
        {
            var myLocks = _locks.Where(lk => lk.Sym == sym).OrderBy(lk => lk.FireSpin).ToList();

            int effectivePost = myLocks.Sum(lk =>
            {
                if (lk.FireSpin >= totalSpins) return 0;
                int reserve   = tokenReserve.GetValueOrDefault(lk.FireSpin, 0);
                int zoneCells = Math.Max(0, lk.Zone - reserve);
                return zoneCells * lk.Stack;
            });

            int remaining = Math.Max(0, target - effectivePost);
            int lastFireSpin = myLocks.Count > 0 ? myLocks[^1].FireSpin : 0;
            int deadline      = myLocks.Count > 0 ? myLocks[0].FireSpin - 1 : totalSpins;

            // Pass 1: fill pre-deadline spins (normal EDF — before the symbol's first WHEEL fires).
            // Spread across the least-loaded eligible spins instead of filling spin 1 to capacity.
            remaining = FillAcrossSlots(sym, remaining, Enumerable.Range(0, deadline), slots, tokenReserve);

            // Pass 2: post-deadline fallback. The symbol can still be collected normally
            // (unstacked) in spins after its WHEEL(s) fire — scan forward from just after
            // the last WHEEL's own zone slot to the second-to-last spin. Skips:
            //   • slot[deadline]..slot[lastFireSpin]: the WHEEL fire spin(s) and their own
            //     zone slots (already populated by the lock's Add call above; IsolateWheelSyms
            //     in Builder also actively clears this symbol from those zone positions).
            if (remaining > 0)
            {
                remaining = FillAcrossSlots(sym, remaining,
                    Enumerable.Range(lastFireSpin + 1, Math.Max(0, totalSpins - 1 - (lastFireSpin + 1))),
                    slots, tokenReserve);
            }

            if (remaining > 0)
            {
                _log.Add($"  WARN: {remaining} unplaced allocations sym={sym} -> forced last slot");
                Add(slots[totalSpins - 1], sym, remaining);
            }
        }

        return slots;
    }

    /// <summary>
    /// Hard ceiling for slot s (= spin s+1's alloc):
    ///   maxZone = freeCols*MAX_PUSH + flushCols*ROWS - reserved
    ///   cap     = maxZone - already
    /// WHEEL spins use the MIN_PUSH ceiling instead, since Builder pins WHEEL spins to
    /// MIN_PUSH unconditionally (to keep zone rows free for cells the WHEEL will stack).
    /// </summary>
    private int Cap(int s, List<Dictionary<int, int>> slots, Dictionary<int, int> tokenReserve)
    {
        int spinNum   = s + 1;
        int flushCols = _placed.Count(f => f.Id == "FLUSH" && f.Spin == spinNum);
        bool isWheel  = _locks.Any(lk => lk.FireSpin == spinNum);
        int freeCols  = K.COLS - flushCols;
        int flushCap  = flushCols * K.ROWS;
        int reserved  = tokenReserve.GetValueOrDefault(s, 0);
        int already   = slots[s].Values.Sum();

        int ceiling = isWheel
            ? freeCols * K.MIN_PUSH + flushCap - reserved
            : freeCols * K.MAX_PUSH + flushCap - reserved;

        return Math.Max(0, ceiling - already);
    }

    private int FillAcrossSlots(int sym, int remaining, IEnumerable<int> candidates,
                                List<Dictionary<int, int>> slots, Dictionary<int, int> tokenReserve)
    {
        var eligible = candidates.Distinct().Where(s => s >= 0 && s < slots.Count).ToList();
        while (remaining > 0)
        {
            var best = eligible
                .Select(s => (Slot: s, Cap: Cap(s, slots, tokenReserve), Load: slots[s].Values.Sum()))
                .Where(x => x.Cap > 0)
                .OrderBy(x => x.Load)
                .ThenBy(x => x.Slot)
                .FirstOrDefault();

            if (best.Cap <= 0) break;

            Add(slots[best.Slot], sym, 1);
            remaining--;
        }

        return remaining;
    }

    private static void Add(Dictionary<int, int> d, int k, int v)
    { if (v <= 0) return; d.TryGetValue(k, out int ex); d[k] = ex + v; }
}
