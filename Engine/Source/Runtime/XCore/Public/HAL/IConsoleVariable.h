// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// IConsoleVariable.h -- abstract CVar interface (Section 9.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 9.1 (Public API) + Section 15 (Hot-Reload
// Compatibility / Reserved-trailing-virtuals pattern; fix M-9).
//
// IConsoleVariable is the abstract base of every CVar concrete type
// (TConsoleVariable<int32>, TConsoleVariable<float>,
// TConsoleVariable<FString>; see TConsoleVariableImpl.cpp). It defines
// the vtable layout that the engine commits to keeping stable across
// rev boundaries.
//
// LOCKED VTABLE LAYOUT (Section 15 fix M-9 generalised):
//
//   Active slots (Rev 1; locked):
//     [0] virtual ~IConsoleVariable
//     [1] GetInt
//     [2] GetFloat
//     [3] GetString
//     [4] SetInt
//     [5] SetFloat
//     [6] SetString
//     [7] GetName
//     [8] GetHelp
//     [9] GetFlags
//
//   Reserved trailing slots (Rev 1; locked):
//     [10] __ABI_Reserved_0  (future: int64_t GetInt64() const)
//     [11] __ABI_Reserved_1  (future: double GetDouble() const)
//     [12] __ABI_Reserved_2  (future: range/clamp queries)
//     [13] __ABI_Reserved_3  (future: validation callbacks)
//
// Filling a reserved slot with a real virtual is a MINOR ABI bump
// (XPACT_CVAR_ABI_TAG bumps from "v1-with-4-reserved-slots" to
// "v2-with-3-reserved-slots"). Consuming all 4 reserved + needing
// more is a MAJOR ABI bump (full engine restart required; no in-
// session hot-reload).
//
// The reserved-slot bodies are no-op inline functions; their existence
// pads the vtable to the committed size without runtime cost on
// unused reserved slots. The reserved virtuals are NOT declared
// `noexcept = 0` (pure) because the engine must produce concrete
// vtable entries for them; declaring them with empty inline bodies
// is the correct ABI shape.
//
// HOT-RELOAD CONTRACT (Section 15):
//   * Vtable layout is locked; adding methods after Reserved_3 is
//     a MAJOR ABI bump.
//   * static_assert(sizeof(IConsoleVariable) <= 16) is the size lock
//     for the abstract base (vtable pointer + no data members on
//     64-bit targets). Concrete types are larger; their size locks
//     live in TConsoleVariableImpl.cpp.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"        // XPACT_CVAR_ABI_TAG
#include "Containers/FString.h"
#include "HAL/ECVarFlags.h"
#include "HAL/ECVarSetByPriority.h"

namespace XCore::Misc
{

    // -----------------------------------------------------------------
    // IConsoleVariable -- the abstract base.
    //
    // Threading: Get* methods are lock-free fast-path; Set* methods
    // take the registry's exclusive RWLock for the SetByPriority
    // cascade arbitration (Section 9.2). Concurrent Find + concurrent
    // Set are TSan-clean.
    //
    // Lifetime: instances are heap-allocated by IConsoleManager at
    // registration time (FAutoConsoleVariable ctor pushes onto the
    // Treiber stack; IConsoleManager::__Initialize() drains the stack
    // and constructs the concrete type via the allocator). Pointers
    // returned by Find/Register are engine-lifetime-valid; never
    // freed until process exit.
    // -----------------------------------------------------------------
    class IConsoleVariable
    {
    public:
        // Virtual dtor MUST be the first virtual so the vtable layout
        // starts at slot 0 with the destructor. C++ standard guarantee.
        virtual ~IConsoleVariable() noexcept = default;

        // --- Active slots (locked) ---

        // GetInt -- typed read of the integer value cell. Calls on a
        // non-integer-typed concrete type return the integer
        // projection of the stored value (e.g., float-truncated, or
        // 0 for string types that don't parse cleanly).
        [[nodiscard]] virtual ::int32 GetInt() const noexcept = 0;

        // GetFloat -- typed read of the float value cell. Calls on a
        // non-float-typed concrete type return the float projection.
        [[nodiscard]] virtual float GetFloat() const noexcept = 0;

        // GetString -- typed read of the string value cell. Calls on
        // a non-string-typed concrete type format the stored value
        // through the canonical FString formatter (FromInt32 /
        // FromFloat) into a fresh FString.
        [[nodiscard]] virtual ::XCore::FString GetString() const noexcept = 0;

