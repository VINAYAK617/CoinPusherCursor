namespace CoinPusherEngine;

/// <summary>
/// Full ticket-generation pipeline:
/// Validate → ResolveFeatures → Placer → BuildLocks → Scheduler → Builder → Resolver → Verifier
///
/// All outcomes are predetermined and sealed. The Engine only replays the plan.
/// </summary>
public sealed class Planner
{
    private readonly MathInput _inp;
    private readonly int       _baseSeed;
    private Random             _rng;
    private static readonly Random SeedRng = new();
    private static readonly object SeedLock = new();

    // Optional-feature probability parameters now live in K (Const.cs) as
    // K.P_WHEEL_OPTIONAL / K.P_FLUSH_OPTIONAL / K.P_NONWIN_WHEEL /
    // K.P_NONWIN_PRIZE_UPGRADE — see the comment there for what they do and do
    // not control. WHEEL and FLUSH are desirable for game variety, but are never
    // forced unless the ticket is structurally infeasible without them.
    private const int    MaxPlanAttempts = 512;

    public Planner(MathInput inp, int? seed = null)
    {
        _inp = inp;
        _baseSeed = seed ?? NextSeed();
        _rng = new Random(_baseSeed);
    }

    private static int NextSeed()
    {
        lock (SeedLock) return SeedRng.Next();
    }

    public GamePlan Plan()
    {
        Validate();

        Exception? last = null;
        for (int attempt = 0; attempt < MaxPlanAttempts; attempt++)
        {
            _rng = new Random(AttemptSeed(_baseSeed, attempt));
            try
            {
                var plan = PlanOnce();
                if (attempt > 0)
                    plan.Log.Insert(0, $"planned after {attempt + 1} internal attempts");
                return plan;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            $"Could not build a verified plan after {MaxPlanAttempts} internal attempts.", last);
    }

    private GamePlan PlanOnce()
    {
        var log      = new List<string>();
        var winSyms  = _inp.Targets.Keys.OrderBy(x => x).ToList();
        var fillSyms = Enumerable.Range(1, _inp.MaxSym).Except(winSyms).ToList();
        var winSet   = new HashSet<int>(winSyms);
        var nonWinTargets = ResolveNonWinTargets(fillSyms);
        var nonWinPrizeTiers = ResolveNonWinPrizeTiers(nonWinTargets);

        // ── Feature resolution ────────────────────────────────────────────
        // Determines which WHEEL / FLUSH / EXTRA_SPIN tokens to include,
        // choosing them first by structural need, then by probability.
        var effectiveInp = ResolveFeatures(winSyms, fillSyms.Count, log, nonWinTargets, nonWinPrizeTiers);

        // ── Exact-count scheduling ────────────────────────────────────────
        // Every requested target is scheduled as an actual win. Earlier versions
        // subtracted a small "decorative" win-symbol budget here and asked Builder
        // to place those missing cells opportunistically in spare zone capacity.
        // That made some seeds finish short by one when a decorative cell could not
        // be placed safely (usually around WHEEL isolation). Keeping the decorative
        // budget disabled preserves the invariant that Scheduler owns the full count
        // for every win symbol, so Verifier failures are not caused by optional art.
        var decorBudget    = new Dictionary<int, int>();
        var reducedTargets = effectiveInp.Targets.ToDictionary(kv => kv.Key, kv => kv.Value);
        var allocTargets = reducedTargets
            .Concat(nonWinTargets)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var schedulingInp  = new MathInput
        {
            Targets       = allocTargets,
            BaseSpins     = effectiveInp.BaseSpins,
            Required      = effectiveInp.Required,
            WheelSymOrder = effectiveInp.WheelSymOrder,
            PrizeTiers    = effectiveInp.PrizeTiers,
            PrizeValues   = effectiveInp.PrizeValues,
            NonWinTargets = nonWinTargets,
            NonWinPrizeTiers = nonWinPrizeTiers,
            MaxSym        = effectiveInp.MaxSym,
        };

        log.Add($"wins=[{string.Join(",", winSyms)}] fills=[{string.Join(",", fillSyms)}]");
        if (nonWinTargets.Count > 0)
            log.Add($"nonWins=[{string.Join(",", nonWinTargets.Select(kv=>$"sym{kv.Key}>={kv.Value}<cap{K.FILL_CAP}"))}]");
        if (nonWinPrizeTiers.Count > 0)
            log.Add($"nonWinPrizeUpgrades=[{string.Join(",", nonWinPrizeTiers.Select(kv=>$"sym{kv.Key}@tier{kv.Value}"))}]");
        if (decorBudget.Values.Any(b => b > 0))
            log.Add($"decorBudget=[{string.Join(",", decorBudget.Where(kv=>kv.Value>0).Select(kv=>$"sym{kv.Key}={kv.Value}"))}]");

        // ── Build pipeline ────────────────────────────────────────────────
        var placed      = new Placer(schedulingInp, _rng, log).Place();
        int totalSpins  = effectiveInp.BaseSpins + placed.Count(f => f.Id == "EXTRA_SPIN");
        var locks       = BuildLocks(placed, log, allocTargets);
        var allocs      = new Scheduler(allocTargets, placed, locks, log, winSyms).Schedule(totalSpins);
        var fillTracker = new FillTracker(fillSyms.ToArray());
        var builder     = new Builder(allocTargets, locks, placed,
                                       fillSyms.ToArray(), log, _rng, fillTracker, decorBudget);
        var spins       = builder.BuildAll(placed, allocs, totalSpins, effectiveInp.BaseSpins);

        new Resolver(fillSyms.ToArray(), winSet, log, _rng, fillTracker, nonWinTargets.Keys).Resolve(spins);

        var prizeTiers = _inp.PrizeTiers != null
            ? _inp.PrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value)
            : new Dictionary<int, int>();
        var prizeValues = ClonePrizeValues(_inp.PrizeValues);

        var plan = new GamePlan
        {
            TotalSpins = totalSpins,
            Targets    = effectiveInp.Targets,
            WinSyms    = winSyms,
            FillSyms   = fillSyms,
            PrizeTiers = prizeTiers,
            PrizeValues = prizeValues,
            NonWinTargets = nonWinTargets,
            NonWinPrizeTiers = nonWinPrizeTiers,
            Spins      = spins,
            Log        = log,
        };

        Verifier.Check(plan);
        plan.Verified = true;
        log.Add("verified OK");

        return plan;
    }

