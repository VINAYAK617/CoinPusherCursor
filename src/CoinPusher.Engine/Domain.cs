namespace CoinPusher.Engine;

public static class EngineConstants
{
    public const int BoardRows = 5;
    public const int BoardColumns = 5;
    public const int MinimumPushValue = 1;
    public const int MaximumPushValue = 3;
    public const int MinimumWheelValue = 1;
    public const int MaximumWheelValue = 3;
    public const int MaximumStackSize = 7;
    public const int MaximumFeatureChainDepth = 1;
}

public enum PrizeLevel
{
    Base = 0,
    Upgrade1 = 1,
    Upgrade2 = 2
}

public enum CellKind
{
    Empty,
    Symbol,
    Feature
}

public enum FeatureKind
{
    PrizeUpgrade,
    Wheel,
    Flush,
    ExtraSpin
}

public enum BoardRotation
{
    None,
    Clockwise,
    CounterClockwise,
    HalfTurn
}

public enum CollectionSource
{
    Push,
    Flush
}

public readonly record struct BoardPosition
{
    public BoardPosition(int row, int column)
    {
        if (row < 0 || row >= EngineConstants.BoardRows)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, "Board row must be between 0 and 4.");
        }

        if (column < 0 || column >= EngineConstants.BoardColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, "Board column must be between 0 and 4.");
        }

        Row = row;
        Column = column;
    }

    public int Row { get; }

    public int Column { get; }

    public override string ToString() => $"R{Row}C{Column}";
}

public sealed record ObjectiveRequirement
{
    public ObjectiveRequirement(string id, int targetCount)
    {
        Id = NormalizeObjectiveId(id);
        if (targetCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetCount), targetCount, "Objective targets cannot be negative.");
        }

        TargetCount = targetCount;
    }

    public string Id { get; }

    public int TargetCount { get; }

    internal static string NormalizeObjectiveId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Objective id is required.", nameof(id));
        }

        return id.Trim().ToUpperInvariant();
    }
}

public sealed record PrizeTableEntry
{
    public PrizeTableEntry(int @base, int upgrade1, int upgrade2)
    {
        if (@base < 0 || upgrade1 < 0 || upgrade2 < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(@base), "Prize values cannot be negative.");
        }

        Base = @base;
        Upgrade1 = upgrade1;
        Upgrade2 = upgrade2;
    }

    public int Base { get; }

    public int Upgrade1 { get; }

    public int Upgrade2 { get; }

    public int GetValue(PrizeLevel level) =>
        level switch
        {
            PrizeLevel.Base => Base,
            PrizeLevel.Upgrade1 => Upgrade1,
            PrizeLevel.Upgrade2 => Upgrade2,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unsupported prize level.")
        };
}

public sealed record PaytableConfiguration
{
    private readonly IReadOnlyDictionary<string, PrizeTableEntry> _entries;

