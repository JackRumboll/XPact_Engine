// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// IConsoleManager.cpp -- CVar registry implementation (Section 9.5).
// =====================================================================
//
// XCore-4a Rev 3, Section 9.1 (Public API) + Section 9.2 (Threading
// contract) + Section 9.5 (Lifetime / static-init ordering; fix C-6
// content-hash keying; fix M-7 double-registration error).
//
// IMPLEMENTATION:
//
//   * The Treiber-stack head is a file-scope constinit
//     std::atomic<FAutoConsoleVariableNode*> that FAutoConsoleVariable
//     instances push to at static-init time.
//   * __Initialize drains the stack into the singleton's hash map.
//     The map is keyed by XXH3(name_bytes, len, seed=0) per fix C-6.
//   * Find / Register* take the FRWLock (shared / exclusive).
//   * ForEach takes the shared lock and walks every entry.
//
// THREADING:
//
//   * Push (Treiber stack):  compare_exchange_weak(memory_order_acq_rel)
//                            -- correct under parallel DLL-loader.
//   * Pop  (Treiber stack):  compare_exchange_weak(memory_order_acquire,
//                                                  memory_order_relaxed)
//                            -- single drain caller at PostStaticInit,
//                            so contention is rare; ABA-safe because
//                            popped nodes outlive the stack (owned by
//                            the FAutoConsoleVariable instance).
//   * Find:                  FRWLock shared lock + TMap lookup.
//   * Register*:             FRWLock exclusive lock + TMap insert.
//
// HASH-KEYING (fix C-6):
//
//   Key = XXH3 over the UTF-8 name bytes (no null terminator). The
//   length is std::strlen at the Register/Find call site. Reads
//   that use different const char* values pointing at byte-identical
//   strings hash to the same key.
//
// =====================================================================

#include "HAL/IConsoleManager.h"

#include "Containers/FString.h"
#include "Containers/TMap.h"
#include "HAL/FAutoConsoleVariable.h"
#include "HAL/FRWLock.h"
#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/TConsoleVariableImpl.h"
#include "HAL/XInitPhase.h"
#include "Hash/FXxh3.h"
#include "Macros/XPactMacros.h"

#include <atomic>
#include <cstring>
#include <new>

namespace XCore::Misc
{

    // -----------------------------------------------------------------
    // Treiber stack head -- file scope, constinit-initialised.
    //
    // Per Section 9.5 wording: "The head pointer is declared
    // constinit std::atomic<FAutoConsoleVariable*> at file scope; each
    // constructor pushes onto the stack via compare_exchange_weak
    // (memory_order_acq_rel)."
    //
    // The constinit guarantee is load-bearing: pre-main static
    // initialisers reading this pointer must see nullptr (the
    // unpopulated stack), not an unspecified value. C++20 constinit
    // ensures the storage is zero-initialised at link time.
    // -----------------------------------------------------------------
    constinit ::std::atomic<FAutoConsoleVariableNode*> g_AutoCVarTreiberHead{nullptr};

    // -----------------------------------------------------------------
    // __PushAutoCVar -- the Treiber-stack push.
    //
    // FAutoConsoleVariable's constructor calls this. The node's Next
    // field is updated to the current head, then a CAS publishes the
    // node as the new head.
    //
    // Loop until the CAS succeeds. compare_exchange_weak may spurious-
    // ly fail; the loop body re-reads Expected and retries.
    //
    // Correct under parallel DLL-loader (Section 9.5 / fix C-6).
    // -----------------------------------------------------------------
    void IConsoleManager::__PushAutoCVar(FAutoConsoleVariableNode* Node) noexcept
    {
        if (Node == nullptr) [[unlikely]]
        {
            return;
        }

        FAutoConsoleVariableNode* Expected = g_AutoCVarTreiberHead.load(::std::memory_order_relaxed);
        do
        {
            Node->Next = Expected;
        }
        while (!g_AutoCVarTreiberHead.compare_exchange_weak(
                    Expected, Node,
                    ::std::memory_order_acq_rel,
                    ::std::memory_order_relaxed));
    }

    // -----------------------------------------------------------------
    // __PopAutoCVar -- the Treiber-stack pop (used by drain).
    //
    // Single-caller path (the drain inside __Initialize); contention
    // is not expected. The CAS-loop is correct regardless.
    //
    // ABA safety: popped nodes are owned by their parent
    // FAutoConsoleVariable instance which has engine-lifetime
    // (file-scope static). The nodes are never freed during the
    // engine's lifetime, so the ABA hazard does not apply.
    // -----------------------------------------------------------------
    FAutoConsoleVariableNode* IConsoleManager::__PopAutoCVar() noexcept
    {
        FAutoConsoleVariableNode* Expected = g_AutoCVarTreiberHead.load(::std::memory_order_acquire);
        while (Expected != nullptr)
        {
            FAutoConsoleVariableNode* NewHead = Expected->Next;
            if (g_AutoCVarTreiberHead.compare_exchange_weak(
                    Expected, NewHead,
                    ::std::memory_order_acquire,
                    ::std::memory_order_acquire))
            {
                Expected->Next = nullptr;
                return Expected;
            }
        }
        return nullptr;
    }

