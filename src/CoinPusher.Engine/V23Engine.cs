namespace CoinPusher.Engine;

public static class CoinPusherV23Constants
{
    public const int Rows = 5;
    public const int Columns = 5;
    public const int MinPush = 1;
    public const int MaxPush = 3;
    public const int FlushPush = Rows;
    public const int WheelStackMultiplier = 4;
    public const int WheelFeatureId = 11;
    public const int ExtraSpinFeatureId = 12;
    public const int FlushFeatureId = 13;
    public const int PrizeUpgradeFeatureId = 14;

    public const string Wheel = "WHEEL";
    public const string ExtraSpin = "EXTRA_SPIN";
    public const string Flush = "FLUSH";
    public const string PrizeUpgrade = "PRIZE_UPGRADE";
}

public sealed record SymbolDefinition(int Id, string Name, string Glyph, int? MaxCollections = null);

public static class SymbolTable
{
    private static readonly IReadOnlyList<SymbolDefinition> Symbols =
    [
        new(1, "DIAMOND", "A"),
        new(2, "GOLD", "B"),
        new(3, "RUBY", "C"),
        new(4, "SEVEN", "D", 25),
        new(5, "CROWN", "E", 20),
        new(6, "STAR", "F", 20),
        new(7, "BELL", "G", 20),
        new(8, "CHERRY", "H", 20),
        new(9, "CLOVER", "I", 20),
        new(10, "COIN", "J", 20),
        new(CoinPusherV23Constants.WheelFeatureId, "WHEEL_SYM", "-"),
        new(CoinPusherV23Constants.ExtraSpinFeatureId, "XSPIN_SYM", "-"),
        new(CoinPusherV23Constants.FlushFeatureId, "FLUSH", "-"),
        new(CoinPusherV23Constants.PrizeUpgradeFeatureId, "PRUP_SYM", "-")
    ];

    private static readonly IReadOnlyDictionary<int, SymbolDefinition> ById =
        Symbols.ToDictionary(symbol => symbol.Id);

    private static readonly IReadOnlyDictionary<string, SymbolDefinition> ByName =
        Symbols.ToDictionary(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<int> NormalIds { get; } = Enumerable.Range(1, 10).ToArray();

    public static IReadOnlyList<int> FeatureTokenIds { get; } =
    [
        CoinPusherV23Constants.WheelFeatureId,
        CoinPusherV23Constants.ExtraSpinFeatureId,
        CoinPusherV23Constants.PrizeUpgradeFeatureId
    ];

    public static SymbolDefinition Get(int id) =>
        ById.TryGetValue(id, out var symbol)
            ? symbol
            : throw new KeyNotFoundException($"Unknown symbol id {id}.");

    public static int IdFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Symbol name is required.", nameof(name));
        }

        var normalized = name.Trim();
        return ByName.TryGetValue(normalized, out var symbol)
            ? symbol.Id
            : throw new KeyNotFoundException($"Unknown symbol '{normalized}'.");
    }

    public static string NameFor(int id) => Get(id).Name;

    public static bool IsNormal(int id) => id is >= 1 and <= 10;

    public static bool IsFeatureToken(int id) => FeatureTokenIds.Contains(id);
}

public sealed record MathInput(
    IReadOnlyDictionary<string, int> TargetCollection,
    int TotalBaseSpins,
    IReadOnlyDictionary<string, int>? RequiredFeatures = null,
    IReadOnlyList<string>? WheelSymbolOrder = null,
    IReadOnlyDictionary<string, string>? PrizeUpgradeMap = null,
    int? RngSeed = null);

public sealed record FeaturePlacementConfig(
    string FeatureId,
    double Probability,
    int MaxInstances,
    int MinSpin,
    int MaxSpin,
    int PlacementOrder);

public sealed record FeatureRegistry(
    IReadOnlyDictionary<string, FeaturePlacementConfig> Features,
    double WheelMultiSymbolProbability,
    double ChainProbability)
{
    public static FeatureRegistry Default(int totalBaseSpins) =>
        new(
            new Dictionary<string, FeaturePlacementConfig>(StringComparer.Ordinal)
            {
                [CoinPusherV23Constants.Wheel] = new(
                    CoinPusherV23Constants.Wheel,
                    0.40,
                    4,
                    2,
                    Math.Max(2, totalBaseSpins - 1),
                    1),
                [CoinPusherV23Constants.Flush] = new(
                    CoinPusherV23Constants.Flush,
                    0.30,
                    5,
                    1,
                    Math.Max(1, totalBaseSpins - 1),
                    2),
                [CoinPusherV23Constants.ExtraSpin] = new(
                    CoinPusherV23Constants.ExtraSpin,
                    0.20,
                    2,
                    1,
                    Math.Max(1, totalBaseSpins - 1),
                    3),
                [CoinPusherV23Constants.PrizeUpgrade] = new(
                    CoinPusherV23Constants.PrizeUpgrade,
                    0.15,
                    1,
                    1,
                    Math.Max(1, totalBaseSpins - 1),
                    4)
            },
            0.15,
            0.03);
}

public sealed record FeatureParams(
    int? WheelSymId = null,
    int? WheelStackMultiplier = null,
    int? FromSymId = null,
    int? ToSymId = null,
    int? GrantedTurnIndex = null,
    int Depth = 0);

public sealed record CellState
{
    public CellState(
        int symId,
        int stackCount = 1,
        bool isFeature = false,
        string? featureId = null,
        int? convertSymId = null,
        FeatureParams? fParams = null)
    {
        if (symId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(symId), symId, "Symbol id must be positive.");
        }