    private static int AttemptSeed(int seed, int attempt)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= (uint)(attempt + 1) * 0x9E3779B9u;
            x ^= x >> 16;
            x *= 0x85EBCA6Bu;
            x ^= x >> 13;
            x *= 0xC2B2AE35u;
            x ^= x >> 16;
            return (int)x;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FEATURE RESOLUTION
    // ═══════════════════════════════════════════════════════════════════════
    //
    // BaseSpins is fixed at K.BASE_SPINS (5) for every ticket LadderCombinator
    // produces — it is never computed or clamped here. The only way to add spin
    // capacity beyond that 5-spin baseline is to plan for and award the EXTRA_SPIN
    // feature, up to (K.MAX_SPINS - K.BASE_SPINS) = 3 times, for a hard ceiling of
    // K.MAX_SPINS (8) total spins per ticket.
    //
    // The user's capacity model (exact):
    //   Without extra-spin, max achievable = COLS × S × ROWS(flush) = 5×S×5 = 25S
    //   Each flush column gives 5 rows instead of MAX_PUSH(3) → more capacity per spin.
    //   WHEEL compresses a symbol's physical cell count → fewer cells needed for same target.
    //
    // Feature types:
    //   COMPULSORY  — caller already set these (PRIZE_UPGRADE from tier counts).
    //                 We never override them.
    //   NECESSARY   — added automatically when the ticket is infeasible without them,
    //                 even at the K.MAX_SPINS (8-spin) ceiling.
    //   OPTIONAL    — added probabilistically when feasibility is already satisfied.
    //
    // Decision order: WHEEL → FLUSH → EXTRA_SPIN. EXTRA_SPIN is the primary capacity
    // lever now that BaseSpins itself can't move — WHEEL/FLUSH reduce physWins first
    // (so fewer extra spins end up being needed), then EXTRA_SPIN is sized to exactly
    // close whatever feasibility gap remains, up to its 3-award cap.
    //
    private MathInput ResolveFeatures(List<int> winSyms, int fillSymCount, List<string> log,
                                      IReadOnlyDictionary<int, int> nonWinTargets,
                                      IReadOnlyDictionary<int, int> nonWinPrizeTiers)
    {
        int physWins = CapacityModel.PhysicalWins(_inp.Targets, 0);
        int spins    = _inp.BaseSpins;   // never clamped — BaseSpins is fixed at 5 by design
        int plannedFillerLoad = nonWinTargets.Values.Sum();
        int nonWinPrupTokens = nonWinPrizeTiers.Values.Sum();
        int tokenLoad = RequiredTokenLoad(_inp.Required) + plannedFillerLoad + nonWinPrupTokens;
        int wheels   = 0, nonWinWheels = 0, flushes = 0, extras = 0;
        bool allowOptionalFeatures = !IsHighPressureTicket();

        if (_inp.Required.ContainsKey("WHEEL") || _inp.Required.ContainsKey("FLUSH") || _inp.Required.ContainsKey("EXTRA_SPIN"))
            return AddNonWinFeaturesIfPossible(_inp, winSyms, nonWinTargets, nonWinPrizeTiers, log);

        // ── 1. WHEEL ──────────────────────────────────────────────────────
        // Compresses each symbol's physical cell footprint. Applied to symbols
        // with largest targets first (they benefit most from stacking compression).
        // NECESSARY when infeasible even at the maximum spin count (5 base + 3 extra);
        // OPTIONAL with P_WHEEL probability when already feasible without it.
        // Checked against the worst-case ceiling (spins + remaining EXTRA_SPIN awards)
        // so WHEEL isn't forced just because EXTRA_SPIN alone could have covered it.
        var ordered = winSyms.OrderByDescending(s => _inp.Targets[s]).ToList();
        foreach (int sym in ordered)
        {
            int tgt     = _inp.Targets[sym];
            int n       = WMath.BestN(tgt);
            int stack   = 1 << n;
            int zone    = WMath.Zone(tgt, stack);
            int physNew = zone + Math.Max(0, tgt - zone * stack);
            if (physNew >= tgt) continue;  // no compression benefit

            bool needed = CapacityModel.MinExtraSpins(physWins, fillSymCount, spins, tokenLoad) < 0;
            bool lucky  = allowOptionalFeatures && !needed && _rng.NextDouble() < K.P_WHEEL_OPTIONAL && tgt >= 10;
            if (needed || lucky)
            {
                wheels++;
                tokenLoad++;
                physWins = physWins - tgt + physNew;
            }
        }

        // Reserve a WHEEL for each ELIGIBLE near-miss filler symbol independently
        // (not just one) — this makes the WHEEL feature visibly apply across
        // multiple non-winning symbols when there's more than one with a large
        // enough target, not capped to a single token regardless of how many
        // qualify. Each candidate is gated by the same P_NONWIN_WHEEL roll and the
        // same cumulative Max-instance cap WHEEL already enforces across the
        // whole ticket (win symbols included) — so this never overshoots the
        // feature's own hard limit, it just stops artificially capping at one
        // when the limit allows more. Purely cosmetic — unlike win-symbol WHEEL
        // above, there is no "needed" override here at all, since a near-miss
        // symbol's collection is never load-bearing for feasibility.
        if (nonWinTargets.Count > 0)
        {
            foreach (var sym in nonWinTargets.Where(kv => kv.Value >= 2).Select(kv => kv.Key))
            {
                if (wheels >= FeatReg.Cfg["WHEEL"].Max) break;
                if (_rng.NextDouble() >= K.P_NONWIN_WHEEL) continue;
                wheels++;
                nonWinWheels++;
                tokenLoad++;
            }
        }

        // ── 2. FLUSH ──────────────────────────────────────────────────────
        // Each FLUSH raises one spin's capacity from NormalVol(15) to FlushVol(25).
        // NECESSARY when still infeasible even at the max spin ceiling; OPTIONAL
        // with P_FLUSH probability. Capped at COLS-1 to avoid degenerate all-flush boards.
        int maxFlush = K.COLS - 1;
        for (int f = 0; f < maxFlush; f++)
        {
            bool needed = CapacityModel.MinExtraSpins(physWins, fillSymCount, spins, tokenLoad) < 0;
            bool lucky  = allowOptionalFeatures && !needed && _rng.NextDouble() < K.P_FLUSH_OPTIONAL;
            if (needed || lucky) flushes++;
            else break;
        }

        // ── 3. EXTRA_SPIN ─────────────────────────────────────────────────
        // BaseSpins is fixed at K.BASE_SPINS (5) — it is never increased directly.
        // The ONLY way to add capacity beyond the 5-spin baseline is to award
        // EXTRA_SPIN, up to K.MAX_SPINS - K.BASE_SPINS (= 3) times, giving a hard
        // ceiling of K.MAX_SPINS (8) total spins per ticket.
        //
        // MinExtraSpins finds the smallest extras count (0..3) at which physWins
        // (after any WHEEL compression above) fits within the filler budget. If it
        // returns -1, the bundle is infeasible even at the 8-spin ceiling — WHEEL/FLUSH
        // already ran first specifically to reduce physWins enough that this still has
        // a chance; if it's still -1 here, the bundle genuinely cannot be built within
        // this game's symbol/filler configuration and the caller's retry loop (or the
        // bundle itself) needs to change, not this method.
        int minExtras = CapacityModel.MinExtraSpins(physWins, fillSymCount, spins, tokenLoad);
        extras = Math.Max(0, minExtras);   // -1 (infeasible) clamps to 0; nothing more we can do here

        bool changed = wheels > 0 || flushes > 0 || extras > 0 || nonWinPrupTokens > 0;
        if (!changed) return _inp;

        var merged = AddRequired(_inp.Required, "PRIZE_UPGRADE", nonWinPrupTokens);
        if (wheels  > 0) merged["WHEEL"]      = wheels;
        if (flushes > 0) merged["FLUSH"]       = flushes;
        if (extras  > 0) merged["EXTRA_SPIN"]  = extras;

        log.Add($"features: wheels={wheels} nonWinWheels={nonWinWheels} flushes={flushes} extra={extras} nonWinPrup={nonWinPrupTokens}" +
                $" baseSpins={spins} totalSpins={spins + extras} physWins={physWins}" +
                $" feasible={CapacityModel.IsFeasible(physWins, spins + extras, fillSymCount, tokenLoad: tokenLoad + extras)}");

        var wheelOrder = BuildWheelOrder(winSyms, nonWinTargets, wheels, nonWinWheels);
        var prizeTiers = MergeTiers(_inp.PrizeTiers, nonWinPrizeTiers);

        return new MathInput
        {
            Targets       = _inp.Targets,
            BaseSpins     = spins,          // always _inp.BaseSpins, untouched
            Required      = merged,
            WheelSymOrder = wheelOrder.Count > 0 ? wheelOrder : _inp.WheelSymOrder,
            PrizeTiers    = prizeTiers.Count > 0 ? prizeTiers : null,
            PrizeValues   = _inp.PrizeValues,
            NonWinTargets = _inp.NonWinTargets,
            NonWinPrizeTiers = _inp.NonWinPrizeTiers,
            MaxSym        = _inp.MaxSym,
        };
    }