    // -----------------------------------------------------------------
    // IConsoleManager singleton state.
    //
    // The Pimpl pattern lets us keep the TMap definition out of the
    // public header (avoids pulling TMap into every TU that includes
    // IConsoleManager.h). The state struct holds the map + the
    // registry-wide RWLock.
    //
    // The struct is allocated by the singleton's first Get() call
    // and held by a function-local static; the storage is engine-
    // lifetime.
    // -----------------------------------------------------------------
    struct FConsoleManagerState
    {
        // Content-hash-keyed map: XXH3(name_bytes, len, 0) -> CVar*.
        ::XCore::TMap<::uint64, IConsoleVariable*> Map;

        // Registry-wide RWLock. Shared on Find / ForEach; exclusive
        // on Register*. Per Section 9.2: "Internal RWLock; Find is
        // lock-free fast-path via published-pointer + acquire fence."
        // The lock-free fast path is the cached-handle surface
        // (TConsoleVariableHandle); Find inside the registry walks
        // under the shared lock.
        mutable ::XCore::HAL::FRWLock Lock;

        // Drain-completed flag. Set by __Initialize once the Treiber
        // stack has been drained. Subsequent __Initialize calls are
        // no-ops; FAutoConsoleVariable constructors that fire after
        // the first drain still push onto the stack, but the next
        // __Initialize call (triggered by plugin load) drains them.
        ::std::atomic<bool> DrainCompleted{false};
    };

    // -----------------------------------------------------------------
    // The singleton accessor.
    //
    // Function-local static; constructed on first call. Safe to call
    // from any phase; pre-PostStaticInit calls return a reference to
    // the same engine-lifetime instance, but the map is empty until
    // __Initialize runs.
    //
    // C++11 guarantee: function-local static initialisation is
    // thread-safe via the compiler-emitted guard variable.
    // -----------------------------------------------------------------
    static IConsoleManager& GetSingletonInstance() noexcept;

    // Inner singleton type. The public IConsoleManager has protected
    // ctor/dtor; the inner type derives so we can instantiate it.
    //
    // The state struct is held by reference (raw new); the
    // FConsoleManagerState's storage outlives the IConsoleManagerImpl
    // by design -- both have engine lifetime.
    class IConsoleManagerImpl final : public IConsoleManager
    {
    public:
        IConsoleManagerImpl() noexcept
            : IConsoleManager()
            , m_state(new (::std::nothrow) FConsoleManagerState())
        {
        }

        ~IConsoleManagerImpl() noexcept
        {
            // Engine-lifetime; the destructor runs at process exit.
            // The CVar objects themselves are NOT freed individually --
            // they were allocated via FMemory and the OS reclaims
            // process memory on exit. Freeing each CVar at process
            // exit would risk re-ordering issues with other
            // statics; the OS-reclaim path is the principled choice.
            //
            // The state struct is freed because it holds the TMap
            // which has destructive resources (allocator-owned
            // buffer); the deletion is well-ordered (engine-internal
            // singleton, single delete site).
            delete m_state;
        }

        FConsoleManagerState* GetState() noexcept
        {
            return m_state;
        }

    private:
        FConsoleManagerState* m_state;
    };

    // Function-local-static container; the standard guarantees
    // thread-safe initialisation. The actual storage is the
    // IConsoleManagerImpl instance.
    static IConsoleManagerImpl& GetSingletonImpl() noexcept
    {
        static IConsoleManagerImpl Instance;
        return Instance;
    }

    static IConsoleManager& GetSingletonInstance() noexcept
    {
        return GetSingletonImpl();
    }

    IConsoleManager& IConsoleManager::Get() noexcept
    {
        return GetSingletonInstance();
    }

    IConsoleManager::IConsoleManager() noexcept = default;
    IConsoleManager::~IConsoleManager() noexcept = default;

    // -----------------------------------------------------------------
    // HashName -- the content-hash keying primitive.
    //
    // XXH3 over the UTF-8 name bytes; no null terminator included.
    // Per Section 5.3 + Section 11.9 the seed is fixed at 0 for
    // determinism / cross-platform bit-exactness.
    // -----------------------------------------------------------------
    [[nodiscard]] static ::uint64 HashName(const char* Name) noexcept
    {
        if (Name == nullptr)
        {
            return 0;
        }
        const ::SIZE_T Len = ::std::strlen(Name);
        return ::XCore::Hash::FXxh3::Hash64(Name, Len, 0);
    }

