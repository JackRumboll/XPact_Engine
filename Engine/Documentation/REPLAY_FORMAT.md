# XReplaySubsystem — Replay File Format

XPact's replay system is **custom** (decision #39); it is NOT a port of Unreal's `UReplaySubsystem` (which requires `DemoNetDriver` + the network replication stack). XReplay records deterministic single-user sessions as input streams + periodic state snapshots, designed for instructor debrief and trainee assessment.

## File Format Overview

Replay file: `<scenario_name>.xreplay`

Layout (little-endian; all multi-byte integers little-endian):

```
+--------------------+
| Header (256 bytes) |
+--------------------+
| Input Stream       |  (one entry per fixed-timestep tick)
| (variable length)  |
+--------------------+
| Snapshot Index     |  (offsets into Snapshot Blob)
+--------------------+
| Snapshot Blob      |  (periodic full-state captures)
+--------------------+
| Footer (32 bytes)  |  (CRC + format version)
+--------------------+
```

## Header (256 bytes, fixed)

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 8 | Magic | ASCII "XREPLAY\0" |
| 8 | 4 | FormatVersion | uint32; bumped on incompatible changes |
| 12 | 4 | XPactEngineVersion | matches XPactPackageFileVersion (decision #15) |
| 16 | 4 | RecordingPlatform | enum (1=Win64, 2=Linux, 3=Mac, …) |
| 20 | 8 | UnixEpochStartMs | int64 — wall-clock at record start |
| 28 | 4 | TimestepHz | int32 — fixed-timestep frequency (60 default) |
| 32 | 4 | TotalTickCount | int32 — number of input-stream entries |
| 36 | 4 | SnapshotCount | int32 — entries in Snapshot Index |
| 40 | 4 | InputStreamOffset | uint32 — file offset |
| 44 | 4 | SnapshotIndexOffset | uint32 — file offset |
| 48 | 4 | SnapshotBlobOffset | uint32 — file offset |
| 52 | 32 | InitialFRandomStreamSeed | 32-byte seed bundle (one entry per named XRandomStream domain) |
| 84 | 64 | ScenarioFQN | UTF-8 fully-qualified scenario class name |
| 148 | 64 | TraineeId | UTF-8 trainee identifier (caller-supplied) |
| 212 | 44 | Reserved | zero-filled |

## Input Stream

One variable-length entry per fixed-timestep tick. Encoding:

```
TickInputEntry {
  varint    tickIndex;              // delta-encoded from prior entry
  varint    actionCount;            // number of EnhancedInput actions this tick
  Action[]  actions {
    uint16  actionId;               // index into Action FQN table (see Footer)
    uint8   triggerEvent;           // started/ongoing/canceled/completed
    union switch(actionValueType) {
      case Bool:     uint8 value;
      case Axis1D:   double value;
      case Axis2D:   double[2] value;
      case Axis3D:   double[3] value;
    }
  }
  varint    cameraDeltaCount;       // 0 most ticks; only when camera-controlled
  ...
}
```

`varint` = UE/Protobuf-style 7-bit-per-byte variable-length integer.

## Snapshot Blob

A snapshot captures the complete deterministic-relevant state at a tick boundary. Captured at intervals controlled by `XReplaySettings::SnapshotIntervalSec` (default 5s = 300 ticks at 60Hz). Snapshots include:

- All `XObject` instances marked `XPROPERTY(SaveGame)` or `XPROPERTY(Replay)`.
- All `URotatingMovementComponent`, `UPawnMovementComponent`, character controller state.
- PhysX rigid body state (transform + linear/angular velocity) for all simulated bodies.
- `XRandomStream` cursor positions per named domain.
- `FAutoRTFM` transactional state (if applicable per decision #27).

Format: tagged-property `FArchive` stream, same binary format as `.xasset` (decision #15) but in-memory rather than via Linker.

## Snapshot Index

Per-snapshot entries:

```
SnapshotIndexEntry {
  int32   tickIndex;              // tick at which snapshot taken
  uint32  blobOffset;             // offset into Snapshot Blob
  uint32  blobLength;             // size of this snapshot
  uint32  crc32;                  // CRC32 of snapshot bytes
}
```

## Footer (32 bytes)

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | CRC32-of-file | excludes the footer itself |
| 4 | 4 | FormatVersion | duplicate from header for tail-read validation |
| 8 | 24 | Reserved | zero-filled |

## Determinism Discipline

Per decision #16, the engine ticks single-threaded gameplay at fixed timestep with seeded `FRandomStream`s. The replay system relies on this: replaying input stream A on identical XAsset state with identical FRandomStream seeds **must** produce byte-identical UObject state at every snapshot tick.

Phase 5 acceptance test: record a 30-second scenario, then play back; verify each snapshot byte-equal between recording and playback.

## Versioning

Replay files include both `FormatVersion` and `XPactEngineVersion`. On load:
1. If `FormatVersion` newer than supported → reject.
2. If `XPactEngineVersion` newer than current engine → reject (asset format may have changed).
3. If `XPactEngineVersion` older → run engine migration pass + replay-format migration if FormatVersion differs.

Migration table tracks ranges of replay-format-compatible-with-engine-version pairs. Industrial training scenarios may be replayed years later — migration is a first-class concern.

## Branching Replay (Phase 6+)

Phase 6 considers branching replay (instructor scrubs to tick N, then forks execution from that state) for trainee assessment. Architecturally supported by the snapshot system; UI Phase 6+ decision.