    private List<int> BuildWheelOrder(List<int> winSyms, IReadOnlyDictionary<int, int> nonWinTargets,
                                      int wheels, int nonWinWheels)
    {
        var winOrder = winSyms.OrderByDescending(s => _inp.Targets[s]).ToList();
        var nonWinOrder = nonWinTargets
            .Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        if (_inp.WheelSymOrder != null)
            return _inp.WheelSymOrder.Concat(nonWinOrder).Distinct().ToList();

        if (nonWinWheels == 0) return winOrder;

        int winWheelCount = Math.Max(0, wheels - nonWinWheels);
        return winOrder.Take(winWheelCount)
            .Concat(nonWinOrder)
            .Concat(winOrder.Skip(winWheelCount))
            .Distinct()
            .ToList();
    }

    private MathInput AddNonWinFeaturesIfPossible(MathInput source, List<int> winSyms,
                                                  IReadOnlyDictionary<int, int> nonWinTargets,
                                                  IReadOnlyDictionary<int, int> nonWinPrizeTiers,
                                                  List<string> log)
    {
        int existingWheels = source.Required.GetValueOrDefault("WHEEL");
        int existingExtras = source.Required.GetValueOrDefault("EXTRA_SPIN");
        int nonWinPrupTokens = nonWinPrizeTiers.Values.Sum();
        int plannedFillerLoad = nonWinTargets.Values.Sum();

        // Same per-symbol, cumulative-cap gating as ResolveFeatures' main path
        // (see its comment) — every eligible near-miss symbol independently rolls
        // against P_NONWIN_WHEEL, stopping once WHEEL's own Max instance cap
        // (shared with whatever win-symbol wheels the caller already requested)
        // is reached.
        int addedWheels = 0;
        int wheelBudget = FeatReg.Cfg["WHEEL"].Max - existingWheels;
        foreach (var sym in nonWinTargets.Where(kv => kv.Value >= 2).Select(kv => kv.Key))
        {
            if (addedWheels >= wheelBudget) break;
            if (_rng.NextDouble() >= K.P_NONWIN_WHEEL) continue;
            addedWheels++;
        }

        var required = AddRequired(source.Required, "PRIZE_UPGRADE", nonWinPrupTokens);
        if (addedWheels > 0) required["WHEEL"] = existingWheels + addedWheels;

        int totalWheels = required.GetValueOrDefault("WHEEL");
        int physWins = CapacityModel.PhysicalWins(source.Targets, totalWheels);
        int tokenLoadExcludingExtraSpins =
            RequiredTokenLoad(required) - existingExtras + plannedFillerLoad;
        int minExtras = CapacityModel.MinExtraSpins(
            physWins,
            Math.Max(1, source.MaxSym - source.Targets.Count),
            source.BaseSpins,
            tokenLoadExcludingExtraSpins);
        if (minExtras > existingExtras)
        {
            required["EXTRA_SPIN"] = minExtras;
            log.Add($"features: raised EXTRA_SPIN from {existingExtras} to {minExtras} for required-feature capacity");
        }

        if (addedWheels == 0 && nonWinPrupTokens == 0 && required.Count == source.Required.Count && required.All(kv => source.Required.TryGetValue(kv.Key, out var count) && count == kv.Value))
            return source;

        var wheelOrder = BuildWheelOrder(winSyms, nonWinTargets, existingWheels + addedWheels, addedWheels);
        var prizeTiers = MergeTiers(source.PrizeTiers, nonWinPrizeTiers);
        log.Add($"features: added nonWinWheels={addedWheels} totalWheels={required.GetValueOrDefault("WHEEL")} nonWinPrup={nonWinPrupTokens}");

        return new MathInput
        {
            Targets       = source.Targets,
            BaseSpins     = source.BaseSpins,
            Required      = required,
            WheelSymOrder = wheelOrder.Count > 0 ? wheelOrder : source.WheelSymOrder,
            PrizeTiers    = prizeTiers.Count > 0 ? prizeTiers : null,
            PrizeValues   = source.PrizeValues,
            NonWinTargets = source.NonWinTargets,
            NonWinPrizeTiers = source.NonWinPrizeTiers,
            MaxSym        = source.MaxSym,
        };
    }

