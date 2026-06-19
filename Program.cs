using CoinPusherEngine;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length > 0 && args[0] == "selftest") { StressTest.Run(); return; }

var rows = new List<PrizeLadderRow>
{
    new() { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
    new() { Target = 20, Tiers = new decimal[] { 2, 4, 8 } },
    new() { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
    new() { Target = 25, Tiers = new decimal[] { 10, 20, 50 } },
    new() { Target = 25, Tiers = new decimal[] { 100, 200, 500 } },
    new() { Target = 30, Tiers = new decimal[] { 10000 } },
};

var bundle = new LadderCombinator(rows).Bundle(new decimal[] { 45 });
Console.WriteLine($"Covered: [${string.Join(",",bundle.Covered.Select(a=>$"${a}"))}]");
Console.WriteLine($"Entries: [{string.Join(";",bundle.Entries.Select(e=>$"sym{e.Sym}@tier{e.Tier}"))}]");
Console.WriteLine($"Targets: [{string.Join(",",bundle.Input.Targets.Select(kv=>$"sym{kv.Key}={kv.Value}"))}]");
Console.WriteLine($"BaseSpins: {bundle.Input.BaseSpins}");

GamePlan? plan = null;
for (int seed = 0; seed < 5000 && plan == null; seed++)
    try { var p = new Planner(bundle.Input, seed).Plan(); if (p.Verified) plan = p; } catch {}

if (plan == null) { Console.WriteLine("No seed found."); return; }
var json = TicketSerializer.ToJson(plan);
Console.WriteLine(json);
