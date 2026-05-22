// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TConsoleVariableHandle.h -- typed cached CVar handle (fix B-C3).
// =====================================================================
//
// XCore-4a Rev 3, Section 9.5 fix B-C3 ("Typed cached handle"):
//
//   "FAutoConsoleVariable<T> exposes a TConsoleVariableHandle<T> that
//    wraps a direct pointer into the value cell. Read of the cached
//    cell is a single load with memory_order_acquire; no string
//    lookup, no map walk, no lock. Hot-path CVar reads use the cached
//    handle exclusively. IConsoleManager::Find(name) remains in the
//    API for diagnostic and one-shot use (e.g., the console parser);
//    its use in hot-path code is a documented anti-pattern."
//
// And Section 9.6 row 5 (divergence from UE):
//   UE hot-path CVar reads walk a name-keyed string map. XPact's
//   cached handle reduces a CVar read to a single atomic load with
//   no string work.
//
// USAGE PATTERN:
//
//   static FAutoConsoleVariable<int> CVarFoo("r.Foo", 42, "help text");
//   static auto Handle = CVarFoo.GetHandle();   // call once at init
//   // ... later, in the hot path:
//   int Current = Handle.Get();                  // one atomic_ref load
//
// THREADING: the Get accessor is fully thread-safe; std::atomic_ref's
// load is atomic on every supported target. No locks, no string work,
// no map walk.
//
// LIFETIME: the cached pointer is engine-lifetime-valid. The
// IConsoleVariable that owns the value cell is allocated by
// IConsoleManager at registration and never freed until process exit
// (Section 9.5 lifetime contract); the handle's pointer can be cached
// at file scope safely.
//
// SIM-PATH SAFETY: the handle's Get accessor is sim-path-safe iff the
// CVar's ECVarFlags::SimPathSafe is set; the compile-time gate lives
// on the FAutoConsoleVariable<T>::GetHandle() return path (a
// [[deprecated]] attribute when SimPathSafe is absent + the consumer
// is sim-path).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"        // XPACT_FORCEINLINE

#include <atomic>

namespace XCore::Misc
{

    // -----------------------------------------------------------------
    // TConsoleVariableHandle<T> -- pointer-into-value-cell wrapper.
    //
    // Trivially-copyable, trivially-destructible. The pointer lives
    // for the engine's lifetime; the handle is a 64-bit value type
    // (one pointer-width) that fits in a register.
    //
    // T must be trivially-copyable and atomic-eligible at the target
    // platform's std::atomic_ref width (int32, float satisfy; FString
    // does NOT and is handled by the string-specific surface
    // separately).
    // -----------------------------------------------------------------
    template<typename T>
    class TConsoleVariableHandle
    {
    public:
        // Default ctor: null handle. Get() returns T{} on a null
        // handle (sentinel for "not yet bound"); the typical pattern
        // is to construct via FAutoConsoleVariable<T>::GetHandle()
        // which returns a bound handle.
        constexpr TConsoleVariableHandle() noexcept = default;

        // Bind to a value cell.
        //
        // Caller contract: Cell points at a T inside an
        // IConsoleVariable's TConsoleVariableData<T> that is engine-
        // lifetime-valid (allocated by IConsoleManager; never freed).
        //
        // The constructor is intentionally explicit-only to prevent
        // accidental construction from arbitrary T*; user code
        // should always go through FAutoConsoleVariable<T>::
        // GetHandle().
        constexpr explicit TConsoleVariableHandle(T* Cell) noexcept
            : m_cell(Cell)
        {}

        // Trivially copyable; default copy/move are fine.
        TConsoleVariableHandle(const TConsoleVariableHandle&)            = default;
        TConsoleVariableHandle& operator=(const TConsoleVariableHandle&) = default;
        TConsoleVariableHandle(TConsoleVariableHandle&&)                 = default;
        TConsoleVariableHandle& operator=(TConsoleVariableHandle&&)      = default;

        // -------------------------------------------------------------
        // Get -- the hot-path read.
        //
        // Single std::atomic_ref load with memory_order_acquire. The
        // pair-side store is in TConsoleVariableData<T>::StoreRelease;
        // the release/acquire pair guarantees the latest published
        // value is observed.
        //
        // On a null handle (default-constructed; not bound) the
        // accessor returns T{} -- this is the engine-shutdown
        // sentinel state, not an error. A bound handle is the
        // common case and is the engine-lifetime-valid path.
        //
        // XPACT_FORCEINLINE because the spec mandates "one atomic
        // load" semantics; inlining is load-bearing for the perf
        // bar in Section 17.7.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE T Get() const noexcept
        {
            if (m_cell == nullptr) [[unlikely]]
            {
                return T{};
            }
            return ::std::atomic_ref<T>(*m_cell).load(::std::memory_order_acquire);
        }

        // -------------------------------------------------------------
        // IsBound -- true iff the handle holds a non-null cell.
        //
        // Diagnostic surface; user code typically doesn't need to
        // check (a bound handle is the engine-init contract).
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE bool IsBound() const noexcept
        {
            return m_cell != nullptr;
        }

        // -------------------------------------------------------------
        // GetCellPtrUnsafe -- the raw cell pointer.
        //
        // Diagnostic-only accessor; intended for the FAutoConsole-
        // Variable infrastructure (e.g., for the SetByPriority
        // arbiter to publish a new value). User code MUST NOT call
        // this directly -- the cascade arbitration is the
        // IConsoleVariable's responsibility.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE T* GetCellPtrUnsafe() const noexcept
        {
            return m_cell;
        }

    private:
        T* m_cell = nullptr;
    };

    // Size lock: trivially-copyable, pointer-width.
    static_assert(sizeof(TConsoleVariableHandle<::int32>) == sizeof(void*),
                  "TConsoleVariableHandle<int32> ABI lock: pointer-width");
    static_assert(sizeof(TConsoleVariableHandle<float>) == sizeof(void*),
                  "TConsoleVariableHandle<float> ABI lock: pointer-width");

} // namespace XCore::Misc
