# Coin Pusher Outcome Engine

## Technical Design Document

Version: 1.0

---

## 1. Purpose

The Coin Pusher Outcome Engine generates a complete deterministic coin-pusher
game session from a desired mathematical outcome.

The engine does not wait for gameplay to discover the result. Instead, it plans
the result first, reconstructs the board states required to create that result,
then verifies the whole session by replaying it forward.

The engine must guarantee:

- exact objective completion
- exact payout realization
- no overcollection
- deterministic replay
- verifiable board states
- natural-looking gameplay pacing

---

## 2. Core Idea

### Traditional game flow

```text
Board is generated
-> player plays
-> outcome is discovered
```

### This engine's flow

```text
Outcome is chosen
-> planner creates exact collections
-> boards are reconstructed backward
-> game is replayed forward
-> verifier proves the result
```

The most important rule is:

```text
The GamePlan is the source of truth.
The board only exists to realize the GamePlan.
```

---

## 3. Game Board

The board is always:

```text
5 rows x 5 columns
```

Coordinates:

```text
R0 C0 = top left
R4 C4 = bottom right
```

The top side contains pushers.

The bottom side is the collection edge.

Example board:

```text
      Pushers

        C0    C1    C2    C3    C4
R0      A     B     C     D     E
R1      B     C     D     E     A
R2      A     A     B     B     C
R3      B     C     D     A     A
R4      A     B     C     D     E

      Collection Edge
```

---

## 4. Symbols

There is no permanent filler-symbol set.

The engine may use symbols such as:

```text
A, B, C, D, E, F, G, H, I, J
```

Every symbol has its own collection threshold.

Example thresholds:

```text
A = 30
B = 30
C = 20
D = 15
E = 25
F = 20
G = 12
H = 12
I = 12
J = 12
```

The math input decides which symbols are intended wins for the current game.

Example current game target:

```text
A = 30
B = 30
C = 20
D = 15
```

Then:

```text
A/B/C/D are target symbols and must hit their thresholds exactly.
E/F/G/H/I/J are non-target symbols for this game.
```

Non-target symbols are still collected and counted.

They are safe only while they stay below their thresholds.

Example:

```text
G threshold = 12
G collected = 11   safe
G collected = 12   accidental win, invalid
```

So "filler" only means:

```text
not intended to win in this generated session
```

It does not mean ignored.

---

## 5. Objectives

Objectives define exact collection targets.

Example:

```text
A = 30
B = 30
C = 20
D = 15
```

This means:

```text
Collect exactly 30 A symbols
Collect exactly 30 B symbols
Collect exactly 20 C symbols
Collect exactly 15 D symbols
```

Valid:

```text
A = 30
```

Invalid:

```text
A = 29   // undercollection
A = 31   // overcollection
```

The verifier rejects both undercollection and overcollection.

---

## 6. Stacks

Symbols may have stack values, but the planner must not invent stacked objective
symbols during normal board reconstruction.

Normal reconstructed objective symbols are always:

```text
A x1
```

Stacked objective symbols are valid only if a prior feature path creates them,
for example a Wheel feature.

Example:

```text
A x4
```

If collected:

```text
A += 4
```

Maximum stack:

```text
7
```

If a feature tries to increase a stack above 7, it is capped at 7.

Invalid normal spawn/reconstruction:

```text
D x7 appears without Wheel or another stack-producing feature
```

Valid:

```text
Wheel targets D
D x1 becomes D x4
Later spin collects D x4
```

---

## 7. Pushers and Collection

Each column has one pusher.

Allowed pusher values:

```text
1, 2, 3
```

The pusher value means how many bottom cells are collected from that column.

Example column:

```text
R0 A
R1 B
R2 C
R3 D
R4 E
```

Pusher value:

```text
2
```

Collected:

```text
D, E
```

Remaining after collection:

```text
R0 empty
R1 empty
R2 empty
R3 A
R4 B
```

The remaining symbols slide downward.

---

## 8. Spin Lifecycle

Every spin executes in this order:

```text
1. Feature Landing
2. Feature Activation
3. Feature Conversion
4. Collection
5. Clockwise Rotation
6. Spawn
7. End Spin
```

The final board after spawn becomes the starting board of the next spin.

---

## 9. Forward Runtime Example

Suppose this is the start board for one spin:

```text
      C0    C1    C2    C3    C4
R0    H     G     A     A     A
R1    I     A     A     A     A
R2    B     A     B     B     B
R3    B     B     B     B     B
R4    B     B     B     B     B
```

