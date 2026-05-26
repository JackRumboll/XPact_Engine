// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FCustomVersionRegistry.h -- process-wide custom-version registry.
// =====================================================================
//
// XCore-4b Rev 3, Section 8.3 (FCustomVersionRegistry (global)).
//
// The global FCustomVersionRegistry is a process-wide singleton holding
// every FCustomVersion every loaded module registered. Modules register
// their FCustomVersions at module-init time (the XHT-emitted aggregator
// calls RegisterCustomVersion()). The registry feeds the per-archive
// container at save time (XSerialization Layer 9 pulls the latest
// registered version for each type whose property graph appears in the
// archive).
//
// THREAD SAFETY: read-heavy, write at module init. The registry is
// protected by an FRWLock; reads acquire shared, writes acquire
// exclusive. Modules typically register all their custom versions
// during static initialisation (PostStaticInit phase per XCore-4a §1.5),
// so the write contention is bounded by the per-DLL-load module count.
//
// HOT-RELOAD SAFETY (Section 9):
//
//   * No virtual methods.
//   * Hot-patching a downstream DLL does NOT clear the registry; the
//     pre-patch entries remain stable.
//   * A re-registration of the SAME Key with the same or higher Version
//     is accepted and overwrites (matching FCustomVersionContainer::
//     Insert semantics). A re-registration with a LOWER Version is
//     rejected with no state change (a regression in version would
//     break monotonic discipline).
//   * An explicit UnregisterCustomVersion is provided for symmetric DLL
//     unload, but is NOT called on hot-patch (entries survive the patch
//     by design; a patched DLL's RegisterCustomVersion calls overwrite
//     the previous entries at the same Key).
//
// PROCESS-SINGLETON DISCIPLINE: the registry is accessed through
// FCustomVersionRegistry::Get() returning a reference to the single
// static instance. The instance is initialised at PostStaticInit (the
// FRWLock primitive must be live; the FRWLock constructor itself is
// noexcept and runs at static-init time, so the registry can construct
// even earlier — but the EInitPhase ladder discipline locks the
// official "registry is consultable" milestone at PostStaticInit).
//
// =====================================================================

