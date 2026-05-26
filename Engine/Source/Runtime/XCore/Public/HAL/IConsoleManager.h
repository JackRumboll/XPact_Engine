// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// IConsoleManager.h -- singleton CVar registry (Section 9.1, 9.5).
// =====================================================================
//
// XCore-4a Rev 3, Section 9.1 (Public API) + Section 9.5 (Lifetime /
// static-init ordering; fix C-6 content-hash keying; fix M-7
// build-time double-registration error).
//
// IConsoleManager is the global CVar registry. Concrete CVars are
// allocated on the engine's allocator (FMemory) and indexed by a
// content-hash-keyed map (XXH3 over the UTF-8 name bytes).
//
// REGISTRATION FLOW (Section 9.5):
//   * FAutoConsoleVariable<T> at file scope queues itself onto a
//     lock-free Treiber stack during static-init. The head pointer
//     is constinit std::atomic<FAutoConsoleVariableNode*> declared
//     in IConsoleManager.cpp. Each ctor pushes onto the stack via
//     compare_exchange_weak(memory_order_acq_rel). The Treiber
//     stack is correct under parallel constructor invocation (DLL
//     parallel-loaders).
//   * IConsoleManager::__Initialize() (called from XEngineInit::
//     Phase_PostStaticInit) drains the stack into the hash map.
//
// QUERYING:
//   * IConsoleManager::Find(name) walks the map under a shared
//     RWLock; the published-pointer fast path is lock-free in
//     steady state (Section 9.5 wording).
//   * Hot-path callers cache the TConsoleVariableHandle<T> returned
//     by FAutoConsoleVariable<T>::GetHandle() (fix B-C3); they
//     never call Find on the hot path.
//
// CONTENT-HASH KEYING (fix C-6):
//   The hash map is keyed by XXH3(name_bytes, len, seed=0). Pointer-
//   identity keying would fail when two DLLs include the same CVar
//   literal (two distinct const char* values pointing at byte-
//   identical strings); the registrations would collide silently
//   in pointer-keyed maps. Content-hash keying makes the collision
//   an explicit registration failure at the second Register call.
//
//   PER SPEC SECTION 9.5 (Rev 1 audit close-out): the collision is
//   reported at the second Register call as a Dev diagnostic naming
//   BOTH source-locations (the existing registration and the new
//   colliding registration), and Register* RETURNS nullptr. The
//   previous behaviour of returning the pre-existing pointer (silent
//   idempotent dedup) was REMOVED -- spec section 9.5 wording "collision
//   is explicit at registration, not silent shadowing" is the
//   contract. Callers' XPACT_CHECK(cvar != nullptr) fires on the
//   collision, which surfaces the bug at the call site rather than
//   masking it with a wrong-value pointer.
//
// BUILD-TIME DOUBLE-REGISTRATION ERROR (fix M-7):
//   XBT pre-scans the codebase for FAutoConsoleVariable<T>
//   constructions and emits a build error if two distinct
//   constructions share the same name. The runtime content-hash
//   keying remains as defense-in-depth. Phase 1g wires the XBT
//   scan; the runtime path here is the load-bearing defense.
//
// ORDERING (Section 9.5):
//   Pre-EInitPhase::PostStaticInit reads return the registered
//   default and assert in Debug / warn in Dev. This inverts UE's
//   pattern where reads against not-yet-registered CVars silently
//   mis-see later registrations.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XAssertionMacros.h"      // XPACT_HAS_SOURCE_LOCATION
#include "HAL/IConsoleVariable.h"
#include "HAL/ECVarFlags.h"
#include "HAL/ECVarSetByPriority.h"

#if XPACT_HAS_SOURCE_LOCATION
    #include <source_location>
#endif

namespace XCore::Misc
{

    // Forward declaration of the internal Treiber-stack node;
    // FAutoConsoleVariable<T> instances queue these at static-init.
    // The full definition lives in IConsoleManager.cpp.
    class FAutoConsoleVariableNode;