    public PaytableConfiguration(IReadOnlyDictionary<string, PrizeTableEntry> entries)
    {
        if (entries.Count == 0)
        {
            throw new ArgumentException("Paytable must contain at least one prize entry.", nameof(entries));
        }

        _entries = entries.ToDictionary(
            entry => ObjectiveRequirement.NormalizeObjectiveId(entry.Key),
            entry => entry.Value,
            StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, PrizeTableEntry> Entries => _entries;

    public PrizeTableEntry GetEntry(string objectiveId)
    {
        var normalizedId = ObjectiveRequirement.NormalizeObjectiveId(objectiveId);
        if (!_entries.TryGetValue(normalizedId, out var entry))
        {
            throw new KeyNotFoundException($"Objective '{normalizedId}' is missing from the paytable.");
        }

        return entry;
    }
}

public sealed record FeatureConfiguration(
    int InitialSpinCount = 5,
    int MaximumStackSize = EngineConstants.MaximumStackSize,
    int MaximumFeatureChainDepth = EngineConstants.MaximumFeatureChainDepth)
{
    public void Validate()
    {
        if (InitialSpinCount <= 0)
        {
            throw new PlanningException("Initial spin count must be positive.");
        }

        if (MaximumStackSize != EngineConstants.MaximumStackSize)
        {
            throw new PlanningException("This engine version supports a fixed maximum stack size of 7.");
        }

        if (MaximumFeatureChainDepth != EngineConstants.MaximumFeatureChainDepth)
        {
            throw new PlanningException("This engine version supports exactly one feature-to-feature chain step.");
        }
    }
}

public sealed record StyleProfile(string Name = "balanced", bool PreferUpgradedPrizes = true);

public sealed record OutcomeRequest(
    int TargetWin,
    IReadOnlyList<ObjectiveRequirement> Objectives,
    PaytableConfiguration Paytable,
    FeatureConfiguration FeatureConfiguration,
    StyleProfile StyleProfile,
    IReadOnlyDictionary<string, int> SymbolThresholds)
{
    public static OutcomeRequest Create(
        int targetWin,
        IReadOnlyList<ObjectiveRequirement> objectives,
        PaytableConfiguration paytable,
        FeatureConfiguration? featureConfiguration = null,
        StyleProfile? styleProfile = null,
        IReadOnlyDictionary<string, int>? symbolThresholds = null) =>
        new(
            targetWin,
            objectives,
            paytable,
            featureConfiguration ?? new FeatureConfiguration(),
            styleProfile ?? new StyleProfile(),
            NormalizeThresholds(objectives, symbolThresholds));

    private static IReadOnlyDictionary<string, int> NormalizeThresholds(
        IReadOnlyList<ObjectiveRequirement> objectives,
        IReadOnlyDictionary<string, int>? symbolThresholds)
    {
        var normalized = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var symbol in new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" })
        {
            normalized[symbol] = 30;
        }

        if (symbolThresholds is not null)
        {
            foreach (var (symbolId, threshold) in symbolThresholds)
            {
                if (threshold <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(symbolThresholds), threshold, "Symbol thresholds must be positive.");
                }

                normalized[ObjectiveRequirement.NormalizeObjectiveId(symbolId)] = threshold;
            }
        }

        foreach (var objective in objectives)
        {
            normalized[objective.Id] = objective.TargetCount;
        }

        return normalized;
    }
}

public sealed record SymbolToken
{
    public SymbolToken(string symbolId, int stackSize = 1, bool contributesToObjective = true)
    {
        SymbolId = ObjectiveRequirement.NormalizeObjectiveId(symbolId);
        if (stackSize <= 0 || stackSize > EngineConstants.MaximumStackSize)
        {
            throw new ArgumentOutOfRangeException(nameof(stackSize), stackSize, "Stack size must be between 1 and 7.");
        }

        StackSize = stackSize;
        ContributesToObjective = contributesToObjective;
    }

    public string SymbolId { get; }

    public string ObjectiveId => SymbolId;

    public int StackSize { get; }

    public bool ContributesToObjective { get; }

    public SymbolToken AddStacks(int stacks) =>
        new(SymbolId, Math.Min(EngineConstants.MaximumStackSize, StackSize + stacks), ContributesToObjective);
}

public sealed record FeatureToken
{
    public FeatureToken(FeatureKind kind, int chainDepth = 0)
    {
        if (chainDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chainDepth), chainDepth, "Feature chain depth cannot be negative.");
        }

        Kind = kind;
        ChainDepth = chainDepth;
    }

    public FeatureKind Kind { get; }

    public int ChainDepth { get; }
}

public sealed record BoardCell
{
    private BoardCell(CellKind kind, SymbolToken? symbol, FeatureToken? feature)
    {
        Kind = kind;
        Symbol = symbol;
        Feature = feature;
    }

    public static BoardCell Empty { get; } = new(CellKind.Empty, null, null);

    public CellKind Kind { get; }

    public SymbolToken? Symbol { get; }

    public FeatureToken? Feature { get; }

    public static BoardCell FromSymbol(string objectiveId, int stackSize = 1) =>
        new(CellKind.Symbol, new SymbolToken(objectiveId, stackSize), null);

    public static BoardCell FromSymbol(SymbolToken symbol) =>
        new(CellKind.Symbol, symbol, null);

    public static BoardCell FromFillerSymbol(string symbolId, int stackSize = 1) =>
        new(CellKind.Symbol, new SymbolToken(symbolId, stackSize), null);

    public static BoardCell FromFeature(FeatureKind kind, int chainDepth = 0) =>
        new(CellKind.Feature, null, new FeatureToken(kind, chainDepth));

    public static BoardCell FromFeature(FeatureToken feature) =>
        new(CellKind.Feature, null, feature);

    public override string ToString() =>
        Kind switch
        {
            CellKind.Empty => ".",
            CellKind.Symbol => $"{Symbol!.SymbolId}x{Symbol.StackSize}",
            CellKind.Feature => $"{Feature!.Kind}@{Feature.ChainDepth}",
            _ => "?"
        };
}

public sealed record FeatureLanding(BoardPosition Position, FeatureToken Feature);

public abstract record FeatureAction(BoardPosition SourcePosition);

