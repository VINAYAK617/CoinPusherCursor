# Coin Pusher Outcome Engine

Deterministic C# engine for generating, replaying, and verifying outcome-driven
coin-pusher sessions.

## What is implemented

- 5x5 board model with symbol stacks and feature cells
- Spin lifecycle:
  1. feature landing
  2. feature activation
  3. feature conversion
  4. collection through planned pushers
  5. 90 degree clockwise rotation
  6. spawn resolution
  7. end of spin
- Exact objective collection with no over-collection
- Filler symbols that can appear on the board without contributing to objectives
- Prize upgrade state and exact payout verification
- Wheel stack upgrades using `current + 1 + wheelValue`, with max stack cap of 7
- Flush collection with dead-flush detection
- Extra-spin awards
- Feature-chain validation
- Deterministic board snapshots for replay verification
- First-pass exact planner that distributes contribution values across a paced spin timeline

## Projects

- `src/CoinPusher.Engine` - domain model, simulator, verifier, and planner
- `tests/CoinPusher.Engine.Tests` - dependency-free executable test harness

## Source layout

- `Abstractions/` - interfaces for planner stages, simulator, verifier, and trace sinks
- `Diagnostics/` - board formatting and console/no-op trace sinks
- `Planning/` - outcome, prize, feasibility, contribution, timeline, and backward-board planners
- `Simulation/` - replay models and deterministic game simulator
- `Verification/` - verification models and final plan verifier
- `BoardState.cs` / `Domain.cs` - board mechanics and core game contracts

## Engine modules

The implementation exposes the major TDD pipeline stages as separate C# types:

- `OutcomeSolver`
- `PrizePlanner`
- `FeasibilitySolver`
- `ContributionPlanner`
- `TimelinePlanner`
- `BackwardBoardPlanner`
- `CoinPusherSimulator`
- `GamePlanVerifier`

`OutcomePlanner` composes those modules and verifies the generated plan before
returning it. The generated `GamePlan` remains the source of truth; board states
are deterministic snapshots used to prove replay correctness.

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

## Console board/state tracing

Pass `ConsoleEngineTraceSink` to the planner or simulator to print board
formation and spin lifecycle state changes:

```csharp
var trace = new ConsoleEngineTraceSink();
var plan = new OutcomePlanner(trace: trace).Generate(request);
var result = new CoinPusherSimulator(trace).Replay(plan);
```

To see a complete trace demo:

```bash
dotnet run --project tests/CoinPusher.Engine.Tests -- --trace-demo
```

## Build and test

```bash
dotnet build CoinPusherOutcomeEngine.sln
dotnet run --project tests/CoinPusher.Engine.Tests
```

These commands require the .NET 8 SDK.
