// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObjectAllocatorBootstrap.cpp -- wires FXObjectAllocator into the
// XCore-4a s_XObjectAllocator hook (XCoreXObject Rev 4 §1.4 +
// XCore-4a §4.4 + Phase 5.a' Contract Rev 13.9 prerequisite).
// =====================================================================
//
// The XCore-4a allocator surface (HAL/FMemory.h) declares two symbols
// as TENTATIVE-DEFINITION externs:
//
//   * `s_XObjectAllocator` -- the 3-arg function-pointer slot the
//                              NewObject hot path dispatches through.
//   * `RegisterXObjectAllocator(Fn)` -- the install hook System 5 uses
//                              to publish FXObjectAllocator::
//                              AllocateRawStatic into the slot.
//
// XCore-4a Rev 3 documents the hook as: "The hook is declared as a
// tentative-definition extern; until XCoreXObject ships (System 5), the
// symbol is supplied by a weak-symbol fallback that initializes to
// nullptr." This file is the System-5 strong definition.
//
// RESPONSIBILITIES of this TU:
//
//   1. Define the storage for `s_XObjectAllocator` (a single global
//      function-pointer slot; nullptr-initialised so any pre-bootstrap
//      caller observing the slot trips the XPACT_CHECK in NewXObject
//      rather than executing a wild jump).
//
//   2. Define the body of `RegisterXObjectAllocator` -- atomic-store
//      the function pointer with memory_order_release so the next
//      acquire-load on s_XObjectAllocator (in NewXObject + every
//      direct caller of the hook) is happens-after the install.
//
//   3. Define the body of `NewXObject` -- the defensive dispatch
//      wrapper that asserts the hook is installed and forwards into
//      the registered allocator. Production NewObject<T> in XCore-4b
//      can bypass this and call `s_XObjectAllocator` directly; this
//      wrapper is the call-site for any code path that wants the
//      diagnostic-on-uninstalled-hook behaviour.
//
//   4. Install the hook at engine static-init via a constinit-
//      initialised auto-register object. The object's constructor runs
//      AT STATIC-INIT TIME (PreStaticInit per the XInitPhase ladder),
//      so by the time any non-bootstrap code touches the allocator the
//      hook is already wired.
//
// REGISTRATION TIMING (per Phase 5.a' dispatch wording):
//
//   Spec target: EInitPhase::PostStaticInit (matches FMallocBinnedX's
//   __Init timing). We do BETTER than that by registering during
//   static-init proper -- a side-effecting ctor of a constinit-eligible
//   object at namespace scope. By the time EInitPhase advances from
//   PreStaticInit -> PostStaticInit the hook is already installed; any
//   `NewObject<T>` call from a PostStaticInit-or-later TU finds the
//   slot live.
//
//   The constinit guarantee: the slot itself (`s_XObjectAllocator`)
//   has trivial value-initialised storage (nullptr at static-storage-
//   duration time per the C++ rules for non-explicitly-initialised
//   externs). The registration ctor must run AFTER `s_XObjectAllocator`
//   is value-initialised; the C++ standard guarantees value-
//   initialisation of static externs precedes any dynamic
//   initialisation in the same TU, so the registration ctor sees a
//   well-defined (nullptr) slot.
//
//   Cross-TU initialisation order is the concern: if the registration
//   ctor runs BEFORE `s_XObjectAllocator` is defined in FMemory.cpp...
//   it would still work because the value-initialisation of `extern`
//   storage precedes ALL dynamic ctors across the whole program. The
//   spec wording on "static-init ordering" (Section 1.5) confirms this.
//
// LOCK DISCIPLINE: the bootstrap is single-threaded (static ctors run
// on the main thread before any worker thread is spawned). No mutex is
// needed for the registration itself.
//
// HOT-RELOAD: the slot is mutable (atomic store with
// memory_order_release); a future hot-replace scenario (XLiveCoding)
// can swap the allocator via a second `RegisterXObjectAllocator` call
// without breaking the invariant. Phase 5.b ships only the install-once
// pattern; the swap path is documented but not exercised at unit-test
// scale.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectAllocator.h"

#include "Macros/XAssertionMacros.h"

#include <atomic>
#include <cstddef>

namespace XCore::HAL
{

    // =================================================================
    // s_XObjectAllocator -- strong definition of the function-pointer
    // slot the NewObject hot path dispatches through.
    //
    // Initialised to nullptr at static-storage-duration time (the C++
    // standard guarantees zero-init for global pointers without an
    // explicit initialiser). The strong definition lives HERE so the
    // XCoreXObject system owns the symbol; XCore-4a's tentative
    // declaration in FMemory.h is satisfied by this strong definition
    // at link time.
    // =================================================================
    void* (*s_XObjectAllocator)(
        ::SIZE_T Size,
        ::SIZE_T Align,
        const ::XCore::Reflect::FClass* ClassDescriptor) noexcept = nullptr;

