namespace CoinPusherEngine;

/// <summary>
/// Builds spin boards backwards (spin N → spin 1).
/// Pipeline per spin: RotCCW(next) → UndoPush → IsolateWheelSyms → FillZone → FillRest.
///
/// Token Slot Reservation:
/// A WHEEL token fires at spin F. Its spawn occupies a slot in spin F's Spawns dict.
/// Those spawns are positions null after simulating spin F's push+rotate, which are
/// positions in spin (F+1)'s board zone. So when building spin (F+1), one zone position
/// is reserved as filler (left null after FillZone; FillRest fills it). The Resolver
/// replaces it with the token. The Scheduler already reduced spin (F+1)'s alloc by 1.
///
/// Zone Capacity — exact match with Scheduler.Cap:
/// Scheduler.Cap allocates at most `maxZone = freeCols*MAX_PUSH + flushCols*ROWS - reserved`
/// wins into any given spin. Since alloc_total is therefore GUARANTEED to never exceed
/// maxZone, Builder can safely set:
///   needed = clamp(alloc_total, freeCols*MIN_PUSH, freeCols*MAX_PUSH)
/// This is always >= alloc_total (because alloc_total <= freeCols*MAX_PUSH by construction),
/// so zone overflow is structurally impossible. When alloc_total is small or zero, needed
/// collapses to the MIN_PUSH floor, avoiding wasteful filler churn in spins with few/no wins.
/// WHEEL spins are always pinned to MIN_PUSH regardless of alloc, to keep zone rows
/// available for cells that will be stacked by the WHEEL fire.
/// </summary>
internal sealed class Builder
{
    private readonly IReadOnlyDictionary<int, int> _targets;
    private readonly IReadOnlyList<WLock>          _locks;
    private readonly List<PlacedFeat>              _placed;
    private readonly int[]                         _fills;
    private readonly List<string>                  _log;
    private readonly Random                        _rng;
    private readonly FillTracker                   _fillTracker;

    // Decorative budget: how many extra (non-scheduled) occurrences of each win symbol
    // may additionally appear as ordinary board filler. Consumed directly from leftover
    // ZONE capacity in FillZone (see there) — never from arbitrary filler positions —
    // so every decorative placement is collected this same spin, by construction,
    // exactly like any other zone cell. There is no cross-spin survival question and
    // no risk of over- or under-counting.
    private readonly Dictionary<int, int> _decorBudget;
    private readonly Dictionary<int, int> _decorPlaced = new();

    internal Builder(IReadOnlyDictionary<int, int> targets, IReadOnlyList<WLock> locks,
                     List<PlacedFeat> placed, int[] fills, List<string> log, Random rng,
                     FillTracker fillTracker, Dictionary<int, int>? decorBudget = null)
    {
        _targets=targets; _locks=locks; _placed=placed; _fills=fills; _log=log; _rng=rng;
        _fillTracker=fillTracker;
        _decorBudget = decorBudget != null ? new Dictionary<int, int>(decorBudget) : new Dictionary<int, int>();
    }

    internal List<SpinPlan> BuildAll(List<PlacedFeat> placed,
                                      List<Dictionary<int, int>> allocs,
                                      int totalSpins, int baseSpins)
    {
        var plans     = new List<SpinPlan>();
        var nextBoard = new Cell?[K.ROWS, K.COLS];

        for (int s = totalSpins; s >= 1; s--)
        {
            var sf    = placed.Where(f => f.Spin == s).ToList();
            var alloc = s <= allocs.Count ? allocs[s - 1] : new Dictionary<int, int>();

            // Count token reservations needed in THIS spin's zone (tokens firing at spin s-1)
            int reservedCount = placed.Count(p => p.Spin == s - 1
                                               && FeatReg.Has(p.Id)
                                               && FeatReg.Get(p.Id).HasToken);

            var (push, flush) = MakePushers(s, sf, alloc, reservedCount);
            var tokenReserved = TokenReservedPositions(s - 1, push, flush);
            var board = BuildBoard(nextBoard, s, push, flush, alloc, tokenReserved);

            var plan = new SpinPlan
            {
                Spin=s, IsExtra=s>baseSpins,
                Board=board, Push=push, Flush=flush, Alloc=alloc,
            };
            foreach (var f in sf.Where(f => FeatReg.Has(f.Id) && FeatReg.Get(f.Id).HasToken))
                plan.Tokens.Add((f.Id, f.Col, MakeFP(f)));

            plans.Insert(0, plan);
            nextBoard = board;
        }

        return plans;
    }

