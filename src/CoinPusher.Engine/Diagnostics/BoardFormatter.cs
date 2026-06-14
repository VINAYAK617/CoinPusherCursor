using System.Text;

namespace CoinPusher.Engine;

public static class BoardFormatter
{
    public static string Format(BoardState board)
    {
        ArgumentNullException.ThrowIfNull(board);

        var builder = new StringBuilder();
        builder.AppendLine("      Pushers");
        builder.AppendLine();
        builder.AppendLine("    C0       C1       C2       C3       C4");

        for (var row = 0; row < EngineConstants.BoardRows; row++)
        {
            builder.Append($"R{row} ");
            for (var column = 0; column < EngineConstants.BoardColumns; column++)
            {
                var cell = board.Get(new BoardPosition(row, column)).ToString();
                builder.Append(cell.PadRight(8));
            }

            builder.AppendLine();
        }

        builder.AppendLine();
        builder.Append("      Collection");
        return builder.ToString();
    }

    public static string FormatCounts(IReadOnlyDictionary<string, int> counts) =>
        string.Join(", ", counts.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={entry.Value}"));

    public static string FormatPrizeLevels(IReadOnlyDictionary<string, PrizeLevel> prizeLevels) =>
        string.Join(", ", prizeLevels.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={entry.Value}"));
}
