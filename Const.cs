namespace CoinPusherEngine;

/// <summary>
/// All hard constants. Win symbol IDs 1–10 are caller-supplied via MathInput.Targets.
/// Feature symbol IDs 11–14 are reserved and never win targets or fillers.
/// </summary>
internal static class K
{
    internal const int ROWS       = 5;
    internal const int COLS       = 5;
    internal const int MIN_PUSH   = 1;    // minimum rows collected per column per spin
    internal const int MAX_PUSH   = 3;    // maximum rows collected per column per spin
    internal const int FILL_CAP   = 20;   // filler over-collection guard: 20+ is invalid

    // Every ticket's baseline spin count is fixed at 5 — this is never computed or
    // clamped by capacity math. The only way to exceed it is to plan for and award
    // the EXTRA_SPIN feature, which can add up to 3 more spins, for a hard ceiling
    // of 8 total spins per ticket.
    internal const int BASE_SPINS = 5;
    internal const int MAX_SPINS  = 8;

    // ── Optional-feature probability ──────────────────────────────────────
    //
    // These ONLY govern OPTIONAL/cosmetic feature placement on tickets that are
    // already feasible without the feature — never a "should this be included"
    // override on a ticket where the feature is actually NEEDED. Compulsory
    // triggers always come first and are never subject to these percentages:
    //   - CapacityModel.MinExtraSpins(...) < 0 (infeasible without it) forces
    //     WHEEL/FLUSH on regardless of these constants.
    //   - PRIZE_UPGRADE token COUNTS on winning symbols are fixed by the prize
    //     ladder's tier structure (LadderCombinator) — there's no "30% chance of
    //     having upgrades" for win symbols; the math team's prize table decides
    //     that, not a probability roll. These constants never touch that.
    //   - EXTRA_SPIN has no optional/cosmetic lane at all, by design: adding more
    //     spins makes the filler budget HARDER to satisfy, not easier (every extra
    //     spin contributes baseline filler volume even at minimum push) — so it is
    //     only ever added when CapacityModel proves it's required, never "for fun."
    //     There is deliberately no P_EXTRA_SPIN_OPTIONAL constant.
    //
    // What these DO control: once a ticket is already solvable on its own, how
    // often the engine still adds the feature anyway, purely for visual variety.

    /// <summary>Per eligible WIN symbol, chance WHEEL is added even though the
    /// ticket doesn't need it for feasibility. Win symbols with target &lt; 10 are
    /// never offered this — too small to benefit meaningfully from compression.</summary>
    internal const double P_WHEEL_OPTIONAL = 0.65;

    /// <summary>Per optional FLUSH slot (up to COLS-1 per ticket), chance it's
    /// added even though the ticket doesn't need it for feasibility.</summary>
    internal const double P_FLUSH_OPTIONAL = 0.35;

    /// <summary>Given at least one near-miss (non-winning) filler symbol with
    /// target &gt;= 2 is eligible, chance one of them additionally gets a WHEEL —
    /// purely cosmetic, the near-miss symbol's count is still governed by
    /// Verifier's non-winning cap either way.</summary>
    internal const double P_NONWIN_WHEEL = 1.0;

    /// <summary>Given at least one near-miss filler symbol is eligible, chance one
    /// of them gets a single visual PRIZE_UPGRADE tier — purely cosmetic, never
    /// turns the symbol into an actual payout target.</summary>
    internal const double P_NONWIN_PRIZE_UPGRADE = 1.0;

    /// <summary>Minimum near-miss collection target for a non-winning filler
    /// symbol. Raised from the original 1-3 range so the near-miss EXPERIENCE
    /// is actually visible to the player, not a token amount that barely
    /// registers. Capped well under FILL_CAP (20) by the call sites that use
    /// this, leaving the same one-cell safety margin used everywhere else.</summary>
    internal const int NONWIN_MIN_TARGET = 15;

    // Feature symbol IDs
    internal const int F_WHEEL    = 11;
    internal const int F_XSPIN    = 12;
    internal const int F_FLUSH_ID = 14;   // pusher flag only — no board token
    internal const int F_PRUP     = 13;
    internal const int F_COIN     = 1;    // fallback filler when CvtSym is invalid — symbol 1 is always valid

    internal static bool IsFeat(int id) => id is F_WHEEL or F_XSPIN or F_PRUP;
}
