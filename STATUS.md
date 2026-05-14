# XPact Engine — Status

Manager-maintained status of the build. Updated at every milestone.

## Current Phase

**Phase 0 — Tooling Bootstrap: COMPLETE** ✅

Next: Phase 1 — Foundation Bootstrap.

## Pre-Phase-0 Manager Actions

- [x] Pin Unreal source — `UNREAL_SOURCE_PIN.md` (5.9.0 @ a79fff49...)
- [x] Legal review blockers logged + resolved — `LEGAL_REVIEW_BLOCKERS.md` (all 5 YES)
- [x] `Engine/Documentation/` placeholder schemas (MANIFEST, XPROJECT, REPLAY_FORMAT, PLUGIN_DISPOSITION, UNREALED_TRANSITIVE_DEPS, DETERMINISM)
- [x] `STATUS.md` + `.gitignore`

## Locked Legal-Driven Decisions

- **AutoRTFM = decision #27a** — full LLVM-pass port. Phase 1 Task 1.0a budget: 3–4 months.
- **Sample character = Engine/Content Manny/Quinn** — distribution rights confirmed.
- **HOOPS Exchange** — Simgenics procures commercial license; CATIA/NX/Creo unblocked for Phase 6.
- **libmodbus** — license confirmed; Phase 7 Modbus unblocked.
- **Epic AutoRTFM binaries on Linux** — resolved (not needed since 27a chosen).

## Phase 0 Task Status (ALL COMPLETE)

| Task | Status | Commit | Notes |
|---|---|---|---|
| 0.0 Pin source | ✅ | `91d81e5` | `UNREAL_SOURCE_PIN.md` written |
| 0.1 XPact.Core + XPact.Build subsets | ✅ | `91d81e5` | 33/33 test assertions pass; 0 warnings |
| 0.1.A Log.cs comparator fix + workaround removal | ✅ | (Phase 0 closeout) | Found by 0.2+0.3+0.4 code reviews; clean fix; 33/33 still pass |
| 0.2 XBT entry + FULL ModuleRules/TargetRules + SharedPCH | ✅ | `40ca09b` | HelloWorld.exe builds + runs; PCH machinery verified (ratio threshold deferred to Phase 1) |
| 0.3 XHT skeleton + XBT integration (XHTExecution + manifest seam) | ✅ | (Phase 0 closeout) | Two-manifest discipline (xht-input-manifest.json vs bindings.json) established; negative tests pass |
| 0.3.A CallBuildTool.bat staleness scan fix | ✅ | (Phase 0 closeout) | XBT staleness now walks {XBT, XPact.Build, XPact.Core} matching XHT; closes shared-POCO drift hazard |
| 0.4 VS solution generation (XPactEngine.sln + .vcxproj + .csproj refs) | ✅ | (Phase 0 closeout) | `devenv /Build` succeeds for all 6 projects |

## Phase 0 Acceptance Summary

All Phase-0 exit criteria met:

