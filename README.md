# Coin Pusher Outcome Engine

Deterministic C# engine for generating, replaying, and verifying outcome-driven
coin-pusher sessions.

## What is implemented

- 5x5 board model with symbol stacks and feature cells
- GDD-compatible ticket replay for `startingBoard`, `winInfo`, and ordered
  turn `pushers`/`spawns`
- Fixed five-phase ticket runtime:
  1. stale feature-token flattening
  2. pusher/FLUSH collection
  3. clockwise board rotation
  4. ordered spawn injection into post-rotation empty positions
  5. board-token feature firing and conversion
- WHEEL token multiplication with next-turn zone isolation
- EXTRA SPIN and PRIZE UPGRADE token conversion as predetermined visual
  events
- FLUSH pushers via `featureId=13`
- Exact ticket verification for declared win-symbol targets and filler caps
- Spin lifecycle:
  1. feature landing
  2. feature activation
  3. feature conversion
  4. push resolution
  5. collection resolution
  6. rotation
  7. spawn resolution
  8. end of spin
- Exact objective collection with no over-collection
- Prize upgrade state and exact payout verification
- Wheel stack upgrades with max stack cap of 7
- Flush collection with dead-flush detection
- Extra-spin awards
- Feature-chain validation
- Deterministic board snapshots for replay verification
- First-pass exact planner that packs contribution values into planned spins

## Projects

- `src/CoinPusher.Engine` - domain model, simulator, verifier, and planner
- `tests/CoinPusher.Engine.Tests` - dependency-free executable test harness

## Usage sketch

```csharp
var request = OutcomeRequest.Create(
    500,
    new[]
    {
        new ObjectiveRequirement("A", 30),
        new ObjectiveRequirement("B", 20),
        new ObjectiveRequirement("C", 10)
    },
    new PaytableConfiguration(new Dictionary<string, PrizeTableEntry>
    {
        ["A"] = new(10, 20, 30),
        ["B"] = new(20, 40, 60),
        ["C"] = new(410, 420, 430)
    }));

var plan = new OutcomePlanner().Generate(request);
var report = new GamePlanVerifier().Verify(plan);
```

## Build and test

```bash
dotnet build CoinPusherOutcomeEngine.sln
dotnet run --project tests/CoinPusher.Engine.Tests
```

The current cloud environment used to create this repository did not include the
.NET SDK, so these commands are expected to run in a .NET 8-capable environment.