    /// <summary>How many decorative occurrences were actually placed per symbol (always
    /// &lt;= the requested budget; Planner uses this to know the true final count).</summary>
    internal IReadOnlyDictionary<int, int> DecorativePlaced => _decorPlaced;

    // ── Token reservation ──────────────────────────────────────────────────
    private HashSet<(int r, int c)> TokenReservedPositions(int fireSpin, int[] push, bool[] flush)
    {
        var reserved = new HashSet<(int, int)>();
        var zoneSet  = Grid.ZoneSet(push, flush);

        foreach (var f in _placed.Where(p => p.Spin == fireSpin
                                          && FeatReg.Has(p.Id)
                                          && FeatReg.Get(p.Id).HasToken))
        {
            var preferred = (f.Col, K.COLS - 1);
            if (zoneSet.Contains(preferred) && !reserved.Contains(preferred))
            { reserved.Add(preferred); continue; }

            bool placed = false;
            for (int r2 = K.ROWS - 1; r2 >= 0 && !placed; r2--)
            {
                var pos = (r2, K.COLS - 1);
                if (zoneSet.Contains(pos) && !reserved.Contains(pos))
                { reserved.Add(pos); placed = true; }
            }
            if (!placed)
            {
                foreach (var pos in zoneSet.Where(p => !reserved.Contains(p))
                                           .OrderByDescending(p => p.c))
                { reserved.Add(pos); break; }
            }
        }
        return reserved;
    }

    // ── Board construction ─────────────────────────────────────────────────
    private Cell?[,] BuildBoard(Cell?[,] next, int spinNum,
                                 int[] push, bool[] flush,
                                 IReadOnlyDictionary<int, int> alloc,
                                 HashSet<(int, int)> tokenReserved)
    {
        var board = Grid.RotCCW(next);

        for (int col = 0; col < K.COLS; col++)
        {
            if (flush[col])
                for (int r = 0; r < K.ROWS; r++) board[r, col] = null;
            else
            {
                int p = push[col];
                for (int r = 0; r < K.ROWS; r++)
                    board[r, col] = r + p < K.ROWS ? board[r + p, col]?.Clone() : null;
            }
        }

        var zoneSet = Grid.ZoneSet(push, flush);
        IsolateWheelSyms(board, spinNum, zoneSet);
        FillZone(board, spinNum, push, flush, zoneSet, alloc, tokenReserved);
        FillRest(board, zoneSet);
        return board;
    }

    private void IsolateWheelSyms(Cell?[,] board, int spinNum, HashSet<(int, int)> zoneSet)
    {
        foreach (var lk in _locks)
        {
            int sym = lk.Sym;
            if (lk.FireSpin == spinNum)
            {
                for (int r = 0; r < K.ROWS; r++)
                for (int c = 0; c < K.COLS; c++)
                {
                    var cell = board[r, c];
                    if (cell != null && !cell.IsFeat && cell.Sym == sym && zoneSet.Contains((r, c)))
                        board[r, c] = null;
                }
            }
            else if (lk.FireSpin + 1 == spinNum)
            {
                for (int r = 0; r < K.ROWS; r++)
                for (int c = 0; c < K.COLS; c++)
                {
                    var cell = board[r, c];
                    if (cell != null && !cell.IsFeat && cell.Sym == sym && !zoneSet.Contains((r, c)))
                        board[r, c] = null;
                }
            }
        }
    }