    // =================================================================
    // RegisterXObjectAllocator -- install the allocator function
    // pointer with release semantics.
    //
    // Per spec §4.4: "Called exactly once per process lifetime;
    // subsequent calls overwrite the slot (the spec invariant is one
    // allocator per process, but the slot is mutable to support future
    // hot-replace scenarios under XLiveCoding cascade)."
    //
    // We use std::atomic_ref to make the write atomic + release-
    // ordered so any subsequent acquire-load of s_XObjectAllocator is
    // happens-after the install. memory_order_release pairs with
    // memory_order_acquire reads in production hot-path callers.
    //
    // The function pointer type is a non-class scalar; atomic_ref over
    // a pointer is lock-free on every supported platform (Win64 +
    // Linux-x86_64 + Android-ARM64) per std::atomic<T*>::
    // is_always_lock_free.
    // =================================================================
    void RegisterXObjectAllocator(
        void* (*Fn)(::SIZE_T Size, ::SIZE_T Align,
                    const ::XCore::Reflect::FClass* ClassDescriptor) noexcept) noexcept
    {
        using FnPtr = void* (*)(::SIZE_T, ::SIZE_T,
                                const ::XCore::Reflect::FClass*) noexcept;
        ::std::atomic_ref<FnPtr> Slot(s_XObjectAllocator);
        Slot.store(Fn, ::std::memory_order_release);
    }

    // =================================================================
    // NewXObject -- defensive dispatch wrapper.
    //
    // Acquire-load the slot; assert it is non-null (the hook is
    // installed); forward into the registered allocator.
    //
    // Production NewObject<T> in XCore-4b may bypass this wrapper and
    // call `s_XObjectAllocator` directly (Section 4.4 trailing prose).
    // This wrapper exists for code paths that want the diagnostic-on-
    // uninstalled-hook behaviour (clean abort at the call site rather
    // than a generic nullptr segfault).
    // =================================================================
    void* NewXObject(
        ::SIZE_T Size,
        ::SIZE_T Align,
        const ::XCore::Reflect::FClass* ClassDescriptor) noexcept
    {
        using FnPtr = void* (*)(::SIZE_T, ::SIZE_T,
                                const ::XCore::Reflect::FClass*) noexcept;
        ::std::atomic_ref<FnPtr> Slot(s_XObjectAllocator);
        FnPtr Hook = Slot.load(::std::memory_order_acquire);
        XPACT_CHECK(Hook != nullptr);
        return Hook(Size, Align, ClassDescriptor);
    }

} // namespace XCore::HAL

namespace XCore
{
    namespace
    {
        // -------------------------------------------------------------
        // FXObjectAllocatorAutoRegister -- one-shot static-init
        // registration object.
        //
        // The ctor calls RegisterXObjectAllocator with FXObjectAllocator
        // ::AllocateRawStatic (the static trampoline that forwards into
        // the singleton). The ctor runs at the TU's dynamic-init time,
        // which is BEFORE main() and BEFORE EInitPhase advances from
        // PreStaticInit. Any subsequent NewObject<T> hot-path caller
        // observes the wired hook.
        //
        // The object lives in static storage; its lifetime is the
        // process lifetime. Its destructor (run at process shutdown)
        // does NOT clear the hook -- shutdown-time XObject allocations
        // (extremely rare; mostly diagnostic test-harness teardown) can
        // still find the allocator. The FXObjectAllocator singleton
        // destructor (separate; runs at the same shutdown phase) tears
        // down its FState.
        //
        // NO inter-TU dependencies: the ctor only references symbols
        // defined within this TU's namespace XCore::HAL block + the
        // FXObjectAllocator::AllocateRawStatic static (defined in
        // FXObjectAllocator.cpp). The C++ runtime guarantees that all
        // value-initialisation completes before any dynamic init
        // runs; the registration ctor sees the value-initialised
        // (nullptr) s_XObjectAllocator slot and replaces it with the
        // function pointer.
        // -------------------------------------------------------------
        struct FXObjectAllocatorAutoRegister
        {
            FXObjectAllocatorAutoRegister() noexcept
            {
                ::XCore::HAL::RegisterXObjectAllocator(
                    &::XCore::FXObjectAllocator::AllocateRawStatic);
            }
        };

        // The auto-register instance. namespace-scope static; its ctor
        // runs at dynamic-init time. The variable is intentionally
        // unused at any call site -- its sole purpose is to fire the
        // ctor side-effect.
        //
        // NOTE: we do NOT mark this `constinit` because the ctor body
        // is non-constexpr (RegisterXObjectAllocator's atomic_ref store
        // is a runtime operation). The variable IS still a namespace-
        // scope static with TU-lifetime storage; the ctor runs during
        // dynamic-init exactly as a non-constinit ctor would.
        //
        // [[maybe_unused]] suppresses the "unused variable" warning
        // since this object's purpose is its ctor side-effect.
        [[maybe_unused]]
        const FXObjectAllocatorAutoRegister g_XObjectAllocatorAutoRegister;
    } // namespace
} // namespace XCore
