// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectHotReloadState.cpp -- global hot-reload flag definition +
// spin-yield wait primitive (XCoreXObject Rev 4 §9.2; Phase 5.j).
// =====================================================================
//
// The atomic flag definition + the WaitWhileHotReloadInProgress body.
// Header documents the contract; this TU owns the storage symbol +
// the spin-yield mechanism.
//
// =====================================================================

#include "XObject/FXObjectHotReloadState.h"

#include "HAL/FPlatformProcess.h"

namespace XCore
{

    // -----------------------------------------------------------------
    // Global hot-reload flag. Default false (no cascade in progress).
    //
    // The atomic backing matches the engine-wide convention
    // (g_XGCIsConcurrentMarkActive, g_XGCAcceptDrains, g_XGCSafePoint
    // Requested) -- a single namespace-scope std::atomic<bool> so the
    // mutator's hot-path checks resolve to one canonical storage.
    // -----------------------------------------------------------------
    ::std::atomic<bool> g_XHotReloadInProgress{false};

    // -----------------------------------------------------------------
    // WaitWhileHotReloadInProgress -- spin-yield until the flag is
    // false.
    //
    // The back-off pattern matches XGCWaitForDrainsAccepted:
    //   * Tight spin for a few iterations (cheap on contemporary CPUs).
    //   * Then Sleep(0) to kick the scheduler.
    //   * Then Sleep(1) for proper back-off.
    //
    // The wait is bounded by §11 acceptance criterion (e) cascade
    // hot-patch timeout (<120 s nominal). In production the typical
    // wait is microseconds (the cascade runs synchronously in the
    // XLiveCoding orchestrator's thread; the mutator threads block
    // only briefly while the orchestrator does the in-place class
    // rebind).
    // -----------------------------------------------------------------
    void WaitWhileHotReloadInProgress() noexcept
    {
        // Spin briefly first; the flag is typically cleared within a
        // few microseconds.
        for (::int32 SpinCount = 0; SpinCount < 16; ++SpinCount)
        {
            if (!g_XHotReloadInProgress.load(::std::memory_order_acquire))
            {
                return;
            }
        }

        // Yield to the scheduler.
        while (g_XHotReloadInProgress.load(::std::memory_order_acquire))
        {
            ::XCore::HAL::FPlatformProcess::Sleep(0.0f);
        }
    }

} // namespace XCore
