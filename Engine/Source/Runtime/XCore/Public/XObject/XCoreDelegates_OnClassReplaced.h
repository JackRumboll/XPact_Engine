// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XCoreDelegates_OnClassReplaced.h -- multicast delegate fired by
// Phase 5.j hot-reload when a class is replaced (XCoreXObject Rev 4
// §10.10.1 + Rev 2 FIX-A-MED-33 / FIX-A-MIN-44).
// =====================================================================
//
// Spec §10.10.1: "When a class is replaced by hot-reload, XCoreXObject
// fires OnClassReplaced(FClassReplacementContext) delegate via
// XCoreDelegates (per FIX-A-MIN-44). XNetworking listens to this
// delegate and invalidates its FReplicationStateDescriptorRegistry
// cache for the affected class."
//
// Phase 5.k publishes the delegate ACCESSOR + the SUBSCRIBE / FIRE
// surface. Phase 5.j (hot-reload integration) wires the FIRE site
// from the actual class-replacement path. XNetworking subscribes via
// the public accessor when it lands.
//
// =====================================================================
//
// DELEGATE SHAPE (Prime Directive design):
//
// The dispatch spec wording references `::XCore::Reflect::TDelegate`
// but XCore does NOT currently ship a TDelegate template (no template
// found in the codebase as of Phase 5.k). The principled options are:
//
//   (a) Defer the delegate ship until TDelegate exists (would block
//       Phase 5.k on a TDelegate sub-phase; not the right tradeoff
//       given the dispatch ASKs for the OnClassReplaced surface).
//
//   (b) Stub TDelegate as a forward declaration; ship a forward-
//       commitment header with no implementation (would not satisfy
//       the "delegate retains subscribers" test requirement).
//
//   (c) Ship a MINIMAL purpose-built multicast delegate type at
//       XCore::CoreDelegates scope for the OnClassReplaced surface;
//       document the divergence + the future-migration path to a
//       generic TDelegate when that ships.
//
// Phase 5.k picks (c). Reasons:
//
//   * The OnClassReplaced delegate is the ONLY delegate Phase 5.k
//     needs to ship; a one-off minimal type keeps the surface area
//     bounded.
//
//   * The delegate's storage shape (TArray<FCallback>) is what a
//     generic TDelegate would ALSO use internally; the migration
//     path is "rename FOnClassReplacedDelegate -> TDelegate<...>",
//     no semantic change.
//
//   * The minimal type ships now, exercised by Phase 5.k tests,
//     wires Phase 5.j's fire-site immediately, and unblocks
//     XNetworking's subscribe code path when it lands. This is the
//     correct sequencing per the Prime Directive ("ship the right
//     thing at the right time").
//
// MIGRATION TODO (Phase 5.j' or later): when XCore ships a generic
// TDelegate<R(Args...)> template, replace FOnClassReplacedDelegate
// with an alias:
//   using FOnClassReplacedDelegate = TDelegate<void(const FClass*,
//                                                  const FClass*)>;
// Call-site code (Subscribe / Unsubscribe / Broadcast) maps 1:1.
//
// =====================================================================
//
// HOT-RELOAD SAFETY:
//
// The delegate type itself has NO virtual methods (members are POD +
// std::function-equivalent type-erased callback). The accessor
// GetOnClassReplaced() returns a reference to a function-local-static
// singleton -- callers store the reference for the process lifetime.
//
// Subscribers register a FOnClassReplacedCallback (a std::function-
// equivalent that takes two const FClass*). The TArray<FCallback>
// storage allocates from FMemTag::Reflection.
//
// CONCURRENCY:
//
// The delegate's Subscribe / Unsubscribe / Broadcast all acquire the
// internal FCriticalSection. Subscribe / Unsubscribe are called from
// module-init paths (rare); Broadcast fires synchronously from the
// hot-reload class-replacement path (rare; engineer-station only).
// The lock contention is negligible.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FCriticalSection.h"
#include "Containers/TArray.h"

#include <cstddef>
#include <cstdint>
#include <functional>
#include <utility>

namespace XCore::Reflect
{
    // FClass is the type whose replacement triggers the delegate.
    // Forward-declared here so this header does not pull
    // Reflection/FClass.h's transitive includes.
    struct FClass;
}

namespace XCore::CoreDelegates
{
    // -----------------------------------------------------------------
    // FOnClassReplacedCallback -- the typed callback signature.
    //
    // Phase 5.k Phase-1 uses std::function for type erasure. Subscribers
    // may bind lambdas, free functions, member-function-on-instance
    // pairs (via std::bind), etc.
    //
    // The signature is:
    //   void(const FClass* OldClass, const FClass* NewClass)
    //
    // where OldClass is the soon-to-be-replaced class and NewClass is
    // the replacement. Both pointers are guaranteed non-null at fire
    // time (the hot-reload path verifies this before invoking
    // Broadcast).
    //
    // The callback runs SYNCHRONOUSLY in the Broadcast caller's thread
    // (per the spec §10.10.1 cache-invalidation contract -- XNetworking
    // expects to invalidate its cache BEFORE the hot-reload returns;
    // an async post would let stale cache entries be read by a parallel
    // worker between Broadcast and the cache-invalidate callback).
    // -----------------------------------------------------------------
    using FOnClassReplacedCallback = ::std::function<void(
        const ::XCore::Reflect::FClass* /*OldClass*/,
        const ::XCore::Reflect::FClass* /*NewClass*/)>;

