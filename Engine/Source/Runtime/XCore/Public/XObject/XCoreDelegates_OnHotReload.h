// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XCoreDelegates_OnHotReload.h -- multicast delegates for the
// hot-reload cascade lifecycle (XCoreXObject Rev 4 §9.2 + §10.10.1;
// Phase 5.j).
// =====================================================================
//
// Spec §9.2 trailing prose:
//   * "Fires OnHotReloadStart delegate (FIX-A-MIN-44)."   (after quiesce)
//   * "Fires OnHotReloadComplete delegate."                (after cascade)
//   * "Fires OnHotReloadAbort delegate."                   (layout drift)
//
// Phase 5.k shipped FOnClassReplacedDelegate (per-class replacement
// notification). This file ships the THREE CASCADE-BRACKET delegates:
//   * FOnHotReloadStartDelegate     -- fires after quiesce window opens.
//   * FOnHotReloadCompleteDelegate  -- fires after cascade completes.
//   * FOnHotReloadAbortDelegate     -- fires when XLiveCoding's Phase 1
//                                       layout-drift gate rejects a
//                                       patch (no cascade ran).
//
// Each delegate is a multicast pattern mirroring
// FOnClassReplacedDelegate (handle-based subscribe / unsubscribe /
// broadcast under FCriticalSection with snapshot-then-release for
// re-entry safety).
//
// =====================================================================
//
// FHotReloadContext -- the broadcast payload.
//
// Per spec §9.2: the cascade is identified by the FClassReplacementMap
// (the {OldClass, NewClass} pairs being replaced) + module-identifier
// metadata. The Phase 5.j context carries:
//
//   * ReplacedClassCount   -- count of classes in the cascade. Zero
//                              for OnHotReloadStart (the cascade
//                              hasn't run yet); non-zero for
//                              OnHotReloadComplete (final count); zero
//                              for OnHotReloadAbort.
//   * TotalInstancesRebound -- count of XObject instances whose
//                              ClassPrivate was rebound. Zero for
//                              OnHotReloadStart and OnHotReloadAbort;
//                              the running total for
//                              OnHotReloadComplete.
//
// Future fields (forward extensibility): module name (FName), patch
// version int64, cascade duration ms double. Phase 5.j ships the
// minimal {ReplacedClassCount, TotalInstancesRebound} pair so consumers
// can route on either. Adding fields is non-breaking (the struct is
// constructed by Phase 5.j's coordinator code only; consumers may
// ignore unknown fields).
//
// =====================================================================
//
// HOT-RELOAD SAFETY:
//
// The delegate types themselves have NO virtual methods (members are
// POD + std::function-equivalent type-erased callback). The accessors
// GetOnHotReloadStart / Complete / Abort return references to function-
// local-static singletons -- callers store the reference for the
// process lifetime.
//
// CONCURRENCY:
//
// Subscribe / Unsubscribe / Broadcast all acquire the internal
// FCriticalSection. Subscribe / Unsubscribe are called from module-
// init paths (rare); Broadcast fires synchronously from the
// FXObjectHotReloadCoordinator's cascade bracket (rare; engineer-
// station-only). The lock contention is negligible.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FCriticalSection.h"
#include "Containers/TArray.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <utility>

namespace XCore::CoreDelegates
{
    // -----------------------------------------------------------------
    // FHotReloadContext -- the cascade-bracket broadcast payload.
    //
    // Carries enough information for subscribers (XNetworking, editor
    // refresh listeners, telemetry consumers) to react to the cascade
    // boundary without forcing a fresh class-walk per delegate fire.
    //
    // The struct is trivially-copyable + standard-layout so it can be
    // passed by value through the std::function-equivalent callback.
    // -----------------------------------------------------------------
    struct FHotReloadContext
    {
        // Count of FClass replacements in this cascade.
        //   * 0 at OnHotReloadStart (cascade hasn't run yet).
        //   * N at OnHotReloadComplete (the final classes-replaced count).
        //   * 0 at OnHotReloadAbort (no cascade ran).
        ::std::int64_t  ReplacedClassCount;