Pushers:

```text
[3] [3] [3] [3] [3]
```

Collected cells:

```text
C0 -> B B B
C1 -> A B B
C2 -> B B B
C3 -> B B B
C4 -> B B B
```

Spin result:

```text
B += 14
A += 1
```

After collection, remaining cells slide down:

```text
      C0    C1    C2    C3    C4
R0    .     .     .     .     .
R1    .     .     .     .     .
R2    .     .     .     .     .
R3    H     G     A     A     A
R4    I     A     A     A     A
```

Then the board rotates clockwise:

```text
      C0    C1    C2    C3    C4
R0    I     H     .     .     .
R1    A     G     .     .     .
R2    A     A     .     .     .
R3    A     A     .     .     .
R4    A     A     .     .     .
```

Then spawn fills empty cells:

```text
      C0    C1    C2    C3    C4
R0    I     H     H     J     D
R1    A     G     G     G     D
R2    A     A     A     C     A
R3    A     A     A     A     A
R4    A     A     A     A     D
```

That becomes the next spin's start board.

---

## 10. Why Backward Reconstruction Is Needed

If we generate boards forward randomly, we may accidentally collect too much or
too little.

Example problem:

```text
Need A = 30
Forward random board produces A = 31
```

That is invalid.

Instead, the planner starts from the desired final outcome and constructs the
boards backward so every future collection is already known.

---

## 11. Backward Reconstruction Flow

Forward runtime:

```text
Start Board
-> Collect
-> Rotate Clockwise
-> Spawn
-> Next Board
```

Backward planning reverses this:

```text
Next Board
-> Remove Spawn
-> Rotate AntiClockwise
-> Restore Collected Symbols
-> Previous Start Board
```

This is the core planning technique.

---

## 12. Backward Reconstruction Step-by-Step

Assume the planner knows spin 5 should collect:

```text
C += 2
D += 1
```

And it knows what the board after spin 5 should look like.

To reconstruct spin 5 start board:

### Step 1: Start from later board

```text
This is the board after spin 5 spawn.
```

### Step 2: Remove spawn cells

The planner removes cells that were spawned after rotation.

```text
Later board
-> remove spawned symbols
```

### Step 3: Undo rotation

Forward rotation is clockwise.

Backward rotation is anti-clockwise.

```text
Rotate anti-clockwise
```

### Step 4: Restore collection rows

If pusher value is 1, restore one bottom cell per column.

If pusher value is 3, restore three bottom cells per column.

Example:

```text
Need C += 2, D += 1
Pushers [1] [1] [1] [1] [1]
```

Bottom row can be restored as:

```text
C0 = C
C1 = C
C2 = D
C3 = non-target symbol below threshold
C4 = non-target symbol below threshold
```

Now when the game runs forward, it will collect exactly:

```text
C += 2
D += 1
```

---

## 13. Feature System

Features are special board cells that activate during a spin.

Supported features:

- prize upgrade
- wheel
- flush
- extra spin

Feature lifecycle:

```text
Feature lands
-> feature activates
-> feature converts into symbol, feature, or empty
```

Maximum feature chain:

```text
Feature -> Feature
```

Invalid:

```text
Feature -> Feature -> Feature
```

---

## 14. Prize Upgrade Feature

Prize upgrade changes payout state only.

It does not change collection counts.

Example:

```text
A objective = 30
```

Prize table:

```text
Base     = 10
Upgrade1 = 20
Upgrade2 = 30
```

If A reaches Upgrade2:

```text
A still needs exactly 30 collections
Payout becomes 30
```

Maximum upgrades:

```text
2
```

States:

```text
Base -> Upgrade1 -> Upgrade2
```

---

## 15. Wheel Feature

Wheel adds stacks to matching symbols already on the board.

Wheel chooses:

```text
Target symbol
Wheel value
```

Wheel value range:

```text
1..3
```

Formula:

```text
new stack = current stack + 1 + wheel value
```

Example:

```text
Target = A
Wheel value = 3
Current A stack = 1
```

Result:

```text
A x4
```

Wheel does not collect immediately. It creates potential future value.

Example:

```text
4 symbols of A
Wheel value +3
Each A becomes A x4
Potential = 16
```

The planner may harvest:

```text
8 now
8 later
```

This helps exact objective completion.

Dead wheel is invalid.

Dead wheel means:

```text
Wheel fires but no matching symbol receives stacks.
```

