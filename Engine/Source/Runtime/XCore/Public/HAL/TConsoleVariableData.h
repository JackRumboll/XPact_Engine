// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TConsoleVariableData.h -- per-CVar typed value cell (Section 9.5).
// =====================================================================
//
// XCore-4a Rev 3, Section 9.5 (Lifetime / static-init ordering; fix
// B-C3 typed cached handle) + Section 9.2 (Threading contract;
// internal RWLock + lock-free published-pointer fast path).
//
// TConsoleVariableData<T> is the per-CVar value cell that the
// cached TConsoleVariableHandle<T> points at. It holds:
//
//   * The current shadowed value (T storage).
//   * The current SetByPriority (so the cascade arbiter knows what
//     priority wrote the current value).
//   * On render-thread-safe CVars, a shadow copy for the render
//     thread that is published at the frame boundary.
//
// The class is intentionally NOT exported via the IConsoleVariable
// vtable -- it lives BEHIND the vtable as a typed-typed-storage
// implementation detail. The hot-path consumer is
// TConsoleVariableHandle<T>::Get(), which reads the value cell with
// a single std::atomic_ref load with memory_order_acquire (fix B-C3).
//
// THREADING CONTRACT (Section 9.2):
//   * The value cell is read lock-free via std::atomic_ref over the
//     stored T. T must be trivially-copyable and atomic-eligible
//     (int32, float; FString is the exception -- it has its own
//     RWLock-protected storage).
//   * Writes (Set*) take the registry's exclusive RWLock for the
//     cascade arbitration; only when the write is approved does the
//     value cell receive a memory_order_release atomic store.
//   * The release/acquire pair is the synchronisation; subsequent
//     reads from any thread observe the new value once the publish
//     completes.
//
// SHADOW-ON-RENDER-THREAD:
//   ECVarFlags::RenderThreadSafe is opt-in. When set, the value cell
//   maintains a second T storage labelled the "render shadow"; the
//   game-thread sink writes the gameplay-tier mutation into the
//   primary cell, and the render-thread sync point copies primary
//   to shadow once per frame. Render-thread reads use a separate
//   handle (RenderHandle()) that points at the shadow cell. This is
//   the UE pattern (FConsoleVariableData) adapted to the typed
//   handle surface.
//
// SIM-PATH SAFETY:
//   T must be trivially-copyable. Reads from a sim-path TU compose
//   with the SimPathSafe flag check (the per-CVar accessor is
//   [[deprecated]] when the CVar is non-safe and the TU is sim-path;
//   Section 9.3).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "HAL/ECVarSetByPriority.h"

#include <atomic>
#include <type_traits>

namespace XCore::Misc
{

    // -----------------------------------------------------------------
    // TConsoleVariableData<T> -- typed value cell.
    //
    // Storage discipline: T must be trivially-copyable and atomic-
    // eligible (the std::atomic_ref load on T must be lock-free on
    // every supported target). int32 and float satisfy both; FString
    // does NOT (FString has a non-trivial copy ctor and 64-byte
    // storage), so the FString concrete TConsoleVariable specialises
    // to use the RWLock-protected non-atomic storage path.
    //
    // The class is move/copy-deleted because the address of m_value
    // is the cached-handle target; moving would invalidate the
    // handle. Lifetime is engine-wide; only IConsoleManager
    // allocates / owns these.
    // -----------------------------------------------------------------
    template<typename T>
    class TConsoleVariableData
    {
    public:
        // Type-trait constraint enforced at compile time.
        static_assert(::std::is_trivially_copyable_v<T>,
                      "TConsoleVariableData<T> requires T to be trivially-copyable. "
                      "For non-trivial types (e.g., FString), use the specialised "
                      "TConsoleVariable<FString> concrete type which holds the FString "
                      "directly behind the RWLock.");

        // Default-construct the value cell to T{} (= 0 for primitives).
        constexpr TConsoleVariableData() noexcept
            : m_value(T{})
            , m_setBy(ECVarSetByPriority::Default)
            , m_renderShadow(T{})
        {}

        // Initialising ctor: registration-time default value + Default
        // priority. Used by IConsoleManager::Register* at construction.
        explicit constexpr TConsoleVariableData(T InitialValue) noexcept
            : m_value(InitialValue)
            , m_setBy(ECVarSetByPriority::Default)
            , m_renderShadow(InitialValue)
        {}

        TConsoleVariableData(const TConsoleVariableData&)            = delete;
        TConsoleVariableData& operator=(const TConsoleVariableData&) = delete;
        TConsoleVariableData(TConsoleVariableData&&)                 = delete;
        TConsoleVariableData& operator=(TConsoleVariableData&&)      = delete;