    private static Dictionary<string, int> AddRequired(IReadOnlyDictionary<string, int> required,
                                                       string id, int count)
    {
        var merged = new Dictionary<string, int>(required);
        if (count <= 0) return merged;
        merged[id] = merged.GetValueOrDefault(id) + count;
        return merged;
    }

    private static Dictionary<int, int> MergeTiers(IReadOnlyDictionary<int, int>? prizeTiers,
                                                   IReadOnlyDictionary<int, int> nonWinPrizeTiers)
    {
        var merged = prizeTiers?.ToDictionary(kv => kv.Key, kv => kv.Value)
                   ?? new Dictionary<int, int>();
        foreach (var (sym, tier) in nonWinPrizeTiers)
            merged[sym] = tier;
        return merged;
    }

    private Dictionary<int, int> ResolveNonWinTargets(IReadOnlyList<int> fillSyms)
    {
        if (_inp.NonWinTargets != null)
            return _inp.NonWinTargets.ToDictionary(kv => kv.Key, kv => kv.Value);

        if (fillSyms.Count == 0) return new Dictionary<int, int>();

        // Near-miss targets are intentionally substantial — at least
        // K.NONWIN_MIN_TARGET (15) — so the near-miss EXPERIENCE is actually
        // visible to the player (a target of 1-3, as earlier versions used,
        // barely registers). Capped at K.FILL_CAP-1 because K.FILL_CAP itself
        // is the first invalid count; Verifier's strict "< FILL_CAP" check
        // everywhere else. This consumes more of the same filler/tokenLoad
        // budget that ResolveFeatures' capacity math (MinExtraSpins, IsFeasible)
        // already accounts for — nothing here bypasses that; if a larger
        // near-miss footprint doesn't fit a given ticket, that check reports
        // infeasible and the normal retry loop handles it, exactly as it
        // already does for any other infeasible combination.
        //
        // High-pressure tickets already need most of the board budget for
        // guaranteed win delivery. A small near-miss may still appear, but only by
        // probability and at a lower target band.
        if (IsHighPressureTicket())
        {
            if (_rng.NextDouble() >= K.P_HIGH_PRESSURE_NONWIN_TARGET)
            {
                return new Dictionary<int, int>();
            }

            int sym = fillSyms[_rng.Next(fillSyms.Count)];
            int target = _rng.Next(
                K.HIGH_PRESSURE_NONWIN_MIN_TARGET,
                Math.Min(K.HIGH_PRESSURE_NONWIN_MAX_TARGET, K.FILL_CAP - 1) + 1);
            return new Dictionary<int, int> { { sym, target } };
        }

        var profile = PickNonWinProfile();
        if (profile.MaxSymbols <= 0 || profile.Max <= 0)
        {
            return new Dictionary<int, int>();
        }

        int count = _rng.Next(1, Math.Min(profile.MaxSymbols, fillSyms.Count) + 1);
        int minTarget = Math.Min(profile.Min, K.FILL_CAP - 1);
        int maxTarget = Math.Min(profile.Max, K.FILL_CAP - 1);

        return fillSyms
            .OrderBy(_ => _rng.Next())
            .Take(count)
            .ToDictionary(sym => sym, _ => _rng.Next(minTarget, maxTarget + 1));
    }

