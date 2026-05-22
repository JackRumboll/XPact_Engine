// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FLocalizationManager.h -- locale manager (Section 11.2; Step 14).
// =====================================================================
//
// XCore-4a Rev 3, Section 11.2 (FLocalizationManager public surface) +
// Section 11.3 (threading: thread-safe) + Section 17.8 (H1, H2, H3
// acceptance) + Section 1.5 (PhaseLadder: en-US fallback initialises
// at PostStaticInit).
//
// FLocalizationManager is the singleton singleton-of-loctables: it
// owns the in-memory `(namespace, key) -> localised FString` table and
// the active locale code. The table is keyed on
//
//   XXH3_64(namespace + "::" + key)
//
// which is the XPact engine's canonical fast hash (Section 11.9). The
// XXH3 hash is scalar + bit-exact across all three platforms (Win64,
// Linux, Android), so the same table file produces the same lookup
// behaviour on every target -- a load-bearing property for the
// Section 17.8 H1 acceptance.
//
// LIFECYCLE (Section 1.5 phase ladder):
//   * PreStaticInit:   FLocalizationManager::Get() is callable but
//                      Lookup returns nullptr (no table loaded). A
//                      LOCTEXT-resolved FText falls back to its
//                      literal -- which is the desired Section 17.8
//                      H1 behaviour ("LOCTEXT resolves to source-
//                      fallback (en-US) when no loctable is loaded").
//
//   * PostStaticInit:  __Initialize() is called by the engine
//                      bootstrap; the en-US fallback table is set as
//                      the active locale (no on-disk file is loaded
//                      -- en-US IS the source-fallback locale).
//                      Lookup returns nullptr for every key (correct;
//                      see source-fallback semantics in FText.h).
//
//   * FrameZero:       any LoadLocalizationTable("ja-JP") call lands
//                      here. Mid-frame SetLocale is forbidden (the
//                      function asserts; Section 11.3).
//
// THREADING (Section 11.3):
//   * Lookup is thread-safe (FRWLock shared-lock).
//   * SetLocale + LoadLocalizationTable + UnloadAll are thread-safe
//     for callers (FRWLock exclusive-lock) BUT the caller is
//     responsible for not invoking them mid-frame; the spec
//     mandates "mid-frame locale changes are forbidden (assert)".
//     The "mid-frame" check is enforced at the call site by
//     XPACT_CHECK against EngineInitPhase() >= FrameZero AND a
//     frame-boundary cookie (not yet implemented in Phase 1f because
//     the frame-boundary signal lands with the renderer; for Phase
//     1f the cookie is a stub that always reads "between frames").
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Containers/FString.h"

#include <cstdint>

namespace XCore::Loc
{

// ---------------------------------------------------------------------
// FLocalizationManager -- singleton locale manager.
//
// Static-only surface (matches the UE Internationalization manager
// shape at `UnrealEngine/Engine/Source/Runtime/Core/Public/
// Internationalization/Internationalization.h` -- though UE's is a
// full singleton class; XPact's is a static-method facade over an
// internal singleton instance for simpler hot-reload behaviour).
// ---------------------------------------------------------------------
class FLocalizationManager
{
public:
    // -----------------------------------------------------------------
    // Get -- accessor for the singleton instance.
    //
    // Returns a reference to the global FLocalizationManager. The
    // instance is constructed on first access (Meyers singleton via a
    // function-local static, NOT a constinit global -- the en-US
    // FString fallback table requires the allocator which is live
    // only after FMemory::__Init in PreStaticInit).
    //
    // Note: the public surface below is static; user code should not
    // need to call Get() directly. Get() is exposed for tests that
    // need to inspect the singleton's internals.
    // -----------------------------------------------------------------
    [[nodiscard]] static FLocalizationManager& Get() noexcept;

    // -----------------------------------------------------------------
    // SetLocale -- change the active locale.
    //
    // Behaviour:
    //   1. If LocaleCode matches the currently-active locale, the
    //      call is a no-op (does NOT flush the cache; FText cache
    //      generation does not bump).
    //   2. Otherwise:
    //      a. Acquires the exclusive lock on the manager.
    //      b. If the new locale's loctable is not yet loaded, calls
    //         LoadLocalizationTable(LocaleCode) implicitly.
    //      c. Bumps the locale-generation counter (the FText cache
    //         invalidation signal per Section 17.8 H2).
    //      d. Records the new locale as active.
    //
    // Mid-frame: forbidden by spec; the function asserts on
    // EngineInitPhase() >= FrameZero AND a frame-boundary check (the
    // latter is a stub for Phase 1f; see header banner).
    //
    // Sim-path: NOT sim-path-safe (the loctable read is not
    // deterministic w.r.t. the install image's filesystem state). The
    // sim-path overlay header [[deprecated]]s this method.
    // -----------------------------------------------------------------
    static void SetLocale(const ::XCore::FString& LocaleCode) noexcept;

