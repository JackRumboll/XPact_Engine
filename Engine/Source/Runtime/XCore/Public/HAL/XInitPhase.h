// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XInitPhase.h -- static-init phase ladder (Section 1.5; fix C-2).
// =====================================================================
//
// XCore-4a Rev 3 Section 1.5. Every subsystem in the engine declares
// which of three phases it comes up in:
//
//   PreStaticInit  (0) -- allocator, threading primitives, macros,
//                         platform HAL atomics. No inter-module
//                         dependencies; may be used by any constinit
//                         object.
//
//   PostStaticInit (1) -- IConsoleManager CVar drain (Section 9.5),
//                         Stat side-table build (Section 10.5),
//                         FLocalizationManager en-US fallback
//                         (Section 11.2). All require the allocator
//                         to be live.
//
//   FrameZero      (2) -- every gameplay-tier + content-tier
//                         subsystem; FText locale tables beyond the
//                         en-US fallback.
//
// The invariant: every public API that depends on PostStaticInit-or-
// later state begins with
//     XPACT_CHECK(::XCore::HAL::EngineInitPhase() >= EInitPhase::PostStaticInit)
// The check is Debug + Development only (zero-cost in Shipping; see
// Section 13 XPACT_CHECK definition) and surfaces "called too early"
// bugs at the call site rather than producing wrong values silently.
//
// Phase advancement is **monotonic and one-shot**: the global
// transitions PreStaticInit -> PostStaticInit -> FrameZero exactly
// once per process. __AdvanceInitPhase aborts cleanly if the caller
// attempts to retreat (advance from FrameZero back to PostStaticInit,
// etc.) or skip (PreStaticInit straight to FrameZero); the bootstrap
// in XEngineInit is responsible for sequencing the calls.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <atomic>

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // EInitPhase -- the three-step ladder.
    //
    // uint8_t-backed so the atomic global is exactly 1 byte (plus the
    // atomic implementation's lock-byte on platforms that need it; on
    // every supported XPact target the 1-byte atomic is lock-free
    // natively).
    // -----------------------------------------------------------------
    enum class EInitPhase : ::uint8
    {
        PreStaticInit  = 0,
        PostStaticInit = 1,
        FrameZero      = 2,
    };

    static_assert(sizeof(EInitPhase) == 1, "EInitPhase ABI lock: 1 byte");

    // -----------------------------------------------------------------
    // g_InitPhase -- the single global phase counter.
    //
    // Declared extern + atomic. The definition lives in
    // Private/HAL/XInitPhase.cpp and is constinit-initialised to
    // EInitPhase::PreStaticInit so the value is read-correct even from
    // constinit constructors that fire before main() (Section 1.5
    // invariant: PreStaticInit is the read at static-storage-duration
    // time).
    //
    // The spec body in Section 1.5 names the variable
    // g_EngineInitPhase; the dispatch instructions for this subagent
    // name it g_InitPhase. The dispatch wording is the more recent
    // direction so we follow it; an alias is left below pointing to
    // the spec wording so a future spec-reconciliation pass can pick
    // either name without breaking call sites. The alias is `inline`
    // so the linker dedupes it to the same storage as g_InitPhase.
    // -----------------------------------------------------------------
    extern ::std::atomic<EInitPhase> g_InitPhase;

    // -----------------------------------------------------------------
    // EngineInitPhase() -- the accessor.
    //
    // Inline + noexcept; the spec body wants this to compile to one
    // acquire-load. The `inline` here matters: a constinit caller in
    // a non-XCore TU must be able to read the phase without taking a
    // dependency on the .cpp body.
    //
    // memory_order_acquire pairs with the memory_order_release used
    // by __AdvanceInitPhase below; the contract is that any state
    // published *before* the advance call is visible to any thread
    // that observes the new phase value.
    // -----------------------------------------------------------------
    [[nodiscard]] inline EInitPhase EngineInitPhase() noexcept
    {
        return g_InitPhase.load(::std::memory_order_acquire);
    }

    // -----------------------------------------------------------------
    // __AdvanceInitPhase -- bootstrap-only phase transition.
    //
    // Monotonic; aborts on retreat or skip. Callers are exclusively
    // the XEngineInit bootstrap (XCore-4a Step 14 has not committed
    // yet; for Phase 1a the function exists with no in-tree callers).
    //
    // The double-underscore prefix flags this as engine-internal
    // bootstrap code that user-tier modules must never call. C++ does
    // not enforce internal-ness at the linker level; the prefix is
    // documentation. A future XPACT_INTERNAL macro could wrap the
    // declaration in an attribute that triggers a warning when called
    // outside an opt-in module; for now the convention is sufficient.
    //
    // The function is noexcept because its abort path is the abort()
    // route via XCore::HAL::AbortWithMessage; throwing out of a phase
    // advance would leave the global in an indeterminate state.
    // -----------------------------------------------------------------
    void __AdvanceInitPhase(EInitPhase NewPhase) noexcept;

} // namespace XCore::HAL
