namespace CoinPusherEngine;

/// <summary>
/// Diagnostic console printer — renders the board at each phase of a spin so the
/// full pipeline (collect → rotate → spawn → fire) can be visually inspected.
/// Not part of the production engine; safe to delete for a minimal build.
/// </summary>
public static class BoardPrinter
{
    public static void TraceGame(GamePlan plan)
    {
        Console.WriteLine($"=== GAME TRACE — {plan.TotalSpins} spins, targets=[{string.Join(",", plan.Targets.Select(kv => $"sym{kv.Key}={kv.Value}"))}] ===\n");

        Print(plan.Spins[0].Board, "STARTING BOARD (before spin 1)", plan);

        var board    = Grid.Clone(plan.Spins[0].Board);
        int fallback = plan.FillSyms.Count > 0 ? plan.FillSyms[0] : K.F_COIN;
        var totals   = new Dictionary<int, int>();

        for (int i = 0; i < plan.Spins.Count; i++)
        {
            var sp   = plan.Spins[i];
            var next = i + 1 < plan.Spins.Count ? plan.Spins[i + 1] : null;

            Console.WriteLine($"\n────────────────────── SPIN {sp.Spin}/{plan.TotalSpins} ──────────────────────");
            Console.WriteLine($"push=[{string.Join(",", Enumerable.Range(0, K.COLS).Select(c => sp.Flush[c] ? "FLUSH" : sp.Push[c].ToString()))}]"
                             + (sp.Tokens.Count > 0 ? $"   tokens firing this spin: [{string.Join(",", sp.Tokens.Select(t => t.Id))}]" : ""));

            // Phase 1: FlatStale
            Sim.FlatStale(board);
            Print(board, "Phase 1 — after FlatStale (stale tokens → filler)", plan);

            // Phase 2: Collect
            var collectedThisSpin = new Dictionary<int, int>();
            for (int col = 0; col < K.COLS; col++)
            {
                if (sp.Flush[col])
                {
                    var ctx = new FireCtx { Board = board, Col = col, Fp = new FP() };
                    foreach (var cell in FeatReg.Get("FLUSH").Collect(ctx))
                    {
                        if (K.IsFeat(cell.Sym)) continue;
                        Acc(totals, cell.Sym, cell.Stack);
                        Acc(collectedThisSpin, cell.Sym, cell.Stack);
                    }
                }
                else
                {
                    int push = sp.Push[col];
                    for (int r = K.ROWS - push; r < K.ROWS; r++)
                    {
                        var cell = board[r, col];
                        if (cell == null || K.IsFeat(cell.Sym)) continue;
                        Acc(totals, cell.Sym, cell.Stack);
                        Acc(collectedThisSpin, cell.Sym, cell.Stack);
                    }
                    for (int r = K.ROWS - 1; r >= 0; r--)
                    {
                        int src = r - push;
                        board[r, col] = src >= 0 ? board[src, col]?.Clone() : null;
                    }
                }
            }
            Print(board, "Phase 2 — after Collect (pushed cells removed, column shifted down)", plan);
            if (collectedThisSpin.Count > 0)
                Console.WriteLine("  collected this spin: " + string.Join(", ", collectedThisSpin.Select(kv => $"sym{kv.Key}+{kv.Value}")));

            // Phase 3: Rotate
            board = Grid.RotCW(board);
            Print(board, "Phase 3 — after 90° Clockwise Rotation", plan);

            // Phase 4: ApplySpawns
            foreach (var kv in sp.Spawns)
                board[kv.Key.Item1, kv.Key.Item2] = kv.Value.Clone();
            Print(board, "Phase 4 — after ApplySpawns (planned cells written in)", plan);

            // Phase 5: FireAll
            Sim.FireAll(board, sp, next, fallback);
            Print(board, "Phase 5 — after FireAll (feature tokens fired & converted)", plan);

            Console.WriteLine("  running totals: " + string.Join(", ",
                plan.Targets.Keys.OrderBy(s => s).Select(s => $"sym{s}={totals.GetValueOrDefault(s)}/{plan.Targets[s]}")));
        }

        Console.WriteLine("\n=== FINAL TOTALS ===");
        foreach (var (sym, target) in plan.Targets.OrderBy(kv => kv.Key))
        {
            int got = totals.GetValueOrDefault(sym);
            Console.WriteLine($"  sym{sym}: {got}/{target}  {(got == target ? "✓" : "✗ MISMATCH")}");
        }
    }

    /// <summary>Print just one board snapshot (e.g. for inspecting a single SpinPlan.Board).</summary>
    internal static void Print(Cell?[,] board, string title, GamePlan? plan = null)
    {
        Console.WriteLine($"\n  {title}");
        var winSyms = plan?.Targets.Keys.ToHashSet() ?? new HashSet<int>();

        Console.Write("       ");
        for (int c = 0; c < K.COLS; c++) Console.Write($"  C{c}  ");
        Console.WriteLine();

        for (int r = 0; r < K.ROWS; r++)
        {
            Console.Write($"   R{r}: ");
            for (int c = 0; c < K.COLS; c++)
                Console.Write(Cell(board[r, c], winSyms));
            Console.WriteLine();
        }
    }

    private static string Cell(Cell? cell, HashSet<int> winSyms)
    {
        if (cell == null) return " ---- ";
        if (cell.IsFeat)  return $" F{cell.Sym,2}  ".PadRight(6);
        string tag = winSyms.Contains(cell.Sym) ? "*" : " ";
        string s   = cell.Stack > 1 ? $"{cell.Sym}x{cell.Stack}" : $"{cell.Sym}";
        return $" {tag}{s,3} ".PadRight(6);
    }

    private static void Acc(Dictionary<int, int> d, int sym, int n)
    { d.TryGetValue(sym, out int ex); d[sym] = ex + n; }
}
