using System.Text;

namespace CoinPusher.Engine;

public sealed class BoardState
{
    private readonly BoardCell[,] _cells;

    public BoardState()
    {
        _cells = new BoardCell[EngineConstants.BoardRows, EngineConstants.BoardColumns];
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                _cells[row, column] = BoardCell.Empty;
            }
        }
    }

    private BoardState(BoardCell[,] cells)
    {
        _cells = new BoardCell[EngineConstants.BoardRows, EngineConstants.BoardColumns];
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                _cells[row, column] = cells[row, column];
            }
        }
    }

    public static BoardState Empty() => new();

    public BoardCell Get(BoardPosition position) => _cells[position.Row, position.Column];

    public void Set(BoardPosition position, BoardCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        _cells[position.Row, position.Column] = cell;
    }

    public BoardState Clone() => new(_cells);

    public IEnumerable<(BoardPosition Position, BoardCell Cell)> Cells()
    {
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                yield return (new BoardPosition(row, column), _cells[row, column]);
            }
        }
    }

    public IReadOnlyList<(BoardPosition Position, BoardCell Cell)> PushColumn(int column, int pushValue)
    {
        if (column < 0 || column >= EngineConstants.BoardColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, "Column must be between 0 and 4.");
        }

        if (pushValue is < EngineConstants.MinimumPushValue or > EngineConstants.MaximumPushValue)
        {
            throw new SimulationException($"Push value for column {column} must be between 1 and 3.");
        }

        var collected = new List<(BoardPosition Position, BoardCell Cell)>(pushValue);
        for (var row = EngineConstants.BoardRows - pushValue; row < EngineConstants.BoardRows; row++)
        {
            collected.Add((new BoardPosition(row, column), _cells[row, column]));
        }

        for (var row = EngineConstants.BoardRows - 1; row >= pushValue; row--)
        {
            _cells[row, column] = _cells[row - pushValue, column];
        }

        for (var row = 0; row < pushValue; row++)
        {
            _cells[row, column] = BoardCell.Empty;
        }

        return collected;
    }

    public IReadOnlyList<(BoardPosition Position, BoardCell Cell)> ClearColumn(int column)
    {
        if (column < 0 || column >= EngineConstants.BoardColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, "Column must be between 0 and 4.");
        }

        var cleared = new List<(BoardPosition Position, BoardCell Cell)>(EngineConstants.BoardRows);
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            var position = new BoardPosition(row, column);
            cleared.Add((position, _cells[row, column]));
            _cells[row, column] = BoardCell.Empty;
        }

        return cleared;
    }

    public void Rotate(BoardRotation rotation)
    {
        if (rotation == BoardRotation.None)
        {
            return;
        }

        var rotated = new BoardCell[EngineConstants.BoardRows, EngineConstants.BoardColumns];
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                var target = rotation switch
                {
                    BoardRotation.Clockwise => new BoardPosition(column, EngineConstants.BoardColumns - 1 - row),
                    BoardRotation.CounterClockwise => new BoardPosition(EngineConstants.BoardRows - 1 - column, row),
                    BoardRotation.HalfTurn => new BoardPosition(EngineConstants.BoardRows - 1 - row, EngineConstants.BoardColumns - 1 - column),
                    _ => throw new SimulationException($"Unsupported board rotation '{rotation}'.")
                };

                rotated[target.Row, target.Column] = _cells[row, column];
            }
        }

        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                _cells[row, column] = rotated[row, column] ?? BoardCell.Empty;
            }
        }
    }

    public void Validate()
    {
        foreach (var (position, cell) in Cells())
        {
            switch (cell.Kind)
            {
                case CellKind.Empty:
                    if (cell.Symbol is not null || cell.Feature is not null)
                    {
                        throw new SimulationException($"Empty cell {position} cannot carry symbol or feature data.");
                    }

                    break;
                case CellKind.Symbol:
                    if (cell.Symbol is null)
                    {
                        throw new SimulationException($"Symbol cell {position} is missing symbol data.");
                    }

                    if (cell.Symbol.StackSize is <= 0 or > EngineConstants.MaximumStackSize)
                    {
                        throw new SimulationException($"Symbol cell {position} has invalid stack size {cell.Symbol.StackSize}.");
                    }

                    break;
                case CellKind.Feature:
                    if (cell.Feature is null)
                    {
                        throw new SimulationException($"Feature cell {position} is missing feature data.");
                    }

                    if (cell.Feature.ChainDepth < 0)
                    {
                        throw new SimulationException($"Feature cell {position} has invalid negative chain depth.");
                    }

                    break;
                default:
                    throw new SimulationException($"Cell {position} has unsupported kind '{cell.Kind}'.");
            }
        }
    }

    public bool ValueEquals(BoardState? other)
    {
        if (other is null)
        {
            return false;
        }

        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                if (!Equals(_cells[row, column], other._cells[row, column]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            if (row > 0)
            {
                builder.AppendLine();
            }

            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                if (column > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append(_cells[row, column]);
            }
        }

        return builder.ToString();
    }
}
