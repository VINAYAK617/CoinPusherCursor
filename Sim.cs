namespace CoinPusherEngine;

/// <summary>
/// Shared forward simulation. Identical logic used by Verifier (plan-time)
/// and Engine (runtime). Any divergence between them is caught at ticket generation.
///
/// Pipeline per spin:
///   1. FlatStale  — convert lingering feature tokens from last spin to their CvtSym
///   2. Collect    — push cells off the board; accumulate win/filler totals
///   3. RotateCW   — 90-degree clockwise board rotation
///   4. ApplySpawns — write planned cells onto the rotated board
///   5. FireAll    — fire feature tokens; run PostWheelIso after WHEEL
/// </summary>
internal static class Sim
{
    internal static Dictionary<int, int> Run(GamePlan plan)
    {
        var totals   = new Dictionary<int, int>();
        var board    = Grid.Clone(plan.Spins[0].Board);
        int fallback = plan.FillSyms.Count > 0 ? plan.FillSyms[0] : K.F_COIN;

        for (int i = 0; i < plan.Spins.Count; i++)
        {
            var sp   = plan.Spins[i];
            var next = i + 1 < plan.Spins.Count ? plan.Spins[i + 1] : null;

            FlatStale(board);
            Collect(board, sp, totals);
            board = Grid.RotCW(board);
            ApplySpawns(board, sp);
            FireAll(board, sp, next, fallback);
        }
        return totals;
    }

    // ── Phase 2: Collect ───────────────────────────────────────────────────
    private static void Collect(Cell?[,] board, SpinPlan sp, Dictionary<int, int> totals)
    {
        for (int col = 0; col < K.COLS; col++)
        {
            if (sp.Flush[col])
            {
                var ctx = new FireCtx { Board = board, Col = col, Fp = new FP() };
                foreach (var cell in FeatReg.Get("FLUSH").Collect(ctx))
                    Acc(totals, cell.Sym, cell.Stack);
            }
            else
            {
                int push = sp.Push[col];
                for (int r = K.ROWS - push; r < K.ROWS; r++)
                    if (board[r, col] != null)
                        Acc(totals, board[r, col]!.Sym, board[r, col]!.Stack);
                for (int r = K.ROWS - 1; r >= 0; r--)
                {
                    int src = r - push;
                    board[r, col] = src >= 0 ? board[src, col]?.Clone() : null;
                }
            }
        }
    }

    // ── Phase 5: FireAll ───────────────────────────────────────────────────
    internal static void FireAll(Cell?[,] board, SpinPlan sp, SpinPlan? next, int fallback)
    {
        bool any;
        do
        {
            any = false;
            for (int r = 0; r < K.ROWS; r++)
            for (int c = 0; c < K.COLS; c++)
            {
                var fc = board[r, c];
                if (fc?.IsFeat != true) continue;

                Feat? feat = fc.FeatId != null && FeatReg.Has(fc.FeatId)
                             ? FeatReg.Get(fc.FeatId)
                             : FeatReg.HasSym(fc.Sym) ? FeatReg.GetSym(fc.Sym) : null;

                if (feat == null) { board[r, c] = Cvt(fc); any = true; continue; }

                feat.Fire(new FireCtx { Board=board, Col=c, Fp=fc.Fp ?? new FP { FeatId=feat.Id } });

                if (feat.Id == "WHEEL" && next != null)
                    PostWheelIso(board, fc.Fp?.WheelSym ?? 0, next, fallback);

                board[r, c] = Cvt(fc);
                any = true;
            }
        }
        while (any && board.Cast<Cell?>().Any(x => x?.IsFeat == true));
    }

    /// <summary>
    /// After WHEEL fires: remove stacked win cells that are NOT in next spin's
    /// planned zone, or that don't match the planned cell at that position.
    /// Uses next.Board for exact planned-vs-stray comparison.
    /// </summary>
    private static void PostWheelIso(Cell?[,] board, int sym, SpinPlan next, int fallback)
    {
        if (sym == 0) return;
        var nextZone = Grid.ZoneSet(next.Push, next.Flush);

        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
        {
            var cell = board[r, c];
            if (cell == null || cell.IsFeat || cell.Sym != sym) continue;

            var  planned = next.Board[r, c];
            bool keep    = nextZone.Contains((r, c))
                        && planned != null
                        && !planned.IsFeat
                        && planned.Sym == sym;

            if (!keep) board[r, c] = Grid.Norm(fallback);
        }
    }

    private static void ApplySpawns(Cell?[,] board, SpinPlan sp)
    {
        foreach (var kv in sp.Spawns)
            board[kv.Key.Item1, kv.Key.Item2] = kv.Value.Clone();
    }

    // ── Phase 1: FlatStale ─────────────────────────────────────────────────
    internal static void FlatStale(Cell?[,] board)
    {
        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
        {
            var cell = board[r, c];
            if (cell?.IsFeat != true) continue;
            board[r, c] = Grid.Norm(cell.CvtSym > 0 && !K.IsFeat(cell.CvtSym)
                                     ? cell.CvtSym : K.F_COIN);
        }
    }

    private static void Acc(Dictionary<int, int> d, int sym, int n)
    {
        if (K.IsFeat(sym)) return;
        d.TryGetValue(sym, out int ex);
        d[sym] = ex + n;
    }

    private static Cell Cvt(Cell fc)
    {
        int id = fc.CvtSym > 0 && !K.IsFeat(fc.CvtSym) ? fc.CvtSym : K.F_COIN;
        return Grid.Norm(id);
    }
}