    // -----------------------------------------------------------------
    // IConsoleManager -- singleton CVar registry.
    //
    // Lifetime: a singleton (Get() returns the live instance). The
    // singleton's storage is a function-local static so it is
    // constructed on first call -- this avoids the static-init
    // ordering trap (any CVar registration during static-init goes
    // onto the Treiber stack, NOT into the singleton; the singleton
    // drains the stack at __Initialize()).
    //
    // Threading: methods are thread-safe. Find is shared-locked +
    // lock-free fast path; Register/ForEach take the exclusive lock.
    // -----------------------------------------------------------------
    class IConsoleManager
    {
    public:
        // -------------------------------------------------------------
        // Get -- the singleton accessor.
        //
        // Returns a reference to the engine-wide CVar registry. Safe
        // to call from any phase >= PostStaticInit; calls during
        // PreStaticInit return a reference to an empty registry
        // (the Treiber stack is still being populated; the drain
        // happens at __Initialize()).
        //
        // The function-local static pattern ensures the singleton
        // is constructed on first call, NOT at static-init time.
        // -------------------------------------------------------------
        [[nodiscard]] static IConsoleManager& Get() noexcept;

        // -------------------------------------------------------------
        // RegisterInt / RegisterFloat / RegisterString --
        // direct registration surfaces.
        //
        // FAutoConsoleVariable<T> calls into these at the drain step
        // (__Initialize) after popping the Treiber stack. Direct
        // user calls are supported for dynamically-registered CVars
        // (plugins / hot-reloaded modules); they take the exclusive
        // RWLock and insert into the map.
        //
        // Name keying: content-hash via XXH3 of the name's UTF-8
        // bytes (fix C-6). The Register* methods compute the hash
        // internally; user code passes the const char* and the
        // implementation hashes it.
        //
        // Duplicate Name (spec section 9.5 fix C-6; Rev 1 audit close-out):
        // if a CVar with the same name already exists, Register*
        // emits a Dev diagnostic naming BOTH source-locations (the
        // existing registration and the new colliding registration)
        // and returns nullptr. Callers' XPACT_CHECK(cvar != nullptr)
        // then fires at the second registration site. The previous
        // silent-idempotent-return-existing behaviour was REMOVED
        // per spec wording "collision is explicit at registration,
        // not silent shadowing". The build-time XBT check (fix M-7;
        // Phase 1g) is the strict-failure surface; the runtime path
        // here is the load-bearing defense for plugin-tier dynamic
        // registrations that escape the build-time scan.
        //
        // Phase guard (spec section 9.5; Rev 1 audit MAJOR-2 close-out): the
        // registry surface methods (Register*, Find) assert in
        // Debug / warn in Dev that EngineInitPhase() >=
        // PostStaticInit. The Treiber-stack push from a constinit
        // FAutoConsoleVariable's ctor at PreStaticInit is NOT a
        // registry-surface call -- it queues the registration
        // metadata; the actual Register call happens at __Initialize
        // (PostStaticInit drain).
        //
        // SourceLocation: defaulted to std::source_location::current().
        // The captured location is used in the collision diagnostic.
        // For FAutoConsoleVariable-mediated registrations the drain
        // step passes the ctor-captured SrcLoc through.
        //
        // Returns the IConsoleVariable* on success; nullptr on a
        // duplicate-name collision (see Duplicate Name above).
        // -------------------------------------------------------------
#if XPACT_HAS_SOURCE_LOCATION
        [[nodiscard]] IConsoleVariable* RegisterInt(
            const char* Name,
            ::int32 Default,
            const char* Help,
            ECVarFlags Flags = ECVarFlags::Default,
            ::std::source_location SrcLoc = ::std::source_location::current()) noexcept;

        [[nodiscard]] IConsoleVariable* RegisterFloat(
            const char* Name,
            float Default,
            const char* Help,
            ECVarFlags Flags = ECVarFlags::Default,
            ::std::source_location SrcLoc = ::std::source_location::current()) noexcept;

        [[nodiscard]] IConsoleVariable* RegisterString(
            const char* Name,
            const char* Default,
            const char* Help,
            ECVarFlags Flags = ECVarFlags::Default,
            ::std::source_location SrcLoc = ::std::source_location::current()) noexcept;
#else
        // Pre-C++20 fallback: no source-location capture. The
        // collision diagnostic degrades to a name-only message.
        [[nodiscard]] IConsoleVariable* RegisterInt(
            const char* Name,
            ::int32 Default,
            const char* Help,
            ECVarFlags Flags = ECVarFlags::Default) noexcept;

        [[nodiscard]] IConsoleVariable* RegisterFloat(
            const char* Name,
            float Default,
            const char* Help,
            ECVarFlags Flags = ECVarFlags::Default) noexcept;

        [[nodiscard]] IConsoleVariable* RegisterString(
            const char* Name,
            const char* Default,
            const char* Help,
            ECVarFlags Flags = ECVarFlags::Default) noexcept;
#endif