---

## 16. Flush Feature

Flush collects an entire column immediately.

Example column:

```text
R0 A x4
R1 C
R2 non-target symbol
R3 D
R4 C
```

Flush result:

```text
A += 4
C += 2
D += 1
```

Non-target symbols still count. The flush is valid only if those non-target
counts remain below their thresholds.

Dead flush is invalid.

Dead flush means:

```text
Flush fires but collects no objective value.
```

---

## 17. Extra Spin Feature

Default game length:

```text
5 spins
```

Planner may award extra spins:

```text
+1, +2, +3, ...
```

Extra spins are used when the exact outcome needs more timeline capacity.

---

## 18. Payout System

Collections complete objectives.

Prize levels determine payout.

Example:

```text
Objective A = 30
Prize level A = Upgrade2
Paytable A Upgrade2 = 30
```

If A is collected exactly 30 times, it pays:

```text
30
```

Final payout:

```text
sum of completed objective prize values
```

The verifier checks:

```text
final payout == target win
```

---

## 19. Planning Pipeline

The engine uses these stages:

```text
Outcome Solver
-> Prize Planner
-> Contribution Planner
-> Feasibility Solver
-> Timeline Planner
-> Backward Board Planner
-> Simulator
-> Verifier
```

---

## 20. Module Responsibilities

### Outcome Solver

Decides which objectives are required.

Input:

```text
Target win and requested objectives
```

Output:

```text
A = 30
B = 30
C = 20
D = 15
```

### Prize Planner

Chooses prize levels that match target payout.

Example:

```text
A -> Upgrade2
B -> Upgrade2
C -> Base
D -> Upgrade1
```

### Contribution Planner

Splits objective targets into collection units.

Example:

```text
A = 30
```

Normal contribution planning becomes individual single-symbol units:

```text
A x1
A x1
A x1
...
A x1
```

Total:

```text
30
```

The planner may later optimize some of those units into Wheel-created stacks,
but it must not place `A x7` directly onto the board without a feature path.

### Feasibility Solver

Checks whether planned contribution units can fit into the timeline.

Example:

```text
Need 90 contribution cells
Each spin can collect max 15 cells
Required spins = 6
```

If only 5 spins are available, the planner needs an extra spin.

### Timeline Planner

Distributes contributions across spins.

Bad pacing:

```text
Spin 1 = 30
Spin 2 = 0
Spin 3 = 0
Spin 4 = 0
Spin 5 = 0
```

Better pacing:

```text
Spin 1 = 8
Spin 2 = 5
Spin 3 = 7
Spin 4 = 4
Spin 5 = 6
```

### Backward Board Planner

Builds actual board states backward.

This module:

- starts from a final board containing non-target symbols below threshold
- removes spawns
- rotates anti-clockwise
- restores collected symbols
- produces start boards and spawn instructions

### Simulator

Replays the plan forward.

It executes:

```text
feature landing
feature activation
feature conversion
collection
rotation
spawn
end spin
```

### Verifier

Final authority.

It checks:

- exact objective counts
- no overcollection
- prize levels
- final payout
- stack limits
- feature validity
- deterministic board snapshots

---

## 21. Complete Example

Input:

```text
Target win = 100

Objectives:
A = 30
B = 30
C = 20
D = 15
```

Paytable:

```text
A Upgrade2 = 25
B Upgrade2 = 25
C Upgrade2 = 25
D Upgrade2 = 25
```

Expected payout:

```text
25 + 25 + 25 + 25 = 100
```

Planner creates timeline:

```text
Spin 1: some A/B/C/D contribution
Spin 2: some A/B/C/D contribution
Spin 3: some A/B/C/D contribution
Spin 4: some A/B/C/D contribution
Spin 5: remaining exact contribution
```

Backward board planner reconstructs boards so that forward replay produces:

```text
A = 30
B = 30
C = 20
D = 15
```

Verifier result:

```text
PASS
```

---

## 22. Console Trace Example

Run:

```bash
dotnet run --project src/CoinPusher.Engine.App
```

The app prints:

```text
[planner] Target win: 100
[planner] Objectives: A=30, B=30, C=20, D=15
[planner] Prize levels: A=Upgrade2, B=Upgrade2, C=Upgrade2, D=Upgrade2
[board-planner] Backward reconstructing 5 spin board states.
```

Then it prints backward construction:

```text
final non-win board seed
spin 4 after removing spawns
spin 4 after anticlockwise undo rotation
planned start board for spin 4
...
planned start board for spin 0
```

