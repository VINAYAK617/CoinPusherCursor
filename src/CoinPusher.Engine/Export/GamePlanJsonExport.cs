using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoinPusher.Engine;

public sealed record ExportIdMap(
    IReadOnlyDictionary<string, int> SymbolIds,
    IReadOnlyDictionary<FeatureKind, int> FeatureIds)
{
    public static ExportIdMap Default { get; } = new(
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["A"] = 1,
            ["B"] = 2,
            ["C"] = 3,
            ["D"] = 4,
            ["E"] = 5,
            ["F"] = 6,
            ["G"] = 7,
            ["H"] = 8,
            ["I"] = 9,
            ["J"] = 10,
            ["GOLD"] = 1,
            ["STAR"] = 2,
            ["BELL"] = 5
        },
        new Dictionary<FeatureKind, int>
        {
            [FeatureKind.Wheel] = 11,
            [FeatureKind.ExtraSpin] = 12,
            [FeatureKind.PrizeUpgrade] = 13,
            [FeatureKind.Flush] = 14
        });
}

public sealed class GamePlanJsonExporter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ExportIdMap _idMap;

    public GamePlanJsonExporter(ExportIdMap? idMap = null)
    {
        _idMap = idMap ?? ExportIdMap.Default;
    }

    public string Export(GamePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var dto = ToDto(plan);
        return JsonSerializer.Serialize(dto, SerializerOptions);
    }

    public ExportGamePlanDto ToDto(GamePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new ExportGamePlanDto
        {
            StartingBoard = ExportBoard(plan.InitialBoard),
            Turns = plan.Spins.Select(ExportTurn).ToArray()
        };
    }

    private IReadOnlyList<IReadOnlyList<ExportCellDto>> ExportBoard(BoardState board)
    {
        var rows = new List<IReadOnlyList<ExportCellDto>>(EngineConstants.BoardRows);
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            var cells = new List<ExportCellDto>(EngineConstants.BoardColumns);
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                cells.Add(ExportCell(board.Get(new BoardPosition(row, column))));
            }

            rows.Add(cells);
        }

        return rows;
    }

    private ExportTurnDto ExportTurn(SpinPlan spin)
    {
        var flushColumns = spin.FeatureActions
            .OfType<FlushAction>()
            .ToDictionary(action => action.Column, action => action, EqualityComparer<int>.Default);

        return new ExportTurnDto
        {
            Pushers = Enumerable
                .Range(0, EngineConstants.BoardColumns)
                .Select(column => ExportPusher(spin.PushValues[column], flushColumns.GetValueOrDefault(column)))
                .ToArray(),
            Spawns = ExportSpawnsAndFeatureLandings(spin)
        };
    }

    private ExportPusherDto ExportPusher(int pushValue, FlushAction? flushAction)
    {
        if (flushAction is null)
        {
            return new ExportPusherDto { PushValue = pushValue };
        }

        return new ExportPusherDto
        {
            PushValue = EngineConstants.BoardRows,
            FeatureId = GetFeatureId(FeatureKind.Flush)
        };
    }

    private IReadOnlyList<ExportSpawnDto> ExportSpawnsAndFeatureLandings(SpinPlan spin)
    {
        var spawns = new List<ExportSpawnDto>();

        foreach (var spawn in spin.Spawns.OrderBy(spawn => ToLinearPosition(spawn.Position)))
        {
            spawns.Add(ExportSpawn(spawn.Position, spawn.Cell, spin));
        }

        // Feature landings are exported as protocol spawn entries with feature metadata
        // so the client can animate "feature lands -> activates -> converts" in the turn.
        foreach (var landing in spin.FeatureLandings.OrderBy(landing => ToLinearPosition(landing.Position)))
        {
            var conversion = spin.FeatureConversions.FirstOrDefault(item => item.SourcePosition == landing.Position);
            var action = spin.FeatureActions.FirstOrDefault(item => item.SourcePosition == landing.Position);
            spawns.Add(ExportFeatureLanding(landing, conversion, action));
        }

        return spawns
            .GroupBy(spawn => spawn.Pos)
            .Select(group => group.Last())
            .OrderBy(spawn => spawn.Pos)
            .ToArray();
    }

    private ExportSpawnDto ExportSpawn(BoardPosition position, BoardCell cell, SpinPlan spin)
    {
        if (cell.Kind == CellKind.Feature)
        {
            var landing = new FeatureLanding(position, cell.Feature!);
            var conversion = spin.FeatureConversions.FirstOrDefault(item => item.SourcePosition == position);
            var action = spin.FeatureActions.FirstOrDefault(item => item.SourcePosition == position);
            return ExportFeatureLanding(landing, conversion, action);
        }

        return new ExportSpawnDto
        {
            Pos = ToLinearPosition(position),
            Id = GetCellId(cell)
        };
    }

    private ExportSpawnDto ExportFeatureLanding(
        FeatureLanding landing,
        FeatureConversion? conversion,
        FeatureAction? action)
    {
        var featureId = GetFeatureId(landing.Feature.Kind);
        return new ExportSpawnDto
        {
            Pos = ToLinearPosition(landing.Position),
            Id = featureId,
            Feature = ExportFeature(landing.Feature.Kind, featureId, conversion, action)
        };
    }

    private ExportFeatureDto ExportFeature(
        FeatureKind kind,
        int featureId,
        FeatureConversion? conversion,
        FeatureAction? action)
    {
        var dto = new ExportFeatureDto
        {
            FeatureId = featureId,
            ConvertToId = conversion is null ? null : GetCellId(conversion.Replacement),
            FReTrigger = []
        };

        switch (action)
        {
            case WheelAction wheel:
                dto.WheelSymbolId = GetSymbolId(wheel.TargetObjectiveId);
                dto.WheelStackMultiplier = 1 + wheel.WheelValue;
                break;
            case PrizeUpgradeAction:
                dto.UPrize = conversion?.Replacement.Symbol?.StackSize;
                break;
            case FlushAction flush:
                dto.Column = flush.Column;
                break;
            case ExtraSpinAction extraSpin:
                dto.ExtraSpins = extraSpin.SpinCount;
                break;
        }

        return dto;
    }

    private ExportCellDto ExportCell(BoardCell cell)
    {
        var dto = new ExportCellDto { Id = GetCellId(cell) };
        if (cell.Kind == CellKind.Symbol && cell.Symbol!.StackSize > 1)
        {
            dto.Stack = cell.Symbol.StackSize;
        }

        return dto;
    }

    private int GetCellId(BoardCell cell) =>
        cell.Kind switch
        {
            CellKind.Empty => 0,
            CellKind.Symbol => GetSymbolId(cell.Symbol!.SymbolId),
            CellKind.Feature => GetFeatureId(cell.Feature!.Kind),
            _ => throw new InvalidOperationException($"Unsupported cell kind '{cell.Kind}'.")
        };

    private int GetSymbolId(string symbolId)
    {
        var normalized = ObjectiveRequirement.NormalizeObjectiveId(symbolId);
        if (_idMap.SymbolIds.TryGetValue(normalized, out var id))
        {
            return id;
        }

        throw new KeyNotFoundException($"No export id mapping exists for symbol '{normalized}'.");
    }

    private int GetFeatureId(FeatureKind featureKind)
    {
        if (_idMap.FeatureIds.TryGetValue(featureKind, out var id))
        {
            return id;
        }

        throw new KeyNotFoundException($"No export id mapping exists for feature '{featureKind}'.");
    }

    private static int ToLinearPosition(BoardPosition position) =>
        (position.Row * EngineConstants.BoardColumns) + position.Column;
}