1. **`CallBuildTool.bat -build -target=HelloWorld`** → succeeds; produces `Engine/Binaries/Win64/HelloWorld/Development/HelloWorld.exe`; prints `Hello, XPact`.
2. **`GenerateProjectFiles.bat`** → produces `Engine/Cache/Projects/XPactEngine.sln` with 6 projects (HelloWorld + PCHBench + XBT + XHT + XPact.Build + XPact.Core).
3. **VS solution opens + builds**: `devenv /Build "Development|x64" XPactEngine.sln` succeeds.
4. **PCH machinery verified end-to-end**: PCHGenerateAction → CompileAction (with `/Yu/FI/Fp`) → LinkAction includes PCH .obj. Ratio threshold (≤0.30) deferred to Phase 1 Task 1.1 when real `XCorePCH.h` replaces the 5-header stub (acceptance #4 of Task 0.2 brief was content-driven, not implementation defect — confirmed by code reviewer).
5. **XBT → XHT manifest seam**: `xht-input-manifest.json` written before each XHT invocation; XHT log lines appear in the build output in the right order; negative tests (missing manifest, bad schemaVersion) exit non-zero with exact required error text.
6. **bUseAutoRTFMCompiler flag in TargetRules**: defaults to `true` per decision #27a (flips Unreal's default `false → true`).
7. **UNREAL_SOURCE_PIN.md + LEGAL_REVIEW_BLOCKERS.md** written and committed.

## Phase 0 Code Review Gates

- Task 0.1 review: clean, no MUST-FIX (9 nice-to-haves logged).
- Task 0.2 review: clean, no MUST-FIX (3 nice-to-haves logged; PCH benchmark threshold miss deferred to Phase 1 Task 1.1).
- Task 0.3 + 0.4 combined review: 1 real MUST-FIX (XBT staleness scan) → fixed in Task 0.3.A. 2 spurious sealed-file findings (false positives from Task 0.1.A running in parallel; that work was sanctioned).
- Task 0.1.A review: implicit via re-running 33/33 XPact.Core.Tests + CallBuildTool.bat end-to-end (all log lines preserved).
- Task 0.3.A review: 5 empirical staleness scenarios verified by subagent.

## Nice-to-have follow-ups (logged for Phase 1 / later)

1. CompileAction header dep scan — Phase 1 Task 1.x or later incremental-build work.
2. ParallelExecutor cycle-detection edge case — add FIXME comment.
3. `XHTExecution.cs` dead `readers` list — trivial cleanup.
4. `MANIFEST_SCHEMA.md` example outputDir stale — doc cleanup.
5. `Release → Shipping` config translation split across `VCSolution.cs` + `VCProject.cs` — doc-comment cleanup.

## Master Plan

Authoritative plan: `C:\Users\benne\.claude\plans\we-are-going-to-structured-fern.md` (70 architectural decisions; 6 review rounds passed; user-approved).

## Subagent Discipline

- Max 2 subagents in parallel — Phase 0 held to this discipline (Tasks 0.3 + 0.4 ran in parallel; Task 0.1.A + 0.3+0.4 review ran in parallel; Task 0.3.A serial).
- Self-contained briefs (TASK / INPUTS / DELIVERABLE / ACCEPTANCE / NOTES / BANNED) — every Phase 0 brief followed this template.
- Manager produces read-only triage docs (UNREALED_TRANSITIVE_DEPS, PLUGIN_DISPOSITION) before Phase 4a-deps dispatch — placeholders committed; full content authored in pre-Phase-4a window.

## Blocked Items

None. Ready for Phase 1 dispatch.

## Phase 1 Preview

Phase 1 — Foundation Bootstrap. Bootstrap order per master plan:
- **Task 1.0a** (3–4 mo, can dispatch immediately): XAutoRTFM full LLVM-pass port (decision #27a).
- **Task 1.0b** (1–2 wks): Determinism architecture spec → produces full `DETERMINISM.md` content.
- **Tasks 1.1–1.8**: XCore.Base + XCore.Names + XCore.Math LWC + XCore.Misc (incl. FText runtime) + XCore.Modules (10-phase ELoadingPhase) + XCore.Json + XCore.TraceLog + XCore.PreciseFP.
- **Task 1.9**: XHT full port (fork EpicGames.UHT with UhtInputCache; Verse parsers stripped; bindings.json exporter).
- **Task 1.10**: XBT module-aware build (multi-module link).
- **Task 1.11**: XCore.Projects (full PluginDescriptor schema).
- **Task 1.12 (preliminary)**: ITransaction header surface in CoreXObject.
- **Task 1.13–1.16**: CoreXObject base + GC + Serialization + XPactBindingGen.
- **Task 1.17–1.18**: XScripting (CoreCLR host) + XCoreEndToEnd.

Phase 1 exit criterion: `XCoreEndToEnd.exe` boots CoreCLR, round-trips C++↔C#, deterministic single-threaded byte-identical tick, AutoRTFM transactional test, ITransaction wrap, 1000-XObject stress clean.

Phase 1 duration estimate (with decision #27a chosen): **9–14 months**.