Then it prints forward replay:

```text
spin 0 start
spin 0 after feature landing
spin 0 target harvest
spin 0 after collection
spin 0 after rotation
spin 0 after spawn/end
...
```

Final summary:

```text
Verifier : PASS
Spins    : 5
Target   : 100
Payout   : 100
Counts   : A=30, B=30, C=20, D=15
Prizes   : A=Upgrade2, B=Upgrade2, C=Upgrade2, D=Upgrade2
```

---

## 23. Protocol JSON Export

The engine can export a generated `GamePlan` into a client-facing JSON shape.

Run:

```bash
dotnet run --project src/CoinPusher.Engine.App -- --json
```

To inspect a small verified plan that visibly contains Wheel and Flush:

```bash
dotnet run --project src/CoinPusher.Engine.App -- --feature-demo
dotnet run --project src/CoinPusher.Engine.App -- --feature-demo --json
```

Output shape:

```json
{
  "startingBoard": [
    [ { "id": 1 }, { "id": 2 }, { "id": 3 }, { "id": 4 }, { "id": 5 } ]
  ],
  "turns": [
    {
      "pushers": [
        { "pushValue": 3 },
        { "pushValue": 3 },
        { "pushValue": 3 },
        { "pushValue": 3 },
        { "pushValue": 3 }
      ],
      "spawns": [
        { "Pos": 2, "id": 4 },
        {
          "Pos": 14,
          "id": 11,
          "feature": {
            "featureId": 11,
            "convertToId": 2,
            "wheelSymbolId": 2,
            "wheelStackMultiplier": 4,
            "fReTrigger": []
          }
        }
      ]
    }
  ]
}
```

Export details:

- `startingBoard` is the board at the beginning of the session.
- `turns[n].pushers` contains one pusher entry per column.
- `turns[n].spawns` contains cells spawned for that turn plus feature landing metadata when present.
- `Pos` is linear board position:

```text
Pos = row * 5 + column
```

- `id` is the numeric symbol or feature id from the export id map.
- Feature payloads are optional and appear only when the cell represents a feature.

The default export id map is:

```text
A=1, B=2, C=3, D=4, E=5, F=6, G=7, H=8, I=9, J=10
Wheel=11, ExtraSpin=12, PrizeUpgrade=13, Flush=14
```

Production integrations can provide a custom `ExportIdMap`.

---

## 24. Hard Invariants

The engine must never allow:

- overcollection
- undercollection
- stack greater than 7
- dead wheel
- dead flush
- invalid feature chain
- invalid prize level
- invalid spin count
- final payout mismatch
- board snapshot mismatch

If any of these happen, the plan is invalid.

---

## 25. Load and Scenario Testing

The repository includes deterministic load tests that exercise:

- many generated outcome requests
- different objective symbol sets
- different symbol thresholds
- extra-spin capacity
- backward board continuity
- JSON export validity
- manual feature mechanics:
  - wheel
  - flush
  - extra spin
  - prize upgrade
  - valid feature chain
  - invalid feature chain

Run:

```bash
dotnet run --project tests/CoinPusher.Engine.Tests -- --load
```

Expected output:

```text
LOAD TEST PASS
Scenarios : 100
Min spins : ...
Max spins : ...
Avg spins : ...
```

This load test is deterministic, so failures are reproducible.

---

## 26. Current Implementation Map

Source layout:

```text
src/CoinPusher.Engine
```

Important folders:

```text
Abstractions/
Diagnostics/
Planning/
Simulation/
Verification/
```

Important classes:

```text
OutcomePlanner
OutcomeSolver
PrizePlanner
ContributionPlanner
FeasibilitySolver
TimelinePlanner
BackwardBoardPlanner
CoinPusherSimulator
GamePlanVerifier
ConsoleEngineTraceSink
```

Runnable app:

```text
src/CoinPusher.Engine.App/Program.cs
```

Run:

```bash
dotnet run --project src/CoinPusher.Engine.App
```

Run summary only:

```bash
dotnet run --project src/CoinPusher.Engine.App -- --quiet
```

---

## 27. Final Success Condition

A generated session is successful only when:

```text
Objectives exact
Collections exact
No overcollection
Payout exact
Replay deterministic
Board snapshots match
Verifier passes
```

In short:

```text
The plan says what must happen.
The board is reconstructed to make it happen.
The simulator proves it happens.
The verifier decides if it is valid.
```