        if (stackCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stackCount), stackCount, "Stack count must be positive.");
        }

        if (isFeature && string.IsNullOrWhiteSpace(featureId))
        {
            throw new ArgumentException("Feature cells require a feature id.", nameof(featureId));
        }

        if (isFeature && convertSymId is null)
        {
            throw new ArgumentException("Feature cells require a convert symbol id.", nameof(convertSymId));
        }

        if (!isFeature && featureId is not null)
        {
            throw new ArgumentException("Normal cells cannot carry a feature id.", nameof(featureId));
        }

        SymId = symId;
        StackCount = stackCount;
        IsFeature = isFeature;
        FeatureId = featureId;
        ConvertSymId = convertSymId;
        FParams = fParams;
    }

    public int SymId { get; }

    public int StackCount { get; }

    public bool IsFeature { get; }

    public string? FeatureId { get; }

    public int? ConvertSymId { get; }

    public FeatureParams? FParams { get; }

    public static CellState Normal(int symId, int stackCount = 1) => new(symId, stackCount);

    public static CellState Feature(int symId, string featureId, int convertSymId, FeatureParams? fParams = null) =>
        new(symId, 1, true, featureId, convertSymId, fParams);

    public CellState ConvertToNormal() =>
        IsFeature
            ? Normal(ConvertSymId!.Value)
            : this;

    public CellState WithStack(int stackCount) => new(SymId, stackCount, IsFeature, FeatureId, ConvertSymId, FParams);
}

public sealed record PusherPlan(int PushValue, string? FeatureId = null)
{
    public bool IsFeature => FeatureId is not null;

    public static PusherPlan Normal(int pushValue) => new(pushValue);

    public static PusherPlan Flush() => new(CoinPusherV23Constants.FlushPush, CoinPusherV23Constants.Flush);
}

public sealed record FeatureSpawn(string FeatureId, int TurnIndex, int Column, int? Row = null);

public sealed record CoinPusherSpinPlan
{
    public CoinPusherSpinPlan(
        int turnIndex,
        CellState?[,] boardAtStart,
        IReadOnlyList<PusherPlan> pushers,
        IReadOnlyDictionary<BoardPosition, CellState> plannedSpawns,
        IReadOnlyList<FeatureSpawn>? featureSpawns = null,
        bool isExtraSpin = false,
        int? parentTurnIndex = null)
    {
        ValidateBoardShape(boardAtStart);
        if (pushers.Count != CoinPusherV23Constants.Columns)
        {
            throw new ArgumentException("Each spin must include one pusher per column.", nameof(pushers));
        }

        TurnIndex = turnIndex;
        BoardAtStart = CloneBoard(boardAtStart);
        Pushers = pushers.ToArray();
        PlannedSpawns = plannedSpawns.ToDictionary(entry => entry.Key, entry => entry.Value);
        FeatureSpawns = (featureSpawns ?? Array.Empty<FeatureSpawn>()).ToArray();
        IsExtraSpin = isExtraSpin;
        ParentTurnIndex = parentTurnIndex;
    }

    public int TurnIndex { get; }

    public CellState?[,] BoardAtStart { get; }

    public IReadOnlyList<PusherPlan> Pushers { get; }

    public IReadOnlyDictionary<BoardPosition, CellState> PlannedSpawns { get; }

    public IReadOnlyList<FeatureSpawn> FeatureSpawns { get; }

    public bool IsExtraSpin { get; }

    public int? ParentTurnIndex { get; }

    internal static CellState?[,] CloneBoard(CellState?[,] board)
    {
        ValidateBoardShape(board);
        var clone = new CellState?[CoinPusherV23Constants.Rows, CoinPusherV23Constants.Columns];
        for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
        {
            for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
            {
                clone[row, column] = board[row, column];
            }
        }

        return clone;
    }

    internal static CellState?[,] EmptyBoard() => new CellState?[CoinPusherV23Constants.Rows, CoinPusherV23Constants.Columns];

    private static void ValidateBoardShape(CellState?[,] board)
    {
        if (board.GetLength(0) != CoinPusherV23Constants.Rows || board.GetLength(1) != CoinPusherV23Constants.Columns)
        {
            throw new ArgumentException("CoinPusherEngine v23 boards must be 5x5.", nameof(board));
        }
    }
}

public sealed record WheelLock(
    int WheelSpin,
    int SymId,
    int Stack,
    int ZoneCells,
    int PreWins,
    int PostWins);

public sealed record GameMasterPlan
{
    public GameMasterPlan(
        int totalSpins,
        IReadOnlyList<CoinPusherSpinPlan> spinPlans,
        IReadOnlyDictionary<int, int> targetCollection,
        IReadOnlyList<int> winSymIds,
        IReadOnlyList<int> fillerSymIds,
        IReadOnlyDictionary<int, int> prizeUpgradeMap,
        IReadOnlyList<WheelLock> wheelLocks,
        bool verified,
        IReadOnlyList<string> planLog,
        int rngSeed,
        int totalBaseSpins,
        FeatureRegistry featureRegistry)
    {
        TotalSpins = totalSpins;
        SpinPlans = spinPlans.ToArray();
        TargetCollection = targetCollection.ToDictionary(entry => entry.Key, entry => entry.Value);
        WinSymIds = winSymIds.ToArray();
        FillerSymIds = fillerSymIds.ToArray();
        PrizeUpgradeMap = prizeUpgradeMap.ToDictionary(entry => entry.Key, entry => entry.Value);
        WheelLocks = wheelLocks.ToArray();
        Verified = verified;
        PlanLog = planLog.ToArray();
        RngSeed = rngSeed;
        TotalBaseSpins = totalBaseSpins;
        FeatureRegistry = featureRegistry;
    }

    public int TotalSpins { get; }

    public IReadOnlyList<CoinPusherSpinPlan> SpinPlans { get; }

    public IReadOnlyDictionary<int, int> TargetCollection { get; }

    public IReadOnlyList<int> WinSymIds { get; }

    public IReadOnlyList<int> FillerSymIds { get; }

    public IReadOnlyDictionary<int, int> PrizeUpgradeMap { get; }

    public IReadOnlyList<WheelLock> WheelLocks { get; }

    public bool Verified { get; }

    public IReadOnlyList<string> PlanLog { get; }

    public int RngSeed { get; }

    public int TotalBaseSpins { get; }

    public FeatureRegistry FeatureRegistry { get; }
}

public sealed record V23ReplayResult(
    IReadOnlyDictionary<int, int> CollectionCounts,
    IReadOnlyList<int> ExtraSpinGrantTurns,
    IReadOnlyList<CellState?[,]> BoardTimeline);

