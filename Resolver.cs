namespace CoinPusherEngine;

/// <summary>
/// Forward-pass spawn resolver.
/// For each consecutive pair (cur, next):
///   1. Simulate cur's push+rotate to get the naturally-delivered board.
///   2. For EVERY position in next.Board (not just zone rows), spawn the planned cell
///      only when the simulation leaves that position empty. Natural cells are never
///      overwritten by spawns; if a non-empty position differs, the natural cell becomes
///      the next planned board cell and Verifier decides whether the outcome still works.
///   3. Place feature tokens into reserved filler-spawn slots only.
///
/// Full-board validation (not zone-only) is required for correctness: a mismatch in a
/// non-zone position is invisible this spin but corrupts whatever zone it rotates into
/// on a future spin. Validating every position, every spin, eliminates this entire class
/// of propagation bugs structurally rather than patching individual symptoms.
/// </summary>
internal sealed class Resolver
{
    private readonly int[]        _fills;
    private readonly HashSet<int> _winSyms;
    private readonly HashSet<int> _nonWinSyms;
    private readonly int[]        _endBoardSyms;
    private readonly List<string> _log;
    private readonly Random       _rng;
    private readonly FillTracker  _fillTracker;

    internal Resolver(int[] fills, HashSet<int> winSyms, List<string> log, Random rng,
                       FillTracker fillTracker, IEnumerable<int>? nonWinSyms = null)
    {
        _fills=fills; _winSyms=winSyms; _log=log; _rng=rng; _fillTracker=fillTracker;
        _nonWinSyms = nonWinSyms != null ? new HashSet<int>(nonWinSyms) : new HashSet<int>();
        _endBoardSyms = _fills.Concat(_winSyms).Distinct().OrderBy(sym => sym).ToArray();
    }

    internal void Resolve(List<SpinPlan> plans)
    {
        for (int i = 0; i < plans.Count - 1; i++)
            DoSpin(plans[i], plans[i + 1]);
        DoLast(plans[^1]);
    }

    /// <summary>
    /// For each position in next.Board, spawn the planned cell only if the natural
    /// simulation leaves that position empty. Correctness requires this check to cover
    /// EVERY board position: a mismatch in an occupied non-zone position must follow
    /// natural board motion, not a visual overwrite.
    /// </summary>
    private void DoSpin(SpinPlan cur, SpinPlan next)
    {
        var sim = Simulate(cur);

        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
        {
            var plan = next.Board[r, c];
            if (plan == null) continue;

            var sv = sim[r, c];

            bool needSpawn = sv == null
                          || sv.IsFeat != plan.IsFeat
                          || sv.Sym    != plan.Sym
                          || sv.Stack  != plan.Stack;

            if (needSpawn && sv != null)
            {
                next.Board[r, c] = sv.Clone();
                continue;
            }

            if (needSpawn)
                cur.Spawns[(r, c)] = plan.Clone();
        }

        PlaceTokens(cur, next);
    }

    private void DoLast(SpinPlan last)
    {
        var sim = Simulate(last);
        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
            if (sim[r, c] == null)
                last.Spawns[(r, c)] = Grid.Norm(EndBoardSym());
    }

    private int EndBoardSym() =>
        _endBoardSyms.Length > 0
            ? _endBoardSyms[_rng.Next(_endBoardSyms.Length)]
            : _fillTracker.Next();

    private static Cell?[,] Simulate(SpinPlan sp)
    {
        var b = Grid.Clone(sp.Board);
        FlattenFeats(b);

        for (int col = 0; col < K.COLS; col++)
        {
            if (sp.Flush[col])
                for (int r = 0; r < K.ROWS; r++) b[r, col] = null;
            else
            {
                int p = sp.Push[col];
                for (int r = K.ROWS - 1; r >= 0; r--)
                {
                    int src = r - p;
                    b[r, col] = src >= 0 ? b[src, col]?.Clone() : null;
                }
            }
        }
        return Grid.RotCW(b);
    }

    private void PlaceTokens(SpinPlan sp, SpinPlan next)
    {
        foreach (var (featId, origCol, fp) in sp.Tokens)
        {
            if (!FeatReg.Has(featId)) continue;
            var feat = FeatReg.Get(featId);

            var slot = FindFillerSlot(sp.Spawns, origCol);

            // FindFillerSlot only searches EXISTING spawn entries — but a spawn entry
            // only exists where the natural forward simulation disagreed with the plan
            // (see Resolver's class doc). If every position the plan reserved for this
            // token's filler happens to match what the natural simulation already
            // delivers there, NO spawn entry exists at all, even though the PLANNED
            // board genuinely has an ordinary filler symbol sitting exactly where
            // Builder's TokenReservedPositions/FillZone meant for this token to land.
            // This was a real, confirmed bug: tokens (mostly EXTRA_SPIN and
            // PRIZE_UPGRADE) were silently dropped from the ticket entirely whenever
            // this coincidence occurred — Verifier never caught it because it only
            // checks COLLECTED TOTALS, not whether every planned feature token
            // actually reached the board.
            //
            // IMPORTANT: sp.Spawns keys are POST-ROTATION coordinates — ApplySpawns
            // writes them onto the board AFTER RotCW, which means they describe
            // positions on `next`'s board, not `sp`'s own pre-rotation board. So the
            // fallback must scan `next.Board`, restricted to `next`'s OWN collection
            // zone (its Push/Flush) — that is the same plan TokenReservedPositions
            // used when it originally reserved room for this exact token.
            if (slot == default)
                slot = FindBoardFallbackSlot(sp.Spawns, next, origCol);

            if (slot == default)
            {
                throw new InvalidOperationException(
                    $"No filler slot available for required {featId} token at spin {sp.Spin}. " +
                    "Retry with a different seed or adjust feature load.");
            }

            int cvt = sp.Spawns.TryGetValue(slot, out var existing)
                ? existing.Sym
                : next.Board[slot.Item1, slot.Item2]!.Sym;
            sp.Spawns[slot] = Grid.Feat(feat.FeatSym, cvt, fp);
        }
    }

