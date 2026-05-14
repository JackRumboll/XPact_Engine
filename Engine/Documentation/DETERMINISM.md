# XPact Engine — Determinism Architecture

Per architectural decisions #16, #34, deterministic execution is a **hard architectural requirement from Phase 1**. This document specifies the contract that every engine system must obey. Industrial training scenarios depend on byte-identical replay; assessment depends on deterministic AI behavior; instructor debrief depends on reproducible state.

**Status: STUB — full design landing in Phase 1 Task 1.0b.** This file is committed in Phase 0 so subagents can reference it. The 1.0b subagent populates the full design.

## Scope of Determinism

Deterministic for replay/assessment:
- **Gameplay tick**: every `AActor::Tick`, `UActorComponent::TickComponent`, animation tick, AI tick.
- **Physics**: PhysX simulation at fixed sub-step.
- **Game-state RNG**: every `FRandomStream` consumed by gameplay or AI.
- **Asset loading order**: never deterministic-significant (async loading can race; gameplay must not depend on completion order).

NOT required to be deterministic:
- **Rendering**: pixel output may vary frame-to-frame (driver, thread interleaving). Renderer is parallel.
- **Audio mixing**: spatial mixing happens on audio thread; deterministic _content_ (which samples play) is, but mixing not.
- **Wall-clock**: `FDateTime::Now()` not used in gameplay path.

## Architectural Rules

### Rule 1: Single-threaded gameplay tick

Gameplay tick runs on the **game thread only**. `FTickTaskSequencer::SingleThreadedMode()` must return `true` for gameplay tick groups. Implementation:
- Override `FPlatformProcess::SupportsMultithreading()` to return `false` in deterministic-sim mode (settable per-target or via console var `r.Sim.Deterministic 1`).
- OR fork `FTickTaskSequencer` with an explicit single-threaded gameplay path.

### Rule 2: Fixed-timestep gameplay clock

`FApp::DeltaTime` for gameplay = `1.0 / TimestepHz` (default 60Hz). Wall-clock variation absorbed by:
- If frame > 1 timestep: catch-up by running multiple gameplay ticks per frame.
- If frame < 1 timestep: render frame without gameplay tick.

Editor PIE uses the same clock — PIE catch-up logic identical to standalone.

### Rule 3: Seeded RNG

Every random number consumer uses a **named `XRandomStream`** with a documented seed:
- `XRandomStream::GameplayMain` (gameplay RNG)
- `XRandomStream::AISpawn` (AI spawn decisions)
- `XRandomStream::Animation` (anim variation)
- `XRandomStream::ProceduralContent` (any procedural geometry/material)

`FMath::Rand()` (global RNG) is **BANNED** in gameplay path. Subagents enforce via linter.

Replay file (per `REPLAY_FORMAT.md`) captures the seed bundle at start; replay reproduces exact RNG cursor at each tick.

### Rule 4: Strict tick-group ordering

Per `Engine/Source/Runtime/Engine/Public/Tickable.h`:
- `TG_PrePhysics` → `TG_StartPhysics` → `TG_DuringPhysics` → `TG_EndPhysics` → `TG_PostPhysics` → `TG_PostUpdateWork` → `TG_LastDemotable`.

Within each tick group, tick order is **dependency-graph deterministic** — actor/component prerequisites form a DAG, and ties are broken by `FName` lexicographic order (NOT object-creation order or memory address).

### Rule 5: Thread-affinity rules

Non-gameplay systems are parallel, but their effects are visible to gameplay only at **frame boundaries**:
- Render thread reads game state at "Sync Point A" (start of frame).
- Audio mixing reads game state at "Sync Point B" (start of audio frame, 100Hz typical).
- Physics writeback happens during `TG_StartPhysics`/`TG_EndPhysics`.

Gameplay code never observes mid-tick state from another thread.

### Rule 6: PhysX sub-stepping

PhysX physics runs at fixed sub-step (default `1.0 / 60Hz` = same as gameplay; can be sub-divided e.g. 4x for `1.0 / 240Hz` physics). Sub-step count is fixed; no variable sub-stepping.

`UPhysicsSettings::bSubstepping = true` enforced for deterministic-sim mode.

### Rule 7: AutoRTFM transactions are deterministic

Per decision #27, AutoRTFM (or its runtime-only fallback) provides transactional memory for BP opcodes 0x70–0x73. Transactions must:
- Commit deterministically (same outcome on replay).
- Abort deterministically (same condition triggers).
- NOT consume RNG outside named XRandomStreams.

## Tracing in Shipping Builds (Decision #51)

`UE_TRACE_ENABLED` auto-disables in `UE_BUILD_SHIPPING && !IS_PROGRAM`. `TRACE_CPUPROFILER_EVENT_SCOPE` and related macros compile out. **Subagents do NOT add `#if !UE_BUILD_SHIPPING` guards manually** — the macros handle it.

## Verification

Phase 1 acceptance: deterministic tick driver runs 1000 ticks; stdout byte-identical between two runs.
Phase 5 acceptance: 30-second replay records and plays back; byte-equal at every snapshot.

## Open Items for Phase 1 Task 1.0b

The 1.0b subagent populates:
- Full RNG-site enumeration (every place `FMath::Rand*`, `FRandomStream`, or external RNG is invoked in the engine subset we're porting).
- Concrete `r.Sim.Deterministic` console var implementation.
- PIE deterministic-clock wiring (editor tick overrides — three concrete points per decision #36 Phase 4a-blueprint).
- Cross-platform RNG consistency (PhysX, OS RNG sources, hardware entropy).
- Snapshot serialization format alignment with `.xasset` (decision #15) and `REPLAY_FORMAT.md`.