public sealed class CoinPusherV23Verifier
{
    public V23ReplayResult Replay(GameMasterPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlanEnvelope(plan);

        var totals = plan.TargetCollection.Keys.ToDictionary(symId => symId, _ => 0);
        var fillerTotals = plan.FillerSymIds.ToDictionary(symId => symId, _ => 0);
        var board = CoinPusherSpinPlan.CloneBoard(plan.SpinPlans[0].BoardAtStart);
        var timeline = new List<CellState?[,]> { CoinPusherSpinPlan.CloneBoard(board) };
        var extraSpinGrants = new List<int>();

        for (var turnIndex = 0; turnIndex < plan.SpinPlans.Count; turnIndex++)
        {
            var spin = plan.SpinPlans[turnIndex];
            if (spin.TurnIndex != turnIndex)
            {
                throw new InvalidOperationException($"Spin index mismatch. Expected {turnIndex}, got {spin.TurnIndex}.");
            }

            AssertBoardsEqual(board, spin.BoardAtStart, $"BoardAtStart mismatch at turn {turnIndex}.");
            FlattenStaleBoardFeatures(board);

            for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
            {
                var pusher = spin.Pushers[column];
                if (pusher.FeatureId == CoinPusherV23Constants.Flush)
                {
                    ExecuteFlush(board, column, totals, fillerTotals, plan);
                    continue;
                }

                if (pusher.IsFeature)
                {
                    throw new InvalidOperationException($"Unsupported pusher feature '{pusher.FeatureId}' at turn {turnIndex}, column {column}.");
                }

                ExecutePush(board, column, pusher.PushValue, totals, fillerTotals, plan);
            }

            board = RotateClockwise(board);
            ApplySpawns(board, spin);
            FireBoardFeatures(board, spin, NextSpin(plan, turnIndex), extraSpinGrants);
            timeline.Add(CoinPusherSpinPlan.CloneBoard(board));
        }

        foreach (var (symId, target) in plan.TargetCollection)
        {
            if (totals[symId] != target)
            {
                throw new InvalidOperationException($"{SymbolTable.NameFor(symId)} collected {totals[symId]}, threshold={target}.");
            }
        }

        foreach (var (symId, count) in fillerTotals)
        {
            var max = SymbolTable.Get(symId).MaxCollections;
            if (max is not null && count >= max.Value)
            {
                throw new InvalidOperationException($"Filler {SymbolTable.NameFor(symId)} collected {count}, max={max.Value}.");
            }
        }

        var expectedExtraSpins = plan.TotalSpins - plan.TotalBaseSpins;
        if (extraSpinGrants.Count != expectedExtraSpins)
        {
            throw new InvalidOperationException($"Expected {expectedExtraSpins} EXTRA_SPIN grants, replay observed {extraSpinGrants.Count}.");
        }

        return new V23ReplayResult(totals, extraSpinGrants, timeline);
    }

    public void VerifyPlan(GameMasterPlan plan) => Replay(plan);

    private static void ValidatePlanEnvelope(GameMasterPlan plan)
    {
        if (plan.TotalSpins != plan.SpinPlans.Count)
        {
            throw new InvalidOperationException($"Plan TotalSpins={plan.TotalSpins}, but contains {plan.SpinPlans.Count} spin plans.");
        }

        if (plan.SpinPlans.Count == 0)
        {
            throw new InvalidOperationException("Plan must contain at least one spin.");
        }

        if (plan.TargetCollection.Count == 0)
        {
            throw new InvalidOperationException("Plan must contain at least one target symbol.");
        }

        foreach (var (symId, target) in plan.TargetCollection)
        {
            if (!SymbolTable.IsNormal(symId))
            {
                throw new InvalidOperationException($"Target symbol {symId} is not a normal symbol.");
            }

            if (target <= 0)
            {
                throw new InvalidOperationException($"Target for symbol {symId} must be positive.");
            }
        }
    }

    private static CoinPusherSpinPlan? NextSpin(GameMasterPlan plan, int turnIndex) =>
        turnIndex + 1 < plan.SpinPlans.Count ? plan.SpinPlans[turnIndex + 1] : null;

    private static void ExecutePush(
        CellState?[,] board,
        int column,
        int pushValue,
        IDictionary<int, int> totals,
        IDictionary<int, int> fillerTotals,
        GameMasterPlan plan)
    {
        if (pushValue is < CoinPusherV23Constants.MinPush or > CoinPusherV23Constants.MaxPush)
        {
            throw new InvalidOperationException($"Normal push value must be 1..3. Got {pushValue}.");
        }

        for (var row = CoinPusherV23Constants.Rows - pushValue; row < CoinPusherV23Constants.Rows; row++)
        {
            Accumulate(board[row, column], totals, fillerTotals, plan);
        }

        for (var row = CoinPusherV23Constants.Rows - 1; row >= pushValue; row--)
        {
            board[row, column] = board[row - pushValue, column];
        }

        for (var row = 0; row < pushValue; row++)
        {
            board[row, column] = null;
        }
    }

    private static void ExecuteFlush(
        CellState?[,] board,
        int column,
        IDictionary<int, int> totals,
        IDictionary<int, int> fillerTotals,
        GameMasterPlan plan)
    {
        for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
        {
            Accumulate(board[row, column], totals, fillerTotals, plan);
            board[row, column] = null;
        }
    }

    private static void Accumulate(
        CellState? cell,
        IDictionary<int, int> totals,
        IDictionary<int, int> fillerTotals,
        GameMasterPlan plan)
    {
        if (cell is null)
        {
            return;
        }

        if (cell.IsFeature)
        {
            cell = cell.ConvertToNormal();
        }

        var countedSymId = plan.PrizeUpgradeMap.TryGetValue(cell.SymId, out var upgradedSymId)
            ? upgradedSymId
            : cell.SymId;

        if (totals.ContainsKey(countedSymId))
        {
            totals[countedSymId] += cell.StackCount;
            return;
        }

        if (fillerTotals.ContainsKey(cell.SymId))
        {
            fillerTotals[cell.SymId] += cell.StackCount;
        }
    }