    // -----------------------------------------------------------------
    // FOnClassReplacedDelegate -- minimal multicast delegate.
    //
    // Each Subscribe returns a TOKEN (an opaque uint64) that the
    // subscriber retains and passes to Unsubscribe at the end of its
    // lifetime. The token + TArray-of-FCallback storage pattern is
    // the same shape UE's FDelegate uses internally.
    //
    // Concurrency: Subscribe / Unsubscribe / Broadcast all acquire
    // m_lock. Broadcast iterates a SNAPSHOT of m_callbacks under the
    // lock then RELEASES the lock before invoking each callback. This
    // makes Broadcast re-entrant safe (a callback may itself Subscribe
    // / Unsubscribe without deadlock).
    //
    // No virtual methods.
    // -----------------------------------------------------------------
    class FOnClassReplacedDelegate
    {
    public:
        // Opaque subscription handle. Returned by Subscribe; passed
        // to Unsubscribe. Values are monotonically increasing
        // uint64s; 0 is the "invalid handle" sentinel.
        using FHandle = ::std::uint64_t;
        static constexpr FHandle kInvalidHandle = 0;

        FOnClassReplacedDelegate() noexcept = default;

        // Non-copy, non-move: the delegate is a process-singleton via
        // GetOnClassReplaced() so copies / moves would be semantic
        // errors.
        FOnClassReplacedDelegate(const FOnClassReplacedDelegate&)            = delete;
        FOnClassReplacedDelegate(FOnClassReplacedDelegate&&)                 = delete;
        FOnClassReplacedDelegate& operator=(const FOnClassReplacedDelegate&) = delete;
        FOnClassReplacedDelegate& operator=(FOnClassReplacedDelegate&&)      = delete;

        ~FOnClassReplacedDelegate() noexcept = default;

        // =============================================================
        // Subscribe -- register a callback. Returns a FHandle that
        // the subscriber retains for the lifetime of the subscription.
        // =============================================================
        [[nodiscard]] FHandle Subscribe(FOnClassReplacedCallback Callback) noexcept;

        // =============================================================
        // Unsubscribe -- remove a previously-registered callback by
        // its handle. Returns true if the handle was found + removed,
        // false if it was already gone or was kInvalidHandle.
        // =============================================================
        bool Unsubscribe(FHandle Handle) noexcept;

        // =============================================================
        // Broadcast -- invoke every subscribed callback with the given
        // (OldClass, NewClass) pair. Synchronous. Callbacks run on the
        // caller's thread.
        //
        // Re-entrancy: a callback may Subscribe / Unsubscribe during
        // Broadcast. The snapshot taken at entry is the visible set
        // for the current invocation; Subscribe / Unsubscribe affect
        // FUTURE Broadcast calls only.
        //
        // EXCEPTIONS: callbacks SHOULD be noexcept (the spec wording
        // is "telemetry/cache-invalidate cannot throw"). The delegate
        // body does not try/catch; an exception from a callback
        // propagates and terminates the Broadcast loop.
        // =============================================================
        void Broadcast(
            const ::XCore::Reflect::FClass* OldClass,
            const ::XCore::Reflect::FClass* NewClass) noexcept;

        // =============================================================
        // Diagnostics.
        // =============================================================
        [[nodiscard]] ::int32 GetSubscriberCount() const noexcept;

        // Test-only: drop every subscription. Used by the Phase 5.k
        // test suite to reset between fixtures.
        void __ResetForTests() noexcept;

    private:
        // One subscription record. Trivially-copyable except for the
        // std::function callback (which has its own move semantics).
        struct FEntry
        {
            FHandle                   Handle;
            FOnClassReplacedCallback  Callback;
        };

        // m_handleCounter -- monotonic handle allocator. Atomic so the
        // handle issuance does not require holding m_lock.
        ::std::atomic<FHandle>        m_handleCounter{1};

        // m_callbacks -- the subscriber table. Append-only on
        // Subscribe; swap-remove on Unsubscribe. The TArray uses
        // FMemTag::Reflection.
        ::XCore::TArray<FEntry>       m_callbacks;

        // m_lock -- the internal serializer. mutable so const
        // accessors (GetSubscriberCount) can acquire shared.
        mutable ::XCore::HAL::FCriticalSection m_lock;
    };

    // =================================================================
    // GetOnClassReplaced -- accessor for the process-singleton
    // delegate.
    //
    // Magic-static. C++11 thread-safe initialisation. The returned
    // reference is stable for the process lifetime.
    //
    // Used by:
    //   * Phase 5.j's hot-reload class-replacement path -- calls
    //     Broadcast(OldClass, NewClass) after the replacement is
    //     committed to the FClass registry.
    //   * XNetworking -- calls Subscribe to install its cache-
    //     invalidation callback.
    //   * XEditor / XReflectionRuntime caches -- subscribe to refresh
    //     their per-class caches.
    // =================================================================
    [[nodiscard]] FOnClassReplacedDelegate& GetOnClassReplaced() noexcept;

} // namespace XCore::CoreDelegates
