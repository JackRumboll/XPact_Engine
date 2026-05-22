// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FOOMPolicy.h -- out-of-memory policy enum + per-build selection
// (Section 4.1 + Section 4.5).
// =====================================================================
//
// XCore-4a Rev 3, Section 4.1 OOM contract (fix M-2):
//
//   * Shipping = Abort         -- a Shipping binary that runs out of
//                                 memory has no graceful path. Log +
//                                 abort with a tagged diagnostic. UE's
//                                 "log + fallback + maybe-abort" path
//                                 (UnrealMemory.h) is explicitly
//                                 rejected here; "honest abort" wins.
//   * Dev / Test = ReturnNull  -- direct callers (NOT container layers)
//                                 may receive nullptr and recover. Used
//                                 by speculative scratch buffers and
//                                 high-water-mark experiments.
//   * Trainee = PanicSnapshot  -- the Trainee build config triggers
//                                 a snapshot dump (heap state, RSS,
//                                 LLM-equivalent report) before
//                                 aborting. The Trainee config is the
//                                 sim-instructor's debug target;
//                                 educators want forensic-grade dumps.
//
// Container layers (TArray, TMap, TSet, FString, TBitArray, the
// lock-free queues' growth paths) NEVER route through FMemory::Malloc
// directly under FOOMPolicy::ReturnNull. They route through
// FMemory::MallocOrAbort (Section 4.1 fix M-2), which calls Malloc
// and aborts on null. The OOM contract section in the spec explains
// why: a half-constructed container with an already-registered
// XGCRootSpan exposes a dangling buffer base to the collector if
// Malloc returns null in mid-grow.
//
// Phase-1b note on the Trainee config: the build-configuration flags
// in XCoreDefines.h are XPACT_DEBUG / XPACT_DEVELOPMENT / XPACT_TEST /
// XPACT_SHIPPING; there is no XPACT_TRAINEE today. The PanicSnapshot
// policy is selected by Debug (the closest current proxy to the
// Trainee tier; the snapshot work happens in Debug); Development
// keeps ReturnNull; Test + Shipping use Abort. When the master plan
// promotes a separate Trainee tier, this header is the one place to
// change the mapping.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // FOOMPolicy -- the three out-of-memory behaviours.
    //
    // uint8_t-backed; the active policy is read-mostly (set once at
    // FMemory::__Init, never changed), so a 1-byte enum keeps the
    // global compact.
    // -----------------------------------------------------------------
    enum class FOOMPolicy : ::uint8
    {
        // ---------- Abort ----------
        // Allocator path: log + abort with the tagged diagnostic.
        // No nullptr returned; the caller's noexcept guarantee is
        // preserved by the abort path being [[noreturn]].
        // Used in: Shipping + Test (zero-divergence in the OOM
        // failure mode between content-tier-trained engine builds
        // and the QA-validated Test binary; both abort identically).
        Abort         = 0,

        // ---------- ReturnNull ----------
        // Allocator path: return nullptr to the direct caller.
        // The caller is responsible for recovery; container layers
        // do NOT use this path (they go through MallocOrAbort which
        // converts ReturnNull to an abort). Used by intentional
        // attempt-and-recover patterns: scratch buffers, optional
        // caches, speculative pre-allocations.
        // Used in: Development (the only config where a Dev-tier
        // recovery path is meaningful; the policy is the spec's
        // explicit Dev default, Section 4.5 row 3).
        ReturnNull    = 1,

        // ---------- PanicSnapshot ----------
        // Allocator path: dump a forensic-grade snapshot (per-tag
        // bytes from GetAllocatedBytes, RSS + AvailableVirtual from
        // FPlatformMemory::GetMemoryStats, top-N leak buckets from
        // FLeakTracker::CaptureReport, then abort. The snapshot is
        // written to a deterministic path (Engine/Saved/Crashes/
        // OOM-{pid}-{wall_clock}.txt) so the educator can review it
        // post-mortem without console access.
        // Used in: Debug (the Trainee-tier proxy; see header
        // comment).
        PanicSnapshot = 2,
    };

    static_assert(sizeof(FOOMPolicy) == 1, "FOOMPolicy ABI lock: 1 byte (uint8 underlying)");
    static_assert(alignof(FOOMPolicy) == 1, "FOOMPolicy ABI lock: 1-byte alignment");

    // -----------------------------------------------------------------
    // The compiled-in default for this build configuration.
    //
    // Resolved at compile time via the XPACT_* configuration flags
    // from XCoreDefines.h. Exactly one of the four flags is 1 (the
    // static_assert in XCoreDefines.h enforces this); the constexpr
    // here selects the right policy based on that flag.
    //
    // Phase-1b note on Trainee mapping: the spec calls for Trainee =
    // PanicSnapshot; the closest current proxy is Debug, so Debug
    // builds get PanicSnapshot. When a future master-plan revision
    // adds a separate Trainee target, this branch is the one place
    // to extend (add `XPACT_TRAINEE` to the conditional and split
    // Debug back to ReturnNull or Abort per the new mapping).
    // -----------------------------------------------------------------
#if XPACT_SHIPPING || XPACT_TEST
    inline constexpr FOOMPolicy kDefaultOOMPolicy = FOOMPolicy::Abort;
#elif XPACT_DEVELOPMENT
    inline constexpr FOOMPolicy kDefaultOOMPolicy = FOOMPolicy::ReturnNull;
#elif XPACT_DEBUG
    inline constexpr FOOMPolicy kDefaultOOMPolicy = FOOMPolicy::PanicSnapshot;
#else
    // Defensive default: the static_assert in XCoreDefines.h should
    // make this branch unreachable, but if a future build flag is
    // added without updating this header, the Abort default keeps
    // the failure mode safe (vs. defaulting to ReturnNull and
    // silently propagating nullptrs).
    inline constexpr FOOMPolicy kDefaultOOMPolicy = FOOMPolicy::Abort;
#endif

    // -----------------------------------------------------------------
    // Tag-name helper for diagnostic output (Dev-only, like
    // GetMemTagName in FMemTag.h).
    // -----------------------------------------------------------------
    [[nodiscard]] inline constexpr const char* GetOOMPolicyName(FOOMPolicy Policy) noexcept
    {
        switch (Policy)
        {
            case FOOMPolicy::Abort:         return "Abort";
            case FOOMPolicy::ReturnNull:    return "ReturnNull";
            case FOOMPolicy::PanicSnapshot: return "PanicSnapshot";
            default:                        return "Unknown";
        }
    }

    // -----------------------------------------------------------------
    // Runtime accessor for the active policy.
    //
    // Reads a global initialised at FMemory::__Init (PreStaticInit
    // phase) from kDefaultOOMPolicy. The accessor is exposed so a
    // test fixture can stub the policy to ReturnNull and exercise the
    // MallocOrAbort code path in a Debug build (where the compile-time
    // default would be PanicSnapshot).
    //
    // Test-only setter declared below; the production path NEVER
    // changes the policy after __Init.
    // -----------------------------------------------------------------
    [[nodiscard]] FOOMPolicy GetActiveOOMPolicy() noexcept;

    // -----------------------------------------------------------------
    // Test-only setter (declared in this header so test TUs can use it
    // without including a separate private header). Production code
    // MUST NOT call this; the Phase 1c XBT lint will scan for
    // non-test TUs calling SetActiveOOMPolicy and emit a build error.
    // -----------------------------------------------------------------
    void __SetActiveOOMPolicy_TestOnly(FOOMPolicy NewPolicy) noexcept;

} // namespace XCore::HAL