    private (double P, int Min, int Max, int MaxSymbols) PickNonWinProfile()
    {
        var roll = _rng.NextDouble();
        var acc = 0.0;
        foreach (var profile in K.NONWIN_TARGET_PROFILES)
        {
            acc += profile.P;
            if (roll <= acc) return profile;
        }

        return K.NONWIN_TARGET_PROFILES[^1];
    }

    private Dictionary<int, int> ResolveNonWinPrizeTiers(IReadOnlyDictionary<int, int> nonWinTargets)
    {
        if (_inp.NonWinPrizeTiers != null)
            return _inp.NonWinPrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value);

        // Earlier versions skipped this entirely whenever the ticket already had
        // ANY PRIZE_UPGRADE tokens for win symbols — but that's checking "does this
        // ticket use upgrade tiers at all", not "is there capacity headroom for one
        // more token". The two are unrelated: tokenLoad accounting in
        // ResolveFeatures already counts a near-miss upgrade token additively
        // alongside whatever win symbols need (see nonWinPrupTokens there), and
        // MinExtraSpins is the real, capacity-aware gate on whether it actually fits
        // a given ticket. That blanket check meant the most common, most interesting
        // tickets (anything using real upgrade tiers) never showed a near-miss
        // visual upgrade at all — removing it lets the existing capacity math do
        // its job instead of pre-emptively assuming there's no room.
        //
        // A symbol whose ladder row has only one tier (e.g. a flat $10000 payout
        // with no upgrade steps at all) has nothing to climb TO — there is no tier-1
        // amount to show. Giving it a PrizeTier anyway is not just a missing visual,
        // it's actively wrong: TicketSerializer.PrizeValueFor falls back to the bare
        // tier NUMBER when no tier-1 entry exists for that symbol, so the ticket would
        // show a fabricated "$1" upgrade on what is supposed to be a single flat
        // $10000 payout. Only symbols whose PrizeValues table actually HAS a tier-1
        // entry are eligible — this is checked directly against PrizeValues (already
        // populated for every symbol in the game, not just win symbols) rather than
        // inferred from anything else, so it can never drift out of sync with what
        // TicketSerializer will actually be able to look up.
        var eligible = nonWinTargets.Where(kv => kv.Value >= 1 && HasUpgradeTier(kv.Key, 1))
                                     .Select(kv => kv.Key)
                                     .OrderBy(_ => _rng.Next())
                                     .ToList();
        if (eligible.Count == 0) return new Dictionary<int, int>();
        if (_rng.NextDouble() >= K.P_NONWIN_PRIZE_UPGRADE) return new Dictionary<int, int>();

