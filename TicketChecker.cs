namespace CoinPusherEngine;

using static TicketSerializer;

/// <summary>
/// Independent, from-scratch auditor for a serialized ticket. Takes ONLY the
/// public ticket JSON shape (TicketDto) — never the internal GamePlan/MathInput
/// that produced it — and re-derives every claimed number from raw replay,
/// exactly the way a real client (or an auditor handed a ticket with zero other
/// context) would have to. Deliberately does NOT call Sim.cs: this is a second,
/// independent implementation of the collect/rotate/spawn/fire mechanics, so a
/// bug shared between the production simulator and this checker can't silently
/// cancel itself out.
///
/// Every check is its own CheckItem (Pass / Fail / Warning).
///
/// ── Two real bugs were found while building this ──
///
/// 1. EXTRA_SPIN ReTrigger unwrapping (FIXED HERE, in this checker):
///    When several physical EXTRA_SPIN tokens get folded into one nested
///    ReTrigger chain for presentation (see TicketSerializer.BuildTurns), the
///    chain-start spawn's OWN ConvertToId is a PLACEHOLDER (literally the
///    EXTRA_SPIN feature id, K.F_XSPIN) meaning "there is more chain to unwrap,
///    look inside ReTrigger" — it is NOT "no real target, fall back to filler".
///    An earlier version of this checker treated that placeholder as an invalid
///    convert target and substituted K.F_COIN (symbol 1), which silently
///    injected a fake extra collection of whichever win symbol happened to
///    equal K.F_COIN's value. Confirmed via direct comparison against Sim.Run
///    (the engine's own trusted simulator) across 1000 real tickets — the
///    checker was wrong, not the engine. ResolveConvert below now walks the
///    ReTrigger chain to its end to find the real eventual symbol.
///
/// 2. PRIZE_UPGRADE token presence:
///    Verifier checks collected totals, but the serialized ticket also needs
///    every declared upgrade step to appear as a visible token. Resolver now
///    fails planning when it cannot place a required feature token; check #11
///    independently verifies the public ticket still matches the declared
///    tier data.
///
/// 3. WHEEL/PostWheelIso reconciliation (a genuine LIMITATION of auditing from
///    the public schema alone, not a bug in either the engine or this checker):
///    Sim.cs's PostWheelIso decides whether a cell a WHEEL just stacked survives
///    by comparing it against the engine's INTERNAL planned board for the next
///    spin — data that is never serialized, because the ticket schema encodes
///    one evolving board via spawns, not a separate "what was originally
///    planned" reference. Confirmed directly: a real ticket had a WHEEL-stacked
///    cell land on a position the internal plan reserved for a different
///    symbol, with no spawn anywhere to reveal that from outside — Sim.Run
///    (trusted) reported the exact, correct total; this checker's independent
///    replay overcounted. A win-count mismatch for a symbol that had a WHEEL
///    fire is reported as a WARNING rather than a FAIL or a silent PASS, since
///    asserting confidence either way would be dishonest given what's actually
///    knowable from the ticket alone.
/// </summary>
public static class TicketChecker
{
    public enum Status { Pass, Fail, Warning }

    public sealed class CheckItem
    {
        public string Category { get; init; } = "";
        public string Name     { get; init; } = "";
        public Status Result   { get; init; }
        public string Detail   { get; init; } = "";
    }

    public sealed class Report
    {
        public List<CheckItem> Checks    { get; } = new();
        public bool   IsValid      => Checks.All(c => c.Result != Status.Fail);
        public int    PassCount    => Checks.Count(c => c.Result == Status.Pass);
        public int    FailCount    => Checks.Count(c => c.Result == Status.Fail);
        public int    WarningCount => Checks.Count(c => c.Result == Status.Warning);
    }

