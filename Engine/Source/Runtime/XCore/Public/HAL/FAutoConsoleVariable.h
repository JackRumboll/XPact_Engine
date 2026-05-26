// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FAutoConsoleVariable.h -- RAII CVar registration wrapper (Section 9.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 9.1 (FAutoConsoleVariable file-scope helper)
// + Section 9.5 (Lifetime: pushes onto Treiber stack at static-init;
// drains at PostStaticInit; fix C-6 content-hash keying; fix B-C3
// typed cached handle).
//
// FAutoConsoleVariable<T> is the recommended file-scope-static
// registration helper. The constructor:
//   1. Allocates an FAutoConsoleVariableNode containing the
//      registration metadata (name, default, help, flags, type tag).
//   2. Pushes the node onto IConsoleManager's lock-free Treiber stack.
//      The push uses compare_exchange_weak(memory_order_acq_rel) so
//      it is correct under parallel constructor invocation across
//      DLL boundaries (per fix C-6).
//
// IConsoleManager::__Initialize() (called from XEngineInit::Phase_
// PostStaticInit) drains the stack; each popped node is converted to
// a concrete TConsoleVariable<T> instance allocated on the engine
// allocator and inserted into the content-hash-keyed map.
//
// USAGE:
//
//   // file scope:
//   static ::XCore::Misc::FAutoConsoleVariable<::int32>
//       CVarFooBar("r.FooBar", 42, "FooBar description",
//                  ::XCore::Misc::ECVarFlags::SimPathSafe);
//
//   // and somewhere reachable (init-time cached):
//   static auto Handle = CVarFooBar.GetHandle();
//
//   // hot path:
//   int Current = Handle.Get();
//
// THREADING:
//   * Constructor (static-init time): push onto Treiber stack; safe
//     under parallel DLL-loader invocation.
//   * Destructor (process-exit time): the IConsoleManager singleton
//     is torn down by the runtime; the destructor here is a no-op
//     (the node is not deallocated -- ownership transfers to the
//     IConsoleManager at __Initialize, which holds the storage
//     until process exit).
//   * GetHandle() (engine-init or later): returns a typed handle
//     pointing at the registered CVar's value cell. Safe to call
//     from any phase >= PostStaticInit; pre-PostStaticInit calls
//     return an unbound handle (Get() returns T{}) and assert
//     in Debug.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XAssertionMacros.h"      // XPACT_HAS_SOURCE_LOCATION
#include "HAL/ECVarFlags.h"
#include "HAL/IConsoleManager.h"
#include "HAL/IConsoleVariable.h"
#include "HAL/TConsoleVariableData.h"
#include "HAL/TConsoleVariableHandle.h"

#include <atomic>                         // std::atomic for Registered (Rev 3 FIX-R2-MAJ-2)

#if XPACT_HAS_SOURCE_LOCATION
    #include <source_location>
#endif

namespace XCore::Misc
{

    // -----------------------------------------------------------------
    // ECVarValueType -- tag for the FAutoConsoleVariableNode payload.
    //
    // The Treiber-stack node carries a type tag so the drain step
    // can construct the right concrete TConsoleVariable<T> when it
    // pops. The tag is a single byte; the node's union payload
    // stores the default value as the matching type.
    // -----------------------------------------------------------------
    enum class ECVarValueType : ::uint8
    {
        Int32   = 0,
        Float   = 1,
        String  = 2,
    };

    // -----------------------------------------------------------------
    // FAutoConsoleVariableNode -- the Treiber-stack node.
    //
    // POD-like: trivially-constructable / destructible storage for
    // the registration metadata. The drain step reads these fields
    // and forwards them to IConsoleManager::Register*.
    //
    // The node is owned by the FAutoConsoleVariable<T> instance
    // (typically file-scope-static; engine-lifetime); the
    // IConsoleManager only borrows pointers to it during the drain.
    // -----------------------------------------------------------------
    class FAutoConsoleVariableNode
    {
    public:
        // The 'next' pointer for the Treiber stack. Atomic at the
        // stack level (the global head is atomic; per-node nexts
        // are written under the CAS protocol so an ordinary pointer
        // suffices).
        FAutoConsoleVariableNode* Next = nullptr;

        // Registration metadata. Pointers reference rodata (string
        // literals) so they are engine-lifetime-valid; the content-
        // hash keying happens at __Initialize time.
        const char* Name        = nullptr;
        const char* Help        = nullptr;
        ECVarFlags  Flags       = ECVarFlags::Default;
        ECVarValueType ValueType= ECVarValueType::Int32;

        // The default value, stored by type tag. Only the field
        // matching ValueType is valid.
        ::int32     DefaultInt   = 0;
        float       DefaultFloat = 0.0f;
        const char* DefaultStr   = nullptr;