        // SetInt -- write under the SetByPriority cascade. If the
        // current SetBy is >= the incoming Priority and the incoming
        // priority is NOT the same level, the write is ignored. Same-
        // priority writes always succeed (recency wins per locked
        // decision 10).
        //
        // Calls on a non-integer-typed concrete type convert through
        // the canonical numeric formatter (Value -> FString via
        // FromInt32 -> stored; or Value -> static_cast<float> ->
        // stored). The implementation documents which projection
        // applies for each concrete TConsoleVariable<T>.
        virtual void SetInt(::int32 Value, ECVarSetByPriority Priority = ECVarSetByPriority::Code) noexcept = 0;

        // SetFloat -- mirror of SetInt for float.
        virtual void SetFloat(float Value, ECVarSetByPriority Priority = ECVarSetByPriority::Code) noexcept = 0;

        // SetString -- mirror of SetInt for FString.
        virtual void SetString(const ::XCore::FString& Value, ECVarSetByPriority Priority = ECVarSetByPriority::Code) noexcept = 0;

        // GetName -- the registered name (UTF-8). Returns by const&
        // because the FString lives in the IConsoleVariable's storage
        // for the engine's lifetime.
        [[nodiscard]] virtual const ::XCore::FString& GetName() const noexcept = 0;

        // GetHelp -- the registration-time help text (UTF-8). Same
        // storage discipline as GetName.
        [[nodiscard]] virtual const ::XCore::FString& GetHelp() const noexcept = 0;

        // GetFlags -- the registration-time feature flags. Flags are
        // immutable after registration; the SetByPriority cascade
        // changes the value cell, not the flags.
        [[nodiscard]] virtual ECVarFlags GetFlags() const noexcept = 0;

        // --- Reserved trailing slots (locked; do not call) ---
        //
        // These exist solely to reserve vtable slots for future ABI
        // evolution per Section 15 fix M-9 generalised. They have
        // no-op inline bodies so concrete subclasses don't need to
        // override them. A future MINOR ABI bump replaces one of
        // these with a real virtual; the slot number is preserved
        // so existing patched-DLL vtables remain compatible.
        //
        // Naming: __ABI_Reserved_N (double-underscore prefix flags
        // these as engine-internal; user code MUST NOT call them).

        virtual void __ABI_Reserved_0() noexcept {}   // future: int64 GetInt64() const
        virtual void __ABI_Reserved_1() noexcept {}   // future: double GetDouble() const
        virtual void __ABI_Reserved_2() noexcept {}   // future: range/clamp queries
        virtual void __ABI_Reserved_3() noexcept {}   // future: validation callbacks

        // --- Diagnostic surface (Debug+Dev only; not part of the
        //     locked vtable) ---
        //
        // GetLastSetBy -- the priority that most recently won the
        // cascade. Used by the `cvar_history` debug command. Inline
        // because it reads a non-virtual field of the concrete
        // implementation through a stable accessor protocol; the
        // implementation in TConsoleVariableImpl.cpp overrides via
        // a tag-dispatch through a static_cast that the call site
        // does NOT need to know about.
        //
        // The accessor is intentionally NOT a virtual -- adding a
        // virtual here would consume an ABI slot. Instead the
        // implementation provides it via a non-virtual public
        // method on the TConsoleVariable<T> concrete type that
        // shadows the IConsoleVariable surface; debug code that
        // wants the last SetBy walks the concrete type directly.

    protected:
        // Trivial protected default ctor so concrete subclasses can
        // be constructed. The abstract base has no data members.
        IConsoleVariable() noexcept = default;

        // Non-copyable, non-movable: the registered CVar's address
        // is the identity (cached handles point at the value cell;
        // moving the CVar invalidates every handle).
        IConsoleVariable(const IConsoleVariable&)            = delete;
        IConsoleVariable& operator=(const IConsoleVariable&) = delete;
        IConsoleVariable(IConsoleVariable&&)                 = delete;
        IConsoleVariable& operator=(IConsoleVariable&&)      = delete;
    };

    // ABI size lock for the abstract base.
    //
    // On 64-bit targets the abstract IConsoleVariable contains only
    // the vtable pointer (8 bytes). The static_assert is a bare
    // upper bound so future protected fields (e.g., a per-CVar lock
    // hint) can be added without breaking the lock; the concrete
    // types' size locks live in TConsoleVariableImpl.cpp.
    static_assert(sizeof(IConsoleVariable) == sizeof(void*),
                  "IConsoleVariable ABI lock: vtable pointer only on the abstract base; "
                  "concrete subclasses' size locks live in TConsoleVariableImpl.cpp.");

} // namespace XCore::Misc
