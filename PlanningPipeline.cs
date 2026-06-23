namespace CoinPusherEngine;

/// <summary>
/// Immutable input for the final board-realization stage. Feature selection,
/// feature placement, WHEEL locks, and per-spin allocations are already decided;
/// this stage is responsible only for turning those decisions into concrete
/// boards/spawns and proving the resulting GamePlan replays exactly.
/// </summary>
internal sealed record PlanAssemblyRequest(
    MathInput SourceInput,
    IReadOnlyDictionary<int, int> EffectiveTargets,
    IReadOnlyList<int> WinSyms,
    IReadOnlyList<int> FillSyms,
    IReadOnlyDictionary<int, int> NonWinTargets,
    IReadOnlyDictionary<int, int> NonWinPrizeTiers,
    IReadOnlyDictionary<int, int> PrizeTiers,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> PrizeValues,
    IReadOnlyList<PlacedFeat> PlacedFeatures,
    IReadOnlyList<WLock> WheelLocks,
    IReadOnlyList<Dictionary<int, int>> Allocations,
    int TotalSpins,
    int BaseSpins,
    IReadOnlyDictionary<int, int> DecorBudget,
    int Seed,
    List<string> Log);

internal interface IPlanAssemblyPipeline
{
    GamePlan Assemble(PlanAssemblyRequest request);
}

internal interface ISpinPlanBuildStage
{
    List<SpinPlan> Build(PlanAssemblyRequest request, Random rng, FillTracker fillTracker);
}

internal interface ISpawnResolutionStage
{
    void Resolve(PlanAssemblyRequest request, List<SpinPlan> spins, Random rng, FillTracker fillTracker);
}

internal interface IPlanVerificationStage
{
    void Verify(GamePlan plan);
}

internal sealed class DefaultPlanAssemblyPipeline : IPlanAssemblyPipeline
{
    private const int LocalRealizationAttempts = 16;

    private readonly ISpinPlanBuildStage _builder;
    private readonly ISpawnResolutionStage _resolver;
    private readonly IPlanVerificationStage _verifier;

    public DefaultPlanAssemblyPipeline(
        ISpinPlanBuildStage? builder = null,
        ISpawnResolutionStage? resolver = null,
        IPlanVerificationStage? verifier = null)
    {
        _builder = builder ?? new BackwardSpinPlanBuildStage();
        _resolver = resolver ?? new SpawnResolutionStage();
        _verifier = verifier ?? new ExactReplayVerificationStage();
    }

    public GamePlan Assemble(PlanAssemblyRequest request)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < LocalRealizationAttempts; attempt++)
        {
            var rng = new Random(AttemptSeed(request.Seed, attempt));
            var fillTracker = new FillTracker(request.FillSyms.ToArray());
            try
            {
                var spins = _builder.Build(request, rng, fillTracker);
                _resolver.Resolve(request, spins, rng, fillTracker);
                var plan = CreatePlan(request, spins);
                _verifier.Verify(plan);

                if (attempt > 0)
                {
                    plan.Log.Add($"local realization succeeded after {attempt + 1} suffix attempts");
                }

                plan.Verified = true;
                plan.Log.Add("verified OK");
                return plan;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            $"Could not realize verified board suffix after {LocalRealizationAttempts} local attempts.",
            last);
    }

    private static GamePlan CreatePlan(PlanAssemblyRequest request, List<SpinPlan> spins) =>
        new()
        {
            TotalSpins = request.TotalSpins,
            Targets = request.EffectiveTargets,
            WinSyms = request.WinSyms.ToArray(),
            FillSyms = request.FillSyms.ToArray(),
            PrizeTiers = request.PrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value),
            PrizeValues = request.PrizeValues.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<int, decimal>)kv.Value.ToDictionary(tier => tier.Key, tier => tier.Value)),
            NonWinTargets = request.NonWinTargets.ToDictionary(kv => kv.Key, kv => kv.Value),
            NonWinPrizeTiers = request.NonWinPrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value),
            Spins = spins,
            Log = request.Log,
        };

    private static int AttemptSeed(int seed, int attempt)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= (uint)(attempt + 1) * 0x7F4A7C15u;
            x ^= x >> 15;
            x *= 0x2C1B3C6Du;
            x ^= x >> 12;
            x *= 0x297A2D39u;
            x ^= x >> 15;
            return (int)x;
        }
    }
}

internal sealed class BackwardSpinPlanBuildStage : ISpinPlanBuildStage
{
    public List<SpinPlan> Build(PlanAssemblyRequest request, Random rng, FillTracker fillTracker)
    {
        var builder = new Builder(
            request.EffectiveTargets,
            request.WheelLocks,
            request.PlacedFeatures.ToList(),
            request.FillSyms.ToArray(),
            request.Log,
            rng,
            fillTracker,
            request.DecorBudget.ToDictionary(kv => kv.Key, kv => kv.Value));

        return builder.BuildAll(
            request.PlacedFeatures.ToList(),
            request.Allocations.Select(allocation => allocation.ToDictionary(kv => kv.Key, kv => kv.Value)).ToList(),
            request.TotalSpins,
            request.BaseSpins);
    }
}

internal sealed class SpawnResolutionStage : ISpawnResolutionStage
{
    public void Resolve(PlanAssemblyRequest request, List<SpinPlan> spins, Random rng, FillTracker fillTracker)
    {
        var winSet = request.WinSyms.ToHashSet();
        new Resolver(
            request.FillSyms.ToArray(),
            winSet,
            request.Log,
            rng,
            fillTracker,
            request.NonWinTargets.Keys).Resolve(spins);
    }
}

internal sealed class ExactReplayVerificationStage : IPlanVerificationStage
{
    public void Verify(GamePlan plan) => Verifier.Check(plan);
}