        // Climb to tier 2 (not just tier 1) when the symbol's own ladder actually
        // has a tier-2 amount to show — PrupFeat.TryPlace already handles multi-
        // tier targets generically for ANY symbol in PrizeTiers (win or near-miss
        // alike, since near-miss symbols are merged into the scheduling Targets
        // dict before Placer ever runs — see PlanOnce), so no placement-side
        // change is needed, only declaring the higher tier here when eligible.
        int sym = eligible[0];
        if (IsHighPressureTicket())
        {
            return new Dictionary<int, int> { { sym, 1 } };
        }

        int tier = HasUpgradeTier(sym, 2) ? 2 : 1;
        return new Dictionary<int, int> { { sym, tier } };
    }

    /// <summary>
    /// True iff symbol `sym`'s ladder row actually has a usable amount at `tier`
    /// (e.g. tier=1 means "does this symbol have a second rung to climb to at
    /// all"). Symbols with only a single flat tier (no PrizeValues entry beyond
    /// index 0) return false — they have nothing meaningful to upgrade to.
    /// Conservative by design: if PrizeValues wasn't provided at all (e.g. a
    /// hand-authored MathInput that only set PrizeTiers), this returns false
    /// rather than guessing, since there is nothing to safely verify against.
    /// </summary>
    private bool HasUpgradeTier(int sym, int tier) =>
        _inp.PrizeValues != null
        && _inp.PrizeValues.TryGetValue(sym, out var tiers)
        && tiers.ContainsKey(tier);

    private bool IsHighPressureTicket() =>
        _inp.Targets.Count >= 4
        || _inp.Targets.Values.Sum() >= 80
        || _inp.Required.GetValueOrDefault("PRIZE_UPGRADE") >= 4;

    private static Dictionary<int, IReadOnlyDictionary<int, decimal>> ClonePrizeValues(
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>>? prizeValues) =>
        prizeValues?.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyDictionary<int, decimal>)kv.Value.ToDictionary(t => t.Key, t => t.Value))
        ?? new Dictionary<int, IReadOnlyDictionary<int, decimal>>();

    private static int RequiredTokenLoad(IReadOnlyDictionary<string, int> required) =>
        required.Where(kv => FeatReg.Has(kv.Key) && FeatReg.Get(kv.Key).HasToken)
                .Sum(kv => kv.Value);

    // ═══════════════════════════════════════════════════════════════════════
    // VALIDATION
    // ═══════════════════════════════════════════════════════════════════════
    private void Validate()
    {
        if (_inp.BaseSpins < 1) throw new ArgumentException("BaseSpins must be >= 1");
        if (_inp.Targets.Count == 0) throw new ArgumentException("Targets must not be empty");
        if (_inp.MaxSym < 1) throw new ArgumentException("MaxSym must be >= 1");

        foreach (var (sym, tgt) in _inp.Targets)
        {
            if (sym < 1 || sym > _inp.MaxSym)
                throw new ArgumentException($"Symbol {sym} out of range 1..{_inp.MaxSym}");
            if (tgt <= 0)
                throw new ArgumentException($"Target for sym {sym} must be > 0");
        }

        int fillerCount = _inp.MaxSym - _inp.Targets.Count;
        if (fillerCount < 2)
            throw new ArgumentException(
                $"Need at least 2 filler symbols; got {fillerCount} " +
                $"({_inp.Targets.Count} win symbols in a {_inp.MaxSym}-symbol game).");

        foreach (var (fid, cnt) in _inp.Required)
        {
            if (!FeatReg.Has(fid)) throw new ArgumentException($"Unknown feature '{fid}'");
            if (cnt < 0) throw new ArgumentException($"Required count for {fid} must be >= 0");
        }

        if (_inp.PrizeTiers != null)
            foreach (var (sym, tier) in _inp.PrizeTiers)
            {
                if (!_inp.Targets.ContainsKey(sym))
                    throw new ArgumentException($"PrizeTiers sym {sym} not in Targets");
                if (tier < 0)
                    throw new ArgumentException($"Prize tier for sym {sym} must be >= 0");
            }

        if (_inp.PrizeValues != null)
            foreach (var (sym, tiers) in _inp.PrizeValues)
            {
                if (sym < 1 || sym > _inp.MaxSym)
                    throw new ArgumentException($"PrizeValues sym {sym} out of range 1..{_inp.MaxSym}");
                foreach (var (tier, value) in tiers)
                {
                    if (tier < 0)
                        throw new ArgumentException($"Prize value tier for sym {sym} must be >= 0");
                    if (value < 0)
                        throw new ArgumentException($"Prize value for sym {sym} tier {tier} must be >= 0");
                }
            }

        if (_inp.NonWinTargets != null)
            foreach (var (sym, target) in _inp.NonWinTargets)
            {
                if (sym < 1 || sym > _inp.MaxSym)
                    throw new ArgumentException($"NonWinTargets sym {sym} out of range 1..{_inp.MaxSym}");
                if (_inp.Targets.ContainsKey(sym))
                    throw new ArgumentException($"NonWinTargets sym {sym} is already a winning target");
                if (target <= 0 || target >= K.FILL_CAP)
                    throw new ArgumentException($"NonWinTargets sym {sym} must be in range 1..{K.FILL_CAP - 1}");
            }

        if (_inp.NonWinPrizeTiers != null)
            foreach (var (sym, tier) in _inp.NonWinPrizeTiers)
            {
                if (sym < 1 || sym > _inp.MaxSym)
                    throw new ArgumentException($"NonWinPrizeTiers sym {sym} out of range 1..{_inp.MaxSym}");
                if (_inp.Targets.ContainsKey(sym))
                    throw new ArgumentException($"NonWinPrizeTiers sym {sym} is already a winning target");
                if (_inp.NonWinTargets != null && !_inp.NonWinTargets.ContainsKey(sym))
                    throw new ArgumentException($"NonWinPrizeTiers sym {sym} must exist in NonWinTargets");
                if (tier <= 0)
                    throw new ArgumentException($"NonWinPrizeTiers sym {sym} must be > 0");
            }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // WHEEL LOCK CONSTRUCTION
    // ═══════════════════════════════════════════════════════════════════════
    private IReadOnlyList<WLock> BuildLocks(List<PlacedFeat> placed, List<string> log,
                                             IReadOnlyDictionary<int, int> targets)
    {
        var locks = new List<WLock>();

        foreach (var g in placed.Where(f => f.Id == "WHEEL" && f.WSym != 0).GroupBy(f => f.WSym))
        {
            int sym = g.Key, tgt = targets[sym];
            var ord = g.OrderBy(f => f.Spin).ToList();

            if (ord.Count > 1)
            {
                int t1 = _rng.Next(Math.Max(1, tgt / 4), Math.Max(2, 3 * tgt / 4));
                int n1 = WMath.BestN(t1), n2 = WMath.BestN(tgt - t1);
                var (lk1, lk2) = WMath.MakeMultiLock(sym, tgt, ord[0].Spin, n1, ord[1].Spin, n2, t1);

                if (lk1.Zone == 0 || lk2.Zone == 0)
                {
                    var best = lk1.Zone >= lk2.Zone ? ord[0] : ord[1];
                    locks.Add(WMath.MakeLock(sym, tgt, best.Spin, WMath.BestN(tgt)));
                    log.Add($"  WHEEL sym={sym} multi-degenerate → single@S{best.Spin}");
                }
                else
                {
                    locks.Add(lk1); locks.Add(lk2);
                    log.Add($"  WHEEL sym={sym} multi S{ord[0].Spin}+S{ord[1].Spin}");
                }
            }
            else
            {
                var lk = WMath.MakeLock(sym, tgt, ord[0].Spin, WMath.BestN(tgt));
                locks.Add(lk);
                log.Add($"  WHEEL sym={sym} tgt={tgt} stack={lk.Stack} pre={lk.Pre} post={lk.Post} zone={lk.Zone} @S{ord[0].Spin}");
            }
        }
        return locks;
    }
}


