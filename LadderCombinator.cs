namespace CoinPusherEngine;

/// <summary>
/// One symbol's full prize ladder, as fully specified by the math team. Unlike
/// PrizeCombinator (which INVENTS a symbol/target for a flat dollar amount),
/// LadderCombinator takes ladders that are already completely defined — the math team
/// has fixed the target count and every tier's payout amount per symbol — and treats
/// it purely as a reverse-lookup/selection problem: "which symbol + tier reaches
/// this exact dollar amount?"
/// </summary>
public sealed class PrizeLadderRow
{
    /// <summary>The collection count required to win this symbol (e.g. 20).</summary>
    public required int Target { get; init; }

    /// <summary>
    /// Payout amounts in tier order: index 0 = base tier (no PRIZE_UPGRADE token needed),
    /// index 1 = after one PRIZE_UPGRADE application, index 2 = after two, etc.
    /// A row with no upgrades simply has a single entry.
    /// </summary>
    public required IReadOnlyList<decimal> Tiers { get; init; }
}

/// <summary>
/// One resolved way to award a specific requested amount: a symbol, its target count,
/// and the tier (0 = base, no upgrade token; 1+ = that many PRIZE_UPGRADE applications)
/// needed to reach the requested amount with that symbol.
/// </summary>
public sealed class LadderCandidate
{
    public required int     Sym    { get; init; }
    public required int     Target { get; init; }
    public required int     Tier   { get; init; }
    public required decimal Amount { get; init; }
}

/// <summary>
/// One symbol's final, resolved contribution inside a bundled multi-symbol ticket:
/// which amount(s) it ended up covering, and at what tier.
/// </summary>
public sealed class BundleEntry
{
    public required int           Sym     { get; init; }
    public required int           Target  { get; init; }
    public required int           Tier    { get; init; }   // final/highest tier this symbol reaches
    public required List<decimal> Amounts { get; init; }    // every requested amount this entry covers
}

/// <summary>
/// Result of bundling a whole prize list into one ticket: the ready-to-plan MathInput,
/// which symbol covered which amount(s), and which requested amounts couldn't be included
/// because every requested prize amount must consume a distinct symbol; if the ladder
/// cannot represent all requested prizes distinctly, bundling throws.
/// </summary>
public sealed class BundleResult
{
    public required MathInput          Input          { get; init; }
    public required List<BundleEntry>  Entries        { get; init; }
    public required List<decimal>      Covered        { get; init; }
    public required List<decimal>      Skipped        { get; init; }
}

/// <summary>
/// Reverse-lookup engine over a set of fully-specified symbol prize ladders.
///
/// Construction: rows are assigned symbol IDs in the order given (row 0 → symbol 1,
/// row 1 → symbol 2, ...), matching the "row order = symbol order, auto-assigned"
/// convention. Building the lookup walks every row's tier list once, so a single
/// dollar amount can map to MULTIPLE candidates across DIFFERENT symbols/tiers —
/// e.g. $2 can be reached by sym2 at tier 0 (its base prize) OR sym1 at tier 1
/// (its first upgrade step) — exactly the ambiguity the math team's table creates.
///
/// Two ways to use this class:
///   • Resolve(amount) — one amount in, one candidate + standalone MathInput out.
///     Useful for inspection or when truly independent tickets are wanted.
///   • Bundle(amounts) — THE primary entry point. Takes a whole prize list (e.g.
///     {1, 2, 5, 10}) and produces ONE multi-symbol MathInput that requires every
///     chosen symbol/tier simultaneously, so the ticket's guaranteed total payout is
///     the sum of every amount covered. See Bundle()'s own doc for the full search
///     and failure rules (including multi-symbol SUM combinations when no single
///     symbol/tier matches an amount exactly).
///
/// Picking: candidates for a given amount are split into "direct" (tier 0 — no
/// PRIZE_UPGRADE token needed) and "upgrade" (tier 1+ — climbs a cheaper symbol's
/// ladder) groups, and a 50/50 coin decides which group is drawn from whenever both
/// exist. When only one group has candidates (e.g. $1 only has a direct path, $10000
/// has no upgrades defined at all), there's no choice to make — that group is used
/// outright. Within a chosen group, ties are broken by lowest symbol id.
/// </summary>
public sealed class LadderCombinator
{
    private readonly List<PrizeLadderRow>                _rows;
    private readonly Dictionary<decimal, List<LadderCandidate>> _lookup;
    private readonly Random                              _rng;

