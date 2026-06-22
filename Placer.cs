namespace CoinPusherEngine;

internal sealed class Placer
{
    private readonly MathInput    _inp;
    private readonly Random       _rng;
    private readonly List<string> _log;
    private const    int          Tries = 400;

    internal Placer(MathInput inp, Random rng, List<string> log)
    { _inp = inp; _rng = rng; _log = log; }

    internal List<PlacedFeat> Place()
    {
        var done = new List<PlacedFeat>();
        var used = new HashSet<(int, int)>();

        // The TRUE total spin count this ticket will end up with: BaseSpins plus
        // whatever EXTRA_SPIN count ResolveFeatures already decided (it runs before
        // Placer and bakes its decision into Required). Every feature's placement
        // ceiling is derived from THIS, not BaseSpins alone — otherwise WHEEL/FLUSH/
        // PRIZE_UPGRADE could never be placed in the bonus spins EXTRA_SPIN adds,
        // cramming everything into a much smaller window than actually exists.
        int knownExtraSpins = _inp.Required.GetValueOrDefault("EXTRA_SPIN", 0);
        int totalSpinsKnown = _inp.BaseSpins + knownExtraSpins;

        foreach (var id in FeatReg.Ordered)
        {
            var (_, maxInst, minS, maxS, _) = FeatReg.Cfg[id];
            int req     = _inp.Required.GetValueOrDefault(id, 0);
            int limit   = req > 0 ? req : maxInst;
            double prob = FeatReg.Cfg[id].P;
            // maxS is an exclusive upper bound throughout this class and in
            // Feat.TryPlace implementations. Token features may fire on the
            // penultimate spin, so pass totalSpinsKnown (not totalSpinsKnown - 1)
            // to keep the full legal placement window available.
            int capSpin = Math.Min(maxS, totalSpinsKnown);

            // EXTRA_SPIN has no safe "optional, for variety" mode at all — unlike
            // WHEEL/FLUSH (whose own ResolveFeatures gate adds a "needed OR lucky"
            // branch before ever setting Required), every additional spin makes the
            // filler budget WORSE, never better (see CapacityModel's doc comment:
            // "more spins WORSENS feasibility"). ResolveFeatures already encodes
            // that — it sets Required["EXTRA_SPIN"] to EXACTLY the count
            // MinExtraSpins decided is needed, and never adds an optional extra on
            // top. But when extras==0, Required simply has no entry for
            // "EXTRA_SPIN" at all, and without this guard the generic optional-roll
            // loop below would still independently roll up to maxInst (3) more
            // instances at 20% each, completely bypassing that decision. This was a
            // real, confirmed bug: ~23% of tickets received EXTRA_SPIN awards
            // ResolveFeatures never decided were needed, occasionally stacking the
            // full 3 awards on a ticket that needed zero. Skip entirely when req==0.
            if (id == "EXTRA_SPIN" && req == 0) continue;

            if ((id == "WHEEL" || id == "FLUSH" || id == "EXTRA_SPIN") && req > 0)
            {
                int placed = PlaceRequiredFeature(id, req, minS, capSpin, done, used);
                if (placed < req)
                {
                    // A genuine placement failure, not a volume/feasibility one: the
                    // (spin,col) grid this feature's window offers — after whatever
                    // higher-priority features (WHEEL, FLUSH; see FeatReg.Cfg's Ord)
                    // already claimed — has fewer free cells than this REQUIRED count
                    // needs. CapacityModel's feasibility math only ever reasons about
                    // collected-cell VOLUME, not this placement GEOMETRY, so it can't
                    // see this coming — the only trustworthy signal is Placer actually
                    // trying and coming up short. Throwing here (instead of silently
                    // returning a ticket missing required tokens) lets Plan()'s
                    // existing retry loop pick a fresh seed, which assigns different
                    // WHEEL/FLUSH/EXTRA_SPIN spin/column choices and may free up
                    // enough room. This mirrors every other kind of infeasibility in
                    // this pipeline: detected by trying, not predicted in advance.
                    throw new InvalidOperationException(
                        $"Could only place {placed}/{req} required {id} — placement window too small");
                }
                continue;
            }

            if (id == "PRIZE_UPGRADE" && req > 0)
            {
                int placed = PlaceRequiredPrizeUpgrades(req, minS, capSpin, done, used);
                if (placed < req)
                    throw new InvalidOperationException(
                        $"Could only place {placed}/{req} required PRIZE_UPGRADE — placement window too small");
                continue;
            }

            int optionalCount = Enumerable.Range(0, limit)
                .Count(_ => _rng.NextDouble() < prob);
            foreach (int target in EvenlySpreadSpins(minS, capSpin, optionalCount))
            {
                var r = PlaceNearSpin(FeatReg.Get(id), target, minS, capSpin, done, used);
                if (r == null) continue;

                done.Add(r);
                used.Add((r.Spin, r.Col));
                _log.Add($"  placed {id}@S{r.Spin}C{r.Col}{(r.WSym != 0 ? $" sym={r.WSym}" : "")}");
            }
        }
        return done;
    }

