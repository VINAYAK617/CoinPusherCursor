using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
namespace CoinPusherEngine;

/// <summary>
/// Converts a verified GamePlan into the ticket JSON structure the front end consumes:
///   { WinInfo: { TotalSpins, WinSymbols, NonWinSymbols, PrizeTiers },
///     StartingBoard: [5x5 of {Id}],
///     Turns: [ { Pushers: [...], Spawns: [{Pos,Id,...}] }, ... ] }
///
/// Feature object shapes:
///   WHEEL         -> { FeatureId, ConvertToId, WheelSymbolId, WheelStackValue }
///                    WheelStackValue is bonus N; collected value is 1 + N.
///   EXTRA_SPIN    -> { FeatureId, ConvertToId, ReTrigger: [...] }
///   PRIZE_UPGRADE -> { FeatureId, ConvertToId, UpgradeSymbolId, UpgradePrizeValue }
///
/// ReTrigger chaining: with configurable probability, no-board-effect feature tokens
/// (EXTRA_SPIN / PRIZE_UPGRADE) may be folded into a nested ReTrigger array under any
/// board feature. WHEEL stays physical because its fire timing affects stacks.
///
/// Pos field: every spawn carries "Pos": row*5+col (flat index), per the established schema.
/// </summary>
public static class TicketSerializer
{
    // DTO models
    public class TicketDto
    {
        public WinInfoDto WinInfo { get; set; } = null!;
        public BoardCellDto[][] StartingBoard { get; set; } = null!;
        public TurnDto[] Turns { get; set; } = null!;
    }

    public class WinInfoDto
    {
        public int TotalSpins { get; set; }
        public WinSymbolDto[] WinSymbols { get; set; } = null!;
        public NonWinSymbolDto[] NonWinSymbols { get; set; } = null!;
        public PrizeTierDto[] PrizeTiers { get; set; } = null!;
    }

    public class WinSymbolDto { public int Id { get; set; } public int Target { get; set; } }
    public class NonWinSymbolDto
    {
        public int Id { get; set; }
        public int MinTarget { get; set; }
        public int MaxThreshold { get; set; }
        public int? PrizeTier { get; set; }
        public decimal? PrizeValue { get; set; }
    }
    public class PrizeTierDto { public int SymId { get; set; } public int Tier { get; set; } }

    public class BoardCellDto { public int Id { get; set; } }

    public class TurnDto
    {
        public PusherDto[] Pushers { get; set; } = null!;
        public SpawnDto[] Spawns { get; set; } = null!;
    }

    public class PusherDto
    {
        public int PushValue { get; set; }
        public int? FeatureId { get; set; }
    }

    public class SpawnDto
    {
        public int Pos { get; set; }
        public int Id { get; set; }
        public int? Stack { get; set; }
        public FeatureDto? Feature { get; set; }
    }

    public class FeatureDto
    {
        public int FeatureId { get; set; }
        public int ConvertToId { get; set; }
        public int? WheelSymbolId { get; set; }
        public int? WheelStackValue { get; set; }
        public int? UpgradeSymbolId { get; set; }
        public decimal? UpgradePrizeValue { get; set; }
        public FeatureDto[] ReTrigger { get; set; } = System.Array.Empty<FeatureDto>();
    }

    /// <summary>Build the plain object graph (no JSON string yet) for a verified GamePlan.</summary>
    public static TicketDto ToTicketObject(GamePlan plan)
    {
        var board = plan.Spins[0].Board;
        var startingBoard = Enumerable.Range(0, K.ROWS).Select(r =>
            Enumerable.Range(0, K.COLS).Select(c => new BoardCellDto { Id = board[r, c]?.Sym ?? 0 }).ToArray()
        ).ToArray();

        return new TicketDto
        {
            WinInfo = new WinInfoDto
            {
                TotalSpins = plan.TotalSpins,
                WinSymbols = plan.Targets.OrderBy(kv => kv.Key)
                                 .Select(kv => new WinSymbolDto { Id = kv.Key, Target = kv.Value }).ToArray(),
                NonWinSymbols = plan.NonWinTargets.OrderBy(kv => kv.Key)
                                 .Select(kv =>
                                 {
                                     plan.NonWinPrizeTiers.TryGetValue(kv.Key, out int tier);
                                     return new NonWinSymbolDto
                                     {
                                         Id = kv.Key,
                                         MinTarget = kv.Value,
                                         MaxThreshold = K.FILL_CAP,
                                         PrizeTier = tier > 0 ? tier : null,
                                         PrizeValue = tier > 0 ? PrizeValueFor(plan, kv.Key, tier) : null,
                                     };
                                 }).ToArray(),
                PrizeTiers = plan.PrizeTiers.OrderBy(kv => kv.Key)
                                 .Select(kv => new PrizeTierDto { SymId = kv.Key, Tier = kv.Value }).ToArray()
            },
            StartingBoard = startingBoard,
            Turns = BuildTurns(plan)
        };
    }

