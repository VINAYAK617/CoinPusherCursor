namespace CoinPusherEngine;

/// <summary>
/// One prize the math team wants the engine to be able to award, fully specified: the
/// dollar amount AND the exact collection count required to win it. The target is never
/// invented by this module — collecting exactly Target wins the prize; collecting fewer
/// than Target is filler, same as everywhere else in the engine.
/// </summary>
public sealed class Prize
{
    public required decimal Amount { get; init; }
    public required int     Target { get; init; }
}

/// <summary>
/// Describes how a single prize amount will be awarded: either a brand-new symbol/target
/// combo ("base"), or a reuse of a cheaper prize's exact combo with a PRIZE_UPGRADE token
/// layered on top ("upgrade"). Returned alongside the MathInput so callers can see WHY a
/// particular combo was chosen — useful for math-team review and for documentation/printing.
/// </summary>
public sealed class PrizeCombo
{
    public required decimal    Amount     { get; init; }
    public required bool       IsUpgrade  { get; init; }
    public          decimal?   UpgradeOf  { get; init; }   // amount of the cheaper prize this derives from, if IsUpgrade
    public required int        Sym        { get; init; }
    public required int        Target     { get; init; }
    public required int        Tier       { get; init; }   // 0 = base tier, 1+ = upgraded tier index
    public required MathInput  Input      { get; init; }
}

/// <summary>
/// Decides, for a list of (amount, target) prizes handed down by the math team, which
/// SYMBOL each one is awarded with — and whether a given prize is a fresh combo or an
/// "upgrade" of a strictly cheaper prize's combo (same Targets, same symbol, same count;
/// only a PRIZE_UPGRADE token + elevated tier is added).
///
/// The target count is always supplied by the caller, never invented here — collecting
/// exactly Target wins the prize; fewer is filler, exactly like the rest of the engine.
/// This module's only real decision is symbol assignment and upgrade-vs-fresh pairing.
///
/// This class produces planning INPUTS only. It never calls Planner.Plan() itself, so
/// callers remain free to retry seeds, inspect the chosen combos, or swap in their own
/// MathInput post-processing before generating tickets.
///
/// Design choices (overridable via PrizeCombinatorOptions):
///   • Symbol pool: 1..10, drawn without repeats across DIFFERENT fresh combos in the same
///     prize set. Once exhausted, wraps around (only relevant for very long prize lists).
///   • Upgrade decision: an UpgradeProbability (default 0.35) chance that any prize other
///     than the very first (cheapest) one reuses an earlier prize's combo as an upgrade
///     instead of getting its own fresh combo. The earlier prize chosen is always strictly
///     cheaper and picked at random among eligible candidates. When a prize is an upgrade,
///     it inherits the SOURCE prize's target — its own Target field, if supplied, is not
///     used, since an upgrade is by definition the same combo as its source.
///   • BaseSpins: derived from the target count using the same heuristic the rest of the
///     engine's example tickets use (roughly target/3, clamped to a sane spin-count range).
/// </summary>
public sealed class PrizeCombinatorOptions
{
    /// <summary>
    /// Symbol pool bounds for FRESH combos. These default to a 10-symbol range for
    /// historical reasons, but MUST be set to match the actual game's symbol count
    /// (e.g. MinSym=1, MaxSym=6 for a six-symbol game) — using the wrong range will
    /// generate Targets/PrizeTiers referencing symbol ids that don't exist in the
    /// game's real config, which silently corrupts the ticket.
    /// </summary>
    public int     MinSym               { get; init; } = 1;
    public int     MaxSym               { get; init; } = 10;
    public double  UpgradeProbability   { get; init; } = 0.35;
    public int?    Seed                 { get; init; }
}

public sealed class PrizeCombinator
{
    private readonly PrizeCombinatorOptions _opt;
    private readonly Random                 _rng;

