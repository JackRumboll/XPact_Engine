// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XInitPhase.cpp -- definitions for the static-init phase ladder.
// =====================================================================
//
// XCore-4a Rev 3, Section 1.5.
//
// Two definitions:
//   1. g_InitPhase  -- constinit-initialised to PreStaticInit.
//   2. __AdvanceInitPhase -- the bootstrap-only monotonic transition.
//
// Both live here so the storage is owned by the XCore .lib/.so;
// every TU that uses XInitPhase.h sees the extern declaration and
// links against this TU.
//
// =====================================================================

#include "HAL/XInitPhase.h"
#include "Macros/XAssertionMacros.h"
#include "Macros/XCoreDefines.h"

#include <cstdio>   // ::std::snprintf for diagnostic-buffer formatting

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // g_InitPhase definition.
    //
    // `constinit` ensures the storage is initialised at program-start
    // (before any user-level constinit constructor runs) rather than
    // at first-use. The Section 1.5 invariant that constinit objects
    // may read EngineInitPhase() and get PreStaticInit depends on
    // this; without constinit the read could see an indeterminate
    // value before the global's own constructor runs (the
    // static-init-order-fiasco classic case).
    //
    // The atomic constructor accepting an EInitPhase value is a
    // C++20 std::atomic constexpr constructor (`atomic(T desired)
    // noexcept`); MSVC, libstdc++ 11+, and libc++ 14+ all ship this.
    // -----------------------------------------------------------------
    constinit ::std::atomic<EInitPhase> g_InitPhase{ EInitPhase::PreStaticInit };

    // -----------------------------------------------------------------
    // __AdvanceInitPhase -- monotonic-only transition.
    //
    // Contract:
    //   * Permitted: PreStaticInit -> PostStaticInit
    //                PostStaticInit -> FrameZero
    //   * Forbidden (aborts): any retreat (newer -> older), any skip
    //                         (PreStaticInit -> FrameZero), any
    //                         self-loop (X -> X).
    //
    // Implementation uses compare_exchange_strong because the
    // contract is "transition from exactly the prior phase to
    // exactly the new phase". A naive store would silently allow
    // retreat from a sister thread; the CAS makes the bootstrap-
    // single-caller contract enforceable.
    //
    // memory_order_release on success pairs with the
    // memory_order_acquire load in EngineInitPhase(): any side
    // effects in the bootstrap's pre-advance code are visible to
    // any later phase observation.
    // -----------------------------------------------------------------
    void __AdvanceInitPhase(EInitPhase NewPhase) noexcept
    {
        EInitPhase Current = g_InitPhase.load(::std::memory_order_acquire);

        // The only permitted transitions are +1 from the current
        // phase. Anything else is a bootstrap bug.
        const bool bIsValidTransition =
            (Current == EInitPhase::PreStaticInit  && NewPhase == EInitPhase::PostStaticInit) ||
            (Current == EInitPhase::PostStaticInit && NewPhase == EInitPhase::FrameZero);

        if (!bIsValidTransition)
        {
            // Compose a diagnostic showing the from/to phases. The
            // numbers correspond to the uint8 underlying values so
            // a CI grep on the log can identify the offending
            // call pattern.
            char Buf[128];
            ::std::snprintf(Buf, sizeof(Buf),
                            "__AdvanceInitPhase: illegal transition %u -> %u",
                            static_cast<unsigned>(Current),
                            static_cast<unsigned>(NewPhase));
            AbortWithMessage(Buf, __FILE__, __LINE__);
        }

        // CAS the transition. compare_exchange_strong updates Current
        // on failure; if a sister thread won the race, we abort
        // because that violates the single-bootstrap-caller contract.
        if (!g_InitPhase.compare_exchange_strong(Current, NewPhase,
                                                 ::std::memory_order_release,
                                                 ::std::memory_order_acquire))
        {
            char Buf[128];
            ::std::snprintf(Buf, sizeof(Buf),
                            "__AdvanceInitPhase: race detected (observed %u, expected pre-CAS)",
                            static_cast<unsigned>(Current));
            AbortWithMessage(Buf, __FILE__, __LINE__);
        }
    }

} // namespace XCore::HAL
