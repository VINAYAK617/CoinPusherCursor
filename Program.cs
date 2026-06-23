using CoinPusherEngine;
using System.Diagnostics;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length > 0 && args[0] == "selftest") { StressTest.Run(); return; }
if (args.Length > 0 && args[0] == "volume")
{
    int count = args.Length > 1 && int.TryParse(args[1], out var parsedCount) ? parsedCount : 1000;
    int seed = args.Length > 2 && int.TryParse(args[2], out var parsedSeed) ? parsedSeed : 20260623;
    RunVolumeTest(count, seed);
    return;
}

var rows = new List<PrizeLadderRow>
{
    new() { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
    new() { Target = 20, Tiers = new decimal[] { 2, 4, 8 } },
    new() { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
    new() { Target = 25, Tiers = new decimal[] { 10, 20, 50 } },
    new() { Target = 25, Tiers = new decimal[] { 100, 200, 500 } },
    new() { Target = 30, Tiers = new decimal[] { 10000 } },
};

var bundle = new LadderCombinator(rows).Bundle(new decimal[] { 1, 50});
Console.WriteLine($"Covered: [${string.Join(",",bundle.Covered.Select(a=>$"${a}"))}]");
Console.WriteLine($"Entries: [{string.Join(";",bundle.Entries.Select(e=>$"sym{e.Sym}@tier{e.Tier}"))}]");
Console.WriteLine($"Targets: [{string.Join(",",bundle.Input.Targets.Select(kv=>$"sym{kv.Key}={kv.Value}"))}]");
Console.WriteLine($"BaseSpins: {bundle.Input.BaseSpins}");

var plan = new Planner(bundle.Input, seed: null).Plan();
var json = TicketSerializer.ToJson(plan);
Console.WriteLine(json);

static void RunVolumeTest(int count, int seed)
{
    var rng = new Random(seed);
    var failures = new List<string>();
    var spinCounts = new Dictionary<int, int>();
    var featureCounts = new Dictionary<int, int>();
    var nonWinTickets = 0;
    var retriggerTickets = 0;
    var maxWheelStackValue = 0;
    var totalWarnings = 0;
    var stopwatch = Stopwatch.StartNew();
    long planMs = 0, internalMs = 0, serializeMs = 0, checkerMs = 0;
    var inputAttempts = 0;
    var maxInputAttempts = 0;
    var plannerAttempts = 0;
    var maxPlannerAttempts = 0;
    var localAttempts = 0;
    var maxLocalAttempts = 0;
    var gate = new object();
    var completed = 0;
    var progressStep = Math.Max(1, count / 10);

    Console.WriteLine($"CoinPusherEngine volume test: {count} random tickets, seed={seed}");

    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, index =>
    {
        var localRng = new Random(VolumeSeed(seed, index));
        var plannerSeed = seed + index;
        try
        {
            var step = Stopwatch.StartNew();
            var plan = GenerateRandomValidPlan(localRng, plannerSeed, out var attemptsUsed);
            step.Stop();
            Interlocked.Add(ref planMs, step.ElapsedMilliseconds);
            Interlocked.Add(ref inputAttempts, attemptsUsed);
            var planAttempts = PlannerAttempts(plan);
            Interlocked.Add(ref plannerAttempts, planAttempts);
            var suffixAttempts = LocalSuffixAttempts(plan);
            Interlocked.Add(ref localAttempts, suffixAttempts);
            lock (gate)
            {
                maxInputAttempts = Math.Max(maxInputAttempts, attemptsUsed);
                maxPlannerAttempts = Math.Max(maxPlannerAttempts, planAttempts);
                maxLocalAttempts = Math.Max(maxLocalAttempts, suffixAttempts);
            }

            step.Restart();
            VerifyInternalReplay(plan);
            step.Stop();
            Interlocked.Add(ref internalMs, step.ElapsedMilliseconds);

            step.Restart();
            var ticket = TicketSerializer.ToTicketObject(plan);
            VerifyTicketShape(ticket);
            step.Stop();
            Interlocked.Add(ref serializeMs, step.ElapsedMilliseconds);

            step.Restart();
            var report = TicketChecker.CheckTicket(ticket);
            step.Stop();
            Interlocked.Add(ref checkerMs, step.ElapsedMilliseconds);
            var failure = report.Checks.FirstOrDefault(check => check.Result == TicketChecker.Status.Fail);
            if (failure != null)
            {
                throw new InvalidOperationException(
                    $"TicketChecker failed {failure.Category}/{failure.Name}: {failure.Detail}");
            }

            var hasRetrigger = false;
            var ticketFeatureCounts = new Dictionary<int, int>();
            var ticketMaxWheelStack = 0;
            foreach (var turn in ticket.Turns)
            foreach (var spawn in turn.Spawns)
            {
                if (spawn.Feature == null) continue;
                CountFeature(spawn.Feature, ticketFeatureCounts);
                hasRetrigger |= spawn.Feature.ReTrigger.Length > 0;
                if (spawn.Feature.WheelStackValue.HasValue)
                    ticketMaxWheelStack = Math.Max(ticketMaxWheelStack, spawn.Feature.WheelStackValue.Value);
            }

            lock (gate)
            {
                totalWarnings += report.Checks.Count(check => check.Result == TicketChecker.Status.Warning);
                spinCounts[ticket.WinInfo.TotalSpins] = spinCounts.GetValueOrDefault(ticket.WinInfo.TotalSpins) + 1;
                if (ticket.WinInfo.NonWinSymbols.Length > 0) nonWinTickets++;
                if (hasRetrigger) retriggerTickets++;
                maxWheelStackValue = Math.Max(maxWheelStackValue, ticketMaxWheelStack);
                foreach (var (featureId, amount) in ticketFeatureCounts)
                {
                    featureCounts[featureId] = featureCounts.GetValueOrDefault(featureId) + amount;
                }
            }
        }
        catch (Exception ex)
        {
            var message = $"#{index} plannerSeed={plannerSeed}: {ex.InnerException?.Message ?? ex.Message}";
            lock (gate)
            {
                failures.Add(message);
                if (failures.Count <= 20)
                {
                    Console.WriteLine("FAIL " + message);
                }
            }
        }

        var done = Interlocked.Increment(ref completed);
        if (done % progressStep == 0)
        {
            lock (gate)
            {
                Console.WriteLine($"progress {done}/{count} failures={failures.Count}");
            }
        }
    });

    Console.WriteLine("==== VOLUME TEST SUMMARY ====");
    Console.WriteLine($"tickets={count}");
    Console.WriteLine($"failures={failures.Count}");
    Console.WriteLine($"warnings={totalWarnings}");
    Console.WriteLine($"nonWinTickets={nonWinTickets}");
    Console.WriteLine($"retriggerTickets={retriggerTickets}");
    Console.WriteLine($"maxWheelStackValue={maxWheelStackValue}");
    Console.WriteLine("spins=" + string.Join(",", spinCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));
    Console.WriteLine("features=" + string.Join(",", featureCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));
    Console.WriteLine($"elapsedMs={stopwatch.ElapsedMilliseconds}");
    Console.WriteLine($"planMs={planMs} internalReplayMs={internalMs} serializeMs={serializeMs} ticketCheckerMs={checkerMs}");
    Console.WriteLine($"inputAttempts={inputAttempts} avgInputAttempts={(count == 0 ? 0 : inputAttempts / (double)count):F2} maxInputAttempts={maxInputAttempts}");
    Console.WriteLine($"plannerAttempts={plannerAttempts} avgPlannerAttempts={(count == 0 ? 0 : plannerAttempts / (double)count):F2} maxPlannerAttempts={maxPlannerAttempts}");
    Console.WriteLine($"localSuffixAttempts={localAttempts} avgLocalSuffixAttempts={(count == 0 ? 0 : localAttempts / (double)count):F2} maxLocalSuffixAttempts={maxLocalAttempts}");

    if (failures.Count > 0)
    {
        Console.WriteLine("sample failures:");
        foreach (var failure in failures.Take(20))
            Console.WriteLine(failure);
        Environment.Exit(1);
    }
}

static int VolumeSeed(int seed, int index)
{
    unchecked
    {
        uint x = (uint)seed;
        x ^= (uint)(index + 1) * 0x9E3779B9u;
        x ^= x >> 16;
        x *= 0x85EBCA6Bu;
        x ^= x >> 13;
        x *= 0xC2B2AE35u;
        x ^= x >> 16;
        return (int)x;
    }
}

static int PlannerAttempts(GamePlan plan)
{
    var marker = plan.Log.FirstOrDefault(line => line.StartsWith("planned after ", StringComparison.Ordinal));
    if (marker == null) return 1;
    var parts = marker.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length >= 3 && int.TryParse(parts[2], out var attempts) ? attempts : 1;
}

static int LocalSuffixAttempts(GamePlan plan)
{
    var marker = plan.Log.FirstOrDefault(line => line.StartsWith("local realization succeeded after ", StringComparison.Ordinal));
    if (marker == null) return 1;
    var parts = marker.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length >= 5 && int.TryParse(parts[4], out var attempts) ? attempts : 1;
}

static GamePlan GenerateRandomValidPlan(Random rng, int plannerSeed, out int attemptsUsed)
{
    Exception? lastPlanningFailure = null;
    for (var attempt = 0; attempt < 50; attempt++)
    {
        attemptsUsed = attempt + 1;
        var input = RandomInput(rng);
        try
        {
            return new Planner(input, plannerSeed + attempt * 10_000).Plan();
        }
        catch (Exception ex)
        {
            // A random input can be valid in shape but too dense for this engine's
            // feature/spin limits. Volume mode wants random valid tickets, so keep
            // sampling until a plan is actually produced; validation after this
            // point is never retried/hidden.
            lastPlanningFailure = ex;
        }
    }

    attemptsUsed = 50;
    throw new InvalidOperationException(
        "Unable to generate a random valid ticket after 50 input attempts.",
        lastPlanningFailure);
}

static MathInput RandomInput(Random rng)
{
    var maxSym = rng.Next(8, 11);
    var maxWinSymbols = Math.Min(5, maxSym - 2);
    var winCount = rng.Next(1, Math.Min(4, maxWinSymbols) + 1);
    var symbols = Enumerable.Range(1, maxSym).OrderBy(_ => rng.Next()).ToArray();
    var winSymbols = symbols.Take(winCount).OrderBy(sym => sym).ToArray();

    var targets = new Dictionary<int, int>();
    foreach (var sym in winSymbols)
    {
        var maxTarget = winCount switch
        {
            1 => 24,
            2 => 20,
            3 => 16,
            _ => 14,
        };
        targets[sym] = rng.Next(6, maxTarget + 1);
    }

    var required = new Dictionary<string, int>();
    // Keep random volume inputs feasible; the planner still adds WHEEL/FLUSH/
    // EXTRA_SPIN naturally through optional and capacity-driven paths. Forced
    // feature combinations are covered by StressTest's named scenarios.
    if (rng.NextDouble() < 0.05) required["WHEEL"] = 1;
    if (rng.NextDouble() < 0.03) required["FLUSH"] = 1;
    if (rng.NextDouble() < 0.03) required["EXTRA_SPIN"] = 1;

    Dictionary<int, int>? prizeTiers = null;
    if (rng.NextDouble() < 0.08)
    {
        prizeTiers = new Dictionary<int, int>();
        var sym = winSymbols[rng.Next(winSymbols.Length)];
        prizeTiers[sym] = 1;
        required["PRIZE_UPGRADE"] = prizeTiers.Values.Sum();
    }

    return new MathInput
    {
        Targets = targets,
        BaseSpins = K.BASE_SPINS,
        Required = required,
        PrizeTiers = prizeTiers,
        PrizeValues = BuildPrizeValues(maxSym),
        MaxSym = maxSym,
    };
}

static void VerifyInternalReplay(GamePlan plan)
{
    var replay = Sim.Run(plan);
    foreach (var (sym, target) in plan.Targets)
    {
        replay.TryGetValue(sym, out var got);
        if (got != target)
            throw new InvalidOperationException($"Internal replay win mismatch sym={sym} got={got} target={target}");
    }

    foreach (var (sym, target) in plan.NonWinTargets)
    {
        replay.TryGetValue(sym, out var got);
        if (got < target || got >= K.FILL_CAP)
            throw new InvalidOperationException($"Internal replay non-win mismatch sym={sym} got={got} target>={target} cap<{K.FILL_CAP}");
    }
}

static void VerifyTicketShape(TicketSerializer.TicketDto ticket)
{
    if (ticket.WinInfo.TotalSpins != ticket.Turns.Length)
        throw new InvalidOperationException($"TotalSpins={ticket.WinInfo.TotalSpins} but turns={ticket.Turns.Length}");
    if (ticket.StartingBoard.Length != K.ROWS || ticket.StartingBoard.Any(row => row.Length != K.COLS))
        throw new InvalidOperationException("StartingBoard must be 5x5.");

    foreach (var (turn, turnIndex) in ticket.Turns.Select((turn, index) => (turn, index)))
    {
        if (turn.Pushers.Length != K.COLS)
            throw new InvalidOperationException($"Turn {turnIndex + 1} has {turn.Pushers.Length} pushers.");

        var seen = new HashSet<int>();
        foreach (var spawn in turn.Spawns)
        {
            if (spawn.Pos < 0 || spawn.Pos >= K.ROWS * K.COLS)
                throw new InvalidOperationException($"Turn {turnIndex + 1} spawn Pos={spawn.Pos} is out of range.");
            if (!seen.Add(spawn.Pos))
                throw new InvalidOperationException($"Turn {turnIndex + 1} has duplicate spawn Pos={spawn.Pos}.");
            VerifyFeaturePayload(spawn.Feature);
        }
    }
}

static void VerifyFeaturePayload(TicketSerializer.FeatureDto? feature)
{
    if (feature == null) return;
    if (feature.FeatureId == K.F_WHEEL)
    {
        if (!feature.WheelSymbolId.HasValue)
            throw new InvalidOperationException("WHEEL feature missing WheelSymbolId.");
        if (!feature.WheelStackValue.HasValue
            || feature.WheelStackValue < K.MIN_WHEEL_STACK_VALUE
            || feature.WheelStackValue > K.MAX_WHEEL_STACK_VALUE)
            throw new InvalidOperationException($"WHEEL feature has invalid WheelStackValue={feature.WheelStackValue}.");
    }

    if (feature.FeatureId == K.F_PRUP && !feature.UpgradeSymbolId.HasValue)
        throw new InvalidOperationException("PRIZE_UPGRADE feature missing UpgradeSymbolId.");

    foreach (var nested in feature.ReTrigger)
        VerifyFeaturePayload(nested);
}

static void CountFeature(TicketSerializer.FeatureDto feature, Dictionary<int, int> counts)
{
    counts[feature.FeatureId] = counts.GetValueOrDefault(feature.FeatureId) + 1;
    foreach (var nested in feature.ReTrigger)
        CountFeature(nested, counts);
}

static IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> BuildPrizeValues(int maxSym)
{
    var values = new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
    for (var sym = 1; sym <= maxSym; sym++)
    {
        values[sym] = new Dictionary<int, decimal>
        {
            [0] = sym,
            [1] = sym * 2,
            [2] = sym * 4,
        };
    }
    return values;
}
