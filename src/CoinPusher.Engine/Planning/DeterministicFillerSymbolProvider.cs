namespace CoinPusher.Engine;

public sealed class DeterministicFillerSymbolProvider : IFillerSymbolProvider
{
    private static readonly string[] Symbols = ["E", "F", "G", "H", "I", "J"];

    public BoardCell CreateFillerCell(int spinIndex, BoardPosition position)
    {
        var index = Math.Abs((spinIndex * 17) + (position.Row * 7) + (position.Column * 3)) % Symbols.Length;
        return BoardCell.FromFillerSymbol(Symbols[index]);
    }
}