    private static CellState?[,] RotateClockwise(CellState?[,] board)
    {
        var rotated = CoinPusherSpinPlan.EmptyBoard();
        for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
        {
            for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
            {
                rotated[column, CoinPusherV23Constants.Columns - 1 - row] = board[row, column];
            }
        }

        return rotated;
    }

    private static void ApplySpawns(CellState?[,] board, CoinPusherSpinPlan spin)
    {
        foreach (var (position, cell) in spin.PlannedSpawns)
        {
            if (board[position.Row, position.Column] is not null)
            {
                throw new InvalidOperationException($"Spawn at {position} would overwrite an occupied cell.");
            }

            board[position.Row, position.Column] = cell;
        }
    }

    private static void FireBoardFeatures(
        CellState?[,] board,
        CoinPusherSpinPlan spin,
        CoinPusherSpinPlan? nextSpin,
        ICollection<int> extraSpinGrants)
    {
        var wheelSyms = new List<int>();
        foreach (var (position, cell) in Cells(board).ToArray())
        {
            if (cell is null || !cell.IsFeature)
            {
                continue;
            }

            switch (cell.FeatureId)
            {
                case CoinPusherV23Constants.Wheel:
                    var wheelSymId = cell.FParams?.WheelSymId
                        ?? throw new InvalidOperationException($"WHEEL token at {position} is missing wheel symbol id.");
                    var stack = cell.FParams?.WheelStackMultiplier
                        ?? CoinPusherV23Constants.WheelStackMultiplier;
                    StackWheelSymbol(board, wheelSymId, stack);
                    wheelSyms.Add(wheelSymId);
                    break;
                case CoinPusherV23Constants.ExtraSpin:
                    extraSpinGrants.Add(spin.TurnIndex);
                    break;
                case CoinPusherV23Constants.PrizeUpgrade:
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported board feature '{cell.FeatureId}' at {position}.");
            }

            board[position.Row, position.Column] = cell.ConvertToNormal();
        }

        foreach (var wheelSymId in wheelSyms)
        {
            PostWheelIsolation(board, nextSpin, wheelSymId);
        }
    }

    private static void StackWheelSymbol(CellState?[,] board, int wheelSymId, int stack)
    {
        var matched = 0;
        foreach (var (position, cell) in Cells(board).ToArray())
        {
            if (cell is null || cell.IsFeature || cell.SymId != wheelSymId)
            {
                continue;
            }

            matched++;
            board[position.Row, position.Column] = cell.WithStack(stack);
        }

        if (matched == 0)
        {
            throw new InvalidOperationException($"Dead WHEEL had no {SymbolTable.NameFor(wheelSymId)} cells to stack.");
        }
    }

    private static void PostWheelIsolation(CellState?[,] board, CoinPusherSpinPlan? nextSpin, int wheelSymId)
    {
        if (nextSpin is null)
        {
            return;
        }

        var zone = BuildZoneSet(nextSpin.Pushers);
        foreach (var (position, cell) in Cells(board).ToArray())
        {
            if (cell is null || cell.IsFeature || cell.SymId != wheelSymId)
            {
                continue;
            }

            var planned = nextSpin.BoardAtStart[position.Row, position.Column];
            var isPlanned = planned is not null && !planned.IsFeature && planned.SymId == wheelSymId;
            if (!zone.Contains(position) || !isPlanned)
            {
                board[position.Row, position.Column] = null;
            }
        }
    }

    private static HashSet<BoardPosition> BuildZoneSet(IReadOnlyList<PusherPlan> pushers)
    {
        var zone = new HashSet<BoardPosition>();
        for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
        {
            var rows = pushers[column].FeatureId == CoinPusherV23Constants.Flush
                ? CoinPusherV23Constants.Rows
                : pushers[column].PushValue;
            for (var row = CoinPusherV23Constants.Rows - rows; row < CoinPusherV23Constants.Rows; row++)
            {
                zone.Add(new BoardPosition(row, column));
            }
        }

        return zone;
    }

    private static void FlattenStaleBoardFeatures(CellState?[,] board)
    {
        foreach (var (position, cell) in Cells(board).ToArray())
        {
            if (cell is not null && cell.IsFeature)
            {
                board[position.Row, position.Column] = cell.ConvertToNormal();
            }
        }
    }

    private static IEnumerable<(BoardPosition Position, CellState? Cell)> Cells(CellState?[,] board)
    {
        for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
        {
            for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
            {
                yield return (new BoardPosition(row, column), board[row, column]);
            }
        }
    }

    private static void AssertBoardsEqual(CellState?[,] actual, CellState?[,] expected, string message)
    {
        for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
        {
            for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
            {
                if (!Equals(actual[row, column], expected[row, column]))
                {
                    throw new InvalidOperationException($"{message} Difference at R{row}C{column}.");
                }
            }
        }
    }
}

public sealed class CoinPusherV23Planner
{
    private readonly CoinPusherV23Verifier _verifier;

    public CoinPusherV23Planner(CoinPusherV23Verifier? verifier = null)
    {
        _verifier = verifier ?? new CoinPusherV23Verifier();
    }

