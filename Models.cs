namespace CoinPusherEngine;

// ── Public input ──────────────────────────────────────────────────────────────

public sealed class MathInput
{
    public required IReadOnlyDictionary<int, int> Targets   { get; init; }
    public required int                           BaseSpins { get; init; }
    public IReadOnlyDictionary<string, int>       Required  { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<int>?                    WheelSymOrder { get; init; }
    public IReadOnlyDictionary<int, int>?         PrizeTiers    { get; init; }
    public IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>>? PrizeValues { get; init; }

    /// <summary>
    /// The highest valid SYMBOL id in this game's config (e.g. 6 for a 6-symbol game).
    /// Filler symbols are drawn from [1, MaxSym], excluding whatever's already in
    /// Targets — the engine never invents symbol ids beyond this boundary. Defaults
    /// to 6 to match the standard six-symbol ladder config; override explicitly for
    /// games with a different symbol count.
    /// </summary>
    public int MaxSym { get; init; } = 6;
}

internal sealed class Cell
{
    internal int     Sym    { get; set; }
    internal int     Stack  { get; set; } = 1;
    internal bool    IsFeat { get; set; }
    internal string? FeatId { get; set; }
    internal int     CvtSym { get; set; }
    internal FP?     Fp     { get; set; }

    internal Cell Clone() { var c = (Cell)MemberwiseClone(); c.Fp = Fp?.Clone(); return c; }
}

internal sealed class FP
{
    internal string FeatId     { get; init; } = "";
    internal int    WheelSym   { get; set; }
    internal int    WheelStack { get; set; }
    internal int    PrupSym    { get; set; }
    internal int    PrupTier   { get; set; }
    internal FP Clone() => (FP)MemberwiseClone();
}

internal sealed class WLock
{
    internal int Sym      { get; init; }
    internal int FireSpin { get; init; }
    internal int Stack    { get; init; }
    internal int Zone     { get; set; }
    internal int Pre      { get; set; }
    internal int Post     { get; set; }
}

internal sealed class PlacedFeat
{
    internal string Id       { get; init; } = "";
    internal int    Spin     { get; init; }
    internal int    Col      { get; init; }
    internal int    WSym     { get; init; }
    internal int    WN       { get; init; }
    internal int    PrupSym  { get; init; }
    internal int    PrupTier { get; init; }
}

internal sealed class PlaceCtx
{
    internal int                       Spin    { get; init; }
    internal int                       Col     { get; init; }
    internal IReadOnlyList<PlacedFeat> Done    { get; init; } = Array.Empty<PlacedFeat>();
    internal Random                    Rng     { get; init; } = new();
    internal MathInput                 Input   { get; init; } = null!;
    internal int                       MaxSpin { get; init; }
    internal int                       MinSpin { get; init; }
    internal HashSet<(int, int)>       Used    { get; init; } = new();
}

internal sealed class FireCtx
{
    internal Cell?[,] Board { get; init; } = new Cell?[K.ROWS, K.COLS];
    internal int      Col   { get; init; }
    internal FP       Fp    { get; init; } = new();
}

internal sealed class SpinPlan
{
    internal int                               Spin    { get; init; }
    internal bool                              IsExtra { get; init; }
    internal Cell?[,]                          Board   { get; init; } = new Cell?[K.ROWS, K.COLS];
    internal int[]                             Push    { get; init; } = new int[K.COLS];
    internal bool[]                            Flush   { get; init; } = new bool[K.COLS];
    internal Dictionary<(int, int), Cell>      Spawns  { get; init; } = new();
    internal List<(string Id, int Col, FP Fp)> Tokens  { get; init; } = new();
    internal IReadOnlyDictionary<int, int>     Alloc   { get; init; } = new Dictionary<int, int>();
}

public sealed class GamePlan
{
    public int                           TotalSpins { get; init; }
    public IReadOnlyDictionary<int, int> Targets    { get; init; } = new Dictionary<int, int>();
    public IReadOnlyList<int>            WinSyms    { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int>            FillSyms   { get; init; } = Array.Empty<int>();
    public IReadOnlyDictionary<int, int> PrizeTiers { get; init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> PrizeValues { get; init; } =
        new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
    public bool                          Verified   { get; set; }
    public List<string>                  Log        { get; init; } = new();
    internal List<SpinPlan>              Spins      { get; init; } = new();
}

public sealed class GameResult
{
    public IReadOnlyDictionary<int, int>  Collected  { get; init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, bool> SymbolsHit { get; init; } = new Dictionary<int, bool>();
    public bool                           Win        { get; init; }
}

/// <summary>
/// Shared filler-symbol usage tracker. One instance per ticket, passed to both Builder
/// and Resolver so they draw from the SAME least-used-symbol pool with a SINGLE
/// continuous round-robin cursor. Two independent cursors (even sharing the same usage
/// dict) can each restart at index 0 and pick the same tied symbol, throwing off the
/// otherwise-guaranteed even split when total fillers placed exactly equals total cap
/// capacity (fillSymCount * FILL_CAP).
/// </summary>
internal sealed class FillerTracker
{
    internal readonly Dictionary<int, int> Used;
    private int _cursor;

    internal FillerTracker(int[] fillSyms) => Used = fillSyms.ToDictionary(f => f, _ => 0);

    internal int Next()
    {
        int minUsed = Used.Values.Min();
        var candidates = Used.Where(kv => kv.Value == minUsed).Select(kv => kv.Key).ToList();
        int sym = candidates[_cursor % candidates.Count];
        _cursor++;
        Used[sym]++;
        return sym;
    }
}

// ── Shared filler round-robin tracker ──────────────────────────────────────────
/// <summary>
/// Ensures filler symbols are distributed evenly across the whole ticket (Builder + Resolver
/// both draw from this), preventing any single filler symbol from exceeding FILL_CAP.
/// Deterministic round-robin avoids the near-miss clustering that pure random choice causes
/// near the cap boundary.
/// </summary>
internal sealed class FillTracker
{
    private readonly int[] _fills;
    private readonly Dictionary<int, int> _used = new();
    private int _cursor;

    internal FillTracker(int[] fills)
    {
        _fills = fills;
        foreach (var f in fills) _used[f] = 0;
    }

    /// <summary>Pick the least-used filler symbol; round-robin tie-break for determinism.</summary>
    internal int Next()
    {
        int best = -1, bestCount = int.MaxValue;
        for (int i = 0; i < _fills.Length; i++)
        {
            int idx = (_cursor + i) % _fills.Length;
            int sym = _fills[idx];
            int cnt = _used[sym];
            if (cnt < bestCount) { bestCount = cnt; best = sym; }
        }
        _used[best]++;
        _cursor = (_cursor + 1) % _fills.Length;
        return best;
    }
}
