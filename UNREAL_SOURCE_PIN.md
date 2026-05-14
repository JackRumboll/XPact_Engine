# Unreal Source Pin

XPact Engine is a port and adaptation of Unreal Engine 5.9.0. All subagent briefs cite this pin.

| Field | Value |
|---|---|
| Engine version | 5.9.0 |
| Branch | UE5 |
| Commit SHA | `a79fff49ae4aa59da34cd1eb9f77eda136b5be48` |
| Commit date | 2026-05-12 20:35:34 -0400 |
| Pin date | 2026-05-14 |
| Source location | `C:\Users\benne\Documents\GitHub\UnrealEngine` |
| License | Unreal Engine source license held by Simgenics |

## Discipline

- Every subagent brief references this pin in its INPUTS block.
- If a subagent discovers the local Unreal checkout has moved off this SHA, **stop and notify manager**. Re-baselining is a deliberate, planned event.
- Re-baselining requires:
  1. New commit SHA captured here with date.
  2. Diff review of all changes since previous pin.
  3. Per-XPact-module re-port assessment.