    // -----------------------------------------------------------------
    // RegisterInt / RegisterFloat / RegisterString.
    //
    // All three follow the same shape:
    //   1. Hash the name.
    //   2. Acquire the exclusive lock.
    //   3. Check for a duplicate registration; return existing if so.
    //   4. Allocate the concrete TConsoleVariable<T> on FMemory.
    //   5. Insert into the map; release the lock.
    //   6. Return the new pointer.
    //
    // Duplicate detection: a pre-existing entry under the same hash
    // returns the existing pointer (idempotent registration). XBT's
    // build-time scan (fix M-7; Phase 1g) is the strict-failure
    // surface; the runtime path here is defense-in-depth.
    // -----------------------------------------------------------------

    IConsoleVariable* IConsoleManager::RegisterInt(const char* Name,
                                                   ::int32 Default,
                                                   const char* Help,
                                                   ECVarFlags Flags) noexcept
    {
        if (Name == nullptr) [[unlikely]]
        {
            return nullptr;
        }

        const ::uint64 Key = HashName(Name);
        FConsoleManagerState* State = GetSingletonImpl().GetState();

        ::XCore::HAL::FScopedWriteLock L(State->Lock);

        if (IConsoleVariable** Existing = State->Map.Find(Key))
        {
            // Idempotent: the second registration sees the first one.
            // The XBT build-time check (Phase 1g) catches the case
            // where the duplicate is unintentional.
            return *Existing;
        }

        // Allocate via FMemory with the Container tag (CVars are
        // engine-lifetime; the Container tag is the principled choice
        // for the registry's internal storage).
        void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
            sizeof(TConsoleVariableInt32),
            alignof(TConsoleVariableInt32),
            ::XCore::HAL::FMemTag::Container);

        TConsoleVariableInt32* CVar = new (Storage) TConsoleVariableInt32(
            ::XCore::FString(Name),
            Default,
            ::XCore::FString(Help != nullptr ? Help : ""),
            Flags);

