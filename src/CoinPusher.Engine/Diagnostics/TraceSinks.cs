namespace CoinPusher.Engine;

public sealed class NullEngineTraceSink : IEngineTraceSink
{
    public static NullEngineTraceSink Instance { get; } = new();

    private NullEngineTraceSink()
    {
    }

    public bool IsEnabled => false;

    public void Write(string message)
    {
    }

    public void WriteBoard(string title, BoardState board)
    {
    }
}

public sealed class ConsoleEngineTraceSink : IEngineTraceSink
{
    public bool IsEnabled => true;

    public void Write(string message) => Console.WriteLine(message);

    public void WriteBoard(string title, BoardState board)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
        Console.WriteLine(BoardFormatter.Format(board));
        Console.WriteLine();
    }
}
