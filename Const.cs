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
    internal const int FILL_CAP   = 19;   // filler over-collection guard

    // Every ticket's baseline spin count is fixed at 5 — this is never computed or
    // clamped by capacity math. The only way to exceed it is to plan for and award
    // the EXTRA_SPIN feature, which can add up to 3 more spins, for a hard ceiling
    // of 8 total spins per ticket.
    internal const int BASE_SPINS = 5;
    internal const int MAX_SPINS  = 8;

    // Feature symbol IDs
    internal const int F_WHEEL    = 11;
    internal const int F_XSPIN    = 12;
    internal const int F_FLUSH_ID = 14;   // pusher flag only — no board token
    internal const int F_PRUP     = 13;
    internal const int F_COIN     = 1;    // fallback filler when CvtSym is invalid — symbol 1 is always valid

    internal static bool IsFeat(int id) => id is F_WHEEL or F_XSPIN or F_PRUP;
}