#include "HAL/FRWLock.h"
#include "Macros/XCoreTypes.h"
#include "Reflection/FCustomVersion.h"
#include "Reflection/FCustomVersionContainer.h"
#include "Reflection/FGuid.h"

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // ERegisterResult -- outcome of RegisterCustomVersion.
    //
    // Explicit-enum return rather than bool so the caller can
    // disambiguate the failure modes for diagnostic logging.
    // -----------------------------------------------------------------
    enum class ERegisterResult : ::uint8
    {
        Registered = 0,         // first registration of this Key
        Updated    = 1,         // existing Key updated to a >= Version
        Rejected   = 2,         // Version < existing; no state change
        InvalidKey = 3,         // Key.IsZero(); zero GUID rejected
    };

    // -----------------------------------------------------------------
    // FCustomVersionRegistry -- process-wide singleton.
    // -----------------------------------------------------------------
    class FCustomVersionRegistry
    {
    public:
        // -------------------------------------------------------------
        // Get -- access the process-singleton instance.
        //
        // The instance is constructed at static-init time (the FRWLock
        // primitive is noexcept and constexpr-constructible-friendly).
        // The first call must happen at or after PostStaticInit per
        // the spec's EInitPhase ladder discipline (§3 cyclic-risk
        // resolution).
        // -------------------------------------------------------------
        [[nodiscard]] static FCustomVersionRegistry& Get() noexcept;

        // -------------------------------------------------------------
        // RegisterCustomVersion -- add or update an entry.
        //
        // RULES:
        //   * Zero GUID Key is rejected with ERegisterResult::InvalidKey.
        //   * First registration of a Key: stored verbatim;
        //     ERegisterResult::Registered.
        //   * Re-registration with Version >= existing Version: the
        //     entry is updated; ERegisterResult::Updated. (Equal-Version
        //     re-registration is treated as Updated; the FriendlyName
        //     and Reserved are overwritten in case they diverged.)
        //   * Re-registration with Version < existing Version: rejected
        //     with no state change; ERegisterResult::Rejected. This is
        //     the monotonic-discipline guard.
        //
        // Thread-safe: acquires the registry's FRWLock in exclusive mode.
        // -------------------------------------------------------------
        ERegisterResult RegisterCustomVersion(FGuid Key,
                                              ::int32 Version,
                                              FName FriendlyName) noexcept;

        // -------------------------------------------------------------
        // UnregisterCustomVersion -- explicit removal.
        //
        // Returns true if an entry was removed, false if no entry with
        // `Key` was present. Called by DLL-unload paths that want to
        // tidy up explicitly; hot-patch paths do NOT call this (entries
        // survive the patch by design).
        //
        // Thread-safe: acquires the registry's FRWLock in exclusive mode.
        // -------------------------------------------------------------
        bool UnregisterCustomVersion(FGuid Key) noexcept;

        // -------------------------------------------------------------
        // GetRegisteredVersionCopy -- by-value lookup (the safe API).
        //
        // Returns a copy of the entry if present (and OutFound=true);
        // returns a zero-initialised FCustomVersion (and OutFound=false)
        // if not. The copy is safe to store beyond the call scope
        // because FCustomVersion is a POD.
        //
        // Thread-safe: acquires the registry's FRWLock in shared mode.
        //
        // XCore-4b Subagent A FIX-A5 NOTE: the prior pointer-returning
        // `GetRegisteredVersion(FGuid)` was REMOVED. That signature
        // released the shared-read lock at return and exposed a dangling
        // pointer if a concurrent writer's RegisterCustomVersion call
        // relocated the FCustomVersionContainer's TArray buffer (e.g.,
        // grow-to-capacity reallocation). The output-param + by-value
        // contract here is the lock-safe replacement; the copy cost is
        // 32 bytes (FCustomVersion = FGuid 16 + int32 + FName 8 + pad 4)
        // which is well within the by-value-return budget for ABI
        // surfaces of this size.
        // -------------------------------------------------------------
        [[nodiscard]] FCustomVersion GetRegisteredVersionCopy(FGuid Key, bool& OutFound) const noexcept;

        // -------------------------------------------------------------
        // TryGetRegisteredVersion -- alternative output-param spelling.
        //
        // Equivalent to GetRegisteredVersionCopy but with the bool
        // return-flag rather than the OutFound parameter. Provided per
        // XCore conventions favouring "Try*" + bool return for lookup
        // APIs where the result is naturally absent (FNamePool-style).
        //
        // Returns true and writes OutVersion on hit; returns false and
        // leaves OutVersion unmodified on miss.
        //
        // Thread-safe: acquires the registry's FRWLock in shared mode.
        // -------------------------------------------------------------
        [[nodiscard]] bool TryGetRegisteredVersion(FGuid Key, FCustomVersion& OutVersion) const noexcept;

        // -------------------------------------------------------------
        // Contains -- true iff an entry with `Key` is registered.
        //
        // Thread-safe: acquires the registry's FRWLock in shared mode.
        // -------------------------------------------------------------
        [[nodiscard]] bool Contains(FGuid Key) const noexcept;

        // -------------------------------------------------------------
        // Size -- number of registered entries.
        //
        // Thread-safe: acquires the registry's FRWLock in shared mode.
        // The returned count is a snapshot; the registry may mutate
        // between this call and the next.
        // -------------------------------------------------------------
        [[nodiscard]] ::int32 Size() const noexcept;

        // -------------------------------------------------------------
        // GetAllRegistered -- return a COPY of the entire container.
        //
        // The returned container is a snapshot taken under the shared
        // lock; subsequent registry mutations do not affect the snapshot.
        //
        // Thread-safe: acquires the registry's FRWLock in shared mode.
        // -------------------------------------------------------------
        [[nodiscard]] FCustomVersionContainer GetAllRegistered() const noexcept;

        // -------------------------------------------------------------
        // EmptyForTesting -- reset the registry to empty.
        //
        // Intended for unit tests that want a clean slate before
        // exercising a registration sequence. Production code MUST NOT
        // call this (the singleton accumulates entries from every loaded
        // module; an EmptyForTesting in production would erase another
        // module's registrations).
        //
        // Thread-safe: acquires the registry's FRWLock in exclusive mode.
        // -------------------------------------------------------------
        void EmptyForTesting() noexcept;

        // Non-copyable / non-movable (it IS the singleton).
        FCustomVersionRegistry(const FCustomVersionRegistry&) = delete;
        FCustomVersionRegistry(FCustomVersionRegistry&&) = delete;
        FCustomVersionRegistry& operator=(const FCustomVersionRegistry&) = delete;
        FCustomVersionRegistry& operator=(FCustomVersionRegistry&&) = delete;

    private:
        // Singleton ctor: private. Only Get() constructs the instance.
        FCustomVersionRegistry() noexcept = default;
        ~FCustomVersionRegistry() = default;

        // The underlying container. Sorted-by-Key; the FRWLock guards
        // every read and every write.
        mutable ::XCore::HAL::FRWLock  Lock;
        FCustomVersionContainer        Container;
    };

    // ---------------------------------------------------------------------
    // TODO(Phase 4b.7): XBT transitive-dirty hook integration point.
    //
    // Per XCore-4b §8.4.3 (FIX-11): modifying a parent class's XPROPERTY
    // declared shape MUST flag XCore4b002 from XBT's transitive-dirty graph
    // at build time. The runtime registry is the data source for that
    // diagnostic; the build-tool integration (Phase 4b.7) walks the
    // registered FCustomVersions, computes SchemaHashes from the declared
    // FProperty shapes, and verifies each SchemaHash matches the value
    // that XHT emitted into the .gen.cpp. Until 4b.7 lands, this header
    // simply hosts the registry; no build-tool side is exposed.
    //
    // The integration point will be a static method
    //   FCustomVersionRegistry::ForEachRegistered(TFunctionRef<void(const FCustomVersion&)>)
    // that XBT calls at build time to walk the in-process registry and
    // validate against the manifest-emitted SchemaHash set.
    // ---------------------------------------------------------------------

} // namespace XCore::Reflect
