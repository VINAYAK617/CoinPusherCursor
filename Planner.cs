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
    private readonly Random    _rng;

    // Optional-feature probability parameters.
    // WHEEL and FLUSH are desirable for game variety, but are never forced unless
    // the ticket is structurally infeasible without them.
    private const double P_WHEEL = 0.65;  // per eligible win symbol
    private const double P_FLUSH = 0.35;  // per optional FLUSH token

    public Planner(MathInput inp, int? seed = null)
    {
        _inp = inp;
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public GamePlan Plan()
    {
        Validate();

        var log      = new List<string>();
        var winSyms  = _inp.Targets.Keys.OrderBy(x => x).ToList();
        var fillSyms = Enumerable.Range(1, _inp.MaxSym).Except(winSyms).ToList();
        var winSet   = new HashSet<int>(winSyms);

        // ── Feature resolution ────────────────────────────────────────────
        // Determines which WHEEL / FLUSH / EXTRA_SPIN tokens to include,
        // choosing them first by structural need, then by probability.
        var effectiveInp = ResolveFeatures(winSyms, fillSyms.Count, log);

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
        var schedulingInp  = new MathInput
        {
            Targets       = reducedTargets,
            BaseSpins     = effectiveInp.BaseSpins,
            Required      = effectiveInp.Required,
            WheelSymOrder = effectiveInp.WheelSymOrder,
            PrizeTiers    = effectiveInp.PrizeTiers,
            PrizeValues   = effectiveInp.PrizeValues,
            MaxSym        = effectiveInp.MaxSym,
        };

        log.Add($"wins=[{string.Join(",", winSyms)}] fills=[{string.Join(",", fillSyms)}]");
        if (decorBudget.Values.Any(b => b > 0))
            log.Add($"decorBudget=[{string.Join(",", decorBudget.Where(kv=>kv.Value>0).Select(kv=>$"sym{kv.Key}={kv.Value}"))}]");

        // ── Build pipeline ────────────────────────────────────────────────
        var placed      = new Placer(schedulingInp, _rng, log).Place();
        int totalSpins  = effectiveInp.BaseSpins + placed.Count(f => f.Id == "EXTRA_SPIN");
        var locks       = BuildLocks(placed, log, schedulingInp.Targets);
        var allocs      = new Scheduler(schedulingInp.Targets, placed, locks, log).Schedule(totalSpins);
        var fillTracker = new FillTracker(fillSyms.ToArray());
        var builder     = new Builder(schedulingInp.Targets, locks, placed,
                                       fillSyms.ToArray(), log, _rng, fillTracker, decorBudget);
        var spins       = builder.BuildAll(placed, allocs, totalSpins, effectiveInp.BaseSpins);

        new Resolver(fillSyms.ToArray(), winSet, log, _rng, fillTracker).Resolve(spins);

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
            Spins      = spins,
            Log        = log,
        };

        Verifier.Check(plan);
        plan.Verified = true;
        log.Add("verified OK");

        return plan;
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
    private MathInput ResolveFeatures(List<int> winSyms, int fillSymCount, List<string> log)
    {
        // Caller already specified WHEEL or EXTRA_SPIN → respect fully, no changes.
        if (_inp.Required.ContainsKey("WHEEL") || _inp.Required.ContainsKey("EXTRA_SPIN"))
            return _inp;

        int physWins = CapacityModel.PhysicalWins(_inp.Targets, 0);
        int spins    = _inp.BaseSpins;   // never clamped — BaseSpins is fixed at 5 by design
        int tokenLoad = RequiredTokenLoad(_inp.Required);
        int wheels   = 0, flushes = 0, extras = 0;

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
            bool lucky  = !needed && _rng.NextDouble() < P_WHEEL && tgt >= 10;
            if (needed || lucky)
            {
                wheels++;
                tokenLoad++;
                physWins = physWins - tgt + physNew;
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
            bool lucky  = !needed && _rng.NextDouble() < P_FLUSH;
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

        bool changed = wheels > 0 || flushes > 0 || extras > 0;
        if (!changed) return _inp;

        var merged = new Dictionary<string, int>(_inp.Required);
        if (wheels  > 0) merged["WHEEL"]      = wheels;
        if (flushes > 0) merged["FLUSH"]       = flushes;
        if (extras  > 0) merged["EXTRA_SPIN"]  = extras;

        log.Add($"features: wheels={wheels} flushes={flushes} extra={extras}" +
                $" baseSpins={spins} totalSpins={spins + extras} physWins={physWins}" +
                $" feasible={CapacityModel.IsFeasible(physWins, spins + extras, fillSymCount, tokenLoad: tokenLoad + extras)}");

        return new MathInput
        {
            Targets       = _inp.Targets,
            BaseSpins     = spins,          // always _inp.BaseSpins, untouched
            Required      = merged,
            WheelSymOrder = _inp.WheelSymOrder,
            PrizeTiers    = _inp.PrizeTiers,
            PrizeValues   = _inp.PrizeValues,
            MaxSym        = _inp.MaxSym,
        };
    }

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
                if (!_inp.Targets.ContainsKey(sym))
                    throw new ArgumentException($"PrizeValues sym {sym} not in Targets");
                foreach (var (tier, value) in tiers)
                {
                    if (tier < 0)
                        throw new ArgumentException($"Prize value tier for sym {sym} must be >= 0");
                    if (value < 0)
                        throw new ArgumentException($"Prize value for sym {sym} tier {tier} must be >= 0");
                }
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