public sealed record PrizeUpgradeAction(BoardPosition SourcePosition, string ObjectiveId)
    : FeatureAction(SourcePosition);

public sealed record WheelAction(BoardPosition SourcePosition, string TargetObjectiveId, int WheelValue)
    : FeatureAction(SourcePosition);

public sealed record FlushAction(BoardPosition SourcePosition, int Column)
    : FeatureAction(SourcePosition);

public sealed record ExtraSpinAction(BoardPosition SourcePosition, int SpinCount)
    : FeatureAction(SourcePosition);

public sealed record FeatureConversion(BoardPosition SourcePosition, BoardCell Replacement);

public sealed record SpawnInstruction(BoardPosition Position, BoardCell Cell);

public sealed record SpinPlan
{
    public SpinPlan(
        int spinIndex,
        IReadOnlyList<FeatureLanding> featureLandings,
        IReadOnlyList<FeatureAction> featureActions,
        IReadOnlyList<FeatureConversion> featureConversions,
        IReadOnlyList<int> pushValues,
        BoardRotation rotation,
        IReadOnlyList<SpawnInstruction> spawns)
    {
        if (spinIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spinIndex), spinIndex, "Spin index cannot be negative.");
        }

        if (pushValues.Count != EngineConstants.BoardColumns)
        {
            throw new ArgumentException("Each spin must provide one push value per column.", nameof(pushValues));
        }

        SpinIndex = spinIndex;
        FeatureLandings = featureLandings.ToArray();
        FeatureActions = featureActions.ToArray();
        FeatureConversions = featureConversions.ToArray();
        PushValues = pushValues.ToArray();
        Rotation = rotation;
        Spawns = spawns.ToArray();
    }

    public int SpinIndex { get; }

    public IReadOnlyList<FeatureLanding> FeatureLandings { get; }

    public IReadOnlyList<FeatureAction> FeatureActions { get; }

    public IReadOnlyList<FeatureConversion> FeatureConversions { get; }

    public IReadOnlyList<int> PushValues { get; }

    public BoardRotation Rotation { get; }

    public IReadOnlyList<SpawnInstruction> Spawns { get; }
}

public sealed record VerificationMetadata(
    string EngineVersion,
    int InitialSpinCount,
    string Planner,
    DateTimeOffset CreatedAt);

public sealed record GamePlan
{
    public GamePlan(
        int targetWin,
        IReadOnlyList<ObjectiveRequirement> objectives,
        PaytableConfiguration paytable,
        FeatureConfiguration featureConfiguration,
        IReadOnlyDictionary<string, PrizeLevel> plannedPrizeLevels,
        IReadOnlyDictionary<string, int> symbolThresholds,
        BoardState initialBoard,
        IReadOnlyList<SpinPlan> spins,
        IReadOnlyList<BoardState> boardStates,
        VerificationMetadata metadata)
    {
        TargetWin = targetWin;
        Objectives = objectives.ToArray();
        Paytable = paytable;
        FeatureConfiguration = featureConfiguration;
        PlannedPrizeLevels = plannedPrizeLevels.ToDictionary(
            entry => ObjectiveRequirement.NormalizeObjectiveId(entry.Key),
            entry => entry.Value,
            StringComparer.Ordinal);
        SymbolThresholds = symbolThresholds.ToDictionary(
            entry => ObjectiveRequirement.NormalizeObjectiveId(entry.Key),
            entry => entry.Value,
            StringComparer.Ordinal);
        InitialBoard = initialBoard.Clone();
        Spins = spins.ToArray();
        BoardStates = boardStates.Select(state => state.Clone()).ToArray();
        Metadata = metadata;
    }

    public int TargetWin { get; }

    public IReadOnlyList<ObjectiveRequirement> Objectives { get; }

    public PaytableConfiguration Paytable { get; }

    public FeatureConfiguration FeatureConfiguration { get; }

    public IReadOnlyDictionary<string, PrizeLevel> PlannedPrizeLevels { get; }

    public IReadOnlyDictionary<string, int> SymbolThresholds { get; }

    public BoardState InitialBoard { get; }

    public IReadOnlyList<SpinPlan> Spins { get; }

    public IReadOnlyList<BoardState> BoardStates { get; }

    public VerificationMetadata Metadata { get; }
}

public class OutcomeEngineException : Exception
{
    public OutcomeEngineException(string message)
        : base(message)
    {
    }
}

public sealed class PlanningException : OutcomeEngineException
{
    public PlanningException(string message)
        : base(message)
    {
    }
}

public sealed class SimulationException : OutcomeEngineException
{
    public SimulationException(string message)
        : base(message)
    {
    }
}