    public GameMasterPlan Generate(MathInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var normalized = Normalize(input);
        var planLog = new List<string>();
        var registry = FeatureRegistry.Default(normalized.TotalBaseSpins);
        var targetRemaining = normalized.TargetCollection.ToDictionary(entry => entry.Key, entry => entry.Value);
        var wheelLocks = new List<WheelLock>();
        var requiredExtraSpins = normalized.RequiredFeatures.GetValueOrDefault(CoinPusherV23Constants.ExtraSpin);
        var requiredWheels = normalized.RequiredFeatures.GetValueOrDefault(CoinPusherV23Constants.Wheel);
        var requiredFlushes = normalized.RequiredFeatures.GetValueOrDefault(CoinPusherV23Constants.Flush);
        var requiredPrizeUpgrades = normalized.RequiredFeatures.GetValueOrDefault(CoinPusherV23Constants.PrizeUpgrade);
        var autoExtraSpins = ComputeAutoExtraSpins(normalized.TargetCollection.Values.Sum(), normalized.TotalBaseSpins, requiredExtraSpins);
        var totalExtraSpins = requiredExtraSpins + autoExtraSpins;
        var totalSpins = normalized.TotalBaseSpins + totalExtraSpins;

        if (requiredWheels > 1)
        {
            throw new PlanningException("This v23 planner implementation currently supports at most one required WHEEL per ticket.");
        }

        if (requiredPrizeUpgrades > 0 && normalized.PrizeUpgradeMap.Count == 0)
        {
            throw new PlanningException("PRIZE_UPGRADE requires MathInput.PrizeUpgradeMap.");
        }

        if (requiredWheels > 0 && totalSpins < 3)
        {
            totalExtraSpins += 3 - totalSpins;
            totalSpins = normalized.TotalBaseSpins + totalExtraSpins;
        }

        planLog.Add($"rng_seed={normalized.RngSeed}");
        planLog.Add($"targets={string.Join(",", normalized.TargetCollection.Select(entry => $"{SymbolTable.NameFor(entry.Key)}:{entry.Value}"))}");
        planLog.Add($"auto_extra_spins={autoExtraSpins}");

        var extraSpinParents = Enumerable.Range(0, totalExtraSpins)
            .Select(index => Math.Min(index, Math.Max(0, normalized.TotalBaseSpins - 1)))
            .ToArray();

        var featureQueue = BuildFeatureQueue(
            requiredWheels,
            requiredFlushes,
            totalExtraSpins,
            requiredPrizeUpgrades,
            normalized,
            totalSpins,
            targetRemaining,
            wheelLocks,
            planLog);

        var boards = new List<CellState?[,]>(totalSpins);
        var spinBuilders = new List<SpinBuilder>(totalSpins);
        var currentBoard = CoinPusherSpinPlan.EmptyBoard();

        FillCollectablePositions(currentBoard, NormalPushers(), targetRemaining, normalized, null);

        for (var turnIndex = 0; turnIndex < totalSpins; turnIndex++)
        {
            var pushers = BuildPushers(turnIndex, featureQueue);
            FillCollectablePositions(currentBoard, pushers, targetRemaining, normalized, null);

            boards.Add(CoinPusherSpinPlan.CloneBoard(currentBoard));
            var builder = new SpinBuilder(turnIndex, CoinPusherSpinPlan.CloneBoard(currentBoard), pushers)
            {
                IsExtraSpin = turnIndex >= normalized.TotalBaseSpins,
                ParentTurnIndex = turnIndex >= normalized.TotalBaseSpins
                    ? extraSpinParents[turnIndex - normalized.TotalBaseSpins]
                    : null
            };

            var postPush = SimulatePushRotateForPlanning(currentBoard, pushers);
            if (turnIndex + 1 < totalSpins)
            {
                var nextPushers = BuildPushers(turnIndex + 1, featureQueue);
                var reserved = featureQueue
                    .Where(feature => feature.SpawnTurn == turnIndex && feature.Kind != CoinPusherV23Constants.Flush)
                    .Select(feature => feature.Kind)
                    .ToArray();

                FillNextBoardViaSpawns(
                    postPush,
                    nextPushers,
                    targetRemaining,
                    normalized,
                    reserved,
                    builder,
                    turnIndex,
                    wheelLocks,
                    planLog);
            }
            else
            {
                AddCurrentTurnFeatures(postPush, builder, featureQueue, turnIndex, normalized, wheelLocks, planLog);
                FirePlannedFeaturesForPlanning(postPush, builder, turnIndex);
            }

            currentBoard = postPush;
            spinBuilders.Add(builder);
        }

        if (targetRemaining.Values.Any(value => value != 0))
        {
            var remaining = string.Join(", ", targetRemaining.Where(entry => entry.Value != 0).Select(entry => $"{SymbolTable.NameFor(entry.Key)}={entry.Value}"));
            throw new PlanningException($"Unable to allocate all target wins. Remaining: {remaining}.");
        }

        var spinPlans = spinBuilders.Select(builder => builder.Build()).ToArray();
        var plan = new GameMasterPlan(
            totalSpins,
            spinPlans,
            normalized.TargetCollection,
            normalized.WinSymIds,
            normalized.FillerSymIds,
            normalized.PrizeUpgradeMap,
            wheelLocks,
            false,
            planLog,
            normalized.RngSeed,
            normalized.TotalBaseSpins,
            registry);

        _verifier.VerifyPlan(plan);
        planLog.Add("verification=passed");

        return new GameMasterPlan(
            totalSpins,
            spinPlans,
            normalized.TargetCollection,
            normalized.WinSymIds,
            normalized.FillerSymIds,
            normalized.PrizeUpgradeMap,
            wheelLocks,
            true,
            planLog,
            normalized.RngSeed,
            normalized.TotalBaseSpins,
            registry);
    }

    private static NormalizedMathInput Normalize(MathInput input)
    {
        if (input.TotalBaseSpins <= 0)
        {
            throw new PlanningException("TotalBaseSpins must be positive.");
        }

        if (input.TargetCollection.Count == 0)
        {
            throw new PlanningException("TargetCollection must contain at least one symbol.");
        }

        var targets = new Dictionary<int, int>();
        foreach (var (name, target) in input.TargetCollection)
        {
            var symId = SymbolTable.IdFor(name);
            if (!SymbolTable.IsNormal(symId))
            {
                throw new PlanningException($"Target symbol '{name}' must be a normal symbol.");
            }

            if (target <= 0)
            {
                throw new PlanningException($"Target for '{name}' must be positive.");
            }

            targets[symId] = target;
        }

        var requiredFeatures = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (feature, count) in input.RequiredFeatures ?? new Dictionary<string, int>())
        {
            var normalizedFeature = NormalizeFeatureId(feature);
            if (count < 0)
            {
                throw new PlanningException($"Required feature count for '{normalizedFeature}' cannot be negative.");
            }

            requiredFeatures[normalizedFeature] = count;
        }

        var prizeUpgradeMap = new Dictionary<int, int>();
        foreach (var (fromName, toName) in input.PrizeUpgradeMap ?? new Dictionary<string, string>())
        {
            var fromSymId = SymbolTable.IdFor(fromName);
            var toSymId = SymbolTable.IdFor(toName);
            if (!SymbolTable.IsNormal(fromSymId) || !SymbolTable.IsNormal(toSymId))
            {
                throw new PlanningException("PrizeUpgradeMap can only reference normal symbols.");
            }

            prizeUpgradeMap[fromSymId] = toSymId;
        }

