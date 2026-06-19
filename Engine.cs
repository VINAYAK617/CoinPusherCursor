namespace CoinPusherEngine;

/// <summary>
/// Production runtime. Replays a verified GamePlan exactly.
/// Logic mirrors Sim.Run() — any divergence would have been caught by Verifier.
/// </summary>
public sealed class Engine
{
    private readonly GamePlan _plan;
    public Engine(GamePlan plan) { _plan = plan; }

    public GameResult Run()
    {
        var totals   = new Dictionary<int, int>();
        var board    = Grid.Clone(_plan.Spins[0].Board);
        int fallback = _plan.FillSyms.Count > 0 ? _plan.FillSyms[0] : K.F_COIN;

        for (int i = 0; i < _plan.Spins.Count; i++)
        {
            var sp   = _plan.Spins[i];
            var next = i + 1 < _plan.Spins.Count ? _plan.Spins[i + 1] : null;

            Sim.FlatStale(board);

            for (int col = 0; col < K.COLS; col++)
            {
                if (sp.Flush[col])
                {
                    var ctx = new FireCtx { Board=board, Col=col, Fp=new FP() };
                    foreach (var cell in FeatReg.Get("FLUSH").Collect(ctx))
                    {
                        if (!K.IsFeat(cell.Sym))
                        {
                            totals.TryGetValue(cell.Sym, out int ex);
                            totals[cell.Sym] = ex + cell.Stack;
                        }
                    }
                }
                else
                {
                    int push = sp.Push[col];
                    for (int r = K.ROWS - push; r < K.ROWS; r++)
                    {
                        var cell = board[r, col];
                        if (cell != null && !K.IsFeat(cell.Sym))
                        {
                            totals.TryGetValue(cell.Sym, out int ex);
                            totals[cell.Sym] = ex + cell.Stack;
                        }
                    }
                    for (int r = K.ROWS - 1; r >= 0; r--)
                    {
                        int src = r - push;
                        board[r, col] = src >= 0 ? board[src, col]?.Clone() : null;
                    }
                }
            }

            board = Grid.RotCW(board);
            foreach (var kv in sp.Spawns)
                board[kv.Key.Item1, kv.Key.Item2] = kv.Value.Clone();
            Sim.FireAll(board, sp, next, fallback);
        }

        var symbolsHit = _plan.Targets.ToDictionary(kv => kv.Key, kv =>
        {
            totals.TryGetValue(kv.Key, out int g);
            return g == kv.Value;
        });

        return new GameResult
        {
            Collected  = totals,
            SymbolsHit = symbolsHit,
            Win        = symbolsHit.Values.All(v => v),
        };
    }
}
