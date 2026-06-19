using System.Text.Json;

namespace CoinPusherEngine;

/// <summary>
/// Converts a verified GamePlan into the ticket JSON structure the front end consumes:
///   { winInfo: { totalSpins, winSymbols, nonWinSymbols, prizeTiers },
///     startingBoard: [5x5 of {id}],
///     turns: [ { pushers: [...], spawns: [{Pos,id,...}] }, ... ] }
///
/// Feature object shapes:
///   WHEEL         -> { featureId, convertToId, wheelSymbolId, wheelStackMultiplier }
///   EXTRA_SPIN    -> { featureId, convertToId, ReTrigger: [...] }
///   PRIZE_UPGRADE -> { featureId, convertToId, upgradeSymbolId, upgradePrizeValue }
///
/// ReTrigger chaining: when MULTIPLE EXTRA_SPIN tokens exist across a ticket, only the
/// FIRST is kept as a real spawn entry — every subsequent one is folded into a nested
/// ReTrigger array inside it, forming one linear chain. This is presentation-only: the
/// engine still treats each EXTRA_SPIN as an independent token internally; spin-count
/// math is computed before this serialization step and is never affected by it.
///
/// Pos field: every spawn carries "Pos": row*5+col (flat index), per the established schema.
/// </summary>
public static class TicketSerializer
{
    /// <summary>Build the plain object graph (no JSON string yet) for a verified GamePlan.</summary>
    public static object ToTicketObject(GamePlan plan)
    {
        var board = plan.Spins[0].Board;
        var startingBoard = Enumerable.Range(0, K.ROWS).Select(r =>
            Enumerable.Range(0, K.COLS).Select(c => (object)new { id = board[r, c]?.Sym ?? 0 }).ToArray()
        ).ToArray();

        return new
        {
            winInfo = new
            {
                totalSpins = plan.TotalSpins,
                winSymbols = plan.Targets.OrderBy(kv => kv.Key)
                                 .Select(kv => new { id = kv.Key, target = kv.Value }).ToArray(),
                nonWinSymbols = plan.NonWinTargets.OrderBy(kv => kv.Key)
                                 .Select(kv => new { id = kv.Key, minTarget = kv.Value, maxThreshold = K.FILL_CAP }).ToArray(),
                prizeTiers = plan.PrizeTiers.OrderBy(kv => kv.Key)
                                 .Select(kv => new { symId = kv.Key, tier = kv.Value }).ToArray()
            },
            startingBoard,
            turns = BuildTurns(plan)
        };
    }

    /// <summary>Serialize a verified GamePlan straight to an indented JSON string.</summary>
    public static string ToJson(GamePlan plan) =>
        JsonSerializer.Serialize(ToTicketObject(plan), new JsonSerializerOptions { WriteIndented = true });

    // ── Turn / spawn assembly ───────────────────────────────────────────────────

    private static object[] BuildTurns(GamePlan plan)
    {
        var allXSpinTokens = plan.Spins
            .SelectMany(sp => sp.Spawns
                .Where(kv => kv.Value.IsFeat && kv.Value.Sym == K.F_XSPIN)
                .Select(kv => (Spin: sp.Spin, Pos: kv.Key, Cell: kv.Value)))
            .OrderBy(t => t.Spin)
            .ThenBy(t => t.Pos.Item1 * K.COLS + t.Pos.Item2)
            .ToList();

        var chainStart = new HashSet<(int Spin, (int, int) Pos)>();
        var suppressed = new HashSet<(int Spin, (int, int) Pos)>();

        for (int i = 0; i < allXSpinTokens.Count; i++)
        {
            var key = (allXSpinTokens[i].Spin, allXSpinTokens[i].Pos);
            if (i == 0) { chainStart.Add(key); continue; }
            suppressed.Add(key);
        }

        object? nested = null;
        for (int i = allXSpinTokens.Count - 1; i >= 1; i--)
        {
            var cell = allXSpinTokens[i].Cell;
            int cvt  = cell.CvtSym > 0 ? cell.CvtSym : K.F_COIN;
            nested = new
            {
                featureId   = K.F_XSPIN,
                convertToId = cvt,
                ReTrigger   = nested is null ? Array.Empty<object>() : new[] { nested }
            };
        }

        var turns = new List<object>();
        foreach (var sp in plan.Spins)
        {
            var pushers = Enumerable.Range(0, K.COLS).Select(c =>
                sp.Flush[c]
                    ? (object)new { pushValue = K.ROWS, featureId = K.F_FLUSH_ID }
                    : (object)new { pushValue = sp.Push[c] }
            ).ToArray();

            var spawns = new List<object>();
            foreach (var kv in sp.Spawns.OrderBy(kv => kv.Key.Item1 * K.COLS + kv.Key.Item2))
            {
                var posKey = (sp.Spin, kv.Key);
                if (suppressed.Contains(posKey)) continue;

                int pos = kv.Key.Item1 * K.COLS + kv.Key.Item2;

                if (chainStart.Contains(posKey) && nested != null)
                {
                    var c   = kv.Value;
                    int cvt = c.CvtSym > 0 ? c.CvtSym : K.F_COIN;
                    spawns.Add(new
                    {
                        Pos = pos,
                        id  = c.Sym,
                        feature = new { featureId = K.F_XSPIN, convertToId = cvt, ReTrigger = new[] { nested } }
                    });
                    continue;
                }

                spawns.Add(SpawnObj(kv.Value, pos, plan));
            }

            turns.Add(new { pushers, spawns = spawns.ToArray() });
        }

        return turns.ToArray();
    }

    private static object SpawnObj(Cell c, int pos, GamePlan plan)
    {
        if (!c.IsFeat)
            return c.Stack > 1
                ? (object)new { Pos = pos, id = c.Sym, stack = c.Stack }
                : (object)new { Pos = pos, id = c.Sym };

        int cvt = c.CvtSym > 0 ? c.CvtSym : K.F_COIN;
        return c.Sym switch
        {
            K.F_WHEEL => (object)new { Pos = pos, id = c.Sym, feature = new {
                featureId = c.Sym, convertToId = cvt,
                wheelSymbolId = c.Fp?.WheelSym ?? 0, wheelStackMultiplier = c.Fp?.WheelStack ?? 0 } },
            K.F_XSPIN => (object)new { Pos = pos, id = c.Sym, feature = new {
                featureId = c.Sym, convertToId = cvt, ReTrigger = Array.Empty<object>() } },
            K.F_PRUP  => (object)new { Pos = pos, id = c.Sym, feature = new {
                featureId = c.Sym, convertToId = cvt,
                upgradeSymbolId = c.Fp?.PrupSym ?? 0,
                upgradePrizeValue = PrizeValueFor(plan, c.Fp?.PrupSym ?? 0, c.Fp?.PrupTier ?? 0) } },
            _ => (object)new { Pos = pos, id = c.Sym, feature = new { featureId = c.Sym, convertToId = cvt } }
        };
    }

    private static decimal PrizeValueFor(GamePlan plan, int sym, int tier)
    {
        if (plan.PrizeValues.TryGetValue(sym, out var tiers)
            && tiers.TryGetValue(tier, out decimal value))
            return value;

        // Hand-authored MathInput may only provide PrizeTiers. Keep serialization usable
        // while LadderCombinator-backed tickets emit actual prize values.
        return tier;
    }
}