public sealed class ExportGamePlanDto
{
    [JsonPropertyName("startingBoard")]
    public IReadOnlyList<IReadOnlyList<ExportCellDto>> StartingBoard { get; init; } = [];

    [JsonPropertyName("turns")]
    public IReadOnlyList<ExportTurnDto> Turns { get; init; } = [];
}

public sealed class ExportCellDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("stack")]
    public int? Stack { get; set; }
}

public sealed class ExportTurnDto
{
    [JsonPropertyName("pushers")]
    public IReadOnlyList<ExportPusherDto> Pushers { get; init; } = [];

    [JsonPropertyName("spawns")]
    public IReadOnlyList<ExportSpawnDto> Spawns { get; init; } = [];
}

public sealed class ExportPusherDto
{
    [JsonPropertyName("pushValue")]
    public int PushValue { get; init; }

    [JsonPropertyName("featureId")]
    public int? FeatureId { get; init; }
}

public sealed class ExportSpawnDto
{
    [JsonPropertyName("Pos")]
    public int Pos { get; init; }

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("feature")]
    public ExportFeatureDto? Feature { get; init; }
}

public sealed class ExportFeatureDto
{
    [JsonPropertyName("featureId")]
    public int FeatureId { get; init; }

    [JsonPropertyName("convertToId")]
    public int? ConvertToId { get; init; }

    [JsonPropertyName("wheelSymbolId")]
    public int? WheelSymbolId { get; set; }

    [JsonPropertyName("wheelStackMultiplier")]
    public int? WheelStackMultiplier { get; set; }

    [JsonPropertyName("uPrize")]
    public int? UPrize { get; set; }

    [JsonPropertyName("column")]
    public int? Column { get; set; }

    [JsonPropertyName("extraSpins")]
    public int? ExtraSpins { get; set; }

    [JsonPropertyName("fReTrigger")]
    public IReadOnlyList<ExportFeatureDto> FReTrigger { get; init; } = [];
}