    /// <summary>Serialize a verified GamePlan straight to an indented JSON string.</summary>
    public static string ToJson(GamePlan plan) =>
        JsonConvert.SerializeObject(ToTicketObject(plan), new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        });

    // ── Turn / spawn assembly ───────────────────────────────────────────────────

    private static TurnDto[] BuildTurns(GamePlan plan)
    {
        var allFeatureTokens = plan.Spins
            .SelectMany(sp => sp.Spawns
                .Where(kv => kv.Value.IsFeat)
                .Select(kv => (Spin: sp.Spin, Pos: kv.Key, Cell: kv.Value)))
            .OrderBy(t => t.Spin)
            .ThenBy(t => t.Pos.Item1 * K.COLS + t.Pos.Item2)
            .ToList();
        var chainPlan = BuildFeatureChainPlan(plan, allFeatureTokens);

        var chainStart = chainPlan.Start is null
            ? new HashSet<(int Spin, (int, int) Pos)>()
            : new HashSet<(int Spin, (int, int) Pos)> { (chainPlan.Start.Value.Spin, chainPlan.Start.Value.Pos) };
        var suppressed = chainPlan.Suppressed
            .Select(token => (token.Spin, token.Pos))
            .ToHashSet();

        var turns = new List<TurnDto>();
        foreach (var sp in plan.Spins)
        {
            var pushers = Enumerable.Range(0, K.COLS).Select(c =>
                sp.Flush[c]
                    ? new PusherDto { PushValue = K.ROWS, FeatureId = K.F_FLUSH_ID }
                    : new PusherDto { PushValue = sp.Push[c], FeatureId = null }
            ).ToArray();

            var spawns = new List<SpawnDto>();
            foreach (var kv in sp.Spawns.OrderBy(kv => kv.Key.Item1 * K.COLS + kv.Key.Item2))
            {
                var posKey = (sp.Spin, kv.Key);

                int pos = kv.Key.Item1 * K.COLS + kv.Key.Item2;

                if (suppressed.Contains(posKey))
                {
                    spawns.Add(ConvertedSpawnObj(kv.Value, pos));
                    continue;
                }

                if (chainStart.Contains(posKey) && chainPlan.Nested != null)
                {
                    var c   = kv.Value;
                    spawns.Add(new SpawnDto
                    {
                        Pos = pos,
                        Id = c.Sym,
                        Feature = FeatureObj(c, plan, new[] { chainPlan.Nested }, depth: 0)
                    });
                    continue;
                }

                spawns.Add(SpawnObj(kv.Value, pos, plan));
            }

            turns.Add(new TurnDto { Pushers = pushers, Spawns = spawns.ToArray() });
        }

        return turns.ToArray();
    }

    private static FeatureChainPlan BuildFeatureChainPlan(
        GamePlan plan,
        IReadOnlyList<(int Spin, (int, int) Pos, Cell Cell)> featureTokens)
    {
        if (featureTokens.Count == 0) return FeatureChainPlan.Empty;

        var chainable = featureTokens
            .Where(token => IsNoBoardEffectFeature(token.Cell))
            .ToList();
        if (chainable.Count == 0) return FeatureChainPlan.Empty;

        var start = featureTokens[DeterministicIndex(plan.TotalSpins, featureTokens[0].Spin, featureTokens[0].Pos, featureTokens.Count)];
        var payload = chainable
            .Where(token => token.Spin != start.Spin || token.Pos != start.Pos)
            .ToList();
        if (payload.Count == 0) return FeatureChainPlan.Empty;

        var roll = DeterministicUnitInterval(plan.TotalSpins, start.Spin, start.Pos);
        if (roll >= K.P_FEATURE_RETRIGGER_CHAIN) return FeatureChainPlan.Empty;

        FeatureDto? nested = null;
        for (int i = payload.Count - 1; i >= 0; i--)
        {
            nested = FeatureObj(
                payload[i].Cell,
                plan,
                nested is null ? System.Array.Empty<FeatureDto>() : new[] { nested },
                depth: i + 1);
        }

        return new FeatureChainPlan(start, payload, nested);
    }

    private static bool IsNoBoardEffectFeature(Cell cell) =>
        cell.Sym is K.F_XSPIN or K.F_PRUP;

    private static int FeatureChainConvertId(Cell cell, int depth, GamePlan plan)
    {
        var ids = plan.WinSyms
            .Concat(plan.NonWinTargets.Keys)
            .Concat(plan.FillSyms)
            .Concat(K.FEATURE_RETRIGGER_BRIDGE_IDS)
            .Distinct()
            .ToArray();
        if (ids.Length == 0) return K.F_COIN;

        var hash = cell.Sym;
        hash = unchecked(hash * 397) ^ cell.CvtSym;
        hash = unchecked(hash * 397) ^ depth;
        hash = unchecked(hash * 397) ^ (cell.Fp?.PrupSym ?? 0);
        return ids[(hash & 0x7fffffff) % ids.Length];
    }

    private sealed record FeatureChainPlan(
        (int Spin, (int, int) Pos, Cell Cell)? Start,
        IReadOnlyList<(int Spin, (int, int) Pos, Cell Cell)> Suppressed,
        FeatureDto? Nested)
    {
        public static FeatureChainPlan Empty { get; } =
            new(null, System.Array.Empty<(int, (int, int), Cell)>(), null);
    }

    private static double DeterministicUnitInterval(int totalSpins, int spin, (int r, int c) pos)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + totalSpins;
            hash = hash * 31 + spin;
            hash = hash * 31 + pos.r;
            hash = hash * 31 + pos.c;
            var positive = hash & 0x7fffffff;
            return positive / (double)int.MaxValue;
        }
    }

    private static int DeterministicIndex(int totalSpins, int spin, (int r, int c) pos, int count)
    {
        if (count <= 1) return 0;
        unchecked
        {
            var hash = 23;
            hash = hash * 31 + totalSpins;
            hash = hash * 31 + spin;
            hash = hash * 31 + pos.r;
            hash = hash * 31 + pos.c;
            return (hash & 0x7fffffff) % count;
        }
    }

    private static SpawnDto SpawnObj(Cell c, int pos, GamePlan plan)
    {
        if (!c.IsFeat)
            return c.Stack > 1
                ? new SpawnDto { Pos = pos, Id = c.Sym, Stack = c.Stack }
                : new SpawnDto { Pos = pos, Id = c.Sym };

        int cvt = c.CvtSym > 0 ? c.CvtSym : K.F_COIN;
        return c.Sym switch
        {
            K.F_WHEEL => new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = FeatureObj(c, plan)
            },
            K.F_XSPIN => new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = FeatureObj(c, plan)
            },
            K.F_PRUP => new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = FeatureObj(c, plan)
            },
            _ => new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = new FeatureDto { FeatureId = c.Sym, ConvertToId = cvt }
            }
        };
    }

    private static FeatureDto FeatureObj(
        Cell c,
        GamePlan plan,
        FeatureDto[]? reTrigger = null,
        int depth = 0)
    {
        var chain = reTrigger ?? System.Array.Empty<FeatureDto>();
        var convertToId = chain.Length > 0
            ? depth == 0
                ? PhysicalChainConvertId(plan)
                : FeatureChainConvertId(c, depth, plan)
            : c.CvtSym > 0 ? c.CvtSym : K.F_COIN;

        var dto = new FeatureDto
        {
            FeatureId = c.Sym,
            ConvertToId = convertToId,
            ReTrigger = chain,
        };

        if (c.Sym == K.F_WHEEL)
        {
            dto.WheelSymbolId = c.Fp?.WheelSym ?? 0;
            // Public JSON uses bonus semantics: N means a collected cell counts
            // as 1 + N. Internally Fp.WheelStack stores the total stack value.
            dto.WheelStackValue = Math.Clamp(
                Math.Max(0, (c.Fp?.WheelStack ?? 1) - 1),
                K.MIN_WHEEL_STACK_VALUE,
                K.MAX_WHEEL_STACK_VALUE);
        }
        else if (c.Sym == K.F_PRUP)
        {
            dto.UpgradeSymbolId = c.Fp?.PrupSym ?? 0;
            dto.UpgradePrizeValue = PrizeValueFor(plan, c.Fp?.PrupSym ?? 0, c.Fp?.PrupTier ?? 0);
        }

        return dto;
    }

    private static int PhysicalChainConvertId(GamePlan plan)
    {
        // This value becomes the actual board cell after the root token fires.
        // Keep it count-safe: ordinary filler first, not declared win/non-win.
        // Nested ReTrigger links may still use win/non-win symbols or feature ids
        // for visual presentation because they are not physical board cells.
        return plan.FillSyms
            .FirstOrDefault(sym => sym > 0
                                   && !plan.Targets.ContainsKey(sym)
                                   && !plan.NonWinTargets.ContainsKey(sym)
                                   && !K.IsFeat(sym)) is var sym && sym > 0
            ? sym
            : K.F_COIN;
    }

    private static SpawnDto ConvertedSpawnObj(Cell c, int pos)
    {
        int cvt = c.CvtSym > 0 && !K.IsFeat(c.CvtSym) ? c.CvtSym : K.F_COIN;
        return new SpawnDto { Pos = pos, Id = cvt };
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
