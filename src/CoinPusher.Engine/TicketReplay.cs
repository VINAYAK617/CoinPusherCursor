using System.Text.Json.Serialization;

namespace CoinPusher.Engine;

public static class TicketSymbolIds
{
    public const int MinimumSymbolId = 1;
    public const int MaximumSymbolId = 10;
    public const int Wheel = 11;
    public const int ExtraSpin = 12;
    public const int Flush = 13;
    public const int PrizeUpgrade = 14;
    public const int FillerCollectionCap = 20;
}

public sealed record CoinPusherTicket(
    [property: JsonPropertyName("startingBoard")] IReadOnlyList<IReadOnlyList<TicketCell>> StartingBoard,
    [property: JsonPropertyName("winInfo")] TicketWinInfo WinInfo,
    [property: JsonPropertyName("turns")] IReadOnlyList<TicketTurn> Turns);

public sealed record TicketWinInfo(
    [property: JsonPropertyName("totalSpins")] int TotalSpins,
    [property: JsonPropertyName("winSymbols")] IReadOnlyList<TicketWinSymbol> WinSymbols,
    [property: JsonPropertyName("prizeTiers")] IReadOnlyList<TicketPrizeTier> PrizeTiers);

public sealed record TicketWinSymbol(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("target")] int Target);

public sealed record TicketPrizeTier(
    [property: JsonPropertyName("symId")] int SymbolId,
    [property: JsonPropertyName("tier")] int Tier);

public sealed record TicketTurn(
    [property: JsonPropertyName("pushers")] IReadOnlyList<TicketPusher> Pushers,
    [property: JsonPropertyName("spawns")] IReadOnlyList<TicketCell> Spawns);

public sealed record TicketPusher
{
    [JsonConstructor]
    public TicketPusher(int pushValue, int? featureId = null)
    {
        PushValue = pushValue;
        FeatureId = featureId;
    }

    [JsonPropertyName("pushValue")]
    public int PushValue { get; }