    private void FillZone(Cell?[,] board, int spinNum, int[] push, bool[] flush,
                           HashSet<(int, int)> zoneSet,
                           IReadOnlyDictionary<int, int> alloc,
                           HashSet<(int, int)> tokenReserved)
    {
        var safe  = new List<(int r, int c)>();
        var spawn = new List<(int r, int c)>();

        for (int col = 0; col < K.COLS; col++)
        {
            IEnumerable<int> rows = flush[col]
                ? Enumerable.Range(0, K.ROWS)
                : Grid.ZoneRows(push[col]);
            foreach (int row in rows)
            {
                var pos = (row, col);
                if (tokenReserved.Contains(pos)) continue;
                (col == K.COLS - 1 ? spawn : safe).Add(pos);
            }
        }
        safe.Sort((a, b) => a.c != b.c ? a.c.CompareTo(b.c) : a.r.CompareTo(b.r));

        var q = new Queue<int>();
        foreach (var (sym, cnt) in alloc.OrderByDescending(kv => kv.Value))
            for (int i = 0; i < cnt; i++) q.Enqueue(sym);

        foreach (var (r, c) in safe.Concat(spawn)) board[r, c] = null;
        foreach (var pos in tokenReserved) board[pos.Item1, pos.Item2] = null;

        foreach (var (r, c) in safe.Concat(spawn))
        {
            if (q.Count > 0) { board[r, c] = Grid.Norm(q.Dequeue()); continue; }

            // Leftover zone capacity after the real, scheduled wins are placed — this
            // position is GUARANTEED collected THIS spin (it's inside the zone), so it's
            // the only place a "decorative" win-symbol filler can be placed with zero
            // risk of over- or under-counting: no rotation, no cross-spin survival
            // question, just an ordinary same-spin collection like any other zone cell.
            //
            // EXCEPTION: a symbol with a WHEEL lock fires by scanning the WHOLE board
            // for any cell matching its symbol and stacking it — a decorative cell would
            // get silently caught in that scan too, turning +1 into +stack. So decoration
            // for a symbol is only eligible at spins STRICTLY BEFORE every one of that
            // symbol's WHEEL fire spins: the cell is collected and gone well before the
            // WHEEL ever runs its scan, never coexisting with it.
            if (_decorBudget.Count > 0)
            {
                var eligible = _decorBudget
                    .Where(kv => kv.Value > 0 && IsDecorationEligible(kv.Key, spinNum))
                    .FirstOrDefault();
                if (eligible.Value > 0)
                {
                    _decorBudget[eligible.Key]--;
                    _decorPlaced[eligible.Key] = _decorPlaced.GetValueOrDefault(eligible.Key, 0) + 1;
                    board[r, c] = Grid.Norm(eligible.Key);
                    continue;
                }
            }
        }

        if (q.Count > 0)
            _log.Add($"  WARN: {q.Count} zone wins overflow — should be structurally impossible");
    }

    /// <summary>
    /// A symbol is eligible for decorative placement at a given spin only if EVERY
    /// WHEEL lock for that symbol fires at a STRICTLY LATER spin — i.e. this decoration
    /// is guaranteed collected and gone before any WHEEL scan for this symbol ever runs,
    /// so it can never be caught by that scan's stacking. Symbols with no WHEEL lock at
    /// all are always eligible.
    /// </summary>
    private bool IsDecorationEligible(int sym, int spinNum) =>
        _locks.Where(lk => lk.Sym == sym).All(lk => spinNum < lk.FireSpin);

    /// <summary>
    /// Fills every position the board still has null after FillZone — by construction
    /// these are positions OUTSIDE this spin's own collection zone (FillZone fills every
    /// zone position, real win or decorative). Always ordinary filler: a non-zone
    /// position isn't collected this spin, so it can't safely carry a decorative win
    /// symbol — see FillZone for where decoration actually happens.
    /// </summary>
    private void FillRest(Cell?[,] board, HashSet<(int, int)> curZoneSet)
    {
        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
            if (board[r, c] == null)
                board[r, c] = Grid.Norm(_fillTracker.Next());
    }