    /// <summary>
    /// Scans `next`'s planned board directly, restricted to positions inside next's
    /// own collection zone (so the token is guaranteed to fire when that spin's
    /// FireAll processes it — anything outside the zone wouldn't fire until some
    /// later spin, breaking the "this token fires at spin S" contract), for an
    /// ordinary filler cell (not a win symbol, not already a feature) that doesn't
    /// already have a Spawns entry in `spawns`. Mirrors FindFillerSlot's preference
    /// order: the token's own reserved column first, then other rows in that column,
    /// then anywhere else in the zone.
    /// </summary>
    private (int r, int c) FindBoardFallbackSlot(Dictionary<(int, int), Cell> spawns,
                                                  SpinPlan next, int origCol)
    {
        var zoneSet = Grid.ZoneSet(next.Push, next.Flush);

        // Same two-tier preference as FindFillerSlot (see its comment): win symbols
        // are never eligible; near-miss symbols are tried last, only if no ordinary
        // filler slot exists anywhere in the zone.
        bool Eligible((int, int) pos, bool allowNearMiss)
        {
            if (!zoneSet.Contains(pos)) return false;
            if (spawns.ContainsKey(pos)) return false;   // already handled by FindFillerSlot
            var cell = next.Board[pos.Item1, pos.Item2];
            if (cell == null || cell.IsFeat || _winSyms.Contains(cell.Sym)) return false;
            return allowNearMiss || !_nonWinSyms.Contains(cell.Sym);
        }

        foreach (bool allowNearMiss in new[] { false, true })
        {
            var primary = (origCol, K.COLS - 1);
            if (Eligible(primary, allowNearMiss)) return primary;

            for (int r = K.ROWS - 1; r >= 0; r--)
                if (Eligible((r, origCol), allowNearMiss)) return (r, origCol);

            foreach (var pos in zoneSet.OrderByDescending(p => p.Item2).ThenBy(p => p.Item1))
                if (Eligible(pos, allowNearMiss)) return pos;
        }

        return default;
    }

    private (int r, int c) FindFillerSlot(Dictionary<(int, int), Cell> spawns, int origCol)
    {
        // Win symbols are NEVER eligible — their exact count is load-bearing.
        // Near-miss symbols (declared minimum, see NonWinTargets/FillZone) are
        // ALSO protected, but only as a PREFERENCE, not an absolute ban: try every
        // search tier using ordinary filler ONLY first (allowNearMiss=false); only
        // if that whole search comes up empty, retry the exact same search tiers
        // allowing near-miss symbols too (allowNearMiss=true). This keeps near-miss
        // minimums safe in the common case (plenty of true ordinary filler
        // available) without reintroducing the silent-token-drop failure in the
        // rare case where near-miss symbols dominate the filler pool and excluding
        // them entirely would leave no eligible slot anywhere. Before this two-tier
        // approach existed, a near-miss symbol's cell could be hijacked freely —
        // never actually wrong in practice only because Verifier.Check would catch
        // a genuine violation and force a retry, which is a safety net catching a
        // mistake, not the mistake being prevented by construction. Confirmed via
        // real-ticket review: a PRIZE_UPGRADE token legitimately converted a
        // near-miss symbol's cell this exact way.
        bool Eligible((int, int) key, bool allowNearMiss) =>
            spawns.TryGetValue(key, out var cell)
            && !_winSyms.Contains(cell.Sym)
            && (allowNearMiss || !_nonWinSyms.Contains(cell.Sym))
            && !cell.IsFeat;

        foreach (bool allowNearMiss in new[] { false, true })
        {
            var primary = (origCol, K.COLS - 1);
            if (Eligible(primary, allowNearMiss)) return primary;

            var byRow = spawns.Keys
                .Where(k => k.Item1 == origCol && Eligible(k, allowNearMiss))
                .OrderByDescending(k => k.Item2)
                .FirstOrDefault();
            if (byRow != default) return byRow;

            var anywhere = spawns.Keys
                .Where(k => Eligible(k, allowNearMiss))
                .OrderByDescending(k => k.Item2)
                .ThenBy(k => k.Item1)
                .FirstOrDefault();
            if (anywhere != default) return anywhere;
        }

        return default;
    }

    private static void FlattenFeats(Cell?[,] b)
    {
        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
        {
            var cell = b[r, c];
            if (cell?.IsFeat != true) continue;
            b[r, c] = Grid.Norm(cell.CvtSym > 0 && !K.IsFeat(cell.CvtSym)
                                 ? cell.CvtSym : K.F_COIN);
        }
    }
}
