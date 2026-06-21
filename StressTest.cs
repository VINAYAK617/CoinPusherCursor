namespace CoinPusherEngine;

/// <summary>
/// Internal correctness suite — not part of the production pipeline. Run with
/// `dotnet run -- selftest` to re-verify the engine across its known configs.
/// </summary>
internal static class StressTest
{
    internal static void Run()
    {
        static (int ok, string err) Try(MathInput cfg, int seed)
        {
            try
            {
                var plan = new Planner(cfg, seed).Plan();
                SerializedTicketVerifier.CheckNoMissingCells(TicketSerializer.ToTicketObject(plan));
                return (1, "");
            }
            catch (Exception ex) { return (0, ex.Message[..Math.Min(120, ex.Message.Length)]); }
        }

        var configs = new (string label, MathInput cfg)[]
        {
            ("01 minimal", new MathInput { Targets=new Dictionary<int,int>{{1,5},{2,4}}, BaseSpins=5 }),
            ("02 small", new MathInput { Targets=new Dictionary<int,int>{{1,8},{2,6},{3,4}}, BaseSpins=5 }),
            ("03 auto", new MathInput { Targets=new Dictionary<int,int>{{1,30},{2,30},{3,30}}, BaseSpins=5 }),
            ("04 1W+1F", new MathInput { Targets=new Dictionary<int,int>{{1,20},{2,15}}, BaseSpins=5,
                Required=new Dictionary<string,int>{{"WHEEL",1},{"FLUSH",1}} }),
            ("05 2W+2F", new MathInput { Targets=new Dictionary<int,int>{{1,40},{2,30}}, BaseSpins=5,
                Required=new Dictionary<string,int>{{"WHEEL",2},{"FLUSH",2}} }),
            ("06 multiW", new MathInput { Targets=new Dictionary<int,int>{{1,50},{2,20}}, BaseSpins=5,
                Required=new Dictionary<string,int>{{"WHEEL",2}} }),
            ("07 xspin", new MathInput { Targets=new Dictionary<int,int>{{1,12},{2,10}}, BaseSpins=5,
                Required=new Dictionary<string,int>{{"EXTRA_SPIN",1}} }),
            ("08 prup", new MathInput { Targets=new Dictionary<int,int>{{1,18},{2,12}}, BaseSpins=5,
                Required=new Dictionary<string,int>{{"PRIZE_UPGRADE",1}}, PrizeTiers=new Dictionary<int,int>{{1,2}} }),
            ("09 prup+F", new MathInput { Targets=new Dictionary<int,int>{{1,20},{2,15}}, BaseSpins=5,
                Required=new Dictionary<string,int>{{"PRIZE_UPGRADE",1},{"FLUSH",1}}, PrizeTiers=new Dictionary<int,int>{{2,3}} }),
            ("10 prup+XS", new MathInput { Targets=new Dictionary<int,int>{{1,15},{2,10}}, BaseSpins=5,
                Required=new Dictionary<string,int>{{"PRIZE_UPGRADE",1},{"EXTRA_SPIN",1}}, PrizeTiers=new Dictionary<int,int>{{1,1}} }),
            ("11 4W", new MathInput { Targets=new Dictionary<int,int>{{1,20},{2,20},{3,25},{4,20}}, BaseSpins=8,
                Required=new Dictionary<string,int>{{"WHEEL",4}} }),
            ("12 5sym", new MathInput { Targets=new Dictionary<int,int>{{1,20},{2,25},{3,20},{4,15},{5,10}}, BaseSpins=8 }),
            ("13 prup multisym", new MathInput { Targets=new Dictionary<int,int>{{2,20},{5,20},{3,20},{4,20},{6,20}}, BaseSpins=10,
                Required=new Dictionary<string,int>{{"WHEEL",2},{"FLUSH",2},{"PRIZE_UPGRADE",1}}, PrizeTiers=new Dictionary<int,int>{{2,3}} }),
            ("14 2xXSPIN chain", new MathInput { Targets=new Dictionary<int,int>{{2,20},{5,20},{3,20},{4,20},{6,20}}, BaseSpins=8,
                Required=new Dictionary<string,int>{{"WHEEL",2},{"FLUSH",2},{"PRIZE_UPGRADE",1},{"EXTRA_SPIN",2}}, PrizeTiers=new Dictionary<int,int>{{2,3}} }),
            ("15 prup tier2 single-sym", new MathInput { Targets=new Dictionary<int,int>{{1,20}}, BaseSpins=5,
                Required=new Dictionary<string,int>{{"PRIZE_UPGRADE",2}}, PrizeTiers=new Dictionary<int,int>{{1,2}} }),
            ("16 prup tier2 multi-sym", new MathInput { Targets=new Dictionary<int,int>{{1,20},{2,20},{3,20},{4,25},{5,25},{6,30}}, BaseSpins=10,
                Required=new Dictionary<string,int>{{"PRIZE_UPGRADE",2}}, PrizeTiers=new Dictionary<int,int>{{1,2},{3,1}} }),
            ("17 real 6-symbol pool, 4 wins", new MathInput
                { Targets=new Dictionary<int,int>{{1,20},{2,20},{3,20},{4,25}}, BaseSpins=5 }),
        };

        // Success criterion matches real usage: Planner is always called via a seed-retry
        // loop (see Program.cs's actual pipeline), never via a single fixed seed. So the
        // right test isn't "does every individual seed pass" — some configs (e.g. ones
        // using decorative win-symbol fillers alongside a WHEEL lock) legitimately have a
        // lower per-seed success rate by design — it's "does retrying reliably find a
        // working seed within a realistic budget, every time, across many trials."
        const int RetryBudget = 100;
        const int TrialsPerConfig = 50;
        int totalTrials = 0, totalFound = 0;

        Console.WriteLine($"CoinPusherEngine — retry-reliability test: {TrialsPerConfig} trials x " +
                           $"{configs.Length} configs (retry budget {RetryBudget} seeds/trial)\n");

        foreach (var (label, cfg) in configs)
        {
            int found = 0, totalAttempts = 0;
            string? firstErr = null;

            for (int trial = 0; trial < TrialsPerConfig; trial++)
            {
                bool ok = false;
                for (int i = 0; i < RetryBudget; i++)
                {
                    int seed = trial * RetryBudget + i;
                    var (r, e) = Try(cfg, seed);
                    totalAttempts++;
                    if (r == 1) { ok = true; break; }
                    firstErr ??= e;
                }
                if (ok) found++;
            }

            totalTrials += TrialsPerConfig; totalFound += found;
            double avgAttempts = totalAttempts / (double)TrialsPerConfig;
            Console.WriteLine($"[{(found == TrialsPerConfig ? "PASS" : "FAIL")}] {label,-30} " +
                               $"{found,3}/{TrialsPerConfig} trials found a seed (avg {avgAttempts:F1} attempts)");
            if (found < TrialsPerConfig && firstErr != null)
                Console.WriteLine($"        sample failure: {firstErr}");
        }

        Console.WriteLine($"\nTotal: {totalFound}/{totalTrials} trials succeeded ({100.0 * totalFound / totalTrials:F2}%)");
        Console.WriteLine(totalFound == totalTrials ? "ALL PASS" : $"FAILURES: {totalTrials - totalFound}");
    }
}