        // The concrete IConsoleVariable* this node maps to after
        // the drain. Written by the IConsoleManager's drain step;
        // read by FAutoConsoleVariable<T>::GetHandle when the
        // caller queries the handle.
        //
        // Rev 3 Round 2 audit FIX-R2-MAJ-2 (hot-reload-plugin-load race):
        // Plain pointer was unsynchronised with respect to drain-time
        // writes from the IConsoleManager TU and steady-state reads
        // from FAutoConsoleVariable<T>::GetHandle in other TUs. On a
        // hot-reload plugin-load path the second __Initialize call
        // drains pending nodes while live readers may be racing on
        // GetHandle; the prior plain-pointer load/store had no
        // ordering guarantee, opening a window where the reader
        // could observe a torn or stale value on weakly-ordered
        // architectures (ARM64).
        //
        // The std::atomic<IConsoleVariable*> writes with release
        // ordering at drain time; readers load with acquire ordering.
        // On x86_64 both compile to plain mov instructions (the ISA
        // is already release/acquire-ordered on aligned scalar
        // accesses); on ARM64 the compiler emits ldar / stlr (the
        // weakly-ordered targets that require the explicit fence).
        // Per-call overhead in the steady state is therefore zero on
        // x86_64 and one fence on ARM64; the correctness gain is
        // unconditional.
        ::std::atomic<IConsoleVariable*> Registered{nullptr};

#if XPACT_HAS_SOURCE_LOCATION
        // Captured at FAutoConsoleVariable<T> ctor time (spec section
        // 9.5 Rev 1 audit close-out for fix C-6). Used by the runtime
        // duplicate-registration diagnostic to name BOTH offending
        // source-locations. The FAutoConsoleVariable<T> ctor assigns
        // SrcLoc from its `SrcLoc = current()` default argument so the
        // captured location is at the user-tier call site, not at the
        // node's default-init point.
        ::std::source_location SrcLoc{};
#endif
    };

    // -----------------------------------------------------------------
    // FAutoConsoleVariable<T> -- typed wrapper.
    //
    // Specialised for int32, float, and FString (the three concrete
    // value types per Section 9.1). The ctor builds the node, pushes
    // it onto the Treiber stack, and returns; the drain step at
    // __Initialize converts the node into a TConsoleVariable<T> and
    // stores the back-link in node->Registered for handle access.
    //
    // T is one of int32, float, FString. The class template's
    // primary version is undeclared; the three specialisations are
    // declared below.
    // -----------------------------------------------------------------
    template<typename T>
    class FAutoConsoleVariable;

    // -----------------------------------------------------------------
    // Int32 specialisation.
    // -----------------------------------------------------------------
    template<>
    class FAutoConsoleVariable<::int32>
    {
    public:
#if XPACT_HAS_SOURCE_LOCATION
        FAutoConsoleVariable(const char* Name, ::int32 Default, const char* Help,
                             ECVarFlags Flags = ECVarFlags::Default,
                             ::std::source_location SrcLoc = ::std::source_location::current()) noexcept
        {
            m_node.Name         = Name;
            m_node.Help         = Help;
            m_node.Flags        = Flags;
            m_node.ValueType    = ECVarValueType::Int32;
            m_node.DefaultInt   = Default;
            m_node.SrcLoc       = SrcLoc;

            IConsoleManager::__PushAutoCVar(&m_node);
        }
#else
        FAutoConsoleVariable(const char* Name, ::int32 Default, const char* Help,
                             ECVarFlags Flags = ECVarFlags::Default) noexcept
        {
            m_node.Name         = Name;
            m_node.Help         = Help;
            m_node.Flags        = Flags;
            m_node.ValueType    = ECVarValueType::Int32;
            m_node.DefaultInt   = Default;

            IConsoleManager::__PushAutoCVar(&m_node);
        }
#endif

        ~FAutoConsoleVariable() noexcept = default;

        FAutoConsoleVariable(const FAutoConsoleVariable&)            = delete;
        FAutoConsoleVariable& operator=(const FAutoConsoleVariable&) = delete;
        FAutoConsoleVariable(FAutoConsoleVariable&&)                 = delete;
        FAutoConsoleVariable& operator=(FAutoConsoleVariable&&)      = delete;

        // GetHandle -- the cached-handle accessor.
        //
        // Returns a TConsoleVariableHandle<int32> pointing at the
        // registered CVar's value cell. Pre-PostStaticInit calls
        // return an unbound handle (m_node.Registered is null) and
        // assert in Debug.
        //
        // The handle wraps a direct pointer into the value cell
        // (fix B-C3); hot-path reads use Get() which is one
        // std::atomic_ref load with memory_order_acquire.
        [[nodiscard]] TConsoleVariableHandle<::int32> GetHandle() const noexcept;

        // operator-> -- diagnostic access to the underlying CVar.
        //
        // Pre-PostStaticInit calls return nullptr; Debug asserts.
        // Hot-path code MUST use GetHandle().Get() instead.
        //
        // Rev 3 FIX-R2-MAJ-2: acquire-load on the atomic Registered
        // field; pairs with the release-store at the drain site
        // (IConsoleManager.cpp).
        [[nodiscard]] IConsoleVariable* operator->() const noexcept
        {
            return m_node.Registered.load(::std::memory_order_acquire);
        }

        // Direct access to the registered CVar; nullptr if drain
        // hasn't happened yet. Acquire-load per FIX-R2-MAJ-2.
        [[nodiscard]] IConsoleVariable* Get() const noexcept
        {
            return m_node.Registered.load(::std::memory_order_acquire);
        }

