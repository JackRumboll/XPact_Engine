# XPact Engine — Status

Manager-maintained status of the build. Updated at every milestone.

## Current Phase

**Phase 0 — Tooling Bootstrap (in progress)**

## Pre-Phase-0 Manager Actions

- [x] Pin Unreal source — `UNREAL_SOURCE_PIN.md` (5.9.0 @ a79fff49...)
- [x] Legal review blockers logged — `LEGAL_REVIEW_BLOCKERS.md`
- [x] `Engine/Documentation/MANIFEST_SCHEMA.md` placeholder
- [x] `Engine/Documentation/XPROJECT_SCHEMA.md` placeholder
- [x] `Engine/Documentation/REPLAY_FORMAT.md` placeholder
- [x] `Engine/Documentation/PLUGIN_DISPOSITION.md` template
- [x] `Engine/Documentation/UNREALED_TRANSITIVE_DEPS.md` template
- [x] `Engine/Documentation/DETERMINISM.md` stub
- [x] Simgenics legal review complete — **all 5 blockers resolved YES**.

## Locked Legal-Driven Decisions

- **AutoRTFM = decision #27a** — full LLVM-pass port. Phase 1 Task 1.0a budget: 3–4 months.
- **Sample character = Engine/Content Manny/Quinn** — confirmed distribution rights (decision #46).
- **HOOPS Exchange** — Simgenics will procure commercial license; CATIA/NX/Creo CAD import unblocked for Phase 6.
- **libmodbus** — Simgenics already holds suitable license; Phase 7 Modbus support unblocked.
- **Epic AutoRTFM binaries on Linux** — fallback resolved (not needed since 27a chosen).

## Phase 0 Task Status

| Task | Status | Owner | Notes |
|---|---|---|---|
| 0.0 Pin source | DONE | Manager | `UNREAL_SOURCE_PIN.md` committed |
| 0.1 XPact.Core + XPact.Build subsets | **DONE** | subagent | net9.0; 0 warnings; 33/33 test assertions pass; deviations documented below |
| 0.1 code review | PENDING | (pr-review-toolkit:code-reviewer) | Manager dispatches before 0.2 starts |
| 0.2 XBT entry + FULL ModuleRules/TargetRules + SharedPCH | NOT STARTED | (subagent) | Awaiting 0.1 review |
| 0.3 XHT skeleton + XBT integration | NOT STARTED | (subagent) | Parallel with 0.4 after 0.2 |
| 0.4 VS solution generation | NOT STARTED | (subagent) | Parallel with 0.3 |

## Task 0.1 Deviations (accepted; documented for manifest history)

Subagent surfaced and resolved the following deviations from the original brief; all preserve public API while excluding out-of-scope dependencies:

1. **Source paths**: Unreal 5.9 has `EpicGames.Core/IO/`, `EpicGames.Core/Compression/` as files-in-root (no subdirs). Brief layout in the XPact tree (`XPact.Core/IO/`) preserved; source pulled from real 5.9 locations.
2. **Compression**: `EpicGames.Core.Compression.*` namespace does not exist in 5.9. `UhtInputCache.cs` uses built-in `System.IO.Compression.Brotli*`. No XPact compression code needed (brief permitted this fallback).
3. **UnrealTargetPlatform / UnrealArch / UnrealArchitectures**: Live in `UnrealBuildTool/Configuration/UEBuildTarget.cs` in 5.9 (not `EpicGames.Build`). Ported from real location to `XPact.Build/Platform/`.
4. **UnrealTargetPlatform / UnrealArch slimmed**: Dropped `IsInGroup`, `UnrealArch.Host`, and binary-archive extension methods that depend on `UEBuildPlatform`, `BuildHostPlatform`, `UnrealArchitectureConfig`, `UnrealPlatformGroup` (all out-of-scope build classes). Core registry-backed identity, Win64/X64/etc. statics, Parse/TryParse/ToString/equality, JSON/type-converter plumbing preserved.
5. **UnrealArchitectures validation**: ValidationPlatform overload that calls `UEBuildPlatform.TryGetBuildPlatform` dropped. Public API surface preserved.
6. **Log.cs re-implemented**: 5.9's Log.cs is ~1594 lines tightly bound to LegacyEventLogger, LogEventParser, Microsoft.Extensions.Logging, DI sinks, EpicGames-internal sinks. XPact ships a slim public-API-compatible re-impl backed by Console.Out/Error with OutputLevel threshold. Zero NuGet deps. Brief explicitly permitted.
7. **ILogger.cs invented**: No `ILogger.cs` exists in EpicGames.Core/Logging in 5.9 (Epic reuses Microsoft.Extensions.Logging.ILogger). XPact defined local `ILogger` interface + LogLevel + EventId + extensions mirroring Microsoft.Extensions.Logging shape. Future swap-in mechanical.
8. **LogValueFormatter slimmed**: Dropped Activity/LogValue/FileReference formatters (their dependencies absent). Kept ILogValueFormatter + primitive/string/dictionary/enumerable formatters + type-annotation registration.
9. **FileUtils slimmed**: 5.9's version is 1378 lines with Win32 P/Invoke. XPact ports only `GetEncoding`, `FindCorrectCase`, `CreateDirectoryTree`, `WrappedFileOrDirectoryException` (the FileReference dependencies). Full P/Invoke ForceDeleteFile path can land in a later task.
10. **BinaryArchive minus LogEvent ops**: `ReadLogEvent` / `WriteLogEvent` reference `LogEvent` (out of scope). All primitive/array/dictionary/object-reference methods ported verbatim — verified by round-trip test.
11. **UHTTypes minus `Write(IMemoryWriter)`**: requires `IMemoryWriter` from `EpicGames.Core/MemoryWriter.cs` (not in scope). `Write(BinaryArchiveWriter)` preserved verbatim — that's the manifest serialization path XHT will use.
12. **Copyright headers**: Each ported file preserves Epic's copyright header and appends `// Modified by Simgenics for XPact Engine.`

## Phase 0 Code Review Gates

Manager dispatches `pr-review-toolkit:code-reviewer` after 0.1, then after 0.2, then after 0.3+0.4 batch. Open issues fixed before Phase 1.

**Active**: Task 0.1 review pending dispatch.

## Master Plan

Authoritative plan: `C:\Users\benne\.claude\plans\we-are-going-to-structured-fern.md` (70 architectural decisions; 6 review rounds passed).

## Subagent Discipline

- Max 2 subagents in parallel.
- Self-contained briefs (TASK / INPUTS / DELIVERABLE / ACCEPTANCE / NOTES / BANNED).
- Manager writes briefs, runs reviews, decides next steps; does not modify code.
- Manager produces read-only triage docs (UNREALED_TRANSITIVE_DEPS, PLUGIN_DISPOSITION) before Phase 4a-deps dispatch.

## Blocked Items

None currently. All 5 legal blockers resolved YES (see `LEGAL_REVIEW_BLOCKERS.md`).