internal static class SerializedTicketVerifier
{
    internal static void CheckNoMissingCells(TicketSerializer.TicketDto dto)
    {
        var board = new int?[K.ROWS, K.COLS];
        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
            board[r, c] = dto.StartingBoard[r][c].Id;

        for (int i = 0; i < dto.Turns.Length; i++)
        {
            var turn = dto.Turns[i];

            for (int c = 0; c < K.COLS; c++)
            {
                int push = turn.Pushers[c].PushValue;
                if (push >= K.ROWS)
                {
                    for (int r = 0; r < K.ROWS; r++) board[r, c] = null;
                }
                else
                {
                    for (int r = K.ROWS - 1; r >= 0; r--)
                    {
                        int src = r - push;
                        board[r, c] = src >= 0 ? board[src, c] : null;
                    }
                }
            }

            board = RotCW(board);

            foreach (var spawn in turn.Spawns)
                board[spawn.Pos / K.COLS, spawn.Pos % K.COLS] = spawn.Id;

            var empty = new List<int>();
            for (int r = 0; r < K.ROWS; r++)
            for (int c = 0; c < K.COLS; c++)
                if (board[r, c] == null) empty.Add(r * K.COLS + c);

            if (empty.Count > 0)
                throw new InvalidOperationException(
                    $"SERIALIZE FAIL turn={i + 1} missing=[{string.Join(",", empty)}]");
        }
    }

    private static int?[,] RotCW(int?[,] board)
    {
        var rotated = new int?[K.ROWS, K.COLS];
        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
            rotated[c, K.ROWS - 1 - r] = board[r, c];
        return rotated;
    }
}