    private:
        // The node lives in this instance's storage; the
        // IConsoleManager borrows a pointer to it.
        mutable FAutoConsoleVariableNode m_node;
    };

    // -----------------------------------------------------------------
    // Float specialisation.
    // -----------------------------------------------------------------
    template<>
    class FAutoConsoleVariable<float>
    {
    public:
#if XPACT_HAS_SOURCE_LOCATION
        FAutoConsoleVariable(const char* Name, float Default, const char* Help,
                             ECVarFlags Flags = ECVarFlags::Default,
                             ::std::source_location SrcLoc = ::std::source_location::current()) noexcept
        {
            m_node.Name         = Name;
            m_node.Help         = Help;
            m_node.Flags        = Flags;
            m_node.ValueType    = ECVarValueType::Float;
            m_node.DefaultFloat = Default;
            m_node.SrcLoc       = SrcLoc;

            IConsoleManager::__PushAutoCVar(&m_node);
        }
#else
        FAutoConsoleVariable(const char* Name, float Default, const char* Help,
                             ECVarFlags Flags = ECVarFlags::Default) noexcept
        {
            m_node.Name         = Name;
            m_node.Help         = Help;
            m_node.Flags        = Flags;
            m_node.ValueType    = ECVarValueType::Float;
            m_node.DefaultFloat = Default;

            IConsoleManager::__PushAutoCVar(&m_node);
        }
#endif

        ~FAutoConsoleVariable() noexcept = default;

        FAutoConsoleVariable(const FAutoConsoleVariable&)            = delete;
        FAutoConsoleVariable& operator=(const FAutoConsoleVariable&) = delete;
        FAutoConsoleVariable(FAutoConsoleVariable&&)                 = delete;
        FAutoConsoleVariable& operator=(FAutoConsoleVariable&&)      = delete;

        [[nodiscard]] TConsoleVariableHandle<float> GetHandle() const noexcept;

        // Rev 3 FIX-R2-MAJ-2: acquire-load on the atomic Registered
        // field; pairs with the release-store at the drain site.
        [[nodiscard]] IConsoleVariable* operator->() const noexcept
        {
            return m_node.Registered.load(::std::memory_order_acquire);
        }

        [[nodiscard]] IConsoleVariable* Get() const noexcept
        {
            return m_node.Registered.load(::std::memory_order_acquire);
        }

    private:
        mutable FAutoConsoleVariableNode m_node;
    };

    // -----------------------------------------------------------------
    // FString specialisation.
    //
    // No TConsoleVariableHandle<FString> -- the FString concrete
    // type holds non-trivially-copyable storage that cannot be
    // accessed via std::atomic_ref. Hot-path FString CVar reads go
    // through the virtual GetString() method on the
    // IConsoleVariable; the cached-handle fast path is reserved for
    // the trivially-copyable primitive specialisations.
    // -----------------------------------------------------------------
    template<>
    class FAutoConsoleVariable<::XCore::FString>
    {
    public:
#if XPACT_HAS_SOURCE_LOCATION
        FAutoConsoleVariable(const char* Name, const char* Default, const char* Help,
                             ECVarFlags Flags = ECVarFlags::Default,
                             ::std::source_location SrcLoc = ::std::source_location::current()) noexcept
        {
            m_node.Name         = Name;
            m_node.Help         = Help;
            m_node.Flags        = Flags;
            m_node.ValueType    = ECVarValueType::String;
            m_node.DefaultStr   = Default;
            m_node.SrcLoc       = SrcLoc;

            IConsoleManager::__PushAutoCVar(&m_node);
        }
#else
        FAutoConsoleVariable(const char* Name, const char* Default, const char* Help,
                             ECVarFlags Flags = ECVarFlags::Default) noexcept
        {
            m_node.Name         = Name;
            m_node.Help         = Help;
            m_node.Flags        = Flags;
            m_node.ValueType    = ECVarValueType::String;
            m_node.DefaultStr   = Default;

            IConsoleManager::__PushAutoCVar(&m_node);
        }
#endif

        ~FAutoConsoleVariable() noexcept = default;

        FAutoConsoleVariable(const FAutoConsoleVariable&)            = delete;
        FAutoConsoleVariable& operator=(const FAutoConsoleVariable&) = delete;
        FAutoConsoleVariable(FAutoConsoleVariable&&)                 = delete;
        FAutoConsoleVariable& operator=(FAutoConsoleVariable&&)      = delete;

        // Rev 3 FIX-R2-MAJ-2: acquire-load on the atomic Registered
        // field; pairs with the release-store at the drain site.
        [[nodiscard]] IConsoleVariable* operator->() const noexcept
        {
            return m_node.Registered.load(::std::memory_order_acquire);
        }

        [[nodiscard]] IConsoleVariable* Get() const noexcept
        {
            return m_node.Registered.load(::std::memory_order_acquire);
        }

    private:
        mutable FAutoConsoleVariableNode m_node;
    };

} // namespace XCore::Misc