    // -----------------------------------------------------------------
    // GetCurrentLocale -- read the active locale code.
    //
    // Returns the active locale as an FString reference. The reference
    // is valid until the next SetLocale call.
    //
    // Initial value (post-__Initialize): "en-US" (the source-fallback
    // locale per Section 11.2).
    // -----------------------------------------------------------------
    [[nodiscard]] static const ::XCore::FString& GetCurrentLocale() noexcept;

    // -----------------------------------------------------------------
    // GetCurrentGeneration -- read the locale-generation counter.
    //
    // The counter monotonically increments on every SetLocale call
    // that actually changes the active locale. FText's resolved-cache
    // uses this counter to detect cache staleness (per the
    // m_resolvedGen field in FText.h).
    //
    // Returns 0 before __Initialize (PreStaticInit); returns >= 1
    // after.
    //
    // Thread-safe: a single atomic load.
    // -----------------------------------------------------------------
    [[nodiscard]] static ::std::uint32_t GetCurrentGeneration() noexcept;

    // -----------------------------------------------------------------
    // Lookup -- (namespace, key) -> localised FString.
    //
    // Returns:
    //   * non-null FString* on hit: the localised value for the
    //     current locale.
    //   * nullptr on miss: no entry under (namespace, key) in the
    //     active loctable. The FText caller falls back to the
    //     literal AND emits a dev-warning (Section 17.8 H3).
    //
    // Special case: namespace == "__Invariant" short-circuits to
    // nullptr (the INVTEXT sentinel; the FText caller uses the
    // literal directly).
    //
    // Thread-safe: FRWLock shared-lock.
    //
    // Phase-safety: callable from PreStaticInit (returns nullptr
    // until __Initialize sets the active locale). The
    // XPACT_CHECK(EngineInitPhase() >= PostStaticInit) is intentionally
    // NOT placed here; the spec is "LOCTEXT resolves to source-
    // fallback (en-US) when no loctable is loaded" (H1) and the
    // PreStaticInit path must therefore return nullptr cleanly
    // rather than asserting.
    // -----------------------------------------------------------------
    [[nodiscard]] static const ::XCore::FString* Lookup(
        const char* Namespace,
        const char* Key) noexcept;

    // -----------------------------------------------------------------
    // LoadLocalizationTable -- load a locale's loctable file.
    //
    // Reads `{Engine}/Content/Localization/<LocaleCode>.loctable` (the
    // path is constructed via FPlatformProcess::GetExecutablePath()
    // -> Engine/Binaries/<Platform>/<ExeName> -> ../../../Content/
    // Localization/<LocaleCode>.loctable).
    //
    // On success: the loctable's entries are added to the in-memory
    // table under the loaded locale's keyspace.
    // On failure (file missing, BLAKE3 verification fails, parse
    // error): the call is a no-op AND a dev-warning is emitted; the
    // already-loaded entries (if any) remain intact.
    //
    // Note: loading is additive; previously-loaded tables remain in
    // memory. UnloadAll() drops all entries; LoadLocalizationTable
    // does NOT replace.
    //
    // Mid-frame: forbidden by spec; the function asserts.
    //
    // Thread-safe: FRWLock exclusive-lock.
    // -----------------------------------------------------------------
    static void LoadLocalizationTable(const ::XCore::FString& LocaleCode);

    // -----------------------------------------------------------------
    // UnloadAll -- drop all loaded loctable entries.
    //
    // The in-memory table is cleared; the active locale and
    // locale-generation counter are reset to their __Initialize
    // values ("en-US", generation = 1).
    //
    // Use case: test teardown (the LocaleSwitch.cpp test loads two
    // loctables sequentially and asserts on the second's lookup
    // results).
    //
    // Thread-safe: FRWLock exclusive-lock.
    // -----------------------------------------------------------------
    static void UnloadAll() noexcept;

    // -----------------------------------------------------------------
    // __Initialize -- PostStaticInit bootstrap hook.
    //
    // Called by the engine bootstrap exactly once during the
    // PostStaticInit phase transition. Sets the active locale to
    // "en-US", initialises the generation counter to 1, and emits no
    // I/O (the en-US fallback is the source literals; no on-disk
    // file is needed).
    //
    // Idempotent: calling twice is a no-op (the second call sees
    // the generation already at 1 and short-circuits).
    //
    // Phase-safety: the function asserts on
    // EngineInitPhase() >= PostStaticInit; calling it earlier is a
    // bug.
    // -----------------------------------------------------------------
    static void __Initialize() noexcept;

private:
    // Construction is private; Get() is the only entry point.
    FLocalizationManager() noexcept;
    ~FLocalizationManager() noexcept;

    FLocalizationManager(const FLocalizationManager&)            = delete;
    FLocalizationManager& operator=(const FLocalizationManager&) = delete;
    FLocalizationManager(FLocalizationManager&&)                 = delete;
    FLocalizationManager& operator=(FLocalizationManager&&)      = delete;
};

} // namespace XCore::Loc