    public LadderCombinator(IReadOnlyList<PrizeLadderRow> rows, int? seed = null)
    {
        _rows   = rows.ToList();
        _lookup = BuildLookup(_rows);
        _rng    = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    /// <summary>All amounts that have at least one candidate, sorted ascending.</summary>
    public IReadOnlyList<decimal> KnownAmounts => _lookup.Keys.OrderBy(a => a).ToList();

    /// <summary>Every (symbol, tier) candidate that reaches the requested amount, unfiltered.</summary>
    public IReadOnlyList<LadderCandidate> CandidatesFor(decimal amount) =>
        _lookup.TryGetValue(amount, out var list) ? list : Array.Empty<LadderCandidate>();

    /// <summary>
    /// Pick a candidate for a requested amount and build the ready-to-plan MathInput for it.
    /// Returns null if no symbol/tier combination in the ladder table reaches this exact
    /// amount. When the amount has BOTH a direct (tier 0) candidate and at least one
    /// upgrade-path (tier 1+) candidate, a 50/50 coin decides which group this ticket
    /// draws from; with only one group available, that group is used outright.
    /// </summary>
    public (LadderCandidate Candidate, MathInput Input)? Resolve(decimal amount)
    {
        var candidates = CandidatesFor(amount);
        if (candidates.Count == 0) return null;

        var direct  = candidates.Where(c => c.Tier == 0).OrderBy(c => c.Sym).ToList();
        var upgrade = candidates.Where(c => c.Tier > 0).OrderBy(c => c.Tier).ThenBy(c => c.Sym).ToList();

        List<LadderCandidate> pool;
        if (direct.Count > 0 && upgrade.Count > 0)
            pool = _rng.NextDouble() < 0.5 ? direct : upgrade;
        else
            pool = direct.Count > 0 ? direct : upgrade;

        var chosen = pool[0];
        return (chosen, BuildInput(chosen));
    }

    /// <summary>
    /// Build ONE multi-symbol ticket that covers an entire prize list at once — the
    /// engine's actual deliverable when math hands down only a flat list of amounts
    /// (e.g. {1, 2, 5, 10}) rather than a hand-authored MathInput.
    ///
    /// For each amount (processed smallest first, since smaller amounts are typically
    /// base tiers that larger amounts upgrade FROM), resolution tries two strategies:
    ///
    ///   1. SINGLE CANDIDATE — does any one symbol/tier in the table pay exactly this
    ///      amount? Candidates are split into "direct" (tier 0) and "upgrade" (tier 1+)
    ///      groups, a 50/50 coin picks which group is tried first, and every candidate
    ///      in that group is tried before falling back to the other group.
    ///
    ///   2. SUM OF MULTIPLE SYMBOLS — if no single candidate matches, search for a
    ///      combination of DIFFERENT symbols (at most one tier each, no cap on how many
    ///      symbols may combine) whose amounts sum exactly to the target. For example,
    ///      $45 with no direct match might be reached via sym4@tier1($20) + sym3@tier2($25).
    ///      When multiple valid combinations exist, one is picked uniformly at random.
    ///
    /// Every requested amount must consume a distinct symbol. A symbol already used by
    /// an earlier amount cannot be reused, even at the same tier, so duplicate prizes
    /// produce separate visible win entries whenever the ladder has enough candidates.
    ///
    /// Every multi-symbol combination is applied atomically — either every symbol in
    /// the chosen combination merges cleanly, or that combination isn't used at all.
    ///
    /// Failure is loud, never silent: if an amount cannot be reached by any distinct
    /// single candidate AND no distinct combination of candidates sums to it, Bundle()
    /// throws an InvalidOperationException naming the amount — it never returns a result
    /// with amounts quietly missing or silently shared.
    /// </summary>
    public BundleResult Bundle(IEnumerable<decimal> amounts)
    {
        var ordered = amounts.OrderBy(a => a).ToList();
        var bySym   = new Dictionary<int, BundleEntry>();
        var covered = new List<decimal>();

        foreach (var amount in ordered)
        {
            var candidates = CandidatesFor(amount);

            // Single-candidate fast path: try a direct/upgrade pick exactly matching the
            // amount, same as before — this covers the common case without any search.
            List<LadderCandidate>? winningCombo = null;

            if (candidates.Count > 0)
            {
                var direct  = candidates.Where(c => c.Tier == 0).OrderBy(c => c.Sym).ToList();
                var upgrade = candidates.Where(c => c.Tier > 0).OrderBy(c => c.Tier).ThenBy(c => c.Sym).ToList();

                List<LadderCandidate> first, second;
                if (direct.Count > 0 && upgrade.Count > 0)
                {
                    bool preferDirect = _rng.NextDouble() < 0.5;
                    first  = preferDirect ? direct  : upgrade;
                    second = preferDirect ? upgrade : direct;
                }
                else
                {
                    first  = direct.Count > 0 ? direct : upgrade;
                    second = new List<LadderCandidate>();
                }

                var single = TryPick(bySym, first) ?? TryPick(bySym, second);
                if (single != null) winningCombo = new List<LadderCandidate> { single };
            }

            // No single candidate worked (either none exist for this exact amount, or every
            // one would reuse an already-consumed symbol) — search for a SUBSET of
            // candidates across unused DIFFERENT symbols whose amounts sum to the target exactly.
            // No cap on how many symbols may combine. When multiple valid combinations
            // exist, one is picked uniformly at random among all of them.
            if (winningCombo == null)
            {
                var allCombos = FindSumCombinations(amount, bySym);
                if (allCombos.Count > 0)
                    winningCombo = allCombos[_rng.Next(allCombos.Count)];
            }

            if (winningCombo == null)
                throw new InvalidOperationException(
                    $"${amount} cannot be reached — no single symbol/tier matches it exactly, " +
                    "and no distinct combination of unused symbols/tiers sums to it either.");

            // Apply the whole winning combination atomically.
            foreach (var c in winningCombo)
            {
                if (!bySym.TryGetValue(c.Sym, out var entry))
                {
                    entry = new BundleEntry { Sym = c.Sym, Target = c.Target, Tier = c.Tier, Amounts = new List<decimal>() };
                    bySym[c.Sym] = entry;
                }
                entry.Amounts.Add(amount);
            }
            covered.Add(amount);
        }

        var input = BuildBundledInput(bySym.Values.ToList());

        return new BundleResult
        {
            Input   = input,
            Entries = bySym.Values.ToList(),
            Covered = covered,
            Skipped = new List<decimal>(),
        };
    }

    /// <summary>
    /// Try every candidate in the given pool, in order, returning the first brand-new
    /// symbol. A requested prize amount must consume a distinct symbol/tier entry; we
    /// never silently share an already-used symbol/tier for a second prize.
    /// </summary>
    private static LadderCandidate? TryPick(Dictionary<int, BundleEntry> bySym, List<LadderCandidate> pool)
    {
        foreach (var candidate in pool)
        {
            if (!bySym.ContainsKey(candidate.Sym))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Search for every combination of candidates — at most ONE candidate per symbol,
    /// and only symbols not already used by previous requested prize amounts — whose
    /// amounts sum exactly to the target. Explores every symbol's
    /// tier options (including "don't use this symbol at all") via bounded recursion;
    /// with a handful of symbols and a few tiers each, the search space stays tiny.
    /// Returns every valid combination found, so the caller can pick one at random.
    /// </summary>
    private List<List<LadderCandidate>> FindSumCombinations(decimal target, Dictionary<int, BundleEntry> bySym)
    {
        // Group every candidate across the WHOLE table by symbol, excluding symbols
        // already consumed by prior requested prize amounts.
        var bySymbolOptions = new List<List<LadderCandidate>>();
        foreach (var symGroup in _rows.Select((row, i) => (Sym: i + 1, Row: row)))
        {
            if (bySym.ContainsKey(symGroup.Sym)) continue;

            var options = new List<LadderCandidate>();
            for (int tier = 0; tier < symGroup.Row.Tiers.Count; tier++)
            {
                options.Add(new LadderCandidate
                {
                    Sym = symGroup.Sym, Target = symGroup.Row.Target, Tier = tier,
                    Amount = symGroup.Row.Tiers[tier],
                });
            }
            if (options.Count > 0) bySymbolOptions.Add(options);
        }

        var results = new List<List<LadderCandidate>>();
        var current = new List<LadderCandidate>();
        Search(0, 0m);
        return results;

        void Search(int symIdx, decimal sumSoFar)
        {
            if (sumSoFar == target && current.Count > 0)
            {
                results.Add(new List<LadderCandidate>(current));
                // Keep searching — there may be other valid combinations too.
            }
            if (sumSoFar >= target || symIdx >= bySymbolOptions.Count) return;

            // Option A: skip this symbol entirely.
            Search(symIdx + 1, sumSoFar);

            // Option B: use exactly one of this symbol's available tiers.
            foreach (var option in bySymbolOptions[symIdx])
            {
                current.Add(option);
                Search(symIdx + 1, sumSoFar + option.Amount);
                current.RemoveAt(current.Count - 1);
            }
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static Dictionary<decimal, List<LadderCandidate>> BuildLookup(List<PrizeLadderRow> rows)
    {
        var lookup = new Dictionary<decimal, List<LadderCandidate>>();

        for (int i = 0; i < rows.Count; i++)
        {
            int sym = i + 1;   // row order = symbol order, auto-assigned
            var row = rows[i];

            for (int tier = 0; tier < row.Tiers.Count; tier++)
            {
                var candidate = new LadderCandidate
                {
                    Sym    = sym,
                    Target = row.Target,
                    Tier   = tier,
                    Amount = row.Tiers[tier],
                };

                if (!lookup.TryGetValue(candidate.Amount, out var list))
                    lookup[candidate.Amount] = list = new List<LadderCandidate>();
                list.Add(candidate);
            }
        }

        return lookup;
    }

    /// <summary>
    /// Merge every bundle entry into one MathInput. A symbol at tier N needs N separate
    /// PRIZE_UPGRADE token placements (one per tier step — see PrupFeat), so
    /// Required["PRIZE_UPGRADE"] is sized to the SUM of every entry's tier, not just the
    /// max — the Placer will place that many tokens total across the ticket, and PrupFeat
    /// assigns each one to whichever symbol still has tier steps left to climb.
    /// </summary>
    private MathInput BuildBundledInput(List<BundleEntry> entries)
    {
        var targets    = new Dictionary<int, int>();
        var prizeTiers = new Dictionary<int, int>();
        int totalUpgradeTokens = 0;
        int maxTarget = 0;

        foreach (var e in entries)
        {
            targets[e.Sym] = e.Target;
            maxTarget = Math.Max(maxTarget, e.Target);
            if (e.Tier > 0)
            {
                prizeTiers[e.Sym] = e.Tier;
                totalUpgradeTokens += e.Tier;
            }
        }

        var required = new Dictionary<string, int>();
        if (totalUpgradeTokens > 0) required["PRIZE_UPGRADE"] = totalUpgradeTokens;

        int fillSymCount = Math.Max(1, _rows.Count - targets.Count);

        return new MathInput
        {
            Targets    = targets,
            BaseSpins  = SpinsForBundle(maxTarget, totalUpgradeTokens, fillSymCount),
            Required   = required,
            PrizeTiers = prizeTiers.Count > 0 ? prizeTiers : null,
            PrizeValues = BuildPrizeValues(Enumerable.Range(1, _rows.Count)),
            MaxSym     = _rows.Count,
        };
    }

    private MathInput BuildInput(LadderCandidate c)
    {
        var required   = new Dictionary<string, int>();
        Dictionary<int, int>? prizeTiers = null;

        if (c.Tier > 0)
        {
            required["PRIZE_UPGRADE"] = c.Tier;
            prizeTiers = new Dictionary<int, int> { { c.Sym, c.Tier } };
        }

        return new MathInput
        {
            Targets    = new Dictionary<int, int> { { c.Sym, c.Target } },
            BaseSpins  = SpinsFor(c.Target, c.Tier),
            Required   = required,
            PrizeTiers = prizeTiers,
            PrizeValues = BuildPrizeValues(Enumerable.Range(1, _rows.Count)),
            MaxSym     = _rows.Count,
        };
    }

    private Dictionary<int, IReadOnlyDictionary<int, decimal>> BuildPrizeValues(IEnumerable<int> syms)
    {
        var values = new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
        foreach (int sym in syms.Distinct())
        {
            if (sym < 1 || sym > _rows.Count) continue;
            values[sym] = _rows[sym - 1].Tiers
                .Select((amount, tier) => (amount, tier))
                .ToDictionary(x => x.tier, x => x.amount);
        }
        return values;
    }

    /// <summary>
    /// BaseSpins is always fixed at K.BASE_SPINS (=5) — never computed from physWins
    /// or filler capacity. Any additional spin capacity needed comes from the
    /// EXTRA_SPIN feature (decided later, in Planner.ResolveFeatures, alongside
    /// WHEEL/FLUSH), not from this method.
    /// </summary>
    private static int SpinsFor(int target, int tier) => K.BASE_SPINS;

    /// <summary>Same fixed baseline for bundled multi-symbol tickets.</summary>
    private static int SpinsForBundle(int physWins, int tier, int fillSymCount) => K.BASE_SPINS;
}
