// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FLeakTracker.h -- Dev-mode allocation tracking + leak report.
// =====================================================================
//
// XCore-4a Rev 3, Section 12.
//
// Gated by XPACT_LEAK_TRACKING_ENABLED (= 1 in Debug + Development; = 0
// in Test + Shipping; see XCoreDefines.h). All FLeakTracker code is
// wrapped in `#if XPACT_LEAK_TRACKING_ENABLED`; the preprocessor
// strips it entirely in Shipping (Section 12.5 acceptance I3:
// "Shipping binary contains no FLeakTracker:: exported symbol").
//
// Architecture (Section 12.2):
//   * Per-tag chained hash map. Bounded chain depth 16; warning on
//     first chain depth 16. Max-live-allocs 10 M (sized against
//     Quest 3 4 GB RAM / 400-byte typical = 10 M * 400 B = 4 GB
//     attribution coverage). On overflow, evict LRU entry + emit
//     warning + increment skipped-attribution counter (surfaced in
//     the captured report).
//   * Per-platform stack walk via Private/HAL/StackWalk/IStackWalk.h.
//     Win64 uses RtlCaptureStackBackTrace + dbghelp SymFromAddr;
//     Linux uses backtrace + backtrace_symbols; Android uses
//     __builtin_frame_address + _Unwind_Backtrace (NDK
//     libunwindstack richer-info is the Step 5 follow-up commit per
//     Section 12.5 fix A-MIN2).
//   * Stack walk depth: 24 frames across all platforms (matches
//     FStackBucket::Frames array length in Section 12.1 spec body).
//   * Bucketing: stack-traces with identical frame-pointer arrays
//     collapse into one bucket; the bucket totals TotalBytes +
//     AllocationCount.
//
// Hook protocol:
//   * __Init installs FMallocBinnedX::SetMallocHook +
//     FMallocBinnedX::SetFreeHook with the tracker's record-on-
//     malloc / forget-on-free routines.
//   * The hooks are called from inside FMallocBinnedX::Malloc /
//     Free with the (already validated) user pointer + size + tag.
//   * The tracker's own internal allocations are tagged
//     FMemTag::LeakTracker and NOT recorded (the tracker is immune
//     to itself; otherwise the shadow table self-records and
//     unbounded recursion).
//
// Determinism contract (Section 12.3):
//   * Off in Shipping. In sim-path TUs with a deterministic allocator,
//     the tracker observes the same allocation order every run.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"
#include "Macros/XCoreFwd.h"      // TArray<T>, FString forward declarations

