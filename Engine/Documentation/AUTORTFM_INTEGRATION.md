# AutoRTFM Integration - XPact Engine

**Status: Path 2 - runtime library ported; compiler-pass integration deferred pending `verse-clang-cl.exe` acquisition.**

This document records how XPact integrates AutoRTFM (Epic's Automatic Resilient Transactional Failure Mode runtime), what works today after Phase 1 Task 1.0a, and what changes once the AutoRTFM clang fork is vendored.

## Background

The XPact master plan (decision #27) considered three options for absorbing AutoRTFM:

- **Path 1**: Port runtime + use the AutoRTFM clang fork already shipping in our UE 5.9 checkout. Maximum semantic fidelity in one step.
- **Path 2**: Port the runtime library as a "build-clean" module that compiles to no-ops under MSVC; wire all `bUseAutoRTFMCompiler` infrastructure so the toolchain swap auto-engages when a real `verse-clang-cl.exe` is dropped in later.
- **Path 3**: Defer the whole thing to Phase 6+ once Simgenics owns the LLVM pipeline.

## Why Path 2

A prior Task 1.0a investigation subagent uncovered:

1. **Two distinct Clang forks exist in UBT**, not one. `WindowsCompiler.ClangRTFM` maps to `verse-clang-cl.exe` (the *actual* AutoRTFM compiler - implements `-Xclang -autortfm-mappings`, the `__AUTORTFM` macro, etc.). `WindowsCompiler.ClangInstrument` maps to `instr-clang-cl.exe`, a separate sanitizer-instrumentation tool gated by `bEnableInstrumentation`, NOT by `bUseAutoRTFMCompiler`. Evidence: `Engine/Source/Programs/UnrealBuildTool/Platform/Windows/{VCEnvironment.cs:232-239, UEBuildWindows.cs:1334-1359}` in the upstream UE 5.9 source.

2. **`verse-clang-cl.exe` is NOT in our UE 5.9 a79fff49 checkout.** A repository-wide search returns zero matches.

3. **`instr-clang-cl.exe` does NOT implement AutoRTFM.** Empirically:
   - `--version` reports "Epic Games, Inc. clang version 20.1.8" (plain clang fork).
   - `-Xclang -autortfm-mappings` returns `unknown argument`.
   - `strings instr-clang-cl.exe | grep -iE "autortfm|verse-clang"` returns 0 results.
   - Vendoring `instr-clang-cl.exe` and trying to compile AutoRTFM-instrumented code would therefore mislead - it does not implement the LLVM pass that gives `Transact` real transactional semantics.

4. **The AutoRTFM runtime library .cpp files are all gated** behind `#if (defined(__AUTORTFM) && __AUTORTFM)`. Under MSVC, all Private .cpp files compile to (mostly) empty translation units. Public headers ship `!UE_AUTORTFM_ENABLED` inline fallback functions where:
   - `Transact(lambda)` runs the lambda and returns `ETransactionResult::Committed`.
   - `AbortTransaction()` is a no-op.
   - `CascadingAbortTransaction()` is a no-op.
   - `IsTransactional()` returns false.
   - `Open(lambda)` runs the lambda directly.
   - `RecordOpenWrite(...)` is a no-op.

Given (1)-(4), Path 1 was unavailable on the current code base. Path 3 would have left a substantial integration risk unresolved late in the schedule. Path 2 was selected as the manager decision: it preserves all of XPact's Phase 1 invariants (API surface compiles and links, toolchain swap is fully wired, mapping files are fully accounted), and pushes only the LLVM-pass-dependent acceptance into a follow-up keyed on acquiring the binary.

## What is working today

- **`XAutoRTFM` module** is ported as `Engine/Source/Runtime/XAutoRTFM/` with the upstream Epic namespace and C-API symbols preserved (`namespace AutoRTFM { ... }` and `autortfm_*` C symbols are NOT renamed, so the ABI matches what `verse-clang-cl.exe` expects at instrumented-call sites). 78 source/header/aem/doc files were ported, including all 16 .cpp Private files, 9 Public/AutoRTFM headers, the master `AutoRTFM.h`, the natvis, .clang-format, and the upstream Documentation/ tree.
- **`XBT` toolchain swap** is fully wired. `TargetRules.bUseAutoRTFMCompiler` (default true) and `TargetRules.bForceNoAutoRTFMCompiler` (default false, kill-switch) drive `BuildMode`. `BuildMode` calls `VCEnvironment.TryGetAutoRTFMCompilerPath(engineDir, out _)` to detect whether the AutoRTFM clang fork is vendored; the result is propagated as `CppCompileEnvironment.bUseAutoRTFMCompilerEffective`. When effective, the compile-action command path swaps from `cl.exe` to `verse-clang-cl.exe` and `XBTWindows.MakeClCompileArgs` emits `-Xclang -autortfm-mappings -Xclang "<absolute path>"` for every entry in the per-module `ModuleRules.AutoRTFMExternalMappingFiles` list (mirroring UBT's `VCToolChain.cs:2422` pattern - the second `-Xclang` is required so clang's driver passes the path argument through to the frontend pass that consumes `-autortfm-mappings`). The AutoRTFM compiler binary and every mapping file are also added as compile-action prerequisites so edits to either correctly trigger recompile.
- **`ModuleRules.AutoRTFMExternalMappingFiles`** is the new XBT surface for per-module mapping-file declarations, matching UnrealBuildTool's `ModuleRules.AutoRTFMExternalMappingFiles`. `XAutoRTFM.Build.cs` already declares `Internal.aem`, `StdLib.Common.aem`, and `StdLib.Windows.aem`. Sanitizer-conditional `.aem` files are deferred to a follow-up task that wires the sanitizer flags onto XBT TargetRules.
- **`XAutoRTFMTests` program target** exists under `Engine/Source/Programs.Targets/XAutoRTFMTests/` with a 5-test driver covering documented MSVC-fallback semantics:
  1. `CommitTest_FallbackOrCompiled` - identical under both toolchains: `Transact` returns Committed.
  2. `AbortFallback_Documented` - explicitly fallback-mode: asserts that `AbortTransaction` is a no-op under MSVC and prints a log line documenting it.
  3. `IsTransactional_Returns_Documented_Value` - explicitly fallback-mode: asserts `IsTransactional()` is false even inside a Transact under MSVC.
  4. `OpenWrite_Compiles_And_Runs` - exercises `AutoRTFM::Open` and `AutoRTFM::RecordOpenWrite` sized + scalar overloads.
  5. `API_Surface_Present` - link-only catch-net for the major API entry points.
- **Build-log advisory line**: when `bUseAutoRTFMCompiler` is requested but `verse-clang-cl.exe` is not present, `BuildMode` logs
  `[XBT] Toolchain: cl.exe (bUseAutoRTFMCompiler=true requested, but verse-clang-cl.exe not vendored at '...' - using MSVC; XAutoRTFM compiles to no-op fallback)`
  so the path-2 state is plainly visible in every build.
- **Mapping-file accounting line**: `BuildMode` prints per-module mapping-file counts and filenames, so the wiring is verifiable from a clean build even in fallback mode. Example: `[XBT] XAutoRTFM has 3 AutoRTFM mapping file(s): Internal.aem, StdLib.Common.aem, StdLib.Windows.aem`.

## What changes when `verse-clang-cl.exe` is vendored

The vendoring contract is intentionally a single drop-in:

1. Place the AutoRTFM clang driver at `Engine/Source/ThirdParty/UnrealInstrumentation/bin/verse-clang-cl.exe`. (Ship the matching `verse-link.exe` alongside if Epic distributes a paired linker; XBT currently still runs MSVC link.exe, which is the upstream UBT default.)
2. Re-run `Engine/Scripts/Windows/CallBuildTool.bat -build -target=XAutoRTFMTests ...`.

That is the entire vendoring procedure. No XBT or rules-file changes are required. From there:

- `VCEnvironment.TryGetAutoRTFMCompilerPath` returns true.
- `BuildMode` flips `bUseAutoRTFMCompilerEffective` true, swaps the compile-action command path to `verse-clang-cl.exe`, and emits `-Xclang -autortfm-mappings -Xclang "<abs path>"` for each `XAutoRTFM.Build.cs` AutoRTFMExternalMappingFiles entry.
- XAutoRTFM's `Private/*.cpp` files start compiling their `#if UE_AUTORTFM`-guarded bodies, producing a real (non-empty) `XAutoRTFM.lib`.
- The two fallback-mode tests (`AbortFallback_Documented`, `IsTransactional_Returns_Documented_Value`) become non-representative of compiled-AutoRTFM semantics. They should be revisited at that point and either:
   - relaxed to "no crash" assertions and supplemented with new tests that exercise real rollback (`AbortInTransact_RollsBackWrites`, `IsTransactionalInsideTransact_True`), OR
   - converted into compile-time toggles selected on `__AUTORTFM`.
   Either way, the existing tests' fallback-mode assertions become a regression net for the *non*-AutoRTFM build mode, which we still expect to support for non-AutoRTFM-instrumented programs.

## Manager action item

Pursue `verse-clang-cl.exe` acquisition from the Simgenics<->Epic channel (likely a Restricted / NotForLicensees distribution, not in the public UE 5.9 source drop). `LEGAL_REVIEW_BLOCKERS.md` item 1 (already YES) covers the legal side of AutoRTFM redistribution; what is needed is the sourcing of the actual binary plus its dependent libraries (clang resource directory, etc.).

## Key files

- `Engine/Source/Runtime/XAutoRTFM/` - the runtime port.
- `Engine/Source/Runtime/XAutoRTFM/XAutoRTFM.Build.cs` - module rules with `AutoRTFMExternalMappingFiles` declarations.
- `Engine/Source/Programs/XBT/Platform/Windows/VCEnvironment.cs::TryGetAutoRTFMCompilerPath` - the vendoring detection.
- `Engine/Source/Programs/XBT/System/CppCompileEnvironment.cs::bUseAutoRTFMCompilerEffective` - the propagated flag.
- `Engine/Source/Programs/XBT/Modes/BuildMode.cs` - the toolchain-swap decision and mapping-file plumbing.
- `Engine/Source/Programs/XBT/Platform/Windows/XBTWindows.cs::MakeClCompileArgs` - emits `-Xclang -autortfm-mappings` when effective.
- `Engine/Source/Programs.Targets/XAutoRTFMTests/` - the acceptance harness.
- `Engine/Documentation/AutoRTFM/{ExternalMappings.md, ModesAndAttributes.md, README.md}` - upstream Epic docs preserved verbatim with copyright + Simgenics modification line.

## Banned

- Vendoring `instr-clang-cl.exe` - it is not the AutoRTFM compiler and would mislead users into believing transactional semantics are active when they are not.
- Renaming `namespace AutoRTFM` to `XAutoRTFM` at the namespace level - the AutoRTFM compiler emits these symbol names into instrumented code, so any rename would break ABI compatibility when the real binary is vendored.
- Relaxing upstream's warnings-as-errors policy below the upstream surface. (TODO: re-add the 70+ `CppCompileWarningSettings.*WarningLevel = WarningLevel.Error;` entries in Phase 1.2 once the `CppCompileWarningSettings` field is wired on XBT `ModuleRules`.)
