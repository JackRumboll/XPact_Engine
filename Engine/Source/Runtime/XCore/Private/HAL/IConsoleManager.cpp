// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// IConsoleManager.cpp -- CVar registry implementation (Section 9.5).
// =====================================================================
//
// XCore-4a Rev 3, Section 9.1 (Public API) + Section 9.2 (Threading
// contract) + Section 9.5 (Lifetime / static-init ordering; fix C-6
// content-hash keying; fix M-7 double-registration error) +
// Rev 1 audit close-out (CRITICAL-3 + AUDIT-AG2: removed silent
// idempotent dedup; collision now produces a Dev diagnostic naming
// BOTH source-locations and returns nullptr per spec section 9.5 wording
// "collision is explicit at registration, not silent shadowing").
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
// DUPLICATE-REGISTRATION POLICY (Rev 1 audit CRITICAL-3 / AG2 close-out):
//
//   The previous implementation silently returned the pre-existing
//   pointer on duplicate-name registration ("idempotent dedup"). That
//   behaviour was the very UE-style silent shadowing that spec fix
//   C-6 was created to eliminate. Per spec section 9.5: "Content-hash
//   keying makes the collision an explicit registration failure
//   that the engine reports at the call site."
//
//   The corrected behaviour:
//     1. Compute the content-hash key (XXH3 of the name bytes).
//     2. Acquire the registry lock exclusively.
//     3. Look up the key. If found, emit a Dev diagnostic naming
//        BOTH source-locations (existing + new) via LogAndAbort-
//        adjacent path (currently a stderr emission; the structured
//        log channel lands in a later phase), and return nullptr.
//     4. Otherwise insert and return the new pointer.
//
//   Callers' XPACT_CHECK(cvar != nullptr) fires on collision,
//   surfacing the bug at the call site rather than masking it
//   behind a value that maps to a different default.
//
// REGISTRY PHASE GUARD (Rev 1 audit MAJOR-2 close-out / fix M-9):
//
//   The registry-surface methods (Register*, Find) assert that
//   EngineInitPhase() >= PostStaticInit. This matches the
//   TConsoleVariableImpl Get*/Set* phase guard (Phase 1g fix M-9)
//   and the spec section 9.5 ordering guarantee. The Treiber-stack push
//   from a constinit FAutoConsoleVariable's ctor at PreStaticInit
//   is NOT a registry-surface call -- it only queues registration
//   metadata; the actual Register* call happens inside __Initialize
//   at PostStaticInit drain time (which itself runs at exactly
//   PostStaticInit, so the guard is satisfied).
//
// ALLOC-OUTSIDE-LOCK (Rev 1 audit TC4 close-out):
//
//   FMemory::MallocOrAbort for CVar storage is invoked BEFORE the
//   registry lock is acquired (the AB-BA hazard between Pool.Mutex
//   and State->Lock is closed). The pre-allocated storage may be
//   abandoned (placement-new constructed object destroyed + freed)
//   if the under-lock duplicate check fires; this is the safe
//   pattern when MallocOrAbort aborts on OOM (no nullptr return
//   to back out of).
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
#include "Macros/XAssertionMacros.h"

#include <atomic>
#include <cstdio>
#include <cstring>
#include <new>

#if XPACT_HAS_SOURCE_LOCATION
    #include <source_location>
#endif