    // ── Pusher calculation ─────────────────────────────────────────────────
    /// <summary>
    /// needed = clamp(alloc_total, freeCols*MIN_PUSH, freeCols*MAX_PUSH)  [non-WHEEL]
    /// needed = freeCols*MIN_PUSH                                         [WHEEL]
    /// Since Scheduler.Cap never allocates more than freeCols*MAX_PUSH + flushCap - reserved
    /// to any spin, alloc_total can never exceed the MAX_PUSH ceiling, so this clamp can
    /// never truncate below alloc_total — zone overflow is structurally impossible.
    /// </summary>
    private (int[] push, bool[] flush) MakePushers(int spinNum, List<PlacedFeat> sf,
                                                    IReadOnlyDictionary<int, int> alloc,
                                                    int reserved)
    {
        var flushCols = sf.Where(f => f.Id == "FLUSH").Select(f => f.Col).ToHashSet();
        bool isWheel  = _locks.Any(lk => lk.FireSpin == spinNum);
        int freeCols  = K.COLS - flushCols.Count;
        // Zone must hold both the allocated wins AND the reserved token slot(s)
        int total     = alloc.Values.Sum() + reserved;

        int needed = isWheel
            ? freeCols * K.MIN_PUSH
            : Math.Clamp(total - flushCols.Count * K.ROWS, freeCols * K.MIN_PUSH, freeCols * K.MAX_PUSH);

        int[] pv = MakeVariedPushValues(freeCols, needed);

        var push  = new int[K.COLS];
        var flush = new bool[K.COLS];
        int fi = 0;
        for (int col = 0; col < K.COLS; col++)
        {
            if (flushCols.Contains(col)) { push[col] = K.ROWS; flush[col] = true; }
            else push[col] = pv[fi++];
        }
        return (push, flush);
    }

    private int[] MakeVariedPushValues(int freeCols, int needed)
    {
        if (freeCols <= 0) return Array.Empty<int>();

        int minTotal = freeCols * K.MIN_PUSH;
        int maxTotal = freeCols * K.MAX_PUSH;
        int targetTotal = Math.Clamp(needed, minTotal, maxTotal);

        if (IsDenseTicket())
            return MakeDeterministicPushValues(freeCols, targetTotal);

        var pv = Enumerable.Repeat(K.MIN_PUSH, freeCols).ToArray();
        int left = targetTotal - minTotal;
        while (left > 0)
        {
            bool placed = false;
            foreach (int idx in Enumerable.Range(0, freeCols).OrderBy(_ => _rng.Next()))
            {
                if (left == 0) break;
                if (pv[idx] >= K.MAX_PUSH) continue;
                pv[idx]++;
                left--;
                placed = true;
            }
            if (!placed) break;
        }

        // Same total, better shape: turn [2,2,2,2,2] into something like
        // [3,2,2,2,1] when possible. This keeps capacity unchanged.
        if (pv.Distinct().Count() == 1 && pv[0] > K.MIN_PUSH && pv[0] < K.MAX_PUSH)
        {
            int hi = _rng.Next(freeCols);
            int lo;
            do { lo = _rng.Next(freeCols); } while (lo == hi);
            pv[hi]++;
            pv[lo]--;
        }

        return pv;
    }

    private bool IsDenseTicket() =>
        _targets.Count >= 4 || _targets.Values.Sum() >= 80;

    private static int[] MakeDeterministicPushValues(int freeCols, int targetTotal)
    {
        var pv = Enumerable.Repeat(K.MIN_PUSH, freeCols).ToArray();
        int left = targetTotal - freeCols * K.MIN_PUSH;
        for (int i = 0; i < freeCols && left > 0; i++)
        {
            int add = Math.Min(K.MAX_PUSH - K.MIN_PUSH, left);
            pv[i] += add;
            left -= add;
        }
        return pv;
    }

    private static FP MakeFP(PlacedFeat f)
    {
        var fp = new FP { FeatId = f.Id };
        if (f.Id == "WHEEL")         { fp.WheelSym = f.WSym; fp.WheelStack = 1 << f.WN; }
        if (f.Id == "PRIZE_UPGRADE") { fp.PrupSym  = f.PrupSym; fp.PrupTier = f.PrupTier; }
        return fp;
    }
}