    [JsonPropertyName("featureId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FeatureId { get; }
}

public sealed record TicketCell
{
    [JsonConstructor]
    public TicketCell(int id, int? stack = null, TicketFeature? feature = null)
    {
        Id = id;
        Stack = stack;
        Feature = feature;
    }

    [JsonPropertyName("id")]
    public int Id { get; }

    [JsonPropertyName("stack")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Stack { get; }

    [JsonPropertyName("feature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TicketFeature? Feature { get; }

    [JsonIgnore]
    public int StackValue => Stack ?? 1;

    public TicketCell WithStackValue(int stackValue) =>
        new(Id, stackValue == 1 ? null : stackValue, Feature);

    public TicketCell WithoutFeature(int replacementId) => new(replacementId);
}

public sealed record TicketFeature
{
    [JsonConstructor]
    public TicketFeature(
        int featureId,
        int convertToId,
        int? wheelSymbolId = null,
        int? wheelStackMultiplier = null,
        int? upgradeSymbolId = null,
        int? upgradePrizeTier = null)
    {
        FeatureId = featureId;
        ConvertToId = convertToId;
        WheelSymbolId = wheelSymbolId;
        WheelStackMultiplier = wheelStackMultiplier;
        UpgradeSymbolId = upgradeSymbolId;
        UpgradePrizeTier = upgradePrizeTier;
    }

    [JsonPropertyName("featureId")]
    public int FeatureId { get; }

    [JsonPropertyName("convertToId")]
    public int ConvertToId { get; }

    [JsonPropertyName("wheelSymbolId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WheelSymbolId { get; }

    [JsonPropertyName("wheelStackMultiplier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WheelStackMultiplier { get; }

    [JsonPropertyName("upgradeSymbolId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UpgradeSymbolId { get; }

    [JsonPropertyName("upgradePrizeTier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UpgradePrizeTier { get; }
}

public sealed record TicketCollectionEvent(
    int TurnIndex,
    int SymbolId,
    int Amount,
    bool IsWinSymbol,
    CollectionSource Source,
    BoardPosition Position);

public sealed record TicketFeatureFireEvent(int TurnIndex, BoardPosition Position, int FeatureId);

public sealed record TicketBoardSnapshot
{
    public TicketBoardSnapshot(IReadOnlyList<IReadOnlyList<TicketCell?>> rows)
    {
        Rows = rows.Select(row => row.ToArray()).ToArray();
    }

    public IReadOnlyList<IReadOnlyList<TicketCell?>> Rows { get; }

    public TicketCell? Get(BoardPosition position) => Rows[position.Row][position.Column];
}

public sealed record TicketTurnReplay(
    int TurnIndex,
    TicketBoardSnapshot StartBoard,
    TicketBoardSnapshot AfterStaleTokenFlattening,
    TicketBoardSnapshot AfterCollection,
    TicketBoardSnapshot AfterRotation,
    TicketBoardSnapshot AfterSpawn,
    TicketBoardSnapshot EndBoard,
    IReadOnlyList<TicketCollectionEvent> Collections,
    IReadOnlyList<TicketFeatureFireEvent> FiredFeatures);

public sealed record TicketReplayResult(
    IReadOnlyDictionary<int, int> WinSymbolCounts,
    IReadOnlyDictionary<int, int> FillerSymbolCounts,
    IReadOnlyDictionary<int, int> PrizeTiers,
    IReadOnlyList<TicketTurnReplay> Turns,
    IReadOnlyList<TicketBoardSnapshot> BoardTimeline,
    TicketBoardSnapshot FinalBoard);

public sealed class CoinPusherTicketReplayEngine
{
    private const int MaximumFeatureFireIterations = EngineConstants.BoardRows * EngineConstants.BoardColumns;

    public TicketReplayResult Replay(CoinPusherTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var context = TicketValidationContext.Create(ticket);
        ValidateTicket(ticket, context);

        var board = CreateBoard(ticket.StartingBoard);
        var winCounts = context.WinTargets.Keys.ToDictionary(id => id, _ => 0);
        var fillerCounts = new Dictionary<int, int>();
        var turnResults = new List<TicketTurnReplay>(ticket.Turns.Count);
        var timeline = new List<TicketBoardSnapshot> { Snapshot(board) };

        for (var turnIndex = 0; turnIndex < ticket.Turns.Count; turnIndex++)
        {
            var turn = ticket.Turns[turnIndex];
            var startBoard = Snapshot(board);

            FlattenStaleTokens(board);
            var afterFlattening = Snapshot(board);

            var collections = ApplyCollection(board, turnIndex, turn, context, winCounts, fillerCounts);
            var afterCollection = Snapshot(board);

            RotateClockwise(board);
            var afterRotation = Snapshot(board);

            ApplySpawns(board, turn, context);
            var afterSpawn = Snapshot(board);

            var firedFeatures = FireFeatureTokens(board, turnIndex, ticket, context);
            var endBoard = Snapshot(board);

            EnsureBoardIsFull(board, $"end of turn {turnIndex}");

            turnResults.Add(new TicketTurnReplay(
                turnIndex,
                startBoard,
                afterFlattening,
                afterCollection,
                afterRotation,
                afterSpawn,
                endBoard,
                collections,
                firedFeatures));
            timeline.Add(endBoard);
        }

        return new TicketReplayResult(
            winCounts,
            fillerCounts,
            context.PrizeTiers,
            turnResults,
            timeline,
            timeline[^1]);
    }

    private static void ValidateTicket(CoinPusherTicket ticket, TicketValidationContext context)
    {
        ValidateStartingBoard(ticket.StartingBoard, context);

        if (ticket.WinInfo.TotalSpins != ticket.Turns.Count)
        {
            throw new SimulationException($"winInfo.totalSpins must equal turns length. Expected {ticket.WinInfo.TotalSpins}, got {ticket.Turns.Count}.");
        }

        for (var turnIndex = 0; turnIndex < ticket.Turns.Count; turnIndex++)
        {
            ValidateTurn(ticket.Turns[turnIndex], turnIndex, context);
        }
    }

    private static void ValidateStartingBoard(IReadOnlyList<IReadOnlyList<TicketCell>> startingBoard, TicketValidationContext context)
    {
        if (startingBoard.Count != EngineConstants.BoardRows)
        {
            throw new SimulationException($"startingBoard must contain exactly {EngineConstants.BoardRows} rows.");
        }

        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            if (startingBoard[row].Count != EngineConstants.BoardColumns)
            {
                throw new SimulationException($"startingBoard row {row} must contain exactly {EngineConstants.BoardColumns} cells.");
            }

            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                var cell = startingBoard[row][column];
                ValidateCell(cell, $"startingBoard[{row}][{column}]", context, allowFeatureToken: false);
            }
        }
    }

    private static void ValidateTurn(TicketTurn turn, int turnIndex, TicketValidationContext context)
    {
        if (turn.Pushers.Count != EngineConstants.BoardColumns)
        {
            throw new SimulationException($"turns[{turnIndex}].pushers must contain exactly {EngineConstants.BoardColumns} pushers.");
        }

        for (var column = 0; column < EngineConstants.BoardColumns; column++)
        {
            ValidatePusher(turn.Pushers[column], turnIndex, column);
        }

        for (var spawnIndex = 0; spawnIndex < turn.Spawns.Count; spawnIndex++)
        {
            ValidateCell(turn.Spawns[spawnIndex], $"turns[{turnIndex}].spawns[{spawnIndex}]", context, allowFeatureToken: true);
        }
    }

    private static void ValidatePusher(TicketPusher pusher, int turnIndex, int column)
    {
        if (pusher.FeatureId is null)
        {
            if (pusher.PushValue is < 0 or > EngineConstants.BoardRows)
            {
                throw new SimulationException($"turns[{turnIndex}].pushers[{column}].pushValue must be between 0 and {EngineConstants.BoardRows}.");
            }

            return;
        }

        if (pusher.FeatureId != TicketSymbolIds.Flush)
        {
            throw new SimulationException($"turns[{turnIndex}].pushers[{column}].featureId must be {TicketSymbolIds.Flush} for FLUSH pushers.");
        }

        if (pusher.PushValue != EngineConstants.BoardRows)
        {
            throw new SimulationException($"turns[{turnIndex}].pushers[{column}] FLUSH pushValue must be {EngineConstants.BoardRows}.");
        }
    }

    private static void ValidateCell(TicketCell cell, string path, TicketValidationContext context, bool allowFeatureToken)
    {
        if (cell.StackValue <= 0)
        {
            throw new SimulationException($"{path}.stack must be positive when present.");
        }

        if (IsBaseSymbol(cell.Id))
        {
            if (cell.Feature is not null)
            {
                throw new SimulationException($"{path} has symbol id {cell.Id} but also carries a feature object.");
            }

            if (cell.StackValue != 1 && !context.WinTargets.ContainsKey(cell.Id))
            {
                throw new SimulationException($"{path}.stack may only be greater than 1 for win-symbol cells.");
            }

            return;
        }

        if (!IsBoardFeatureToken(cell.Id))
        {
            throw new SimulationException($"{path}.id {cell.Id} is outside the supported symbol registry.");
        }

        if (!allowFeatureToken)
        {
            throw new SimulationException($"{path} cannot contain feature token id {cell.Id}.");
        }

        if (cell.Feature is null)
        {
            throw new SimulationException($"{path} feature token must include a feature object.");
        }

        if (cell.StackValue != 1)
        {
            throw new SimulationException($"{path} feature tokens cannot carry a stack value.");
        }

        ValidateFeature(cell.Id, cell.Feature, path, context);
    }

    private static void ValidateFeature(int tokenId, TicketFeature feature, string path, TicketValidationContext context)
    {
        if (feature.FeatureId != tokenId)
        {
            throw new SimulationException($"{path}.feature.featureId must match token id {tokenId}.");
        }

        if (!IsBaseSymbol(feature.ConvertToId) || context.WinTargets.ContainsKey(feature.ConvertToId))
        {
            throw new SimulationException($"{path}.feature.convertToId must be a non-win filler symbol id between 1 and 10.");
        }

        switch (feature.FeatureId)
        {
            case TicketSymbolIds.Wheel:
                if (feature.WheelSymbolId is null || !context.WinTargets.ContainsKey(feature.WheelSymbolId.Value))
                {
                    throw new SimulationException($"{path}.feature.wheelSymbolId must reference a declared win symbol.");
                }

                if (feature.WheelStackMultiplier is null || !IsWheelMultiplier(feature.WheelStackMultiplier.Value))
                {
                    throw new SimulationException($"{path}.feature.wheelStackMultiplier must be one of 2, 4, 8, 16, 32 or 64.");
                }

                break;
            case TicketSymbolIds.ExtraSpin:
                break;
            case TicketSymbolIds.PrizeUpgrade:
                if (feature.UpgradeSymbolId is null || !context.WinTargets.ContainsKey(feature.UpgradeSymbolId.Value))
                {
                    throw new SimulationException($"{path}.feature.upgradeSymbolId must reference a declared win symbol.");
                }

                if (feature.UpgradePrizeTier is null || feature.UpgradePrizeTier.Value < 1)
                {
                    throw new SimulationException($"{path}.feature.upgradePrizeTier must be at least 1.");
                }

                break;
            default:
                throw new SimulationException($"{path}.feature.featureId {feature.FeatureId} is not a board-token feature.");
        }
    }

    private static TicketCell?[,] CreateBoard(IReadOnlyList<IReadOnlyList<TicketCell>> rows)
    {
        var board = new TicketCell?[EngineConstants.BoardRows, EngineConstants.BoardColumns];
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                board[row, column] = rows[row][column];
            }
        }

        return board;
    }

    private static void FlattenStaleTokens(TicketCell?[,] board)
    {
        foreach (var (position, cell) in Cells(board))
        {
            if (cell is null || !IsBoardFeatureToken(cell.Id))
            {
                continue;
            }

            if (cell.Feature is null)
            {
                throw new SimulationException($"Feature token at {position} is missing its feature object.");
            }

            board[position.Row, position.Column] = cell.WithoutFeature(cell.Feature.ConvertToId);
        }
    }

    private static IReadOnlyList<TicketCollectionEvent> ApplyCollection(
        TicketCell?[,] board,
        int turnIndex,
        TicketTurn turn,
        TicketValidationContext context,
        IDictionary<int, int> winCounts,
        IDictionary<int, int> fillerCounts)
    {
        var collections = new List<TicketCollectionEvent>();
        for (var column = 0; column < EngineConstants.BoardColumns; column++)
        {
            var pusher = turn.Pushers[column];
            var pushValue = pusher.FeatureId == TicketSymbolIds.Flush
                ? EngineConstants.BoardRows
                : pusher.PushValue;

            if (pushValue == 0)
            {
                continue;
            }

            for (var row = EngineConstants.BoardRows - pushValue; row < EngineConstants.BoardRows; row++)
            {
                var position = new BoardPosition(row, column);
                var cell = board[row, column];
                if (cell is not null)
                {
                    CollectCell(turnIndex, position, cell, pusher.FeatureId == TicketSymbolIds.Flush ? CollectionSource.Flush : CollectionSource.Push, context, winCounts, fillerCounts, collections);
                }
            }

            for (var row = EngineConstants.BoardRows - 1; row >= pushValue; row--)
            {
                board[row, column] = board[row - pushValue, column];
            }

            for (var row = 0; row < pushValue; row++)
            {
                board[row, column] = null;
            }
        }

        return collections;
    }

    private static void CollectCell(
        int turnIndex,
        BoardPosition position,
        TicketCell cell,
        CollectionSource source,
        TicketValidationContext context,
        IDictionary<int, int> winCounts,
        IDictionary<int, int> fillerCounts,
        ICollection<TicketCollectionEvent> collections)
    {
        if (IsBoardFeatureToken(cell.Id))
        {
            throw new SimulationException($"Feature token id {cell.Id} at {position} cannot be collected by a pusher.");
        }

        if (context.WinTargets.TryGetValue(cell.Id, out var target))
        {
            var nextCount = winCounts[cell.Id] + cell.StackValue;
            if (nextCount > target)
            {
                throw new SimulationException($"Over-collection for win symbol {cell.Id}. Target {target}, attempted {nextCount}.");
            }

            winCounts[cell.Id] = nextCount;
            collections.Add(new TicketCollectionEvent(turnIndex, cell.Id, cell.StackValue, true, source, position));
            return;
        }

        var nextFillerCount = fillerCounts.GetValueOrDefault(cell.Id) + cell.StackValue;
        if (nextFillerCount >= TicketSymbolIds.FillerCollectionCap)
        {
            throw new SimulationException($"Filler symbol {cell.Id} reached the collection cap of {TicketSymbolIds.FillerCollectionCap}.");
        }

        fillerCounts[cell.Id] = nextFillerCount;
        collections.Add(new TicketCollectionEvent(turnIndex, cell.Id, cell.StackValue, false, source, position));
    }

    private static void RotateClockwise(TicketCell?[,] board)
    {
        var rotated = new TicketCell?[EngineConstants.BoardRows, EngineConstants.BoardColumns];
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                rotated[column, EngineConstants.BoardRows - 1 - row] = board[row, column];
            }
        }

        CopyBoard(rotated, board);
    }

    private static void ApplySpawns(TicketCell?[,] board, TicketTurn turn, TicketValidationContext context)
    {
        var emptyPositions = Cells(board)
            .Where(entry => entry.Cell is null)
            .Select(entry => entry.Position)
            .OrderBy(position => position.Row * EngineConstants.BoardColumns + position.Column)
            .ToArray();

        if (turn.Spawns.Count != emptyPositions.Length)
        {
            throw new SimulationException($"Turn spawn count mismatch. Expected {emptyPositions.Length} spawns for empty post-rotation positions, got {turn.Spawns.Count}.");
        }

        for (var index = 0; index < turn.Spawns.Count; index++)
        {
            var spawn = turn.Spawns[index];
            ValidateCell(spawn, $"spawn[{index}]", context, allowFeatureToken: true);
            var position = emptyPositions[index];
            board[position.Row, position.Column] = spawn;
        }

        EnsureBoardIsFull(board, "spawn phase");
    }

    private static IReadOnlyList<TicketFeatureFireEvent> FireFeatureTokens(
        TicketCell?[,] board,
        int turnIndex,
        CoinPusherTicket ticket,
        TicketValidationContext context)
    {
        var fired = new List<TicketFeatureFireEvent>();
        for (var iteration = 0; iteration < MaximumFeatureFireIterations; iteration++)
        {
            var nextFeature = Cells(board).FirstOrDefault(entry => entry.Cell is not null && IsBoardFeatureToken(entry.Cell.Id));
            if (nextFeature.Cell is null)
            {
                return fired;
            }

            var cell = nextFeature.Cell;
            if (cell.Feature is null)
            {
                throw new SimulationException($"Feature token id {cell.Id} at {nextFeature.Position} is missing its feature object.");
            }

            FireFeature(board, turnIndex, ticket, context, nextFeature.Position, cell);
            fired.Add(new TicketFeatureFireEvent(turnIndex, nextFeature.Position, cell.Id));
        }

        throw new SimulationException("Feature firing exceeded the maximum board-token count.");
    }

    private static void FireFeature(
        TicketCell?[,] board,
        int turnIndex,
        CoinPusherTicket ticket,
        TicketValidationContext context,
        BoardPosition position,
        TicketCell tokenCell)
    {
        var feature = tokenCell.Feature!;
        switch (feature.FeatureId)
        {
            case TicketSymbolIds.Wheel:
                FireWheel(board, turnIndex, ticket, feature);
                break;
            case TicketSymbolIds.ExtraSpin:
            case TicketSymbolIds.PrizeUpgrade:
                break;
            default:
                throw new SimulationException($"Feature id {feature.FeatureId} cannot fire as a board token.");
        }

        board[position.Row, position.Column] = tokenCell.WithoutFeature(feature.ConvertToId);
        ValidateFeature(tokenCell.Id, feature, $"feature at {position}", context);
    }

    private static void FireWheel(TicketCell?[,] board, int turnIndex, CoinPusherTicket ticket, TicketFeature feature)
    {
        var targetSymbolId = feature.WheelSymbolId!.Value;
        var multiplier = feature.WheelStackMultiplier!.Value;

        foreach (var (position, cell) in Cells(board).ToArray())
        {
            if (cell is null || cell.Id != targetSymbolId || cell.Feature is not null)
            {
                continue;
            }

            checked
            {
                board[position.Row, position.Column] = cell.WithStackValue(cell.StackValue * multiplier);
            }
        }

        ApplyPostWheelIsolation(board, turnIndex, ticket, targetSymbolId, feature.ConvertToId);
    }

    private static void ApplyPostWheelIsolation(
        TicketCell?[,] board,
        int turnIndex,
        CoinPusherTicket ticket,
        int targetSymbolId,
        int fallbackFillerId)
    {
        var nextTurn = turnIndex + 1 < ticket.Turns.Count ? ticket.Turns[turnIndex + 1] : null;
        foreach (var (position, cell) in Cells(board).ToArray())
        {
            if (cell is null || cell.Id != targetSymbolId || cell.Feature is not null)
            {
                continue;
            }

            if (nextTurn is not null && IsInZone(position, nextTurn))
            {
                continue;
            }

            board[position.Row, position.Column] = new TicketCell(fallbackFillerId);
        }
    }

    private static bool IsInZone(BoardPosition position, TicketTurn turn)
    {
        var pusher = turn.Pushers[position.Column];
        var pushValue = pusher.FeatureId == TicketSymbolIds.Flush
            ? EngineConstants.BoardRows
            : pusher.PushValue;

        return pushValue > 0 && position.Row >= EngineConstants.BoardRows - pushValue;
    }

    private static void EnsureBoardIsFull(TicketCell?[,] board, string phase)
    {
        var empty = Cells(board).FirstOrDefault(entry => entry.Cell is null);
        if (empty.Cell is null && HasNullAt(board, empty.Position))
        {
            throw new SimulationException($"Board contains an empty cell at {empty.Position} after {phase}.");
        }
    }

    private static bool HasNullAt(TicketCell?[,] board, BoardPosition position) =>
        board[position.Row, position.Column] is null;

    private static TicketBoardSnapshot Snapshot(TicketCell?[,] board)
    {
        var rows = new TicketCell?[EngineConstants.BoardRows][];
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            rows[row] = new TicketCell?[EngineConstants.BoardColumns];
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                rows[row][column] = board[row, column];
            }
        }

        return new TicketBoardSnapshot(rows);
    }

    private static IEnumerable<(BoardPosition Position, TicketCell? Cell)> Cells(TicketCell?[,] board)
    {
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                yield return (new BoardPosition(row, column), board[row, column]);
            }
        }
    }

    private static void CopyBoard(TicketCell?[,] source, TicketCell?[,] target)
    {
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                target[row, column] = source[row, column];
            }
        }
    }

    private static bool IsBaseSymbol(int id) =>
        id is >= TicketSymbolIds.MinimumSymbolId and <= TicketSymbolIds.MaximumSymbolId;

    private static bool IsBoardFeatureToken(int id) =>
        id is TicketSymbolIds.Wheel or TicketSymbolIds.ExtraSpin or TicketSymbolIds.PrizeUpgrade;

    private static bool IsWheelMultiplier(int value) =>
        value is 2 or 4 or 8 or 16 or 32 or 64;

    private sealed record TicketValidationContext(
        IReadOnlyDictionary<int, int> WinTargets,
        IReadOnlyDictionary<int, int> PrizeTiers)
    {
        public static TicketValidationContext Create(CoinPusherTicket ticket)
        {
            if (ticket.WinInfo.WinSymbols.Count is < 1 or > 5)
            {
                throw new SimulationException("winInfo.winSymbols must contain between 1 and 5 entries.");
            }

            var winTargets = new Dictionary<int, int>();
            foreach (var winSymbol in ticket.WinInfo.WinSymbols)
            {
                if (!IsBaseSymbol(winSymbol.Id))
                {
                    throw new SimulationException($"Win symbol id {winSymbol.Id} must be between 1 and 10.");
                }

                if (winSymbol.Target < 0)
                {
                    throw new SimulationException($"Win symbol {winSymbol.Id} target cannot be negative.");
                }

                if (!winTargets.TryAdd(winSymbol.Id, winSymbol.Target))
                {
                    throw new SimulationException($"Duplicate win symbol id {winSymbol.Id}.");
                }
            }

            var prizeTiers = winTargets.Keys.ToDictionary(id => id, _ => 0);
            foreach (var prizeTier in ticket.WinInfo.PrizeTiers)
            {
                if (!winTargets.ContainsKey(prizeTier.SymbolId))
                {
                    throw new SimulationException($"Prize tier override references unknown win symbol {prizeTier.SymbolId}.");
                }

                if (prizeTier.Tier < 1)
                {
                    throw new SimulationException($"Prize tier override for symbol {prizeTier.SymbolId} must be at least 1.");
                }

                prizeTiers[prizeTier.SymbolId] = prizeTier.Tier;
            }

            return new TicketValidationContext(winTargets, prizeTiers);
        }
    }
}

public sealed record TicketVerificationReport(
    bool IsValid,
    IReadOnlyList<VerificationIssue> Issues,
    TicketReplayResult? ReplayResult);

public sealed class CoinPusherTicketVerifier
{
    private readonly CoinPusherTicketReplayEngine _replayEngine;

