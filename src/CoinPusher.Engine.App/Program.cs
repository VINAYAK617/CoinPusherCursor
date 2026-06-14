using CoinPusher.Engine;

var traceEnabled = !args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
var jsonEnabled = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
var featureDemoEnabled = args.Contains("--feature-demo", StringComparer.OrdinalIgnoreCase);
IEngineTraceSink trace = traceEnabled ? new ConsoleEngineTraceSink() : NullEngineTraceSink.Instance;

if (jsonEnabled)
{
    trace = NullEngineTraceSink.Instance;
}
else
{
    Console.WriteLine("Coin Pusher Outcome Engine");
    Console.WriteLine("==========================");
    Console.WriteLine(traceEnabled
        ? "Trace: enabled. Use --quiet to print only the final summary."
        : "Trace: disabled.");
    Console.WriteLine();
}

var plan = featureDemoEnabled
    ? FeatureDemoPlanFactory.Create()
    : new OutcomePlanner(trace: trace).Generate(CreateDemoRequest());
var verifier = new GamePlanVerifier(featureDemoEnabled ? new CoinPusherSimulator(trace) : null);
var report = verifier.Verify(plan);

if (jsonEnabled)
{
    if (!report.IsValid)
    {
        Console.Error.WriteLine("Generated plan failed verification.");
        foreach (var issue in report.Issues)
        {
            Console.Error.WriteLine($"- {issue.Code}: {issue.Message}");
        }

        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine(new GamePlanJsonExporter().Export(plan));
    return;
}

Console.WriteLine();
Console.WriteLine("Final Result");
Console.WriteLine("------------");
Console.WriteLine($"Verifier : {(report.IsValid ? "PASS" : "FAIL")}");
Console.WriteLine($"Spins    : {plan.Spins.Count}");
Console.WriteLine($"Target   : {plan.TargetWin}");

if (report.SimulationResult is not null)
{
    Console.WriteLine($"Payout   : {report.SimulationResult.FinalPayout}");
    Console.WriteLine($"Counts   : {BoardFormatter.FormatCounts(report.SimulationResult.CollectionCounts)}");
    Console.WriteLine($"Prizes   : {BoardFormatter.FormatPrizeLevels(report.SimulationResult.PrizeLevels)}");
}

if (!report.IsValid)
{
    Console.WriteLine();
    Console.WriteLine("Issues:");
    foreach (var issue in report.Issues)
    {
        Console.WriteLine($"- {issue.Code}: {issue.Message}");
    }

    Environment.ExitCode = 1;
}

static OutcomeRequest CreateDemoRequest() =>
    OutcomeRequest.Create(
        100,
        new[]
        {
            new ObjectiveRequirement("A", 30),
            new ObjectiveRequirement("B", 30),
            new ObjectiveRequirement("C", 20),
            new ObjectiveRequirement("D", 15)
        },
        new PaytableConfiguration(new Dictionary<string, PrizeTableEntry>
        {
            ["A"] = new(25, 25, 25),
            ["B"] = new(25, 25, 25),
            ["C"] = new(25, 25, 25),
            ["D"] = new(25, 25, 25)
        }),
        symbolThresholds: new Dictionary<string, int>
        {
            ["A"] = 30,
            ["B"] = 30,
            ["C"] = 20,
            ["D"] = 15,
            ["E"] = 25,
            ["F"] = 20,
            ["G"] = 18,
            ["H"] = 18,
            ["I"] = 18,
            ["J"] = 18
        });