        State->Map.Add(Key, static_cast<IConsoleVariable*>(CVar));
        return CVar;
    }

    IConsoleVariable* IConsoleManager::RegisterFloat(const char* Name,
                                                     float Default,
                                                     const char* Help,
                                                     ECVarFlags Flags) noexcept
    {
        if (Name == nullptr) [[unlikely]]
        {
            return nullptr;
        }

        const ::uint64 Key = HashName(Name);
        FConsoleManagerState* State = GetSingletonImpl().GetState();

        ::XCore::HAL::FScopedWriteLock L(State->Lock);

        if (IConsoleVariable** Existing = State->Map.Find(Key))
        {
            return *Existing;
        }

        void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
            sizeof(TConsoleVariableFloat),
            alignof(TConsoleVariableFloat),
            ::XCore::HAL::FMemTag::Container);

        TConsoleVariableFloat* CVar = new (Storage) TConsoleVariableFloat(
            ::XCore::FString(Name),
            Default,
            ::XCore::FString(Help != nullptr ? Help : ""),
            Flags);

        State->Map.Add(Key, static_cast<IConsoleVariable*>(CVar));
        return CVar;
    }

    IConsoleVariable* IConsoleManager::RegisterString(const char* Name,
                                                      const char* Default,
                                                      const char* Help,
                                                      ECVarFlags Flags) noexcept
    {
        if (Name == nullptr) [[unlikely]]
        {
            return nullptr;
        }

        const ::uint64 Key = HashName(Name);
        FConsoleManagerState* State = GetSingletonImpl().GetState();

        ::XCore::HAL::FScopedWriteLock L(State->Lock);

        if (IConsoleVariable** Existing = State->Map.Find(Key))
        {
            return *Existing;
        }

        void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
            sizeof(TConsoleVariableString),
            alignof(TConsoleVariableString),
            ::XCore::HAL::FMemTag::Container);

        TConsoleVariableString* CVar = new (Storage) TConsoleVariableString(
            ::XCore::FString(Name),
            ::XCore::FString(Default != nullptr ? Default : ""),
            ::XCore::FString(Help != nullptr ? Help : ""),
            Flags);

        State->Map.Add(Key, static_cast<IConsoleVariable*>(CVar));
        return CVar;
    }

    // -----------------------------------------------------------------
    // Find -- shared-locked lookup.
    //
    // Per Section 9.5: "lock-free fast-path via published-pointer +
    // acquire fence" applies to TConsoleVariableHandle. The Find
    // surface here is the diagnostic-only path; taking the shared
    // lock is correct.
    // -----------------------------------------------------------------
    IConsoleVariable* IConsoleManager::Find(const char* Name) noexcept
    {
        if (Name == nullptr) [[unlikely]]
        {
            return nullptr;
        }

        const ::uint64 Key = HashName(Name);
        FConsoleManagerState* State = GetSingletonImpl().GetState();

        ::XCore::HAL::FScopedReadLock L(State->Lock);

        if (IConsoleVariable** Existing = State->Map.Find(Key))
        {
            return *Existing;
        }
        return nullptr;
    }

    // -----------------------------------------------------------------
    // ForEach -- shared-locked iteration.
    //
    // Walks every entry in the map; the visitor is called once per
    // CVar under the shared lock. The visitor MUST NOT call
    // Register* (would deadlock); Find is safe (re-entrant shared).
    // -----------------------------------------------------------------
    void IConsoleManager::ForEach(void (*Visitor)(IConsoleVariable*, void*), void* UserData) noexcept
    {
        if (Visitor == nullptr) [[unlikely]]
        {
            return;
        }

        FConsoleManagerState* State = GetSingletonImpl().GetState();
        ::XCore::HAL::FScopedReadLock L(State->Lock);

        for (auto& Pair : State->Map)
        {
            Visitor(Pair.Value, UserData);
        }
    }

    // -----------------------------------------------------------------
    // __Initialize -- the PostStaticInit drain hook.
    //
    // Pops every node from the Treiber stack and registers it via
    // the matching Register* method. The node->Registered field is
    // updated so FAutoConsoleVariable<T>::GetHandle() can find the
    // backing CVar.
    //
    // The DrainCompleted flag prevents accidental double-drain
    // (idempotent; subsequent calls are no-ops).
    // -----------------------------------------------------------------
    void IConsoleManager::__Initialize() noexcept
    {
        IConsoleManagerImpl& Impl = GetSingletonImpl();
        FConsoleManagerState* State = Impl.GetState();

        // Idempotency: if a prior __Initialize already ran, we still
        // drain any new nodes (plugin load pushes new ones), but we
        // don't reset the DrainCompleted flag.
        FAutoConsoleVariableNode* Node = nullptr;
        while ((Node = __PopAutoCVar()) != nullptr)
        {
            IConsoleVariable* Registered = nullptr;
            switch (Node->ValueType)
            {
            case ECVarValueType::Int32:
                Registered = Impl.RegisterInt(Node->Name, Node->DefaultInt, Node->Help, Node->Flags);
                break;
            case ECVarValueType::Float:
                Registered = Impl.RegisterFloat(Node->Name, Node->DefaultFloat, Node->Help, Node->Flags);
                break;
            case ECVarValueType::String:
                Registered = Impl.RegisterString(Node->Name, Node->DefaultStr, Node->Help, Node->Flags);
                break;
            }
            Node->Registered = Registered;
        }

        State->DrainCompleted.store(true, ::std::memory_order_release);
    }

    // -----------------------------------------------------------------
    // FAutoConsoleVariable<T>::GetHandle definitions.
    //
    // Each specialisation's GetHandle returns a typed handle to the
    // concrete CVar's value cell. Pre-drain calls return an unbound
    // handle (m_node.Registered is null).
    //
    // The bodies live here (in the IConsoleManager.cpp TU) because
    // they reference the private concrete types
    // (TConsoleVariableInt32, etc.). Putting them in the public
    // header would leak the private types.
    // -----------------------------------------------------------------

    TConsoleVariableHandle<::int32> FAutoConsoleVariable<::int32>::GetHandle() const noexcept
    {
        if (m_node.Registered == nullptr) [[unlikely]]
        {
            return TConsoleVariableHandle<::int32>();
        }
        // Down-cast to the concrete type to access the value cell.
        // The concrete type is guaranteed because the registration
        // path picks the concrete by ValueType.
        TConsoleVariableInt32* Concrete = static_cast<TConsoleVariableInt32*>(m_node.Registered);
        return TConsoleVariableHandle<::int32>(Concrete->__GetValueCellPtr());
    }

    TConsoleVariableHandle<float> FAutoConsoleVariable<float>::GetHandle() const noexcept
    {
        if (m_node.Registered == nullptr) [[unlikely]]
        {
            return TConsoleVariableHandle<float>();
        }
        TConsoleVariableFloat* Concrete = static_cast<TConsoleVariableFloat*>(m_node.Registered);
        return TConsoleVariableHandle<float>(Concrete->__GetValueCellPtr());
    }

} // namespace XCore::Misc
