// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectHotReloadState.h -- global hot-reload-in-progress flag
// (XCoreXObject Rev 4 §9.2 + §5.6; Phase 5.j).
// =====================================================================
//
// XCoreXObject Rev 4 §9 ("Hot-reload class replacement") + §5.6 (SATB
// queue management during quiesce) + §11.5 (Phase 5.j scope).
//
// THIS HEADER PROVIDES:
//
//   * g_XHotReloadInProgress -- the global atomic<bool> that gates the
//                                hot-reload quiesce window. Set by
//                                FXObjectHotReloadCoordinator::Begin
//                                HotReloadQuiesce; cleared by
//                                FinishHotReloadCascade.
//
//   * IsHotReloadInProgress() -- inline accessor for hot-path checks.
//
//   * WaitWhileHotReloadInProgress() -- spin-yield primitive for
//                                callers that need to block on the
//                                flag (FXObjectArray::ReserveSlot /
//                                ReleaseSlot, FXObjectCollector::
//                                Trigger).
//
// =====================================================================
//
// QUIESCE FLAG SEMANTICS:
//
// The mutator's ReserveSlot / ReleaseSlot hot path checks
// g_XHotReloadInProgress with memory_order_acquire on every call.
// When false (the common case outside a hot-reload), the path
// proceeds without contention. When true (the rare case during
// XLiveCoding's cascade), the caller spin-yields until the flag is
// cleared.
//
// The FXObjectCollector::Trigger path also consults this flag: a
// concurrent trigger during quiesce MUST NOT start a new cycle. The
// in-flight cycle (if any) is allowed to complete because Begin
// HotReloadQuiesce explicitly waits for any in-progress cycle before
// returning.
//
// RELAXED-LOAD RATIONALE (mirror of XGCConcurrentState.h's gate flag):
//
// The check is memory_order_acquire (NOT relaxed) because the caller
// reads STATE that BeginHotReloadQuiesce stages BEFORE setting the
// flag (specifically, the OnHotReloadStart delegate fires, the SATB
// drain pauses via g_XGCAcceptDrains, etc.). The acquire-load pairs
// with BeginHotReloadQuiesce's release-store so the caller observes
// a consistent quiesce-window state.
//
// This is STRONGER than g_XGCAcceptDrains's relaxed-load posture
// because hot-reload involves more shared state to publish atomically.
// The acquire cost is justified: the check is per-allocate (~1 every
// few hundred microseconds typical), not per-write (g_XGC's check
// fires per reference store; relaxed is the right call there).
//
// =====================================================================
//
// SPIN-YIELD BOUND:
//
// The wait primitive WaitWhileHotReloadInProgress spin-yields
// (Sleep(0) / Sleep(1)) until the flag is cleared. The bound is
// XCoreXObject Rev 4 §11 acceptance criterion (e) cascade hot-patch
// timeout (<120 s nominal / <150 s kill).
//
// No condition variable backing the wait (mirror of XGCWaitForDrains
// Accepted): the wait is rare (engineer-station only), the contention
// is bounded, and a condvar would force every ReserveSlot/ReleaseSlot
// to acquire a mutex on the wait path (which would itself serialise
// allocations under contention).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <atomic>
#include <cstddef>
#include <cstdint>

namespace XCore
{

    // -----------------------------------------------------------------
    // Global hot-reload-in-progress flag (per spec §9.2 + Phase 5.j).
    //
    // True when an XLiveCoding cascade is mid-way between
    // BeginHotReloadQuiesce and FinishHotReloadCascade. The mutator's
    // ReserveSlot / ReleaseSlot / Trigger paths consult this flag.
    //
    // INITIAL VALUE: false. No hot-reload is in progress at process
    // start.
    //
    // EXPOSED AS extern -- callers in mutator hot paths resolve to the
    // canonical storage location, not a per-TU duplicate. The
    // definition lives in Private/XObject/FXObjectHotReloadState.cpp.
    // -----------------------------------------------------------------
    extern ::std::atomic<bool> g_XHotReloadInProgress;

    // -----------------------------------------------------------------
    // IsHotReloadInProgress -- inline accessor for the global flag.
    //
    // memory_order_acquire pairs with BeginHotReloadQuiesce's
    // release-store so the caller observes a happens-before-correct
    // view of the staged quiesce state (OnHotReloadStart delegate
    // fired, SATB drains paused, etc.).
    //
    // Returns true iff the hot-reload quiesce window is open. The
    // typical mutator check site:
    //
    //   while (IsHotReloadInProgress()) {
    //       // back off until the cascade completes.
    //       WaitWhileHotReloadInProgress();
    //   }
    //
    // The wait primitive is below.
    // -----------------------------------------------------------------
    [[nodiscard]] XPACT_FORCEINLINE bool IsHotReloadInProgress() noexcept
    {
        return g_XHotReloadInProgress.load(::std::memory_order_acquire);
    }

    // -----------------------------------------------------------------
    // WaitWhileHotReloadInProgress -- spin-yield until the flag is
    // cleared.
    //
    // Called by FXObjectArray::ReserveSlot / ReleaseSlot / FreeEntry
    // and FXObjectCollector::Trigger when those paths observe the flag
    // set. The calling thread Sleep(0)s (kicks the scheduler) then
    // Sleep(1)s in increasing back-off until the flag transitions to
    // false.
    //
    // The wait is bounded by §11 acceptance criterion (e) cascade
    // hot-patch timeout (<120 s nominal). No condition-variable
    // backing (mirror of XGCWaitForDrainsAccepted) because:
    //   1. The wait is RARE (engineer-station hot-reload only).
    //   2. The hot-reload code-path is engineer-only.
    //   3. A condvar would require every wait site to acquire a
    //      mutex on the wait path (serialising allocations under
    //      contention).
    //
    // Body lives in Private/XObject/FXObjectHotReloadState.cpp; the
    // body is non-inline so the spin-yield mechanism (FPlatformProcess
    // ::Sleep) is hidden behind one symbol per platform.
    // -----------------------------------------------------------------
    void WaitWhileHotReloadInProgress() noexcept;

} // namespace XCore