#if XPACT_LEAK_TRACKING_ENABLED

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // FLeakTracker -- the tracker class.
    //
    // All methods static + noexcept (matching FMemory's shape). The
    // tracker has process-wide state; per-thread shards are an
    // implementation detail in FLeakTracker.cpp.
    //
    // The class is in `XCore::HAL` namespace (consistent with the
    // rest of XCore-4a's HAL surface).
    // -----------------------------------------------------------------
    class FLeakTracker
    {
    public:
        // -----------------------------------------------------------------
        // FStackBucket -- one row in the leak report's per-callstack
        // breakdown.
        //
        // Frames array length = 24 (Section 12.2 spec body: "Per-platform
        // stack-walk depth is fixed at 24 frames across Win64 / Linux /
        // Android, matching the FStackBucket::Frames array length in
        // 12.1"). The stack walk fills Frames[0..FrameCount-1]; the
        // remaining slots are nullptr.
        //
        // TotalBytes / AllocationCount are the aggregate over all
        // outstanding allocations that share this callstack.
        // -----------------------------------------------------------------
        struct FStackBucket
        {
            void*    Frames[24];
            int      FrameCount;
            ::SIZE_T TotalBytes;
            ::SIZE_T AllocationCount;
        };

        // -----------------------------------------------------------------
        // FLeakReport -- captured snapshot of the tracker's state.
        //
        // Section 12.1 spec body field layout. Spec body declares
        // Buckets as `TArray<FStackBucket>`; TArray is forward-declared
        // in XCoreFwd.h (Phase 1c provides the body).
        //
        // PHASE-1B BUCKETS STORAGE: TArray cannot be embedded by value
        // in Phase 1b because TArray has no working body yet (its
        // sizeof / alignof are unknown to the compiler, blocking
        // FLeakReport's own sizeof computation). We hold the Buckets
        // list via an opaque pointer (`BucketsImpl`) instead; the
        // CaptureReport path allocates the implementation type on
        // demand and exposes it through the BucketCount + GetBucket
        // accessors below. When Phase 1c lands TArray, the spec-
        // wording "TArray<FStackBucket> Buckets" can be restored
        // verbatim with no behavioural change.
        //
        // TODO(Phase 1c): restore the spec-literal field
        //     ::XCore::TArray<FStackBucket> Buckets;
        // once TArray has a working body. The current opaque-pointer
        // shim is the minimum-viable interface that satisfies the
        // dispatch's "FLeakReport has count=N, bytes=M" assertion
        // without requiring TArray's body.
        // -----------------------------------------------------------------
        struct FLeakReport
        {
            ::SIZE_T  TotalLeakedBytes;
            ::SIZE_T  LeakedAllocationCount;
            ::SIZE_T  BucketCount;       // number of distinct call-stack buckets

            // Opaque pointer to the bucket-array implementation. For
            // Phase 1b, the implementation is an internal FStackBucket
            // array held inside the tracker's TLS-style state and
            // freed at FLeakReport destruction.
            //
            // The destructor calls into FLeakTracker::FreeReport-
            // Buckets to release the storage; structured to keep this
            // header free of TArray's body until Phase 1c.
            void*     BucketsImpl;

            // Constructor / destructor for the opaque-pointer pattern.
            // The destructor frees BucketsImpl via the tracker's
            // accessor (declared inline below to keep the header
            // single-file).
            FLeakReport() noexcept
                : TotalLeakedBytes(0), LeakedAllocationCount(0),
                  BucketCount(0), BucketsImpl(nullptr) {}
            ~FLeakReport() noexcept;  // body in FLeakTracker.cpp

            // Non-copyable. Move-constructed via the captured-state
            // pattern (no-op transfers; the caller owns the report
            // until destruction).
            FLeakReport(const FLeakReport&)            = delete;
            FLeakReport& operator=(const FLeakReport&) = delete;
            FLeakReport(FLeakReport&& Other) noexcept;
            FLeakReport& operator=(FLeakReport&& Other) noexcept;

            // Accessor: returns the I-th bucket. Bounds-checked in
            // Debug/Dev; UB in Shipping on out-of-range I.
            // Phase 1b: returns a copy from the opaque BucketsImpl.
            [[nodiscard]] FStackBucket GetBucket(::SIZE_T I) const noexcept;
        };

        // ============================================================
        // Engine init / shutdown hooks (Section 1.5 PreStaticInit).
        // ============================================================

        // -----------------------------------------------------------------
        // __Init -- engine bootstrap.
        //
        // Installs the FMemory hooks (Malloc/Free callbacks) so every
        // subsequent allocation is recorded into the per-tag shadow
        // tables. Called at PreStaticInit AFTER FMemory::__Init.
        //
        // Phase 1b: also initialises the platform stack walker
        // (loads dbghelp on Win64; eager-init backtrace on Linux/
        // Android).
        // -----------------------------------------------------------------
        static void __Init() noexcept;

        // -----------------------------------------------------------------
        // __Shutdown -- engine shutdown.
        //
        // Captures the final FLeakReport, writes it to the default
        // location (Engine/Saved/Leaks/leaks-{wall_clock}.txt), and
        // clears the per-tag shadow tables. Uninstalls the FMemory
        // hooks before clearing the tables so a final post-shutdown
        // allocation does not re-record into a freed table.
        // -----------------------------------------------------------------
        static void __Shutdown() noexcept;

        // ============================================================
        // Snapshot API (called by tests + by `stat leaks` console
        // command).
        // ============================================================

        // -----------------------------------------------------------------
        // CaptureReport -- fill the out parameter with the current
        // tracker state.
        //
        // Thread-safe: walks the per-tag shadow tables under the
        // per-tag mutex; the snapshot is consistent across tags
        // (each tag's snapshot is consistent within the tag; cross-
        // tag inconsistency is bounded by one allocation's worth of
        // staleness).
        //
        // Phase 1b: TotalLeakedBytes + LeakedAllocationCount are
        // populated; Buckets is empty (see FLeakReport comment).
        // -----------------------------------------------------------------
        static void CaptureReport(FLeakReport& Out) noexcept;

        // -----------------------------------------------------------------
        // WriteReport -- snapshot the tracker and write a human-
        // readable text file to `Path`.
        //
        // The file format matches `FMemory::DumpUsageReport` in spirit
        // but adds per-bucket callstack + symbol resolution. Path is
        // a FString (XCoreFwd.h forward-declared); the implementation
        // converts to a C-string when calling the platform file open.
        //
        // Phase 1b: writes a text file with per-tag aggregate counts;
        // per-bucket stack-trace lines deferred to Phase 1c (TArray
        // dependency).
        // -----------------------------------------------------------------
        static void WriteReport(const ::XCore::FString& Path);

        // ============================================================
        // Diagnostic surfaces (called by tests).
        // ============================================================

        // -----------------------------------------------------------------
        // GetLiveAllocationCount -- the count of currently-recorded
        // allocations across all tags. Used by tests to verify that
        // Free properly forgets allocations.
        // -----------------------------------------------------------------
        [[nodiscard]] static ::SIZE_T GetLiveAllocationCount() noexcept;

        // -----------------------------------------------------------------
        // GetSkippedAttributionCount -- the count of allocations that
        // were dropped because the shadow table overflowed (LRU
        // eviction). Used by tests to verify the overflow path fires
        // when expected.
        // -----------------------------------------------------------------
        [[nodiscard]] static ::SIZE_T GetSkippedAttributionCount() noexcept;
    };

} // namespace XCore::HAL

#endif  // XPACT_LEAK_TRACKING_ENABLED

// =====================================================================
// TODO(Phase 1c):
//   * Populate FLeakReport::Buckets once TArray<FStackBucket> has a
//     working body. The per-bucket aggregation logic exists in the
//     .cpp; only the populate-the-TArray path is gated.
//   * Wire the symbolic-name resolution into WriteReport. Phase 1b
//     emits raw frame addresses; Phase 1c will call
//     IStackWalk::SymbolizeFrame for each address.
//   * Android libunwindstack richer-info per Section 12.5 fix A-MIN2:
//     replace the __builtin_frame_address + _Unwind_Backtrace baseline
//     with the NDK libunwindstack call. Acceptance I-extra: Android
//     leak-tracker symbol resolution within 95% of Win64 dbghelp on a
//     1000-stack-trace sample.
//   * XPACT_STOMP_ALLOC stomp-allocator opt-in (Section 12.6 spec body
//     "XPact ships an equivalent stomp-allocator opt-in via the
//     XPACT_STOMP_ALLOC macro"). Implements use-after-free detection
//     by routing through a guard-page-isolated allocator.
// =====================================================================
