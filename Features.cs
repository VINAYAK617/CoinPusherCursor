namespace CoinPusherEngine;

// ── Base ──────────────────────────────────────────────────────────────────────
internal abstract class Feat
{
    internal abstract string   Id      { get; }
    internal abstract int      FeatSym { get; }
    internal virtual  bool     HasToken => true;
    internal abstract PlacedFeat? TryPlace(PlaceCtx ctx);
    internal abstract void        Fire(FireCtx ctx);
    internal virtual  IEnumerable<Cell> Collect(FireCtx ctx) => Enumerable.Empty<Cell>();
}

// ── WHEEL ─────────────────────────────────────────────────────────────────────
internal sealed class WheelFeat : Feat
{
    internal override string Id      => "WHEEL";
    internal override int    FeatSym => K.F_WHEEL;

    internal override PlacedFeat? TryPlace(PlaceCtx ctx)
    {
        int spin = ctx.Spin, col = ctx.Col;
        // ctx.MaxSpin already reflects the true placement ceiling computed by Placer
        // (BaseSpins + any EXTRA_SPIN awards already decided) — trust it directly
        // rather than re-deriving a stale ceiling from BaseSpins alone, which would
        // wrongly exclude bonus spins added by EXTRA_SPIN.
        int maxSpin = ctx.MaxSpin;
        if (spin < ctx.MinSpin || spin >= maxSpin || col >= K.COLS - 1) return null;
        if (ctx.Used.Contains((spin, col))) return null;

        int sym = PickSym(ctx);
        if (sym == 0) return null;
        if (!ctx.Input.Targets.TryGetValue(sym, out int tgt) || tgt <= 0) return null;

        int n = WMath.BestN(tgt), stack = 1 << n, zone = WMath.Zone(tgt, stack);
        if (zone == 0) return null;

        bool isMulti = ctx.Done.Any(f => f.Id == "WHEEL" && f.WSym == sym);
        if (isMulti)
        {
            if (maxSpin < 5) return null;
            int last = ctx.Done.Where(f => f.Id == "WHEEL" && f.WSym == sym).Max(f => f.Spin);
            if (spin <= last + 1) return null;
        }

        if (!WMath.EdfOk(Math.Max(0, tgt - zone * stack), spin, ctx.Done, ctx.Input.Targets, isMulti))
            return null;

        // Don't overflow a spin's zone with multiple WHEELs
        int existZone = ctx.Done
            .Where(f => f.Id == "WHEEL" && f.Spin == spin)
            .Sum(f => ctx.Input.Targets.TryGetValue(f.WSym, out int ft) ? WMath.Zone(ft, 1 << f.WN) : 0);
        if (existZone + zone > K.COLS - 1) return null;

        return new PlacedFeat { Id="WHEEL", Spin=spin, Col=col, WSym=sym, WN=n };
    }

    internal override void Fire(FireCtx ctx)
    {
        int sym = ctx.Fp.WheelSym, st = ctx.Fp.WheelStack;
        if (sym == 0 || st <= 1) return;
        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
        {
            var cell = ctx.Board[r, c];
            if (cell != null && !cell.IsFeat && cell.Sym == sym)
                cell.Stack = st;
        }
    }

    private static int PickSym(PlaceCtx ctx)
    {
        var order = ctx.Input.WheelSymOrder?.Where(s => ctx.Input.Targets.ContainsKey(s)).Distinct().ToList()
                 ?? ctx.Input.Targets.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        var usedCount = ctx.Done.Where(f => f.Id=="WHEEL" && f.WSym!=0)
                            .GroupBy(f => f.WSym).ToDictionary(g => g.Key, g => g.Count());
        var fresh = order.Where(s => !usedCount.ContainsKey(s)).ToList();
        if (fresh.Count > 0) return fresh[0];
        var once = usedCount.Where(kv => kv.Value==1).Select(kv => kv.Key).ToList();
        if (once.Count > 0 && ctx.Rng.NextDouble() < 0.15) return once[ctx.Rng.Next(once.Count)];
        return 0;
    }
}

// ── FLUSH ─────────────────────────────────────────────────────────────────────
internal sealed class FlushFeat : Feat
{
    internal override string Id       => "FLUSH";
    internal override int    FeatSym  => K.F_COIN;
    internal override bool   HasToken => false;

    internal override PlacedFeat? TryPlace(PlaceCtx ctx)
    {
        if (ctx.Spin < ctx.MinSpin || ctx.Spin >= ctx.MaxSpin) return null;
        if (ctx.Used.Contains((ctx.Spin, ctx.Col))) return null;
        return new PlacedFeat { Id="FLUSH", Spin=ctx.Spin, Col=ctx.Col };
    }

    internal override void Fire(FireCtx _) { }

    internal override IEnumerable<Cell> Collect(FireCtx ctx)
    {
        for (int r = 0; r < K.ROWS; r++)
        {
            var cell = ctx.Board[r, ctx.Col];
            if (cell == null) continue;
            yield return cell.Clone();
            ctx.Board[r, ctx.Col] = null;
        }
    }
}

// ── EXTRA_SPIN ────────────────────────────────────────────────────────────────
internal sealed class XSpinFeat : Feat
{
    internal override string Id      => "EXTRA_SPIN";
    internal override int    FeatSym => K.F_XSPIN;