    public CoinPusherTicketVerifier(CoinPusherTicketReplayEngine? replayEngine = null)
    {
        _replayEngine = replayEngine ?? new CoinPusherTicketReplayEngine();
    }

    public TicketVerificationReport Verify(CoinPusherTicket ticket)
    {
        var issues = new List<VerificationIssue>();
        TicketReplayResult? result = null;

        try
        {
            result = _replayEngine.Replay(ticket);
            VerifyExactTargets(ticket, result, issues);
        }
        catch (OutcomeEngineException exception)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Error, "ticket_replay_failed", exception.Message));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Error, "invalid_ticket", exception.Message));
        }

        return new TicketVerificationReport(issues.Count == 0, issues, result);
    }

    private static void VerifyExactTargets(
        CoinPusherTicket ticket,
        TicketReplayResult result,
        ICollection<VerificationIssue> issues)
    {
        foreach (var winSymbol in ticket.WinInfo.WinSymbols)
        {
            var actual = result.WinSymbolCounts[winSymbol.Id];
            if (actual != winSymbol.Target)
            {
                issues.Add(new VerificationIssue(
                    VerificationSeverity.Error,
                    "ticket_objective_mismatch",
                    $"Win symbol {winSymbol.Id} expected {winSymbol.Target}, collected {actual}."));
            }
        }
    }
}