        // Sum of XObject instances whose ClassPrivate was rebound
        // across the cascade.
        //   * 0 at OnHotReloadStart.
        //   * Sum at OnHotReloadComplete.
        //   * 0 at OnHotReloadAbort.
        ::std::int64_t  TotalInstancesRebound;
    };

    static_assert(sizeof(FHotReloadContext) == 16,
                  "FHotReloadContext layout: two int64 fields = 16 bytes.");

    // -----------------------------------------------------------------
    // FOnHotReloadCallback -- typed callback signature for the
    // three cascade-bracket delegates.
    //
    // Phase 5.j uses std::function for type erasure (mirror of the
    // Phase 5.k FOnClassReplacedCallback pattern). Subscribers may
    // bind lambdas, free functions, member-function-on-instance pairs.
    //
    // Signature:
    //   void(const FHotReloadContext& Context)
    //
    // The callback runs SYNCHRONOUSLY in the Broadcast caller's thread
    // (the FXObjectHotReloadCoordinator's bracket thread; typically
    // the XLiveCoding orchestrator's thread). Subscribers SHOULD be
    // noexcept (the delegate body does not try/catch).
    // -----------------------------------------------------------------
    using FOnHotReloadCallback = ::std::function<void(
        const FHotReloadContext& /*Context*/)>;

    // -----------------------------------------------------------------
    // FOnHotReloadDelegate -- minimal multicast delegate (mirror of
    // FOnClassReplacedDelegate's shape).
    //
    // We ship a single named type usable for the three cascade
    // delegates (Start, Complete, Abort). The three singletons are
    // distinct instances of the SAME class -- the dispatch contract
    // is identical (synchronous broadcast of FHotReloadContext);
    // sharing the class minimises code surface vs three near-identical
    // copies.
    //
    // Each Subscribe returns a TOKEN (an opaque uint64) the subscriber
    // retains and passes to Unsubscribe at the end of its lifetime.
    //
    // Concurrency: Subscribe / Unsubscribe / Broadcast all acquire
    // m_lock. Broadcast iterates a SNAPSHOT of m_callbacks under the
    // lock then RELEASES the lock before invoking each callback. This
    // makes Broadcast re-entrant safe (a callback may itself Subscribe
    // / Unsubscribe without deadlock).
    //
    // No virtual methods.
    // -----------------------------------------------------------------
    class FOnHotReloadDelegate
    {
    public:
        // Opaque subscription handle. Returned by Subscribe; passed to
        // Unsubscribe. Values are monotonically-increasing uint64s; 0
        // is the "invalid handle" sentinel.
        using FHandle = ::std::uint64_t;
        static constexpr FHandle kInvalidHandle = 0;

        FOnHotReloadDelegate() noexcept = default;

        // Non-copy, non-move: the delegate is a process-singleton via
        // the per-event accessor.
        FOnHotReloadDelegate(const FOnHotReloadDelegate&)            = delete;
        FOnHotReloadDelegate(FOnHotReloadDelegate&&)                 = delete;
        FOnHotReloadDelegate& operator=(const FOnHotReloadDelegate&) = delete;
        FOnHotReloadDelegate& operator=(FOnHotReloadDelegate&&)      = delete;

        ~FOnHotReloadDelegate() noexcept = default;

        // =============================================================
        // Subscribe -- register a callback. Returns a FHandle that the
        // subscriber retains for the lifetime of the subscription.
        // =============================================================
        [[nodiscard]] FHandle Subscribe(FOnHotReloadCallback Callback) noexcept;

        // =============================================================
        // Unsubscribe -- remove a previously-registered callback by
        // its handle. Returns true if the handle was found + removed,
        // false if it was already gone or was kInvalidHandle.
        // =============================================================
        bool Unsubscribe(FHandle Handle) noexcept;

        // =============================================================
        // Broadcast -- invoke every subscribed callback with the given
        // context. Synchronous. Callbacks run on the caller's thread.
        //
        // Re-entrancy: a callback may Subscribe / Unsubscribe during
        // Broadcast. The snapshot taken at entry is the visible set
        // for the current invocation; Subscribe / Unsubscribe affect
        // FUTURE Broadcast calls only.
        // =============================================================
        void Broadcast(const FHotReloadContext& Context) noexcept;