    public PrizeCombinator(PrizeCombinatorOptions? options = null)
    {
        _opt = options ?? new PrizeCombinatorOptions();
        _rng = _opt.Seed.HasValue ? new Random(_opt.Seed.Value) : new Random();
    }

    /// <summary>
    /// Decide a symbol/target combo (and optionally an upgrade relationship) for every
    /// prize amount supplied, returning one PrizeCombo — and one ready-to-plan MathInput —
    /// per prize. Order of the returned list matches ascending amount, not input order.
    /// </summary>
    public List<PrizeCombo> Decide(IEnumerable<Prize> prizes)
    {
        var sorted = prizes.OrderBy(p => p.Amount).ToList();
        if (sorted.Count == 0) return new List<PrizeCombo>();

        var combos    = new List<PrizeCombo>();
        var symPool   = BuildSymbolPool(sorted.Count);
        int symCursor = 0;

        for (int i = 0; i < sorted.Count; i++)
        {
            var amount = sorted[i].Amount;

            // The cheapest prize can never be an upgrade — there's nothing cheaper to derive from.
            bool tryUpgrade = i > 0 && _rng.NextDouble() < _opt.UpgradeProbability;

            if (tryUpgrade)
            {
                // Pick a strictly cheaper prize already decided, preferring a FRESH (base)
                // combo as the upgrade source so upgrade chains stay shallow and easy to read.
                var baseCandidates = combos.Where(c => !c.IsUpgrade).ToList();
                var candidates      = baseCandidates.Count > 0 ? baseCandidates : combos;
                var source          = candidates[_rng.Next(candidates.Count)];

                int tier = source.Tier + 1;
                var input = BuildInput(source.Sym, source.Target,
                                        extraRequired: "PRIZE_UPGRADE",
                                        prizeTiers: new Dictionary<int, int> { { source.Sym, tier } });

                combos.Add(new PrizeCombo
                {
                    Amount    = amount,
                    IsUpgrade = true,
                    UpgradeOf = source.Amount,
                    Sym       = source.Sym,
                    Target    = source.Target,
                    Tier      = tier,
                    Input     = input,
                });
                continue;
            }

            // Fresh combo: new symbol (drawn from the pool, wrapping if exhausted), and the
            // EXACT target the caller supplied for this prize — never invented or rescaled.
            int sym    = symPool[symCursor % symPool.Count];
            symCursor++;
            int target = sorted[i].Target;

            var freshInput = BuildInput(sym, target, extraRequired: null, prizeTiers: null);

            combos.Add(new PrizeCombo
            {
                Amount    = amount,
                IsUpgrade = false,
                UpgradeOf = null,
                Sym       = sym,
                Target    = target,
                Tier      = 0,
                Input     = freshInput,
            });
        }

        return combos;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private List<int> BuildSymbolPool(int count)
    {
        var pool = Enumerable.Range(_opt.MinSym, _opt.MaxSym - _opt.MinSym + 1).ToList();
        // Shuffle once so repeated runs with different seeds don't always hand out
        // symbol 1 to the cheapest prize, symbol 2 to the next, etc.
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return pool;
    }

    /// <summary>
    /// BaseSpins heuristic: roughly target/3, clamped to [4, 18] — matches the spin-count
    /// range used across the engine's other example tickets so generated MathInputs behave
    /// like the realistic tiers already validated against the stress suite.
    /// </summary>
    private static int SpinsFor(int target) => Math.Clamp((int)Math.Round(target / 3.0), 4, 18);

    private MathInput BuildInput(int sym, int target, string? extraRequired,
                                  Dictionary<int, int>? prizeTiers)
    {
        var required = new Dictionary<string, int>();
        if (extraRequired != null) required[extraRequired] = 1;

        return new MathInput
        {
            Targets    = new Dictionary<int, int> { { sym, target } },
            BaseSpins  = SpinsFor(target),
            Required   = required,
            PrizeTiers = prizeTiers,
        };
    }
}
