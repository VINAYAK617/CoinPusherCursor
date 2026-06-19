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
            int capSpin = Math.Min(maxS, totalSpinsKnown - 1);

            if ((id == "FLUSH" || id == "EXTRA_SPIN") && req > 0)
            {
                int placed = PlaceRequiredFeature(id, req, minS, capSpin, done, used);
                if (placed < req)
                    _log.Add($"  WARN: could only place {placed}/{req} required {id}");
                continue;
            }

            if (id == "PRIZE_UPGRADE" && req > 0)
            {
                int placed = PlaceRequiredPrizeUpgrades(req, minS, capSpin, done, used);
                if (placed < req)
                    _log.Add($"  WARN: could only place {placed}/{req} required PRIZE_UPGRADE");
                continue;
            }

            for (int i = 0; i < limit; i++)
            {
                bool must = i < req;
                if (!must && _rng.NextDouble() >= prob) continue;

                var r = TryPlace(id, minS, capSpin, done, used);
                if (r != null)
                {
                    done.Add(r);
                    used.Add((r.Spin, r.Col));
                    _log.Add($"  placed {id}@S{r.Spin}C{r.Col}{(r.WSym != 0 ? $" sym={r.WSym}" : "")}");
                }
                else if (must)
                    _log.Add($"  WARN: could not place required {id}");
            }
        }
        return done;
    }

    private int PlaceRequiredFeature(string id, int req, int minS, int maxS,
                                     List<PlacedFeat> done, HashSet<(int, int)> used)
    {
        var feat = FeatReg.Get(id);
        int placed = 0;

        for (int spin = minS; spin < maxS && placed < req; spin++)
        for (int col = 0; col < K.COLS && placed < req; col++)
        {
            if (used.Contains((spin, col))) continue;

            var r = feat.TryPlace(new PlaceCtx
            {
                Spin=spin, Col=col, Done=done, Rng=_rng,
                Input=_inp, MaxSpin=maxS, MinSpin=minS, Used=used,
            });
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

        // PRIZE_UPGRADE tiers must be chronological per symbol. Scan spins in order
        // and columns left-to-right so tier 1 is guaranteed to land before tier 2,
        // instead of relying on random retries to discover a valid ordering.
        for (int spin = minS; spin < maxS && placed < req; spin++)
        for (int col = 0; col < K.COLS - 1 && placed < req; col++)
        {
            if (used.Contains((spin, col))) continue;

            var r = feat.TryPlace(new PlaceCtx
            {
                Spin=spin, Col=col, Done=done, Rng=_rng,
                Input=_inp, MaxSpin=maxS, MinSpin=minS, Used=used,
            });
            if (r == null) continue;

            done.Add(r);
            used.Add((r.Spin, r.Col));
            placed++;
            _log.Add($"  placed PRIZE_UPGRADE@S{r.Spin}C{r.Col} sym={r.PrupSym} tier={r.PrupTier}");
        }

        return placed;
    }

    private PlacedFeat? TryPlace(string id, int minS, int maxS,
                                  List<PlacedFeat> done, HashSet<(int, int)> used)
    {
        var feat = FeatReg.Get(id);
        for (int a = 0; a < Tries; a++)
        {
            int spin = _rng.Next(minS, maxS + 1);
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