        var fromSymIds = prizeUpgradeMap.Keys.ToHashSet();
        var winSymIds = targets.Keys.OrderBy(id => id).ToArray();
        var fillerSymIds = SymbolTable.NormalIds
            .Where(id => !winSymIds.Contains(id) && !fromSymIds.Contains(id))
            .ToArray();

        if (fillerSymIds.Length == 0)
        {
            throw new PlanningException("At least one filler symbol must remain after targets and prize-upgrade from symbols are reserved.");
        }

        var wheelOrder = (input.WheelSymbolOrder ?? input.TargetCollection.Keys.ToArray())
            .Select(SymbolTable.IdFor)
            .Where(id => targets.ContainsKey(id))
            .Distinct()
            .ToArray();

        if (wheelOrder.Length == 0)
        {
            wheelOrder = winSymIds;
        }

        return new NormalizedMathInput(
            targets,
            input.TotalBaseSpins,
            requiredFeatures,
            wheelOrder,
            prizeUpgradeMap,
            winSymIds,
            fillerSymIds,
            input.RngSeed ?? 0x1723);
    }

    private static string NormalizeFeatureId(string featureId)
    {
        var normalized = featureId.Trim().ToUpperInvariant();
        return normalized switch
        {
            "WHEEL" => CoinPusherV23Constants.Wheel,
            "FLUSH" => CoinPusherV23Constants.Flush,
            "EXTRA_SPIN" or "XSPIN" => CoinPusherV23Constants.ExtraSpin,
            "PRIZE_UPGRADE" or "PRUP" => CoinPusherV23Constants.PrizeUpgrade,
            _ => throw new PlanningException($"Unknown feature '{featureId}'.")
        };
    }

    private static int ComputeAutoExtraSpins(int totalWins, int baseSpins, int requiredExtraSpins)
    {
        var capacity = 15 + Math.Max(0, baseSpins + requiredExtraSpins - 1) * 9;
        if (totalWins <= capacity)
        {
            return 0;
        }

        return (int)Math.Ceiling((totalWins - capacity) / 9.0);
    }

    private static IReadOnlyList<PlannedFeature> BuildFeatureQueue(
        int requiredWheels,
        int requiredFlushes,
        int totalExtraSpins,
        int requiredPrizeUpgrades,
        NormalizedMathInput input,
        int totalSpins,
        IDictionary<int, int> targetRemaining,
        ICollection<WheelLock> wheelLocks,
        ICollection<string> planLog)
    {
        var features = new List<PlannedFeature>();
        if (requiredWheels > 0)
        {
            var wheelTurn = Math.Min(1, totalSpins - 2);
            var wheelSymId = PickWheelSymbol(input, targetRemaining);
            var zoneCells = Math.Min(3, Math.Max(1, targetRemaining[wheelSymId] / CoinPusherV23Constants.WheelStackMultiplier));
            var postWins = zoneCells * CoinPusherV23Constants.WheelStackMultiplier;
            if (postWins > targetRemaining[wheelSymId])
            {
                zoneCells = 1;
                postWins = CoinPusherV23Constants.WheelStackMultiplier;
            }

            if (targetRemaining[wheelSymId] < CoinPusherV23Constants.WheelStackMultiplier)
            {
                throw new PlanningException("Required WHEEL needs a target with at least 4 remaining wins.");
            }

            features.Add(new PlannedFeature(CoinPusherV23Constants.Wheel, wheelTurn, 4, wheelSymId));
            targetRemaining[wheelSymId] -= postWins;
            wheelLocks.Add(new WheelLock(
                wheelTurn,
                wheelSymId,
                CoinPusherV23Constants.WheelStackMultiplier,
                zoneCells,
                targetRemaining[wheelSymId] - postWins,
                postWins));
            planLog.Add($"wheel_lock sym={SymbolTable.NameFor(wheelSymId)} turn={wheelTurn} stack=4 zone_cells={zoneCells} post_wins={postWins}");
        }

        for (var index = 0; index < requiredFlushes; index++)
        {
            features.Add(new PlannedFeature(CoinPusherV23Constants.Flush, Math.Min(index, totalSpins - 1), index % CoinPusherV23Constants.Columns, null));
        }

        for (var index = 0; index < totalExtraSpins; index++)
        {
            var spawnTurn = Math.Min(index, Math.Max(0, input.TotalBaseSpins - 1));
            features.Add(new PlannedFeature(CoinPusherV23Constants.ExtraSpin, spawnTurn, 4, null));
        }

        for (var index = 0; index < requiredPrizeUpgrades; index++)
        {
            features.Add(new PlannedFeature(CoinPusherV23Constants.PrizeUpgrade, Math.Min(index, totalSpins - 2), 4, null));
        }

        return features
            .OrderBy(feature => feature.SpawnTurn)
            .ThenBy(feature => feature.Kind, StringComparer.Ordinal)
            .ToArray();
    }

    private static int PickWheelSymbol(NormalizedMathInput input, IDictionary<int, int> remaining)
    {
        var excludedToSyms = input.PrizeUpgradeMap.Values.ToHashSet();
        foreach (var symId in input.WheelSymbolOrder)
        {
            if (!excludedToSyms.Contains(symId) && remaining.TryGetValue(symId, out var target) && target >= CoinPusherV23Constants.WheelStackMultiplier)
            {
                return symId;
            }
        }

        throw new PlanningException("No valid target symbol is available for WHEEL.");
    }

    private static IReadOnlyList<PusherPlan> BuildPushers(int turnIndex, IReadOnlyList<PlannedFeature> features)
    {
        var isWheelTurn = features.Any(feature => feature.Kind == CoinPusherV23Constants.Wheel && feature.SpawnTurn == turnIndex);
        var pushers = Enumerable
            .Repeat(PusherPlan.Normal(isWheelTurn ? CoinPusherV23Constants.MinPush : CoinPusherV23Constants.MaxPush), CoinPusherV23Constants.Columns)
            .ToArray();

        foreach (var flush in features.Where(feature => feature.Kind == CoinPusherV23Constants.Flush && feature.SpawnTurn == turnIndex))
        {
            pushers[flush.Column] = PusherPlan.Flush();
        }

        return pushers;
    }

    private static IReadOnlyList<PusherPlan> NormalPushers() =>
        Enumerable.Repeat(PusherPlan.Normal(CoinPusherV23Constants.MaxPush), CoinPusherV23Constants.Columns).ToArray();

    private static void FillCollectablePositions(
        CellState?[,] board,
        IReadOnlyList<PusherPlan> pushers,
        IDictionary<int, int> targetRemaining,
        NormalizedMathInput input,
        int? onlyColumn)
    {
        foreach (var position in CollectablePositions(pushers).Where(position => onlyColumn is null || position.Column == onlyColumn.Value))
        {
            if (board[position.Row, position.Column] is not null)
            {
                continue;
            }

            var cell = NextWinCell(targetRemaining, input, 1);
            if (cell is null)
            {
                break;
            }

            board[position.Row, position.Column] = cell;
        }
    }

    private static void FillNextBoardViaSpawns(
        CellState?[,] postPush,
        IReadOnlyList<PusherPlan> nextPushers,
        IDictionary<int, int> targetRemaining,
        NormalizedMathInput input,
        IReadOnlyList<string> featuresForCurrentTurn,
        SpinBuilder builder,
        int turnIndex,
        IReadOnlyList<WheelLock> wheelLocks,
        ICollection<string> planLog)
    {
        var nullPositions = NullPositions(postPush).ToList();
        var reservedPositions = AddCurrentTurnFeatures(postPush, builder, featuresForCurrentTurn, turnIndex, input, wheelLocks, planLog);
        var wheelLock = wheelLocks.FirstOrDefault(lockInfo => lockInfo.WheelSpin == turnIndex);
        var wheelZoneRemaining = wheelLock?.ZoneCells ?? 0;
        var zone = CollectablePositions(nextPushers)
            .Where(position => nullPositions.Contains(position) && !reservedPositions.Contains(position))
            .ToArray();

        foreach (var position in zone)
        {
            if (wheelLock is not null && wheelZoneRemaining > 0)
            {
                postPush[position.Row, position.Column] = CellState.Normal(wheelLock.SymId);
                builder.Spawns[position] = CellState.Normal(wheelLock.SymId);
                wheelZoneRemaining--;
                continue;
            }

            var cell = NextWinCell(targetRemaining, input, 1);
            if (cell is null)
            {
                continue;
            }

            postPush[position.Row, position.Column] = cell;
            builder.Spawns[position] = cell;
        }

        foreach (var position in nullPositions.Where(position => postPush[position.Row, position.Column] is null))
        {
            var filler = CellState.Normal(input.FillerSymIds[0]);
            postPush[position.Row, position.Column] = filler;
            builder.Spawns[position] = filler;
        }

        FirePlannedFeaturesForPlanning(postPush, builder, turnIndex);
    }

    private static HashSet<BoardPosition> AddCurrentTurnFeatures(
        CellState?[,] postPush,
        SpinBuilder builder,
        IReadOnlyList<PlannedFeature> featureQueue,
        int turnIndex,
        NormalizedMathInput input,
        IReadOnlyList<WheelLock> wheelLocks,
        ICollection<string> planLog)
    {
        var featuresForCurrentTurn = featureQueue
            .Where(feature => feature.SpawnTurn == turnIndex && feature.Kind != CoinPusherV23Constants.Flush)
            .Select(feature => feature.Kind)
            .ToArray();
        return AddCurrentTurnFeatures(postPush, builder, featuresForCurrentTurn, turnIndex, input, wheelLocks, planLog);
    }

    private static HashSet<BoardPosition> AddCurrentTurnFeatures(
        CellState?[,] postPush,
        SpinBuilder builder,
        IReadOnlyList<string> featuresForCurrentTurn,
        int turnIndex,
        NormalizedMathInput input,
        IReadOnlyList<WheelLock> wheelLocks,
        ICollection<string> planLog)
    {
        var reserved = new HashSet<BoardPosition>();
        foreach (var feature in featuresForCurrentTurn)
        {
            var slot = FindFeatureSlot(postPush, reserved);
            var convertSymId = postPush[slot.Row, slot.Column]?.SymId ?? input.FillerSymIds[0];
            var cell = BuildFeatureCell(feature, convertSymId, turnIndex, input, wheelLocks);
            postPush[slot.Row, slot.Column] = cell;
            builder.Spawns[slot] = cell;
            builder.FeatureSpawns.Add(new FeatureSpawn(feature, turnIndex, slot.Column, slot.Row));
            reserved.Add(slot);
            planLog.Add($"feature_spawn feature={feature} turn={turnIndex} pos={slot.Row * CoinPusherV23Constants.Columns + slot.Column} convertToId={convertSymId}");
        }

        return reserved;
    }

    private static CellState BuildFeatureCell(
        string feature,
        int convertSymId,
        int turnIndex,
        NormalizedMathInput input,
        IReadOnlyList<WheelLock> wheelLocks)
    {
        return feature switch
        {
            CoinPusherV23Constants.Wheel => CellState.Feature(
                CoinPusherV23Constants.WheelFeatureId,
                CoinPusherV23Constants.Wheel,
                convertSymId,
                new FeatureParams(
                    WheelSymId: wheelLocks.First(lockInfo => lockInfo.WheelSpin == turnIndex).SymId,
                    WheelStackMultiplier: CoinPusherV23Constants.WheelStackMultiplier)),
            CoinPusherV23Constants.ExtraSpin => CellState.Feature(
                CoinPusherV23Constants.ExtraSpinFeatureId,
                CoinPusherV23Constants.ExtraSpin,
                convertSymId,
                new FeatureParams(GrantedTurnIndex: turnIndex)),
            CoinPusherV23Constants.PrizeUpgrade => CellState.Feature(
                CoinPusherV23Constants.PrizeUpgradeFeatureId,
                CoinPusherV23Constants.PrizeUpgrade,
                convertSymId,
                BuildPrizeUpgradeParams(input)),
            _ => throw new PlanningException($"Unsupported token feature '{feature}'.")
        };
    }

    private static FeatureParams BuildPrizeUpgradeParams(NormalizedMathInput input)
    {
        var first = input.PrizeUpgradeMap.First();
        return new FeatureParams(FromSymId: first.Key, ToSymId: first.Value);
    }

    private static void FirePlannedFeaturesForPlanning(CellState?[,] board, SpinBuilder builder, int turnIndex)
    {
        foreach (var spawn in builder.FeatureSpawns.Where(spawn => spawn.TurnIndex == turnIndex))
        {
            var slot = new BoardPosition(spawn.Row!.Value, spawn.Column);
            var featureCell = board[slot.Row, slot.Column]
                ?? throw new PlanningException($"Feature spawn at {slot} is missing from the planned board.");
            FireFeatureForPlanning(board, slot, featureCell);
        }
    }

    private static void FireFeatureForPlanning(CellState?[,] board, BoardPosition slot, CellState featureCell)
    {
        if (!featureCell.IsFeature)
        {
            throw new PlanningException($"Expected feature token at {slot}.");
        }

        if (featureCell.FeatureId == CoinPusherV23Constants.Wheel)
        {
            var wheelSymId = featureCell.FParams!.WheelSymId!.Value;
            for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
            {
                for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
                {
                    var cell = board[row, column];
                    if (cell is not null && !cell.IsFeature && cell.SymId == wheelSymId)
                    {
                        board[row, column] = cell.WithStack(CoinPusherV23Constants.WheelStackMultiplier);
                    }
                }
            }
        }

        board[slot.Row, slot.Column] = featureCell.ConvertToNormal();
    }

    private static BoardPosition FindFeatureSlot(CellState?[,] board, ISet<BoardPosition> reserved)
    {
        var candidates = NullPositions(board)
            .Where(position => !reserved.Contains(position))
            .OrderByDescending(position => position.Column)
            .ThenBy(position => position.Row)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new PlanningException("No spawn slot is available for a feature token.");
        }

        return candidates[0];
    }

    private static CellState? NextWinCell(
        IDictionary<int, int> targetRemaining,
        NormalizedMathInput input,
        int stackCount)
    {
        var next = targetRemaining.FirstOrDefault(entry => entry.Value >= stackCount);
        if (next.Key == 0)
        {
            return null;
        }

        targetRemaining[next.Key] -= stackCount;
        var boardSymId = ReverseUpgrade(next.Key, input.PrizeUpgradeMap);
        return CellState.Normal(boardSymId, stackCount);
    }

    private static int ReverseUpgrade(int targetSymId, IReadOnlyDictionary<int, int> upgradeMap)
    {
        foreach (var (fromSymId, toSymId) in upgradeMap)
        {
            if (toSymId == targetSymId)
            {
                return fromSymId;
            }
        }

        return targetSymId;
    }

    private static IEnumerable<BoardPosition> CollectablePositions(IReadOnlyList<PusherPlan> pushers)
    {
        for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
        {
            var rows = pushers[column].FeatureId == CoinPusherV23Constants.Flush
                ? CoinPusherV23Constants.Rows
                : pushers[column].PushValue;
            for (var row = CoinPusherV23Constants.Rows - rows; row < CoinPusherV23Constants.Rows; row++)
            {
                yield return new BoardPosition(row, column);
            }
        }
    }

    private static IEnumerable<BoardPosition> NullPositions(CellState?[,] board)
    {
        for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
        {
            for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
            {
                if (board[row, column] is null)
                {
                    yield return new BoardPosition(row, column);
                }
            }
        }
    }

    private static CellState?[,] SimulatePushRotateForPlanning(CellState?[,] boardAtStart, IReadOnlyList<PusherPlan> pushers)
    {
        var board = CoinPusherSpinPlan.CloneBoard(boardAtStart);
        for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
        {
            var pusher = pushers[column];
            if (pusher.FeatureId == CoinPusherV23Constants.Flush)
            {
                for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
                {
                    board[row, column] = null;
                }

                continue;
            }

            for (var row = CoinPusherV23Constants.Rows - 1; row >= pusher.PushValue; row--)
            {
                board[row, column] = board[row - pusher.PushValue, column];
            }

            for (var row = 0; row < pusher.PushValue; row++)
            {
                board[row, column] = null;
            }
        }

        var rotated = CoinPusherSpinPlan.EmptyBoard();
        for (var row = 0; row < CoinPusherV23Constants.Rows; row++)
        {
            for (var column = 0; column < CoinPusherV23Constants.Columns; column++)
            {
                rotated[column, CoinPusherV23Constants.Columns - 1 - row] = board[row, column];
            }
        }

        return rotated;
    }

    private sealed record NormalizedMathInput(
        IReadOnlyDictionary<int, int> TargetCollection,
        int TotalBaseSpins,
        IReadOnlyDictionary<string, int> RequiredFeatures,
        IReadOnlyList<int> WheelSymbolOrder,
        IReadOnlyDictionary<int, int> PrizeUpgradeMap,
        IReadOnlyList<int> WinSymIds,
        IReadOnlyList<int> FillerSymIds,
        int RngSeed);

    private sealed record PlannedFeature(string Kind, int SpawnTurn, int Column, int? SymId);

    private sealed class SpinBuilder
    {
        public SpinBuilder(int turnIndex, CellState?[,] boardAtStart, IReadOnlyList<PusherPlan> pushers)
        {
            TurnIndex = turnIndex;
            BoardAtStart = boardAtStart;
            Pushers = pushers;
        }

        public int TurnIndex { get; }

        public CellState?[,] BoardAtStart { get; }

        public IReadOnlyList<PusherPlan> Pushers { get; }

        public Dictionary<BoardPosition, CellState> Spawns { get; } = [];

        public List<FeatureSpawn> FeatureSpawns { get; } = [];

        public bool IsExtraSpin { get; init; }

        public int? ParentTurnIndex { get; init; }

        public CoinPusherSpinPlan Build() =>
            new(TurnIndex, BoardAtStart, Pushers, Spawns, FeatureSpawns, IsExtraSpin, ParentTurnIndex);
    }
}
