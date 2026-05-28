// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XCoreDelegates_OnScenarioBoundary.h -- multicast delegate broadcast
// by XScenarios at scenario boundary transitions
// (XCoreXObject Rev 4 §3.6 + §4.7; Phase 5.i).
// =====================================================================
//
// Spec §3.6 + §4.7: scenario boundaries are the structural point at
// which the allocator's mark-region-clearing optimisation engages.
// XScenarios (post-foundation system) broadcasts this delegate when:
//
//   (a) A scenario is about to UNLOAD (broadcast PRE-unload so
//       subscribers can react -- typically the FXObjectCollector
//       triggers a pre-emptive GC cycle per §4.7 "Pre-scenario-unload"
//       to maximise the size-class pool's reclaim opportunity).
//
//   (b) A scenario has finished LOADING (broadcast POST-load so
//       subscribers can update caches that depend on the live set of
//       scenarios; future use).
//
// PHASE 5.i SHIPS:
//   * The delegate type at XCore::CoreDelegates scope.
//   * The accessor GetOnScenarioBoundary() returning the process-
//     singleton instance.
//   * Subscribe / Unsubscribe / Broadcast surface.
//
// The XScenarios system does NOT exist yet (Master Plan post-foundation
// work). The Phase 5.i ship is a FORWARD COMMITMENT: the API surface
// XScenarios will call. When XScenarios lands it will:
//   1. Broadcast OnScenarioBoundary at the appropriate transitions.
//   2. (Optionally) call FXObjectAllocator::ReleaseClassPool per-class
//      AFTER the pre-emptive GC's mark phase completes.
//
// CONSUMER PATTERN (when XScenarios lands):
//
//   const auto Handle = GetOnScenarioBoundary().Subscribe(
//       [](const FScenarioBoundaryContext& Ctx) noexcept
//       {
//           if (Ctx.Phase == EScenarioBoundaryPhase::kPreUnload)
//           {
//               FXObjectCollector::Get().Trigger(
//                   EXGCTriggerReason::kPreScenarioUnload);
//           }
//       });
//
// =====================================================================
//
// DELEGATE SHAPE (Prime Directive design; mirror of OnClassReplaced):
//
// Phase 5.i ships the SAME minimal multicast delegate shape as
// FOnClassReplacedDelegate (Phase 5.k). The migration target is a
// generic TDelegate<R(Args...)> template when XCore ships it; until
// then, the minimal purpose-built type keeps the surface bounded.
//
// =====================================================================
//
// CONCURRENCY:
//
// Subscribe / Unsubscribe / Broadcast all acquire the internal
// FCriticalSection. Subscribe / Unsubscribe are called from module-
// init paths (rare); Broadcast fires synchronously from the scenario
// transition path (rare; ~10 per training session).
//
// Re-entrancy: Broadcast takes a SNAPSHOT under the lock then releases
// before invoking callbacks (mirror of FOnClassReplacedDelegate).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FCriticalSection.h"
#include "Containers/TArray.h"
#include "Reflection/FName.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <utility>

namespace XCore::CoreDelegates
{
    // -----------------------------------------------------------------
    // EScenarioBoundaryPhase -- the lifecycle position the broadcast
    // identifies.
    //
    // Phase 5.i ships kPreUnload + kPostLoad. Future XScenarios
    // expansions may add (e.g., kPreLoad / kPostUnload) without
    // breaking the ABI: the enum is uint8 + extra variants are
    // additive at the high end.
    // -----------------------------------------------------------------
    enum class EScenarioBoundaryPhase : ::uint8
    {
        kPreUnload,    // about to unload; subscribers may trigger pre-emptive work (e.g. GC)
        kPostLoad,     // just finished loading; subscribers may refresh caches
    };

    // -----------------------------------------------------------------
    // FScenarioBoundaryContext -- the broadcast payload.
    //
    // Carries the scenario name + the boundary phase. The scenario
    // name is an FName (engine-canonical interned identifier; 8 bytes;
    // no per-broadcast allocation).
    //
    // Future fields: scenario root XObject pointer, scenario-specific
    // FClass list (which classes XScenarios wants to release). For
    // Phase 5.i we carry the minimal {Name, Phase} pair so consumers
    // can route on either.
    // -----------------------------------------------------------------
    struct FScenarioBoundaryContext
    {
        ::XCore::Reflect::FName ScenarioName;
        EScenarioBoundaryPhase  Phase;
    };

    static_assert(sizeof(EScenarioBoundaryPhase) == 1,
                  "EScenarioBoundaryPhase ABI lock: uint8 underlying.");