// ---------------------------------------------------------------------
// OutputDebugStringA forward declaration -- file scope (Win32 only).
//
// Declared at the global namespace (NOT inside an anonymous namespace)
// so the call site can reach it via `::OutputDebugStringA`. Hosting the
// declaration inside `namespace XCore::Misc { namespace { ... } }` would
// place the symbol in the TU-local anonymous namespace, which the
// `::`-qualified call cannot find. The `extern "C"` keeps the C linkage
// the Win32 ABI requires.
//
// Forward-declared (rather than `#include <debugapi.h>`) to avoid
// dragging in <windows.h>'s ~500k lines of macro pollution (every
// engine identifier `Far`, `Near`, `IN`, `OUT`, etc., would otherwise
// be redefined / shadowed).
// ---------------------------------------------------------------------
#if defined(_WIN64) || defined(_WIN32)
extern "C" __declspec(dllimport) void __stdcall OutputDebugStringA(const char*);
#endif

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
    // -----------------------------------------------------------------
    // FRegistryEntry -- the per-CVar registry record (Rev 1 audit
    // CRITICAL-3 close-out).
    //
    // The map's value type carries both the CVar pointer AND the
    // source-location of the original registration. The location is
    // used by the duplicate-registration diagnostic to name the
    // pre-existing call site.
    //
    // SrcLoc is conditionally present (gated on
    // XPACT_HAS_SOURCE_LOCATION) so older toolchains still build;
    // the diagnostic degrades to a name-only message in that case.
    // -----------------------------------------------------------------
    struct FRegistryEntry
    {
        IConsoleVariable* CVar = nullptr;
#if XPACT_HAS_SOURCE_LOCATION
        ::std::source_location SrcLoc{};
#endif
    };

    struct FConsoleManagerState
    {
        // Content-hash-keyed map: XXH3(name_bytes, len, 0) -> FRegistryEntry.
        // The entry carries both the CVar pointer and the source-
        // location of the original registration (used by the
        // duplicate-registration diagnostic).
        //
        // Rev 3 Round 2 audit FIX-R2-MIN-1+2: tagged FMemTag::CVar so
        // the allocator's per-tag accounting attributes registry
        // allocations to the CVar subsystem (not the default
        // Container tag). Initialised via the FMemTag-taking TMap
        // ctor; m_tag propagates into every subsequent Rehash /
        // ClearAndDeallocate allocation.
        ::XCore::TMap<::uint64, FRegistryEntry> Map{::XCore::HAL::FMemTag::CVar};

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
    // The state struct is held by reference (FMemory-allocated; the
    // Rev 1 audit MS2 close-out replaced raw operator new with
    // FMemory::MallocOrAbort + placement-new so the allocation is
    // attributed to FMemTag::CVar). The FConsoleManagerState's
    // storage outlives the IConsoleManagerImpl by design -- both
    // have engine lifetime.
    class IConsoleManagerImpl final : public IConsoleManager
    {
    public:
        // Rev 1 audit MS2 close-out: route state allocation through
        // FMemory::MallocOrAbort with FMemTag::CVar instead of raw
        // operator new. The placement-new constructs the state in
        // the FMemory-owned storage; the dtor explicitly destroys
        // the state and frees via FMemory::Free.
        IConsoleManagerImpl() noexcept
            : IConsoleManager()
            , m_state(static_cast<FConsoleManagerState*>(
                ::XCore::HAL::FMemory::MallocOrAbort(
                    sizeof(FConsoleManagerState),
                    alignof(FConsoleManagerState),
                    ::XCore::HAL::FMemTag::CVar)))
        {
            // Placement-new construct the state at the
            // FMemory-allocated storage.
            ::new (static_cast<void*>(m_state)) FConsoleManagerState();
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
            // singleton, single delete site). Rev 1 audit MS2: the
            // tear-down explicitly destroys the placement-newd state
            // then frees via FMemory::Free (matched to the ctor's
            // MallocOrAbort).
            if (m_state != nullptr)
            {
                m_state->~FConsoleManagerState();
                ::XCore::HAL::FMemory::Free(m_state);
                m_state = nullptr;
            }
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
    // EmitDuplicateRegistrationDiagnostic -- the Dev-only diagnostic
    // for the duplicate-name collision (Rev 1 audit CRITICAL-3
    // close-out for fix C-6).
    //
    // The diagnostic names BOTH source-locations (the existing
    // registration and the new colliding one) so a developer can
    // immediately identify the conflict. The format is
    // forensic-grep-stable:
    //
    //   "[XPACT CVAR COLLISION] duplicate CVar name '<Name>'
    //    new={file}:{line} ({function})
    //    existing={file}:{line} ({function})\n"
    //
    // Routed via stderr (and OutputDebugStringA on Win64) so the
    // diagnostic surfaces in both attached-debugger and console
    // contexts. The structured-log channel (XLog) is a later-phase
    // upgrade; for Rev 1 the stderr emission is the contract.
    //
    // Shipping configuration: the function still emits to stderr
    // (the runtime collision IS a bug; we want it visible even in
    // Shipping). The build-time XBT scan (fix M-7) is the primary
    // gate; the runtime diagnostic is the back-stop.
    // -----------------------------------------------------------------
    namespace
    {
        // (OutputDebugStringA is forward-declared at file scope above so
        // the `::OutputDebugStringA` call below finds it in the global
        // namespace, not in this anonymous namespace.)

#if XPACT_HAS_SOURCE_LOCATION
        void EmitDuplicateRegistrationDiagnostic(const char* Name,
                                                 const ::std::source_location& NewLoc,
                                                 const ::std::source_location& ExistingLoc) noexcept
        {
            char Buf[1536];
            ::std::snprintf(Buf, sizeof(Buf),
                "[XPACT CVAR COLLISION] duplicate CVar name '%s'\n"
                "    new={file=%s, line=%u, function=%s}\n"
                "    existing={file=%s, line=%u, function=%s}\n"
                "    (spec section 9.5 fix C-6: collision is explicit at "
                "registration; Register* returns nullptr)\n",
                Name != nullptr ? Name : "(null)",
                NewLoc.file_name() != nullptr ? NewLoc.file_name() : "(no file)",
                static_cast<unsigned>(NewLoc.line()),
                NewLoc.function_name() != nullptr ? NewLoc.function_name() : "(no fn)",
                ExistingLoc.file_name() != nullptr ? ExistingLoc.file_name() : "(no file)",
                static_cast<unsigned>(ExistingLoc.line()),
                ExistingLoc.function_name() != nullptr ? ExistingLoc.function_name() : "(no fn)");
#if defined(_WIN64) || defined(_WIN32)
            // Diagnostic surfaces in attached-debugger output AND
            // stderr so test harnesses capturing either see the
            // message.
            ::OutputDebugStringA(Buf);
#endif
            ::std::fputs(Buf, stderr);
            ::std::fflush(stderr);
        }
#else
        void EmitDuplicateRegistrationDiagnostic(const char* Name) noexcept
        {
            char Buf[256];
            ::std::snprintf(Buf, sizeof(Buf),
                "[XPACT CVAR COLLISION] duplicate CVar name '%s' "
                "(source-location capture disabled on this toolchain; "
                "spec section 9.5 fix C-6: Register* returns nullptr)\n",
                Name != nullptr ? Name : "(null)");
            ::std::fputs(Buf, stderr);
            ::std::fflush(stderr);
        }
#endif
    } // anonymous

    // -----------------------------------------------------------------
    // RegisterInt / RegisterFloat / RegisterString.
    //
    // All three follow the same shape (Rev 1 audit close-out for
    // CRITICAL-3 / MAJOR-2 / TC4):
    //   1. Phase guard: EngineInitPhase() >= PostStaticInit (B5).
    //   2. Hash the name.
    //   3. PRE-ALLOCATE the concrete TConsoleVariable<T> on FMemory
    //      BEFORE acquiring the lock (TC4 / B11: closes the AB-BA
    //      hazard between Pool.Mutex inside FMemory and State->Lock).
    //   4. Placement-new construct the CVar in the pre-allocated
    //      storage.
    //   5. Acquire the exclusive lock.
    //   6. Check for duplicate. If found: release lock, destruct +
    //      free the pre-allocated CVar, emit Dev diagnostic naming
    //      BOTH source-locations, return nullptr (B1 / C-6).
    //   7. Insert into the map.
    //   8. Return the new pointer.
    //
    // Duplicate behaviour (B1 / spec section 9.5 fix C-6): the previous
    // silent-idempotent-return-existing behaviour was REMOVED. The
    // collision is now an explicit registration failure (nullptr
    // return) with a Dev diagnostic that the caller's
    // XPACT_CHECK(cvar != nullptr) catches.
    // -----------------------------------------------------------------

#if XPACT_HAS_SOURCE_LOCATION
    IConsoleVariable* IConsoleManager::RegisterInt(const char* Name,
                                                   ::int32 Default,
                                                   const char* Help,
                                                   ECVarFlags Flags,
                                                   ::std::source_location SrcLoc) noexcept
#else
    IConsoleVariable* IConsoleManager::RegisterInt(const char* Name,
                                                   ::int32 Default,
                                                   const char* Help,
                                                   ECVarFlags Flags) noexcept
#endif
    {
        // Phase guard (Rev 1 audit MAJOR-2 / fix M-9 ladder consistency).
        // The Register* methods are the registry surface; they may be
        // called only at PostStaticInit or later. Treiber-stack
        // pushes from constinit FAutoConsoleVariable ctors run at
        // PreStaticInit but do NOT reach this surface -- they queue
        // metadata only; the actual Register* call happens inside
        // __Initialize at PostStaticInit drain.
        XPACT_CHECK(::XCore::HAL::EngineInitPhase() >= ::XCore::HAL::EInitPhase::PostStaticInit);

        if (Name == nullptr) [[unlikely]]
        {
            return nullptr;
        }

        const ::uint64 Key = HashName(Name);
        FConsoleManagerState* State = GetSingletonImpl().GetState();

        // ALLOC OUTSIDE LOCK (Rev 1 audit TC4 close-out): allocate
        // the CVar storage and construct the object BEFORE acquiring
        // State->Lock. FMemory::MallocOrAbort may internally take
        // Pool.Mutex; doing so while holding State->Lock would
        // create an AB-BA lock-order hazard against any other site
        // that takes Pool.Mutex then State->Lock.
        void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
            sizeof(TConsoleVariableInt32),
            alignof(TConsoleVariableInt32),
            ::XCore::HAL::FMemTag::CVar);

        TConsoleVariableInt32* CVar = new (Storage) TConsoleVariableInt32(
            ::XCore::FString(Name),
            Default,
            ::XCore::FString(Help != nullptr ? Help : ""),
            Flags);

        {
            ::XCore::HAL::FScopedWriteLock L(State->Lock);

            if (FRegistryEntry* Existing = State->Map.Find(Key))
            {
                // Duplicate-name collision (Rev 1 audit CRITICAL-3 / C-6):
                // emit diagnostic naming both source-locations, abandon
                // the pre-allocated CVar, return nullptr. The pre-
                // allocation is the cost of the alloc-outside-lock
                // pattern; the alternative (lookup-then-alloc-under-
                // lock) recreates the AB-BA hazard.
#if XPACT_HAS_SOURCE_LOCATION
                EmitDuplicateRegistrationDiagnostic(Name, SrcLoc, Existing->SrcLoc);
#else
                EmitDuplicateRegistrationDiagnostic(Name);
#endif
                // Drop the lock before freeing (the dtor + Free should
                // not run under the registry lock).
                CVar->~TConsoleVariableInt32();
                ::XCore::HAL::FMemory::Free(Storage);
                return nullptr;
            }

            FRegistryEntry Entry;
            Entry.CVar = static_cast<IConsoleVariable*>(CVar);
#if XPACT_HAS_SOURCE_LOCATION
            Entry.SrcLoc = SrcLoc;
#endif
            State->Map.Add(Key, Entry);
        }

        return CVar;
    }

#if XPACT_HAS_SOURCE_LOCATION
    IConsoleVariable* IConsoleManager::RegisterFloat(const char* Name,
                                                     float Default,
                                                     const char* Help,
                                                     ECVarFlags Flags,
                                                     ::std::source_location SrcLoc) noexcept
#else
    IConsoleVariable* IConsoleManager::RegisterFloat(const char* Name,
                                                     float Default,
                                                     const char* Help,
                                                     ECVarFlags Flags) noexcept
#endif
    {
        XPACT_CHECK(::XCore::HAL::EngineInitPhase() >= ::XCore::HAL::EInitPhase::PostStaticInit);

        if (Name == nullptr) [[unlikely]]
        {
            return nullptr;
        }

        const ::uint64 Key = HashName(Name);
        FConsoleManagerState* State = GetSingletonImpl().GetState();

        void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
            sizeof(TConsoleVariableFloat),
            alignof(TConsoleVariableFloat),
            ::XCore::HAL::FMemTag::CVar);

        TConsoleVariableFloat* CVar = new (Storage) TConsoleVariableFloat(
            ::XCore::FString(Name),
            Default,
            ::XCore::FString(Help != nullptr ? Help : ""),
            Flags);

        {
            ::XCore::HAL::FScopedWriteLock L(State->Lock);

            if (FRegistryEntry* Existing = State->Map.Find(Key))
            {
#if XPACT_HAS_SOURCE_LOCATION
                EmitDuplicateRegistrationDiagnostic(Name, SrcLoc, Existing->SrcLoc);
#else
                EmitDuplicateRegistrationDiagnostic(Name);
#endif
                CVar->~TConsoleVariableFloat();
                ::XCore::HAL::FMemory::Free(Storage);
                return nullptr;
            }

            FRegistryEntry Entry;
            Entry.CVar = static_cast<IConsoleVariable*>(CVar);
#if XPACT_HAS_SOURCE_LOCATION
            Entry.SrcLoc = SrcLoc;
#endif
            State->Map.Add(Key, Entry);
        }

        return CVar;
    }

#if XPACT_HAS_SOURCE_LOCATION
    IConsoleVariable* IConsoleManager::RegisterString(const char* Name,
                                                      const char* Default,
                                                      const char* Help,
                                                      ECVarFlags Flags,
                                                      ::std::source_location SrcLoc) noexcept
#else
    IConsoleVariable* IConsoleManager::RegisterString(const char* Name,
                                                      const char* Default,
                                                      const char* Help,
                                                      ECVarFlags Flags) noexcept
#endif
    {
        XPACT_CHECK(::XCore::HAL::EngineInitPhase() >= ::XCore::HAL::EInitPhase::PostStaticInit);

        if (Name == nullptr) [[unlikely]]
        {
            return nullptr;
        }

        const ::uint64 Key = HashName(Name);
        FConsoleManagerState* State = GetSingletonImpl().GetState();

        void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
            sizeof(TConsoleVariableString),
            alignof(TConsoleVariableString),
            ::XCore::HAL::FMemTag::CVar);

        TConsoleVariableString* CVar = new (Storage) TConsoleVariableString(
            ::XCore::FString(Name),
            ::XCore::FString(Default != nullptr ? Default : ""),
            ::XCore::FString(Help != nullptr ? Help : ""),
            Flags);

        {
            ::XCore::HAL::FScopedWriteLock L(State->Lock);

            if (FRegistryEntry* Existing = State->Map.Find(Key))
            {
#if XPACT_HAS_SOURCE_LOCATION
                EmitDuplicateRegistrationDiagnostic(Name, SrcLoc, Existing->SrcLoc);
#else
                EmitDuplicateRegistrationDiagnostic(Name);
#endif
                CVar->~TConsoleVariableString();
                ::XCore::HAL::FMemory::Free(Storage);
                return nullptr;
            }

            FRegistryEntry Entry;
            Entry.CVar = static_cast<IConsoleVariable*>(CVar);
#if XPACT_HAS_SOURCE_LOCATION
            Entry.SrcLoc = SrcLoc;
#endif
            State->Map.Add(Key, Entry);
        }

        return CVar;
    }

    // -----------------------------------------------------------------
    // Find -- shared-locked lookup.
    //
    // Per Section 9.5: "lock-free fast-path via published-pointer +
    // acquire fence" applies to TConsoleVariableHandle. The Find
    // surface here is the diagnostic-only path; taking the shared
    // lock is correct.
    //
    // Phase guard (Rev 1 audit MAJOR-2 / fix M-9 ladder consistency):
    // Find is part of the registry surface and requires
    // PostStaticInit or later.
    // -----------------------------------------------------------------
    IConsoleVariable* IConsoleManager::Find(const char* Name) noexcept
    {
        XPACT_CHECK(::XCore::HAL::EngineInitPhase() >= ::XCore::HAL::EInitPhase::PostStaticInit);

        if (Name == nullptr) [[unlikely]]
        {
            return nullptr;
        }

        const ::uint64 Key = HashName(Name);
        FConsoleManagerState* State = GetSingletonImpl().GetState();

        ::XCore::HAL::FScopedReadLock L(State->Lock);

        if (FRegistryEntry* Existing = State->Map.Find(Key))
        {
            return Existing->CVar;
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
            Visitor(Pair.Value.CVar, UserData);
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
        //
        // Rev 1 audit CRITICAL-3 close-out: each Register* call
        // receives the Treiber-stack node's captured SrcLoc (set at
        // FAutoConsoleVariable<T> ctor time) so the duplicate-
        // collision diagnostic can name the original registration's
        // call site rather than reporting the drain loop as the
        // origin.
        FAutoConsoleVariableNode* Node = nullptr;
        while ((Node = __PopAutoCVar()) != nullptr)
        {
            IConsoleVariable* Registered = nullptr;
            switch (Node->ValueType)
            {
            case ECVarValueType::Int32:
#if XPACT_HAS_SOURCE_LOCATION
                Registered = Impl.RegisterInt(Node->Name, Node->DefaultInt, Node->Help, Node->Flags, Node->SrcLoc);
#else
                Registered = Impl.RegisterInt(Node->Name, Node->DefaultInt, Node->Help, Node->Flags);
#endif
                break;
            case ECVarValueType::Float:
#if XPACT_HAS_SOURCE_LOCATION
                Registered = Impl.RegisterFloat(Node->Name, Node->DefaultFloat, Node->Help, Node->Flags, Node->SrcLoc);
#else
                Registered = Impl.RegisterFloat(Node->Name, Node->DefaultFloat, Node->Help, Node->Flags);
#endif
                break;
            case ECVarValueType::String:
#if XPACT_HAS_SOURCE_LOCATION
                Registered = Impl.RegisterString(Node->Name, Node->DefaultStr, Node->Help, Node->Flags, Node->SrcLoc);
#else
                Registered = Impl.RegisterString(Node->Name, Node->DefaultStr, Node->Help, Node->Flags);
#endif
                break;
            }
            // Note: Registered may be nullptr if the node collided
            // with a previously-drained node of the same name (the
            // collision diagnostic has already been emitted by
            // Register*). The downstream GetHandle path returns an
            // unbound handle when Registered is nullptr, which is
            // the safe behaviour for the colliding second
            // registration.
            //
            // Rev 3 Round 2 audit FIX-R2-MAJ-2: release-store on the
            // atomic Registered field; pairs with the acquire-load
            // at every GetHandle reader. The release ordering
            // publishes the Registered pointer with happens-before
            // relative to any concurrent reader on a hot-reload-
            // plugin-load drain.
            Node->Registered.store(Registered, ::std::memory_order_release);
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
        // Rev 3 Round 2 audit FIX-R2-MAJ-2: single acquire-load on the
        // atomic Registered field. The local copy snapshots the value
        // so the (Registered != nullptr) check and the subsequent
        // down-cast use the same observed pointer (a concurrent drain
        // that re-flips Registered between the two reads on the prior
        // plain-pointer path could have produced inconsistent reads;
        // the snapshot eliminates that window).
        IConsoleVariable* const Registered =
            m_node.Registered.load(::std::memory_order_acquire);
        if (Registered == nullptr) [[unlikely]]
        {
            return TConsoleVariableHandle<::int32>();
        }
        // Down-cast to the concrete type to access the value cell.
        // The concrete type is guaranteed because the registration
        // path picks the concrete by ValueType.
        TConsoleVariableInt32* Concrete = static_cast<TConsoleVariableInt32*>(Registered);
        return TConsoleVariableHandle<::int32>(Concrete->__GetValueCellPtr());
    }

    TConsoleVariableHandle<float> FAutoConsoleVariable<float>::GetHandle() const noexcept
    {
        // Rev 3 Round 2 audit FIX-R2-MAJ-2: see int32 GetHandle for the
        // acquire-load + snapshot rationale.
        IConsoleVariable* const Registered =
            m_node.Registered.load(::std::memory_order_acquire);
        if (Registered == nullptr) [[unlikely]]
        {
            return TConsoleVariableHandle<float>();
        }
        TConsoleVariableFloat* Concrete = static_cast<TConsoleVariableFloat*>(Registered);
        return TConsoleVariableHandle<float>(Concrete->__GetValueCellPtr());
    }

} // namespace XCore::Misc
