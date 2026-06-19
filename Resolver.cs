namespace CoinPusherEngine;

/// <summary>
/// Forward-pass spawn resolver.
/// For each consecutive pair (cur, next):
///   1. Simulate cur's push+rotate to get the naturally-delivered board.
///   2. For EVERY position in next.Board (not just zone rows), spawn the planned cell
///      only when the simulation leaves that position empty. Natural cells are never
///      overwritten by spawns; if a non-empty position differs, this plan is rejected.
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
    private readonly List<string> _log;
    private readonly Random       _rng;
    private readonly FillTracker  _fillTracker;

    internal Resolver(int[] fills, HashSet<int> winSyms, List<string> log, Random rng, FillTracker fillTracker)
    { _fills=fills; _winSyms=winSyms; _log=log; _rng=rng; _fillTracker=fillTracker; }

    internal void Resolve(List<SpinPlan> plans)
    {
        for (int i = 0; i < plans.Count - 1; i++)
            DoSpin(plans[i], plans[i + 1]);
        DoLast(plans[^1]);
    }

    /// <summary>
    /// For each position in next.Board, spawn the planned cell only if the natural
    /// simulation leaves that position empty. Correctness requires this check to cover
    /// EVERY board position: a mismatch in an occupied non-zone position would otherwise
    /// require visually overwriting an existing cell.
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
                throw new InvalidOperationException(
                    $"SPAWN OVERWRITE S{cur.Spin} pos=({r},{c}) natural=sym{sv.Sym}x{sv.Stack} planned=sym{plan.Sym}x{plan.Stack}");

            if (needSpawn)
                cur.Spawns[(r, c)] = plan.Clone();
        }

        PlaceTokens(cur);
    }

    private void DoLast(SpinPlan last)
    {
        var sim = Simulate(last);
        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
            if (sim[r, c] == null)
                last.Spawns[(r, c)] = Grid.Norm(_fillTracker.Next());
    }

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

    private void PlaceTokens(SpinPlan sp)
    {
        foreach (var (featId, origCol, fp) in sp.Tokens)
        {
            if (!FeatReg.Has(featId)) continue;
            var feat = FeatReg.Get(featId);

            var slot = FindFillerSlot(sp.Spawns, origCol);
            if (slot == default)
            {
                _log.Add($"  WARN: no filler slot for {featId}@S{sp.Spin}");
                continue;
            }

            int cvt = sp.Spawns[slot].Sym;
            sp.Spawns[slot] = Grid.Feat(feat.FeatSym, cvt, fp);
        }
    }

    private (int r, int c) FindFillerSlot(Dictionary<(int, int), Cell> spawns, int origCol)
    {
        bool Eligible((int, int) key) =>
            spawns.TryGetValue(key, out var cell)
            && !_winSyms.Contains(cell.Sym)
            && !cell.IsFeat;

        var primary = (origCol, K.COLS - 1);
        if (Eligible(primary)) return primary;

        var byRow = spawns.Keys
            .Where(k => k.Item1 == origCol && Eligible(k))
            .OrderByDescending(k => k.Item2)
            .FirstOrDefault();
        if (byRow != default) return byRow;

        return spawns.Keys
            .Where(Eligible)
            .OrderByDescending(k => k.Item2)
            .ThenBy(k => k.Item1)
            .FirstOrDefault();
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
