namespace CoinPusherEngine;

internal static class Grid
{
    internal static Cell?[,] Clone(Cell?[,] src)
    {
        var d = new Cell?[K.ROWS, K.COLS];
        for (int r = 0; r < K.ROWS; r++)
        for (int c = 0; c < K.COLS; c++)
            d[r, c] = src[r, c]?.Clone();
        return d;
    }

    // CW: result[col, ROWS-1-row] = board[row, col]
    internal static Cell?[,] RotCW(Cell?[,] b)
    {
        var r = new Cell?[K.ROWS, K.COLS];
        for (int row = 0; row < K.ROWS; row++)
        for (int col = 0; col < K.COLS; col++)
            r[col, K.ROWS - 1 - row] = b[row, col]?.Clone();
        return r;
    }

    // CCW (inverse of CW): result[row, col] = board[col, ROWS-1-row]
    internal static Cell?[,] RotCCW(Cell?[,] b)
    {
        var r = new Cell?[K.ROWS, K.COLS];
        for (int row = 0; row < K.ROWS; row++)
        for (int col = 0; col < K.COLS; col++)
            r[row, col] = b[col, K.ROWS - 1 - row]?.Clone();
        return r;
    }

    // Bottom `push` row indices collected during a normal push
    internal static int[] ZoneRows(int push) =>
        Enumerable.Range(K.ROWS - push, push).ToArray();

    // All (row,col) positions collected this spin
    internal static HashSet<(int r, int c)> ZoneSet(int[] push, bool[] flush)
    {
        var s = new HashSet<(int, int)>();
        for (int col = 0; col < K.COLS; col++)
        {
            if (flush[col]) { for (int r = 0; r < K.ROWS; r++) s.Add((r, col)); }
            else            { foreach (int r in ZoneRows(push[col])) s.Add((r, col)); }
        }
        return s;
    }

    internal static Cell Norm(int sym) => new() { Sym = sym };
    internal static Cell Fill(int[] fills, Random rng) => Norm(fills[rng.Next(fills.Length)]);
    internal static Cell Feat(int featSym, int cvt, FP fp) =>
        new() { Sym = featSym, IsFeat = true, FeatId = fp.FeatId, CvtSym = cvt, Fp = fp };
}