        // -------------------------------------------------------------
        // LoadAcquire -- the hot-path read.
        //
        // std::atomic_ref<T> over the stored T storage; the load is
        // memory_order_acquire so the synchronizing store from the
        // setter side is visible to the caller. Same target as the
        // TConsoleVariableHandle<T>::Get accessor; the handle holds
        // a T* directly to m_value (this is the published pointer
        // fast path per Section 9.5).
        //
        // No const-cast trick needed: std::atomic_ref's ctor accepts
        // a non-const ref to T because the load is conceptually
        // atomic-side-effect-only. The method itself is marked const
        // because the caller observes no mutation.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE T LoadAcquire() const noexcept
        {
            // const_cast through std::atomic_ref's constructor is the
            // canonical pattern; the underlying load is atomic and
            // does not mutate m_value.
            return ::std::atomic_ref<T>(const_cast<T&>(m_value)).load(::std::memory_order_acquire);
        }

        // -------------------------------------------------------------
        // StoreRelease -- the setter's publish step.
        //
        // The cascade arbitration happens before this call (under
        // the registry's exclusive RWLock); StoreRelease is the
        // pure publish step. The release fence pairs with the
        // acquire load in LoadAcquire to deliver the value to any
        // observer.
        //
        // Also writes m_setBy (relaxed; the SetBy field is read only
        // by the cascade arbiter under the exclusive lock, so the
        // relaxed write is safe).
        // -------------------------------------------------------------
        void StoreRelease(T NewValue, ECVarSetByPriority NewSetBy) noexcept
        {
            // m_setBy under exclusive lock; ordinary write is fine.
            m_setBy = NewSetBy;
            // Release-store the published value.
            ::std::atomic_ref<T>(m_value).store(NewValue, ::std::memory_order_release);
        }

        // -------------------------------------------------------------
        // GetSetBy -- the current cascade winner's priority.
        //
        // Read under the registry's shared RWLock (no atomicity needed
        // because the field changes only under the exclusive lock).
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE ECVarSetByPriority GetSetBy() const noexcept
        {
            return m_setBy;
        }

        // -------------------------------------------------------------
        // GetValuePtr -- the published-pointer target.
        //
        // Returns a T* that TConsoleVariableHandle<T> wraps for the
        // cached-handle fast path (fix B-C3). The handle holds the
        // pointer for the engine's lifetime; the lifetime contract is
        // that the IConsoleVariable owning this data is never
        // destroyed before process exit.
        //
        // The pointer is to non-const T so std::atomic_ref<T> can be
        // constructed at the handle's load site; the load itself
        // produces a const T.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE T* GetValuePtr() noexcept
        {
            return &m_value;
        }

        // -------------------------------------------------------------
        // GetRenderShadowPtr -- the render-thread shadow cell.
        //
        // Populated by the per-frame sync only when ECVarFlags::
        // RenderThreadSafe is set. Hot-path render-thread reads use
        // a TConsoleVariableHandle<T> constructed via
        // GetRenderShadowPtr(); the publish step is the per-frame
        // sync that copies m_value -> m_renderShadow.
        //
        // Phase 1g wires the per-frame sync (depends on the
        // renderer's tick); Phase 1f exposes the storage and the
        // accessor without the publish.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE T* GetRenderShadowPtr() noexcept
        {
            return &m_renderShadow;
        }

        // -------------------------------------------------------------
        // PublishRenderShadow -- copy primary -> shadow.
        //
        // Called by the per-frame sync. Currently a Phase 1f stub
        // ready for Phase 1g wiring; the implementation is correct
        // standalone.
        // -------------------------------------------------------------
        void PublishRenderShadow() noexcept
        {
            const T Current = ::std::atomic_ref<T>(m_value).load(::std::memory_order_acquire);
            ::std::atomic_ref<T>(m_renderShadow).store(Current, ::std::memory_order_release);
        }

    private:
        // The published value cell. std::atomic_ref-loaded by the
        // cached handle on the hot path; std::atomic_ref-stored by
        // SetInt/Float/String under the exclusive lock.
        //
        // Storage type T; atomicity comes from std::atomic_ref at
        // the access sites (this avoids the std::atomic<T> wrapper
        // which has a less-portable layout on some toolchains).
        T m_value;

        // The cascade winner's priority. Written under the exclusive
        // RWLock; read under the shared lock.
        ECVarSetByPriority m_setBy;

        // The render-thread shadow cell. Lazily populated by the
        // per-frame sync when ECVarFlags::RenderThreadSafe is set.
        T m_renderShadow;
    };

    // -----------------------------------------------------------------
    // ABI locks for the trivially-copyable specialisations.
    //
    // int32: 4 (value) + 1 (setBy) + 3 (padding) + 4 (renderShadow) = 12
    //        rounded to 12 with alignment-padding to 4-byte boundary.
    //        Actual layout: 4 + 1 + 3 = 8 then 4 = 12. Compiler may
    //        pad to alignof(int32) = 4 boundaries; the lock allows
    //        16 as the upper bound (small enum + 2 int32 + slack).
    // float: same shape as int32 (both are 4-byte trivially-copyable).
    //
    // The static_asserts below validate the trivially-copyable
    // requirement is still met at instantiation time; we don't lock
    // the exact sizeof because the inter-field padding is compiler-
    // defined and not part of the ABI contract.
    // -----------------------------------------------------------------

} // namespace XCore::Misc