    // -----------------------------------------------------------------
    // FOnScenarioBoundaryCallback -- the typed callback signature.
    //
    // Phase 5.i uses std::function for type erasure (mirror of
    // FOnClassReplacedCallback). Subscribers may bind lambdas, free
    // functions, member-function-on-instance pairs.
    //
    // Signature:
    //   void(const FScenarioBoundaryContext& Ctx)
    //
    // The callback runs SYNCHRONOUSLY in the Broadcast caller's
    // thread. Subscribers SHOULD be noexcept (the delegate body does
    // not try/catch).
    // -----------------------------------------------------------------
    using FOnScenarioBoundaryCallback = ::std::function<void(
        const FScenarioBoundaryContext& /*Ctx*/)>;

    // -----------------------------------------------------------------
    // FOnScenarioBoundaryDelegate -- minimal multicast delegate.
    //
    // Each Subscribe returns a TOKEN (opaque uint64) the subscriber
    // retains and passes to Unsubscribe at the end of its lifetime.
    //
    // Concurrency: Subscribe / Unsubscribe / Broadcast all acquire
    // m_lock. Broadcast snapshots m_callbacks under the lock then
    // RELEASES before invoking each callback. Re-entrant safe.
    //
    // No virtual methods.
    // -----------------------------------------------------------------
    class FOnScenarioBoundaryDelegate
    {
    public:
        // Opaque subscription handle. Returned by Subscribe; passed
        // to Unsubscribe. Values are monotonically-increasing
        // uint64s; 0 is the "invalid handle" sentinel.
        using FHandle = ::std::uint64_t;
        static constexpr FHandle kInvalidHandle = 0;

        FOnScenarioBoundaryDelegate() noexcept = default;

        // Non-copy, non-move (process-singleton via
        // GetOnScenarioBoundary()).
        FOnScenarioBoundaryDelegate(const FOnScenarioBoundaryDelegate&)            = delete;
        FOnScenarioBoundaryDelegate(FOnScenarioBoundaryDelegate&&)                 = delete;
        FOnScenarioBoundaryDelegate& operator=(const FOnScenarioBoundaryDelegate&) = delete;
        FOnScenarioBoundaryDelegate& operator=(FOnScenarioBoundaryDelegate&&)      = delete;

        ~FOnScenarioBoundaryDelegate() noexcept = default;

        // =============================================================
        // Subscribe -- register a callback. Returns a FHandle.
        // =============================================================
        [[nodiscard]] FHandle Subscribe(FOnScenarioBoundaryCallback Callback) noexcept;

        // =============================================================
        // Unsubscribe -- remove a previously-registered callback by
        // its handle. Returns true if the handle was found + removed,
        // false if it was already gone or was kInvalidHandle.
        // =============================================================
        bool Unsubscribe(FHandle Handle) noexcept;

        // =============================================================
        // Broadcast -- invoke every subscribed callback with the
        // given context. Synchronous. Callbacks run on the caller's
        // thread.
        //
        // Re-entrancy: a callback may Subscribe / Unsubscribe during
        // Broadcast. The snapshot taken at entry is the visible set
        // for the current invocation; Subscribe / Unsubscribe affect
        // FUTURE Broadcast calls only.
        // =============================================================
        void Broadcast(const FScenarioBoundaryContext& Ctx) noexcept;

        // =============================================================
        // Diagnostics.
        // =============================================================
        [[nodiscard]] ::int32 GetSubscriberCount() const noexcept;

        // Test-only: drop every subscription.
        void __ResetForTests() noexcept;

    private:
        struct FEntry
        {
            FHandle                       Handle;
            FOnScenarioBoundaryCallback   Callback;
        };

        // m_handleCounter -- monotonic handle allocator; atomic so
        // handle issuance does not require holding m_lock.
        ::std::atomic<FHandle>            m_handleCounter{1};

        // m_callbacks -- the subscriber table.
        ::XCore::TArray<FEntry>           m_callbacks;

        // m_lock -- internal serializer. mutable so const accessors
        // can acquire.
        mutable ::XCore::HAL::FCriticalSection m_lock;
    };

    // =================================================================
    // GetOnScenarioBoundary -- accessor for the process-singleton
    // delegate.
    //
    // Magic-static. C++11 thread-safe initialisation. The returned
    // reference is stable for the process lifetime.
    //
    // Used by:
    //   * XScenarios (post-foundation; the broadcaster).
    //   * FXObjectCollector (subscriber: triggers pre-emptive GC at
    //     kPreUnload per spec §4.7 trigger heuristic; wiring lives in
    //     the engine bootstrap once XScenarios + the bootstrap evolve
    //     to call it).
    //   * Diagnostic / test consumers.
    // =================================================================
    [[nodiscard]] FOnScenarioBoundaryDelegate& GetOnScenarioBoundary() noexcept;

} // namespace XCore::CoreDelegates