        // =============================================================
        // Diagnostics.
        // =============================================================
        [[nodiscard]] ::int32 GetSubscriberCount() const noexcept;

        // Test-only: drop every subscription. Used by the Phase 5.j
        // test suite to reset between fixtures.
        void __ResetForTests() noexcept;

    private:
        // One subscription record. Trivially-copyable except for the
        // std::function callback (which has its own move semantics).
        struct FEntry
        {
            FHandle                Handle;
            FOnHotReloadCallback   Callback;
        };

        // m_handleCounter -- monotonic handle allocator. Atomic so the
        // handle issuance does not require holding m_lock.
        ::std::atomic<FHandle>     m_handleCounter{1};

        // m_callbacks -- the subscriber table. Append-only on
        // Subscribe; swap-remove on Unsubscribe.
        ::XCore::TArray<FEntry>    m_callbacks;

        // m_lock -- the internal serialiser. mutable so const
        // accessors (GetSubscriberCount) can acquire shared.
        mutable ::XCore::HAL::FCriticalSection m_lock;
    };

    // =================================================================
    // GetOnHotReloadStart -- accessor for the process-singleton
    // OnHotReloadStart delegate.
    //
    // Fires AFTER FXObjectHotReloadCoordinator::BeginHotReloadQuiesce
    // completes (quiesce window is now open; the cascade is about to
    // begin). FHotReloadContext payload at this point:
    //   * ReplacedClassCount    = 0 (cascade hasn't run yet).
    //   * TotalInstancesRebound = 0.
    //
    // Subscribers can take pre-cascade snapshots (e.g., editor saves
    // unsaved data, telemetry consumers mark a cascade-start
    // timestamp).
    //
    // Magic-static. C++11 thread-safe initialisation. The returned
    // reference is stable for the process lifetime.
    // =================================================================
    [[nodiscard]] FOnHotReloadDelegate& GetOnHotReloadStart() noexcept;

    // =================================================================
    // GetOnHotReloadComplete -- accessor for the process-singleton
    // OnHotReloadComplete delegate.
    //
    // Fires AFTER FXObjectHotReloadCoordinator::FinishHotReloadCascade
    // completes (cascade is done; quiesce window is closed; the new
    // class set is live). FHotReloadContext payload:
    //   * ReplacedClassCount    = N (the count from FClassReplacementMap).
    //   * TotalInstancesRebound = total instance count across all N
    //                              ApplyClassReplacement calls.
    //
    // Subscribers can refresh their caches (XNetworking re-validates
    // replication descriptors, editor refreshes type-info panels).
    //
    // Magic-static. C++11 thread-safe initialisation. The returned
    // reference is stable for the process lifetime.
    // =================================================================
    [[nodiscard]] FOnHotReloadDelegate& GetOnHotReloadComplete() noexcept;

    // =================================================================
    // GetOnHotReloadAbort -- accessor for the process-singleton
    // OnHotReloadAbort delegate.
    //
    // Fires when XLiveCoding's Phase 1 pre-commit gate REJECTS a patch
    // (layout drift detected; see spec §9.2 trailing prose at "Fires
    // OnHotReloadAbort delegate."). XCoreXObject NEVER opened the
    // quiesce window in this case; the abort delegate exists for
    // subscribers that need to know about rejected patches (telemetry,
    // editor error display, etc.). FHotReloadContext payload:
    //   * ReplacedClassCount    = 0.
    //   * TotalInstancesRebound = 0.
    //
    // Subscribers can display the "close and reopen" message in the
    // editor + emit a telemetry event.
    //
    // PHASE 5.j AUTHORITY: Phase 5.j publishes the delegate ACCESSOR.
    // The actual FIRE site for OnHotReloadAbort lives in XLiveCoding's
    // Phase 1 gate (when XLiveCoding ships). Until then this delegate
    // exists in a "forward-commitment" state -- subscribers register
    // now; fires when XLiveCoding lands.
    //
    // Magic-static. C++11 thread-safe initialisation. The returned
    // reference is stable for the process lifetime.
    // =================================================================
    [[nodiscard]] FOnHotReloadDelegate& GetOnHotReloadAbort() noexcept;

} // namespace XCore::CoreDelegates