    private int PlaceRequiredFeature(string id, int req, int minS, int maxS,
                                     List<PlacedFeat> done, HashSet<(int, int)> used)
    {
        var feat = FeatReg.Get(id);
        int placed = 0;

        // Aim each successive token at an evenly-spaced target spin across the whole
        // [minS, maxS) window, then search outward from that target for the nearest
        // feasible, unused cell. A naive "scan from the start, take the first
        // feasible cell" approach (what this used to do) always greedily fills the
        // earliest spins first, regardless of scan direction — flooding one or two
        // spins with every feature while later spins stay empty. Targeting a spread
        // of preferred spins up front is what actually distributes placements across
        // the full ticket, not just the order cells are visited in.
        foreach (int target in EvenlySpreadSpins(minS, maxS, req))
        {
            if (placed >= req) break;
            var r = PlaceNearSpin(feat, target, minS, maxS, done, used);
            if (r == null) continue;

            done.Add(r);
            used.Add((r.Spin, r.Col));
            placed++;
            _log.Add($"  placed {id}@S{r.Spin}C{r.Col}{(r.WSym != 0 ? $" sym={r.WSym}" : "")}");
        }

        return placed;
    }

    private int PlaceRequiredPrizeUpgrades(int req, int minS, int maxS,
                                           List<PlacedFeat> done, HashSet<(int, int)> used)
    {
        var feat = FeatReg.Get("PRIZE_UPGRADE");
        int placed = 0;

        // Same even-spread + outward-search strategy as PlaceRequiredFeature (see its
        // comment for the rationale). Chronological tier ordering per symbol (tier N
        // must land after tier N-1) is enforced INSIDE PrupFeat.TryPlace itself, by
        // checking each symbol's already-placed tokens — not by the order spins are
        // tried in — so aiming at a spread-out target is safe: TryPlace simply
        // rejects any candidate that would violate that ordering, exactly as before.
        foreach (int target in EvenlySpreadSpins(minS, maxS, req))
        {
            if (placed >= req) break;
            var r = PlaceNearSpin(feat, target, minS, maxS, done, used, maxCol: K.COLS - 1);
            if (r == null) continue;

            done.Add(r);
            used.Add((r.Spin, r.Col));
            placed++;
            _log.Add($"  placed PRIZE_UPGRADE@S{r.Spin}C{r.Col} sym={r.PrupSym} tier={r.PrupTier}");
        }

        return placed;
    }

    /// <summary>
    /// Splits the half-open window [minS, maxS) into `count` equal slices and
    /// returns the (rounded) center spin of each slice, in order — e.g. window
    /// [1,8) with count=3 yields roughly [2, 5, 7]. Every preferred spin is clamped
    /// back into [minS, maxS-1] in case rounding pushes it to an edge. Returns an
    /// empty sequence if count <= 0.
    /// </summary>
    private static IEnumerable<int> EvenlySpreadSpins(int minS, int maxS, int count)
    {
        if (count <= 0) return Array.Empty<int>();
        int window = Math.Max(1, maxS - minS);
        var targets = new int[count];
        for (int i = 0; i < count; i++)
        {
            int t = minS + (int)Math.Round((i + 0.5) * window / count);
            targets[i] = Math.Clamp(t, minS, maxS - 1);
        }
        return targets;
    }

    /// <summary>
    /// Searches outward from `target` (target, target+1, target-1, target+2, ...)
    /// for the nearest spin within [minS, maxS) where `feat` can be placed in some
    /// unused column (0..maxCol-1, scanned left to right at each candidate spin).
    /// Returns null if no feasible cell exists anywhere in the window.
    /// </summary>
    private PlacedFeat? PlaceNearSpin(Feat feat, int target, int minS, int maxS,
                                       List<PlacedFeat> done, HashSet<(int, int)> used,
                                       int maxCol = K.COLS)
    {
        for (int offset = 0; offset <= maxS - minS; offset++)
        {
            foreach (int spin in offset == 0
                         ? new[] { target }
                         : new[] { target - offset, target + offset })
            {
                if (spin < minS || spin >= maxS) continue;

                for (int col = 0; col < maxCol; col++)
                {
                    if (used.Contains((spin, col))) continue;

                    var r = feat.TryPlace(new PlaceCtx
                    {
                        Spin=spin, Col=col, Done=done, Rng=_rng,
                        Input=_inp, MaxSpin=maxS, MinSpin=minS, Used=used,
                    });
                    if (r != null) return r;
                }
            }
        }
        return null;
    }

    private PlacedFeat? TryPlace(string id, int minS, int maxS,
                                  List<PlacedFeat> done, HashSet<(int, int)> used)
    {
        if (minS >= maxS) return null;
        var feat = FeatReg.Get(id);
        for (int a = 0; a < Tries; a++)
        {
            int spin = _rng.Next(minS, maxS);
            int col  = _rng.Next(0, K.COLS);
            var r    = feat.TryPlace(new PlaceCtx
            {
                Spin=spin, Col=col, Done=done, Rng=_rng,
                Input=_inp, MaxSpin=maxS, MinSpin=minS, Used=used,
            });
            if (r != null) return r;
        }
        return null;
    }
}