    internal override PlacedFeat? TryPlace(PlaceCtx ctx)
    {
        if (ctx.Spin < ctx.MinSpin || ctx.Spin >= ctx.MaxSpin || ctx.Col >= K.COLS - 1) return null;
        if (ctx.Used.Contains((ctx.Spin, ctx.Col))) return null;
        return new PlacedFeat { Id="EXTRA_SPIN", Spin=ctx.Spin, Col=ctx.Col };
    }

    internal override void Fire(FireCtx _) { }
}

// ── PRIZE_UPGRADE ─────────────────────────────────────────────────────────────
/// <summary>
/// Board token — visual only. Fire() is a no-op.
/// Prize tier overrides are pre-declared in GamePlan.PrizeTiers and read at payout time only.
/// Does NOT affect collection counts, board cells, stacks, or spin count.
/// </summary>
/// <summary>
/// Board token — visual only. Fire() is a no-op.
/// Prize tier overrides are pre-declared in GamePlan.PrizeTiers and read at payout time only.
/// Does NOT affect collection counts, board cells, stacks, or spin count.
///
/// Multi-level upgrade chains: a symbol's declared tier in MathInput.PrizeTiers (e.g. tier=2)
/// is reached by placing that many SEPARATE PRIZE_UPGRADE tokens for that symbol, each at its
/// own spin — token #1 escalates the symbol from tier 0 to tier 1, token #2 escalates it from
/// tier 1 to tier 2, and so on. Each token's own Fp.PrupTier records the tier it escalates TO,
/// so a ticket can show the step-by-step climb (e.g. "$1 → $2 → $5") rather than a single jump.
/// The symbol's FINAL tier — and therefore its payout — is whichever tier the LAST token in the
/// chain reaches, which by construction always equals the declared target in PrizeTiers.
/// </summary>
internal sealed class PrupFeat : Feat
{
    internal override string Id      => "PRIZE_UPGRADE";
    internal override int    FeatSym => K.F_PRUP;

    internal override PlacedFeat? TryPlace(PlaceCtx ctx)
    {
        if (ctx.Input.PrizeTiers == null || ctx.Input.PrizeTiers.Count == 0) return null;
        if (ctx.Spin < ctx.MinSpin || ctx.Spin >= ctx.MaxSpin || ctx.Col >= K.COLS - 1) return null;
        if (ctx.Used.Contains((ctx.Spin, ctx.Col))) return null;

        // For each declared (symbol, targetTier) pair, find how many PRIZE_UPGRADE tokens
        // already target that symbol. The next token for that symbol escalates it by exactly
        // one tier step. Once a symbol has reached its declared target tier, no further tokens
        // are placed for it. Symbols are tried in id order for determinism; the first symbol
        // that still has remaining tier steps to climb is the one this token will represent.
        var alreadyPerSym = ctx.Done
            .Where(f => f.Id == "PRIZE_UPGRADE")
            .GroupBy(f => f.PrupSym)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (sym, targetTier) in ctx.Input.PrizeTiers.OrderBy(kv => kv.Key))
        {
            if (!ctx.Input.Targets.ContainsKey(sym)) continue;   // sym must be a real win target
            if (targetTier <= 0) continue;                       // tier 0 needs no token at all

            int already = alreadyPerSym.GetValueOrDefault(sym, 0);
            if (already >= targetTier) continue;                 // this symbol already fully climbed

            int nextTier = already + 1;
            return new PlacedFeat { Id="PRIZE_UPGRADE", Spin=ctx.Spin, Col=ctx.Col,
                                     PrupSym=sym, PrupTier=nextTier };
        }

        return null;   // every declared symbol has already reached its target tier
    }

    internal override void Fire(FireCtx _) { }  // intentional no-op
}

// ── Registry ──────────────────────────────────────────────────────────────────
internal static class FeatReg
{
    private static readonly Dictionary<string, Feat> ById = new()
    {
        ["WHEEL"]         = new WheelFeat(),
        ["FLUSH"]         = new FlushFeat(),
        ["EXTRA_SPIN"]    = new XSpinFeat(),
        ["PRIZE_UPGRADE"] = new PrupFeat(),
    };
    private static readonly Dictionary<int, Feat> BySym =
        ById.Where(kv => kv.Value.HasToken)
            .ToDictionary(kv => kv.Value.FeatSym, kv => kv.Value);

    // (prob, maxInstances, minSpin, maxSpin, placementOrder)
    internal static readonly IReadOnlyDictionary<string, (double P, int Max, int MinS, int MaxS, int Ord)> Cfg =
        new Dictionary<string, (double, int, int, int, int)>
        {
            ["WHEEL"]         = (0.40, 4, 2, 98, 1),
            ["FLUSH"]         = (0.30, 5, 1, 99, 2),
            ["EXTRA_SPIN"]    = (0.20, 3, 1, 97, 3),
            ["PRIZE_UPGRADE"] = (0.15, 2, 1, 97, 4),
        };

    internal static IEnumerable<string> Ordered => Cfg.OrderBy(kv => kv.Value.Ord).Select(kv => kv.Key);
    internal static bool Has(string id)  => ById.ContainsKey(id);
    internal static bool HasSym(int sym) => BySym.ContainsKey(sym);
    internal static Feat Get(string id)  => ById[id];
    internal static Feat GetSym(int sym) => BySym[sym];
}