        // -------------------------------------------------------------
        // Find -- diagnostic-only lookup.
        //
        // Returns the IConsoleVariable* for the given name, or
        // nullptr if no such CVar is registered. The lookup is
        // content-hash-keyed; the const char* Name need NOT be the
        // same pointer that was passed to Register* (the hash is
        // over the bytes, not the pointer).
        //
        // PER SPEC SECTION 9.5 fix B-C3:
        //   "IConsoleManager::Find(name) remains in the API for
        //    diagnostic and one-shot use (e.g., the console parser);
        //    its use in hot-path code is a documented anti-pattern."
        //
        // Hot-path callers use TConsoleVariableHandle<T> (Section 9.5
        // fix B-C3); calls to Find on the hot path are detected by
        // CI grep at Phase 1g.
        //
        // Threading: takes the shared RWLock; safe for concurrent
        // callers.
        // -------------------------------------------------------------
        [[nodiscard]] IConsoleVariable* Find(const char* Name) noexcept;

        // -------------------------------------------------------------
        // ForEach -- iterate all registered CVars.
        //
        // The visitor function is called once per CVar; the call
        // is made under the shared RWLock so the visitor must not
        // call Register* (would deadlock). Find calls inside the
        // visitor are safe (re-entrant shared lock).
        //
        // The visitor signature is a C-style function pointer
        // rather than std::function so the call site has no
        // allocator interaction and the call surface is C-ABI-
        // compatible (for FFI / debug-tool integration).
        //
        // Visitor contract: the IConsoleVariable* is engine-
        // lifetime-valid; UserData is opaque pass-through. The
        // visitor should be re-entrant-safe (no shared mutable
        // state without synchronisation).
        // -------------------------------------------------------------
        void ForEach(void (*Visitor)(IConsoleVariable*, void*), void* UserData) noexcept;

        // -------------------------------------------------------------
        // __Initialize -- PostStaticInit drain hook.
        //
        // Called by XEngineInit::Phase_PostStaticInit. Drains the
        // FAutoConsoleVariable Treiber stack into the registry. The
        // double-underscore prefix flags this as engine-internal
        // bootstrap code; user code MUST NOT call it directly.
        //
        // Idempotent: re-entrant calls during the same phase are
        // no-ops. Calls before PostStaticInit (i.e., during static-
        // init when the EInitPhase is still PreStaticInit) are a
        // bug and assert in Debug + warn in Dev.
        //
        // After __Initialize the Treiber stack is empty; subsequent
        // FAutoConsoleVariable ctors push onto the stack and the
        // NEXT __Initialize call (if any -- typically there is only
        // one; plugin-load triggers another) drains the new entries.
        // -------------------------------------------------------------
        static void __Initialize() noexcept;

        // -------------------------------------------------------------
        // __PushAutoCVar -- the Treiber-stack push.
        //
        // FAutoConsoleVariable<T>'s constructor calls this with a
        // FAutoConsoleVariableNode containing the registration
        // metadata. The push uses compare_exchange_weak with
        // memory_order_acq_rel; correct under parallel constructor
        // invocation across DLL boundaries.
        //
        // The node is allocated by the caller (typically as a
        // member of the FAutoConsoleVariable instance); the
        // IConsoleManager does NOT take ownership -- the
        // FAutoConsoleVariable owns the node for its lifetime
        // (which is the engine's lifetime when used as a file-scope
        // static).
        // -------------------------------------------------------------
        static void __PushAutoCVar(FAutoConsoleVariableNode* Node) noexcept;

        // -------------------------------------------------------------
        // __PopAutoCVar -- the drain step.
        //
        // Pops one node from the Treiber stack; returns nullptr when
        // the stack is empty. Called by __Initialize in a loop until
        // the stack drains. Pop uses memory_order_acquire to pair
        // with the push's memory_order_acq_rel; the value-side data
        // in the node is visible to the popper.
        //
        // Engine-internal; user code MUST NOT call.
        // -------------------------------------------------------------
        [[nodiscard]] static FAutoConsoleVariableNode* __PopAutoCVar() noexcept;

    protected:
        // The singleton is allocated by Get(); no public ctor.
        IConsoleManager() noexcept;
        ~IConsoleManager() noexcept;

        IConsoleManager(const IConsoleManager&)            = delete;
        IConsoleManager& operator=(const IConsoleManager&) = delete;
        IConsoleManager(IConsoleManager&&)                 = delete;
        IConsoleManager& operator=(IConsoleManager&&)      = delete;
    };

} // namespace XCore::Misc