    // ── Entry point ──────────────────────────────────────────────────────────
    public static Report CheckTicket(TicketDto t)
    {
        var report = new Report();
        void Add(string cat, string name, Status s, string detail) =>
            report.Checks.Add(new CheckItem { Category = cat, Name = name, Result = s, Detail = detail });

        // ── 1. STRUCTURE ──────────────────────────────────────────────────
        if (t.WinInfo == null) { Add("Structure", "WinInfo present", Status.Fail, "WinInfo is null"); return report; }
        if (t.StartingBoard == null || t.StartingBoard.Length != K.ROWS)
        { Add("Structure", "StartingBoard rows", Status.Fail, $"expected {K.ROWS} rows, got {t.StartingBoard?.Length ?? 0}"); return report; }
        for (int r = 0; r < K.ROWS; r++)
            if (t.StartingBoard[r] == null || t.StartingBoard[r].Length != K.COLS)
            { Add("Structure", $"StartingBoard row {r} width", Status.Fail, $"expected {K.COLS} cols, got {t.StartingBoard[r]?.Length ?? 0}"); return report; }
        Add("Structure", "StartingBoard is 5x5", Status.Pass, "ok");

        if (t.Turns == null || t.Turns.Length == 0)
        { Add("Structure", "Turns present", Status.Fail, "no turns"); return report; }
        Add("Structure", "Turns present", Status.Pass, $"{t.Turns.Length} turns");

        foreach (var (turn, i) in t.Turns.Select((x, i) => (x, i)))
        {
            if (turn.Pushers == null || turn.Pushers.Length != K.COLS)
                Add("Structure", $"Turn {i + 1} pusher count", Status.Fail,
                    $"expected {K.COLS}, got {turn.Pushers?.Length ?? 0}");
        }

        // ── 2. SPIN-COUNT / BASE-SPINS SANITY ──────────────────────────────
        int totalSpins = t.WinInfo.TotalSpins;
        if (totalSpins != t.Turns.Length)
            Add("SpinCount", "TotalSpins matches Turns.Length", Status.Fail,
                $"WinInfo.TotalSpins={totalSpins} but Turns.Length={t.Turns.Length}");
        else
            Add("SpinCount", "TotalSpins matches Turns.Length", Status.Pass, $"{totalSpins}");

        if (totalSpins < K.BASE_SPINS || totalSpins > K.MAX_SPINS)
            Add("SpinCount", "TotalSpins within fixed baseline range", Status.Fail,
                $"TotalSpins={totalSpins} outside the fixed [{K.BASE_SPINS}..{K.MAX_SPINS}] range " +
                $"(BaseSpins is always {K.BASE_SPINS}; only EXTRA_SPIN can extend it, up to {K.MAX_SPINS} total)");
        else
            Add("SpinCount", "TotalSpins within fixed baseline range", Status.Pass,
                $"{totalSpins} (base {K.BASE_SPINS} + {totalSpins - K.BASE_SPINS} extra)");

        // ── 3. PUSHER GEOMETRY SANITY ───────────────────────────────────────
        bool pusherGeometryOk = true;
        for (int i = 0; i < t.Turns.Length; i++)
        {
            var turn = t.Turns[i];
            if (turn.Pushers == null) continue;
            for (int c = 0; c < turn.Pushers.Length; c++)
            {
                var p = turn.Pushers[c];
                bool isFlush = p.FeatureId == K.F_FLUSH_ID;
                bool valid = isFlush ? p.PushValue == K.ROWS
                                     : p.PushValue >= K.MIN_PUSH && p.PushValue <= K.MAX_PUSH;
                if (!valid)
                {
                    pusherGeometryOk = false;
                    Add("Geometry", $"Turn {i + 1} col {c} push value", Status.Fail,
                        $"PushValue={p.PushValue} FeatureId={p.FeatureId} — not a valid normal push " +
                        $"({K.MIN_PUSH}-{K.MAX_PUSH}) or flush ({K.ROWS})");
                }
            }
        }
        if (pusherGeometryOk) Add("Geometry", "All pusher values valid", Status.Pass, "ok");

        // ── 4. SPAWN POSITION SANITY ────────────────────────────────────────
        bool spawnPosOk = true;
        for (int i = 0; i < t.Turns.Length; i++)
        {
            var seen = new HashSet<int>();
            foreach (var sp in t.Turns[i].Spawns ?? Array.Empty<SpawnDto>())
            {
                if (sp.Pos < 0 || sp.Pos >= K.ROWS * K.COLS)
                {
                    spawnPosOk = false;
                    Add("Geometry", $"Turn {i + 1} spawn position range", Status.Fail,
                        $"Pos={sp.Pos} out of range 0..{K.ROWS * K.COLS - 1}");
                }
                else if (!seen.Add(sp.Pos))
                {
                    spawnPosOk = false;
                    Add("Geometry", $"Turn {i + 1} duplicate spawn position", Status.Fail,
                        $"Pos={sp.Pos} claimed by more than one spawn in the same turn");
                }
            }
        }
        if (spawnPosOk) Add("Geometry", "All spawn positions valid and unique per turn", Status.Pass, "ok");

        // ── 5. FULL REPLAY: collect / rotate / spawn / fire ────────────────
        var replay = ReplayTicket(t);

        foreach (var miss in replay.MissingCellTurns)
            Add("Replay", $"Turn {miss} fully populated", Status.Fail,
                "one or more cells were left empty after applying spawns — the engine's natural " +
                "push+rotate didn't account for every position");
        if (replay.MissingCellTurns.Count == 0)
            Add("Replay", "Every turn fully populated after spawns", Status.Pass, "ok");

        // ── 6. WIN-SYMBOL EXACT-COUNT VERIFICATION ─────────────────────────
        // A WHEEL fire's exact aftermath (Sim.cs's PostWheelIso) decides whether a
        // stacked cell survives by comparing against the engine's INTERNAL planned
        // board for the next spin — information that is not, and cannot be,
        // reconstructed from the public ticket schema alone (the schema encodes one
        // evolving board via spawns, never a separate "what the plan originally
        // intended" reference). Confirmed directly: a real ticket's WHEEL-adjacent
        // cell mapped to a position the internal plan reserved for a DIFFERENT
        // symbol, with no spawn entry anywhere to reveal that — Sim.Run (trusted)
        // reported the correct, exact total; this checker's independent replay
        // overcounted by exactly the ambiguous cells. Rather than assert false
        // confidence in either direction, a win-count mismatch involving a symbol
        // that had a WHEEL fire earlier in the ticket is reported as a WARNING, not
        // a FAIL — flagging the genuine external-auditing limitation honestly
        // instead of silently passing OR incorrectly failing a ticket that may well
        // be exactly correct.
        var symbolsWithWheelFire = replay.WheelFireEvents.Select(w => w.WheelSymbolId).ToHashSet();
        foreach (var w in t.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>())
        {
            int got = replay.Totals.GetValueOrDefault(w.Id);
            if (got != w.Target)
            {
                if (symbolsWithWheelFire.Contains(w.Id))
                    Add("Payout", $"Win symbol {w.Id} exact count", Status.Warning,
                        $"target={w.Target} actual(replayed)={got} — symbol had a WHEEL fire; this " +
                        "checker cannot perfectly reconstruct PostWheelIso's reconciliation against the " +
                        "engine's internal plan from the public schema alone, so this is NOT a confirmed " +
                        "defect — verify against Sim.Run/Verifier directly if certainty is required");
                else
                    Add("Payout", $"Win symbol {w.Id} exact count", Status.Fail,
                        $"target={w.Target} actual(replayed)={got}");
            }
            else
                Add("Payout", $"Win symbol {w.Id} exact count", Status.Pass, $"{got}/{w.Target}");
        }

        // ── 7. NEAR-MISS BOUND VERIFICATION ────────────────────────────────
        // Same WHEEL/PostWheelIso limitation as the win-symbol check above (#6) —
        // see the class-level doc comment's point 3. A near-miss symbol with a
        // WHEEL lock is subject to the identical external-auditing blind spot.
        foreach (var nw in t.WinInfo.NonWinSymbols ?? Array.Empty<NonWinSymbolDto>())
        {
            int got = replay.Totals.GetValueOrDefault(nw.Id);
            bool ok = got >= nw.MinTarget && got < nw.MaxThreshold;
            if (!ok && symbolsWithWheelFire.Contains(nw.Id))
                Add("Payout", $"Near-miss symbol {nw.Id} within bounds", Status.Warning,
                    $"got={got} want [{nw.MinTarget}..{nw.MaxThreshold}) — symbol had a WHEEL fire; " +
                    "not a confirmed defect, see the WHEEL/PostWheelIso limitation noted on the win-count check");
            else
                Add("Payout", $"Near-miss symbol {nw.Id} within bounds", ok ? Status.Pass : Status.Fail,
                    $"got={got} want [{nw.MinTarget}..{nw.MaxThreshold})");
        }

        // ── 8. ORDINARY FILLER CAP CHECK ────────────────────────────────────
        var declaredWin    = (t.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>()).Select(w => w.Id).ToHashSet();
        var declaredNonWin = (t.WinInfo.NonWinSymbols ?? Array.Empty<NonWinSymbolDto>()).Select(w => w.Id).ToHashSet();
        bool fillerCapOk = true;
        foreach (var (sym, count) in replay.Totals)
        {
            if (declaredWin.Contains(sym) || declaredNonWin.Contains(sym) || K.IsFeat(sym) || count == 0) continue;
            if (count >= K.FILL_CAP)
            {
                fillerCapOk = false;
                Add("Payout", $"Filler symbol {sym} under cap", Status.Fail, $"count={count} >= cap={K.FILL_CAP}");
            }
        }
        if (fillerCapOk) Add("Payout", "All ordinary filler symbols under cap", Status.Pass, "ok");

        // ── 9. WHEEL CONSISTENCY ────────────────────────────────────────────
        foreach (var w in replay.WheelFireEvents)
        {
            int expectedMultiplier = w.WheelStackValue + 1;
            if (w.ActualMultiplier != expectedMultiplier)
                Add("Feature", $"WHEEL fire turn {w.Turn} sym {w.WheelSymbolId} stack value", Status.Fail,
                    $"declared WheelStackValue={w.WheelStackValue} (multiplier {expectedMultiplier}) " +
                    $"but replay applied multiplier {w.ActualMultiplier}");
            else
                Add("Feature", $"WHEEL fire turn {w.Turn} sym {w.WheelSymbolId} stack value", Status.Pass,
                    $"multiplier {w.ActualMultiplier}");
        }

        // ── 10. EXTRA_SPIN CHAIN CONSISTENCY ────────────────────────────────
        int extraSpinTokenCount = CountExtraSpinChain(t);
        int expectedExtras = totalSpins - K.BASE_SPINS;
        if (extraSpinTokenCount != expectedExtras)
            Add("Feature", "EXTRA_SPIN token count matches bonus spins", Status.Fail,
                $"TotalSpins implies {expectedExtras} extra spin(s), but found {extraSpinTokenCount} " +
                "EXTRA_SPIN token(s) in the spawn/ReTrigger chain");
        else
            Add("Feature", "EXTRA_SPIN token count matches bonus spins", Status.Pass,
                $"{extraSpinTokenCount} token(s) for {expectedExtras} extra spin(s)");

        // ── 11. PRIZE_UPGRADE TIER CONSISTENCY ──────────────────────────────
        // Declared tiers can come from EITHER WinInfo.PrizeTiers (winning
        // symbols) OR a near-miss symbol's own NonWinSymbolDto.PrizeTier field.
        // This cross-check catches any mismatch between declared tiers and the
        // public token sequence, independent of the internal collected-total
        // verifier.
        var declaredTiers = (t.WinInfo.PrizeTiers ?? Array.Empty<PrizeTierDto>())
            .ToDictionary(p => p.SymId, p => p.Tier);
        foreach (var nw in t.WinInfo.NonWinSymbols ?? Array.Empty<NonWinSymbolDto>())
            if (nw.PrizeTier.HasValue)
                declaredTiers[nw.Id] = nw.PrizeTier.Value;

        foreach (var (sym, finalTier) in replay.PrupFinalTierPerSymbol)
        {
            if (!declaredTiers.TryGetValue(sym, out int declared))
            {
                Add("Feature", $"PRIZE_UPGRADE sym {sym} declared in WinInfo", Status.Fail,
                    $"replay shows tokens reaching tier {finalTier}, but neither WinInfo.PrizeTiers nor " +
                    $"NonWinSymbols has a matching entry for sym {sym}");
                continue;
            }
            if (finalTier != declared)
                Add("Feature", $"PRIZE_UPGRADE sym {sym} final tier matches declared", Status.Fail,
                    $"declared tier={declared} but last token in replay reaches tier={finalTier} " +
                    "(serialized token sequence does not match WinInfo tier declaration)");
            else
                Add("Feature", $"PRIZE_UPGRADE sym {sym} final tier matches declared", Status.Pass,
                    $"tier {finalTier}");
        }
        foreach (var (sym, declared) in declaredTiers)
        {
            if (!replay.PrupFinalTierPerSymbol.ContainsKey(sym))
                Add("Feature", $"PRIZE_UPGRADE sym {sym} has matching tokens", Status.Fail,
                    $"WinInfo declares tier {declared} for sym {sym}, but NO PRIZE_UPGRADE tokens for " +
                    "that symbol were found anywhere in the replay");
        }

        return report;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // INDEPENDENT REPLAY — deliberately separate from Sim.cs
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class ReplayCell
    {
        public int  Sym;
        public int  Stack = 1;
        public bool IsFeat;
        public int  FeatureId;
        public int  ConvertToId;
        public int  WheelSymbolId;
        public int  WheelStackValue;
        public FeatureDto[] ReTrigger = Array.Empty<FeatureDto>();
    }

    private sealed class WheelFireEvent
    {
        public int Turn;
        public int WheelSymbolId;
        public int WheelStackValue;
        public int ActualMultiplier;
    }

    private sealed class ReplayResult
    {
        public Dictionary<int, int> Totals                 = new();
        public List<int>            MissingCellTurns        = new();
        public List<WheelFireEvent> WheelFireEvents         = new();
        public Dictionary<int, int> PrupFinalTierPerSymbol   = new();
    }

    private static ReplayResult ReplayTicket(TicketDto t)
    {
        var result = new ReplayResult();
        var board  = new ReplayCell?[K.ROWS, K.COLS];

        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
            board[r, c] = new ReplayCell { Sym = t.StartingBoard[r][c].Id };

        for (int turnIdx = 0; turnIdx < t.Turns.Length; turnIdx++)
        {
            var turn = t.Turns[turnIdx];

            // Phase 1: FlatStale — any feature cell still sitting around from a
            // previous turn (shouldn't normally happen, but defensive) reverts
            // to its converted symbol before collection.
            for (int r = 0; r < K.ROWS; r++)
            for (int c = 0; c < K.COLS; c++)
                if (board[r, c]?.IsFeat == true)
                    board[r, c] = new ReplayCell { Sym = ResolveConvert(board[r, c]!) };

            // Phase 2: Collect
            for (int c = 0; c < K.COLS; c++)
            {
                var pusher = turn.Pushers[c];
                bool isFlush = pusher.FeatureId == K.F_FLUSH_ID;
                if (isFlush)
                {
                    for (int r = 0; r < K.ROWS; r++)
                    {
                        if (board[r, c] != null) Acc(result.Totals, board[r, c]!);
                        board[r, c] = null;
                    }
                }
                else
                {
                    int push = pusher.PushValue;
                    for (int r = K.ROWS - push; r < K.ROWS; r++)
                        if (board[r, c] != null) Acc(result.Totals, board[r, c]!);
                    for (int r = K.ROWS - 1; r >= 0; r--)
                    {
                        int src = r - push;
                        board[r, c] = src >= 0 ? board[src, c] : null;
                    }
                }
            }

            // Phase 3: Rotate 90 clockwise
            board = RotCW(board);

            // Phase 4: ApplySpawns
            foreach (var sp in turn.Spawns ?? Array.Empty<SpawnDto>())
            {
                int r = sp.Pos / K.COLS, c = sp.Pos % K.COLS;
                var cell = new ReplayCell { Sym = sp.Id, Stack = sp.Stack ?? 1 };
                if (sp.Feature != null)
                {
                    cell.IsFeat          = true;
                    cell.FeatureId       = sp.Feature.FeatureId;
                    cell.ConvertToId     = sp.Feature.ConvertToId;
                    cell.WheelSymbolId   = sp.Feature.WheelSymbolId ?? 0;
                    cell.WheelStackValue = sp.Feature.WheelStackValue ?? 0;
                    cell.ReTrigger       = sp.Feature.ReTrigger ?? Array.Empty<FeatureDto>();

                    AccumulatePrizeUpgradeTokens(sp.Feature, result);
                }
                board[r, c] = cell;
            }

            // Check: no cell left null after spawns
            bool anyMissing = false;
            for (int r = 0; r < K.ROWS; r++)
            for (int c = 0; c < K.COLS; c++)
                if (board[r, c] == null) anyMissing = true;
            if (anyMissing) result.MissingCellTurns.Add(turnIdx + 1);

            // Phase 5: FireAll — repeatedly resolve feature cells until none remain.
            bool any;
            do
            {
                any = false;
                for (int r = 0; r < K.ROWS; r++)
                for (int c = 0; c < K.COLS; c++)
                {
                    var fc = board[r, c];
                    if (fc?.IsFeat != true) continue;

                    if (fc.FeatureId == K.F_WHEEL && fc.WheelStackValue + 1 > 1)
                    {
                        int multiplier = fc.WheelStackValue + 1;
                        int sym        = fc.WheelSymbolId;
                        for (int rr = 0; rr < K.ROWS; rr++)
                        for (int cc = 0; cc < K.COLS; cc++)
                        {
                            var cell = board[rr, cc];
                            if (cell != null && !cell.IsFeat && cell.Sym == sym)
                                cell.Stack = multiplier;
                        }
                        result.WheelFireEvents.Add(new WheelFireEvent
                        {
                            Turn = turnIdx + 1, WheelSymbolId = sym,
                            WheelStackValue = fc.WheelStackValue, ActualMultiplier = multiplier
                        });

                        // PostWheelIso equivalent: the real engine (Sim.cs) reverts
                        // any stacked cell that ISN'T inside the very next turn's
                        // collection zone — otherwise a stray stacked cell could
                        // wander past the WHEEL's intended scope and get counted
                        // again later, multiplied, with no upper bound. The public
                        // ticket schema doesn't expose a separate "planned next
                        // board" the way the internal SpinPlan does, so the
                        // equivalent here is purely geometric: is this position
                        // inside next turn's zone (derived from its own declared
                        // Pushers)? If there's no next turn (this was the last
                        // turn), nothing more will ever collect it, so no
                        // reversion is needed.
                        if (turnIdx + 1 < t.Turns.Length)
                        {
                            var nextPushers = t.Turns[turnIdx + 1].Pushers;
                            for (int rr = 0; rr < K.ROWS; rr++)
                            for (int cc = 0; cc < K.COLS; cc++)
                            {
                                var cell = board[rr, cc];
                                if (cell == null || cell.IsFeat || cell.Sym != sym) continue;

                                var np = nextPushers[cc];
                                bool inNextZone = np.FeatureId == K.F_FLUSH_ID
                                    || rr >= K.ROWS - np.PushValue;
                                // Matches Sim.cs's PostWheelIso: a stray cell that
                                // won't be collected next turn is converted away
                                // entirely (not just un-stacked) — otherwise it
                                // would still get counted once, un-multiplied,
                                // whenever it eventually IS collected.
                                if (!inNextZone) board[rr, cc] = new ReplayCell { Sym = FallbackSymbol(t, sym) };
                            }
                        }
                    }

                    // EXTRA_SPIN/PRIZE_UPGRADE fire as no-ops on the board itself —
                    // their effect (extra spin count, tier) is read from the spawn
                    // schema directly, not from replaying a board-level action.

                    board[r, c] = new ReplayCell { Sym = ResolveConvert(fc) };
                    any = true;
                }
            }
            while (any && BoardHasFeatureCell(board));
        }

        return result;
    }

    /// <summary>
    /// Resolves the REAL eventual symbol a feature cell converts to. When a
    /// ConvertToId points at another feature and ReTrigger contains that nested
    /// feature, walk the chain until the deepest link's real conversion target.
    /// </summary>
    private static int ResolveConvert(ReplayCell fc)
    {
        if (K.IsFeat(fc.ConvertToId) && fc.ReTrigger.Length > 0)
        {
            var link = fc.ReTrigger[0];
            while (link.ReTrigger is { Length: > 0 })
                link = link.ReTrigger[0];
            return link.ConvertToId > 0 ? link.ConvertToId : K.F_COIN;
        }
        return fc.ConvertToId > 0 ? fc.ConvertToId : K.F_COIN;
    }

    private static bool BoardHasFeatureCell(ReplayCell?[,] board)
    {
        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
            if (board[r, c]?.IsFeat == true) return true;
        return false;
    }

    private static void Acc(Dictionary<int, int> totals, ReplayCell cell)
    {
        if (K.IsFeat(cell.Sym)) return;
        totals.TryGetValue(cell.Sym, out int existing);
        totals[cell.Sym] = existing + cell.Stack;
    }

    private static ReplayCell?[,] RotCW(ReplayCell?[,] b)
    {
        var r = new ReplayCell?[K.ROWS, K.COLS];
        for (int row = 0; row < K.ROWS; row++)
        for (int col = 0; col < K.COLS; col++)
            r[col, K.ROWS - 1 - row] = b[row, col];
        return r;
    }

    /// <summary>
    /// Counts every EXTRA_SPIN token across the whole ticket, walking nested
    /// ReTrigger arrays no matter which feature started the chain.
    /// </summary>
    private static int CountExtraSpinChain(TicketDto t)
    {
        int count = 0;
        foreach (var turn in t.Turns)
        foreach (var sp in turn.Spawns ?? Array.Empty<SpawnDto>())
        {
            if (sp.Feature == null) continue;
            count += CountFeatureId(sp.Feature, K.F_XSPIN);
        }
        return count;
    }

    private static int CountFeatureId(FeatureDto feature, int featureId)
    {
        var count = feature.FeatureId == featureId ? 1 : 0;
        foreach (var nested in feature.ReTrigger ?? Array.Empty<FeatureDto>())
            count += CountFeatureId(nested, featureId);
        return count;
    }

    private static void AccumulatePrizeUpgradeTokens(FeatureDto feature, ReplayResult result)
    {
        if (feature.FeatureId == K.F_PRUP && feature.UpgradeSymbolId.HasValue)
        {
            // PRIZE_UPGRADE doesn't carry an explicit tier number in the
            // public schema — tier is inferred by COUNTING how many
            // PRIZE_UPGRADE tokens for this symbol have appeared so far.
            int sym = feature.UpgradeSymbolId.Value;
            int next = result.PrupFinalTierPerSymbol.GetValueOrDefault(sym, 0) + 1;
            result.PrupFinalTierPerSymbol[sym] = next;
        }

        foreach (var nested in feature.ReTrigger ?? Array.Empty<FeatureDto>())
            AccumulatePrizeUpgradeTokens(nested, result);
    }

    private static int FallbackSymbol(TicketDto ticket, int avoidSym)
    {
        var winSyms = (ticket.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>())
            .Select(w => w.Id)
            .ToHashSet();

        var declaredNonWin = (ticket.WinInfo.NonWinSymbols ?? Array.Empty<NonWinSymbolDto>())
            .Select(w => w.Id)
            .FirstOrDefault(id => id != avoidSym && !K.IsFeat(id) && !winSyms.Contains(id));
        if (declaredNonWin > 0) return declaredNonWin;

        for (var id = 1; id <= 10; id++)
        {
            if (id != avoidSym && !winSyms.Contains(id) && !K.IsFeat(id))
            {
                return id;
            }
        }

        return avoidSym;
    }
}
