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
/// ReTrigger chaining: when MULTIPLE EXTRA_SPIN tokens exist across a ticket, only the
/// FIRST is kept as a real feature spawn — every subsequent feature is folded into a
/// nested ReTrigger array inside it. The later physical board slots are still emitted as
/// ordinary converted cells so the serialized board replay stays fully populated.
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
        var allXSpinTokens = plan.Spins
            .SelectMany(sp => sp.Spawns
                .Where(kv => kv.Value.IsFeat && kv.Value.Sym == K.F_XSPIN)
                .Select(kv => (Spin: sp.Spin, Pos: kv.Key, Cell: kv.Value)))
            .OrderBy(t => t.Spin)
            .ThenBy(t => t.Pos.Item1 * K.COLS + t.Pos.Item2)
            .ToList();
        bool chainExtraSpins = ShouldChainExtraSpins(plan, allXSpinTokens);

        var chainStart = new HashSet<(int Spin, (int, int) Pos)>();
        var suppressed = new HashSet<(int Spin, (int, int) Pos)>();

        for (int i = 0; chainExtraSpins && i < allXSpinTokens.Count; i++)
        {
            var key = (allXSpinTokens[i].Spin, allXSpinTokens[i].Pos);
            if (i == 0) { chainStart.Add(key); continue; }
            suppressed.Add(key);
        }

        FeatureDto? nested = null;
        for (int i = allXSpinTokens.Count - 1; i >= 1; i--)
        {
            var cell = allXSpinTokens[i].Cell;
            int cvt  = cell.CvtSym > 0 ? cell.CvtSym : K.F_COIN;
            bool hasRetrigger = nested is not null;
            var f = new FeatureDto
            {
                FeatureId = K.F_XSPIN,
                ConvertToId = hasRetrigger
                    ? ExtraSpinChainConvertId(cell, i)
                    : cvt,
                ReTrigger = hasRetrigger ? new[] { nested! } : System.Array.Empty<FeatureDto>()
            };
            nested = f;
        }

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

                if (chainStart.Contains(posKey) && nested != null)
                {
                    var c   = kv.Value;
                    int cvt = c.CvtSym > 0 ? c.CvtSym : K.F_COIN;
                    spawns.Add(new SpawnDto
                    {
                        Pos = pos,
                        Id = c.Sym,
                        Feature = new FeatureDto
                        {
                            FeatureId = K.F_XSPIN,
                            ConvertToId = ExtraSpinChainConvertId(c, 0),
                            ReTrigger = new[] { nested }
                        }
                    });
                    continue;
                }

                spawns.Add(SpawnObj(kv.Value, pos, plan));
            }

            turns.Add(new TurnDto { Pushers = pushers, Spawns = spawns.ToArray() });
        }

        return turns.ToArray();
    }

    private static bool ShouldChainExtraSpins(
        GamePlan plan,
        IReadOnlyList<(int Spin, (int, int) Pos, Cell Cell)> extraSpinTokens)
    {
        if (extraSpinTokens.Count <= 1) return false;
        var roll = DeterministicUnitInterval(plan.TotalSpins, extraSpinTokens[0].Spin, extraSpinTokens[0].Pos);
        return roll < K.P_EXTRA_SPIN_RETRIGGER_CHAIN;
    }

    private static int ExtraSpinChainConvertId(Cell cell, int depth)
    {
        var ids = K.EXTRA_SPIN_RETRIGGER_CONVERT_IDS;
        if (ids.Length == 0) return K.F_XSPIN;

        var hash = cell.Sym;
        hash = unchecked(hash * 397) ^ cell.CvtSym;
        hash = unchecked(hash * 397) ^ depth;
        hash = unchecked(hash * 397) ^ (cell.Fp?.PrupSym ?? 0);
        return ids[(hash & 0x7fffffff) % ids.Length];
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
                Feature = new FeatureDto
                {
                    FeatureId = c.Sym,
                    ConvertToId = cvt,
                    WheelSymbolId = c.Fp?.WheelSym ?? 0,
                    // Public JSON uses bonus semantics: N means a collected cell counts
                    // as 1 + N. Internally Fp.WheelStack stores the total stack value.
                    WheelStackValue = Math.Max(0, (c.Fp?.WheelStack ?? 1) - 1)
                }
            },
            K.F_XSPIN => new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = new FeatureDto
                {
                    FeatureId = c.Sym,
                    ConvertToId = cvt,
                    ReTrigger = System.Array.Empty<FeatureDto>()
                }
            },
            K.F_PRUP => new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = new FeatureDto
                {
                    FeatureId = c.Sym,
                    ConvertToId = cvt,
                    UpgradeSymbolId = c.Fp?.PrupSym ?? 0,
                    UpgradePrizeValue = PrizeValueFor(plan, c.Fp?.PrupSym ?? 0, c.Fp?.PrupTier ?? 0)
                }
            },
            _ => new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = new FeatureDto { FeatureId = c.Sym, ConvertToId = cvt }
            }
        };
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
