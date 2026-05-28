// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// CDOManagement.cpp -- Class Default Object lifecycle body
// (XCoreXObject Rev 4 §8 + §8.1 + §8.1.1 + Rev 3 FIX-H-R2-6 +
// Phase 5.d).
// =====================================================================
//
// Four entry points + the EagerCDO queue + diagnostic accessors:
//
//   * GetClassDefaultObject(Class) -- lazy CDO accessor with
//     atomic-CAS construction (the standard double-checked locking
//     pattern at the per-FClass atomic slot).
//   * ComposeCDOName(Class)         -- "Default__<ClassName>" FName.
//   * EnqueueEagerCDO(Class)        -- append to g_PendingEagerCDOs.
//   * DrainPendingEagerCDOs()       -- walk + materialise.
//   * IsEagerCDO(Class)             -- predicate against CLASS_EagerCDO.
//   * GetPendingEagerCDOCount()     -- diagnostic.
//   * __ResetCDOsForTests()         -- test-only state reset.
//
// =====================================================================

#include "XObject/CDOManagement.h"

#include "Containers/FString.h"
#include "Containers/TArray.h"
#include "HAL/FCriticalSection.h"
#include "Reflection/EClassFlags.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/EObjectFlags.h"
#include "XObject/NewObject.h"
#include "XObject/XObject.h"

#include "Macros/XAssertionMacros.h"

#include <atomic>
#include <cstddef>
#include <cstring>

namespace XCore
{

    namespace
    {
        // -------------------------------------------------------------
        // g_PendingEagerCDOs -- the deferred queue of EagerCDO-flagged
        // FClasses awaiting PostStaticInit construction (Rev 3 per
        // FIX-H-R2-6).
        //
        // Populated by EnqueueEagerCDO during static-init (when
        // Z_Construct_FClass_* / FClass::Link for an EagerCDO class
        // detects the flag). Walked by DrainPendingEagerCDOs at the
        // PostStaticInit boundary.
        //
        // The TArray uses FMemTag::XObject for its backing allocation
        // (consistent with the rest of XCoreXObject's allocations per
        // the FMemTag::XObject route).
        //
        // Lifetime: the global TArray is value-initialised at
        // static-storage-duration time (empty); the first
        // EnqueueEagerCDO populates it. The TArray destructor at
        // process exit drops the storage. The pending count drops to
        // zero after Drain; subsequent module-load Enqueues populate
        // it again.
        // -------------------------------------------------------------
        ::XCore::TArray<const ::XCore::Reflect::FClass*> g_PendingEagerCDOs;

        // -------------------------------------------------------------
        // g_EagerCDOLock -- guards both g_PendingEagerCDOs mutations
        // AND the DrainPendingEagerCDOs walk.
        //
        // The lock is acquired briefly to enqueue/dequeue + held for
        // the duration of the drain walk (the drain processes the
        // queue + populates each class's CDO via GetClassDefaultObject;
        // the CDO construction happens INSIDE the lock here because
        // the drain is a static-init / engine-bring-up step, not a
        // high-throughput path; deferring the construction outside
        // the lock would require a snapshot-and-reset pattern which
        // adds complexity for no real benefit at the bring-up scale).
        //
        // Per the engine-wide lock-discipline contract: NewObject
        // calls under the lock are acceptable here because the lock
        // is single-use at static init / bring-up; there is no
        // re-entrancy concern (EnqueueEagerCDO is not called from
        // within NewObject).
        // -------------------------------------------------------------
        ::XCore::HAL::FCriticalSection g_EagerCDOLock;
    } // namespace

    // =================================================================
    // ComposeCDOName -- "Default__<ClassName>" FName composition.
    //
    // Per spec §8.2 reference impl: the CDO's FName is "Default__"
    // prepended to the FClass's Name. We build the composed UTF-8
    // bytes in a stack buffer + intern via FName(const char*,
    // ByteLen).
    //
    // The stack buffer is 256 bytes (sufficient for any realistic
    // class name; the FName cap is 1024 bytes per FName.h §4.2). If
    // a name exceeds the buffer, we fall back to FString concatenation
    // + FName(FString) interning (slower, but correct).
    //
    // Returns NAME_None if Class is nullptr (defensive).
    // =================================================================
    ::XCore::Reflect::FName ComposeCDOName(
        const ::XCore::Reflect::FClass* Class) noexcept
    {
        if (Class == nullptr)
        {
            return ::XCore::Reflect::FName();
        }

        // Read the class's base name bytes + length.
        // FStruct's accessor is `GetFName()` (mirroring UE UObject's
        // GetFName for typed FName retrieval); FClass inherits it.
        const ::XCore::Reflect::FName ClassName = Class->GetFName();
        const char* const ClassBaseBytes        = ClassName.GetBaseBytes();
        const ::int32 ClassBaseLength            = ClassName.GetBaseLength();

        // "Default__" prefix is 9 bytes.
        constexpr const char kPrefix[] = "Default__";
        constexpr ::int32 kPrefixLen   = 9;  // strlen("Default__")

        // Total composed length: prefix + class base.
        const ::int32 ComposedLen = kPrefixLen + ClassBaseLength;

        // Stack buffer for the common case (class names well under
        // 256 bytes). The FName cap is 1024 bytes per spec; 256 covers
        // every reasonable class name in practice.
        constexpr ::int32 kStackBufLen = 256;
        if (ComposedLen <= kStackBufLen)
        {
            char Buffer[kStackBufLen];
            ::std::memcpy(Buffer, kPrefix, kPrefixLen);
            if (ClassBaseLength > 0)
            {
                ::std::memcpy(Buffer + kPrefixLen, ClassBaseBytes, ClassBaseLength);
            }
            return ::XCore::Reflect::FName(Buffer, ComposedLen);
        }

        // Fallback: build via FString + intern.
        ::XCore::FString Composed;
        Composed.Append(kPrefix);
        if (ClassBaseLength > 0)
        {
            // Append the class base bytes. FString's Append(const
            // char*, ByteLen) takes a NUL-terminated string; since
            // ClassBaseBytes IS NUL-terminated (per FName.h
            // GetBaseBytes contract), the standard append works.
            Composed.Append(ClassBaseBytes);
        }
        return ::XCore::Reflect::FName(Composed);
    }

    // =================================================================
    // GetClassDefaultObject -- lazy CDO accessor (per spec §8.1 +
    // §8.2 reference impl).
    //
    // ALGORITHM (atomic CAS double-checked pattern):
    //   1. Acquire-load Class->ClassDefaultObject.
    //   2. If non-null, return it (fast path; one atomic load).
    //   3. Otherwise: NewObject the CDO via NewObjectImpl(Class,
    //      Outer=nullptr, Name="Default__<ClassName>", Flags=...,
    //      Archetype=nullptr).
    //   4. compare_exchange the slot from nullptr to our CDO pointer.
    //   5. If we win: return our CDO.
    //   6. If we lose (concurrent construction by another thread):
    //      release our just-constructed CDO + return the winner.
    //
    // CDO FLAGS: per spec §8.2:
    //   * ClassDefaultObject -- distinguishes the CDO from regular
    //                            instances.
    //   * ArchetypeObject    -- the CDO IS the canonical archetype
    //                            for instances of Class.
    //   * Public             -- the CDO is visible across package
    //                            boundaries (other classes may
    //                            reference it as an archetype).
    //   * MarkAsRootSet      -- pinned in the GC root set; the CDO is
    //                            never collected during normal
    //                            operation. Hot-reload class
    //                            replacement is the only legitimate
    //                            destruction path (spec §9.3).
    //
    // The returned pointer is `const XObject*` per Rev 3 FIX-M-R2-30:
    // the immutable-CDO discipline is enforced at the type system.
    // =================================================================
    const XObject* GetClassDefaultObject(
        const ::XCore::Reflect::FClass* Class) noexcept
    {
        if (Class == nullptr)
        {
            return nullptr;
        }

        // ----- Fast path: read the atomic slot. -----
        //
        // The FClass::ClassDefaultObject slot is
        // `std::atomic<const FObject*>` (Phase 5.d type-system
        // immutable-CDO enforcement). The forward-decl resolves to
        // an opaque pointer; cast through `const XObject*` since
        // XCoreXObject owns the XObject type definition.
        const ::XCore::Reflect::FObject* const Existing =
            Class->ClassDefaultObject.load(::std::memory_order_acquire);
        if (Existing != nullptr)
        {
            return reinterpret_cast<const XObject*>(Existing);
        }

        // ----- Slow path: construct the CDO. -----
        //
        // The CDO is constructed without an explicit Outer (nullptr
        // means "top-level"); production engine code would supply a
        // TransientPackage Outer once the package subsystem ships
        // (Layer 9 XSerialization). The Phase 5.d posture is "the
        // CDO's Outer is nullptr" which is correct for the in-memory
        // representation; the package binding is XSerialization's
        // concern.
        const ::XCore::Reflect::FName CDOName = ComposeCDOName(Class);

        const EObjectFlags CDOFlags =
            EObjectFlags::ClassDefaultObject |
            EObjectFlags::ArchetypeObject |
            EObjectFlags::Public |
            EObjectFlags::MarkAsRootSet;

        // Construct via NewObjectImpl. The Archetype IS nullptr for
        // CDO construction (the CDO has no archetype; it IS the
        // archetype).
        XObject* const NewCDO = ::XCore::NewObjectImpl(
            Class,
            /*Outer=*/      nullptr,
            CDOName,
            CDOFlags,
            /*Archetype=*/  nullptr);

        if (NewCDO == nullptr)
        {
            // Construction failed (allocation OOM or other pre-
            // condition). NewObjectImpl asserts in Dev/Debug; in
            // Shipping the failure surfaces here as a nullptr return.
            // The caller (the first GetClassDefaultObject) sees
            // nullptr; subsequent calls re-attempt construction.
            return nullptr;
        }

        // ----- CAS-publish the CDO. -----
        //
        // compare_exchange_strong: if the slot is still nullptr,
        // store our NewCDO and return success; if some other thread
        // raced ahead, leave their CDO in place + return failure.
        const ::XCore::Reflect::FObject* Expected = nullptr;
        const ::XCore::Reflect::FObject* const NewCDOAsFObject =
            reinterpret_cast<const ::XCore::Reflect::FObject*>(NewCDO);

        const bool bWon = Class->ClassDefaultObject.compare_exchange_strong(
            Expected,
            NewCDOAsFObject,
            ::std::memory_order_acq_rel,
            ::std::memory_order_acquire);

        if (bWon)
        {
            return NewCDO;
        }

        // ----- Lost the race -----
        //
        // Another thread constructed + published its CDO first. We
        // discard ours: mark it as garbage so the GC sweep at next
        // cycle reclaims it (the FXObjectArray slot is freed +
        // SerialNumber bumped at FreeEntry time).
        //
        // Phase 5.d doesn't have FXObjectCollector yet; the
        // discarded CDO leaks into the GC-pinned set until the
        // collector ships (Phase 5.h). The MarkAsGarbage call is the
        // explicit teardown signal; the FXObjectArray slot is
        // unclaimed but the storage is held until the sweep runs.
        //
        // For the race-collision path this is acceptable -- the
        // race is structurally rare (only multi-threaded first-call
        // would trigger it, and the per-class CDO is typically
        // first-called on the main thread at PostStaticInit before
        // worker threads spawn).
        NewCDO->MarkAsGarbage();

        // Return the winner's CDO.
        return reinterpret_cast<const XObject*>(Expected);
    }

    // =================================================================
    // IsEagerCDO -- predicate against EClassFlags::CLASS_EagerCDO.
    // =================================================================
    bool IsEagerCDO(const ::XCore::Reflect::FClass* Class) noexcept
    {
        if (Class == nullptr)
        {
            return false;
        }
        const ::XCore::Reflect::EClassFlags Flags = Class->GetClassFlags();
        return (Flags & ::XCore::Reflect::EClassFlags::CLASS_EagerCDO)
            != ::XCore::Reflect::EClassFlags::CLASS_None;
    }

    // =================================================================
    // EnqueueEagerCDO -- append to the deferred queue.
    //
    // Idempotent: skips if the FClass is already in the queue (linear
    // scan; the queue is typically small at Foundation Prototype
    // scale, so O(N) lookup is acceptable).
    //
    // No-op if Class is nullptr or if Class does NOT have the
    // EagerCDO flag.
    // =================================================================
    void EnqueueEagerCDO(const ::XCore::Reflect::FClass* Class) noexcept
    {
        if (Class == nullptr || !IsEagerCDO(Class))
        {
            return;
        }

        ::XCore::HAL::FScopedLock Lock(g_EagerCDOLock);

        // Linear scan for idempotency.
        for (const ::XCore::Reflect::FClass* const Existing : g_PendingEagerCDOs)
        {
            if (Existing == Class)
            {
                return;
            }
        }

        g_PendingEagerCDOs.Add(Class);
    }

    // =================================================================
    // DrainPendingEagerCDOs -- walk + materialise.
    //
    // Per Rev 3 FIX-H-R2-6: called at PostStaticInit boundary by the
    // engine-bring-up TU. Walks the queue in registration order;
    // each entry's CDO is materialised via GetClassDefaultObject (the
    // standard atomic-CAS lazy path).
    //
    // POST-CONDITION: g_PendingEagerCDOs is empty. Subsequent
    // EnqueueEagerCDO calls (from a hot-reloaded module) populate it
    // again; a subsequent Drain materialises them.
    //
    // Returns the count of CDOs materialised in this drain.
    // =================================================================
    ::int32 DrainPendingEagerCDOs() noexcept
    {
        ::XCore::HAL::FScopedLock Lock(g_EagerCDOLock);

        const ::int32 Count = g_PendingEagerCDOs.Num();
        for (::int32 i = 0; i < Count; ++i)
        {
            const ::XCore::Reflect::FClass* const Class = g_PendingEagerCDOs[i];
            if (Class != nullptr)
            {
                // GetClassDefaultObject is the standard lazy-or-eager
                // entry point. The drain path uses it directly so the
                // race-aware double-checked locking discipline is
                // shared with the lazy first-call path.
                (void)GetClassDefaultObject(Class);
            }
        }

        // Clear the queue.
        g_PendingEagerCDOs.Reset();

        return Count;
    }

    // =================================================================
    // GetPendingEagerCDOCount -- diagnostic accessor.
    // =================================================================
    ::int32 GetPendingEagerCDOCount() noexcept
    {
        ::XCore::HAL::FScopedLock Lock(g_EagerCDOLock);
        return g_PendingEagerCDOs.Num();
    }

    // =================================================================
    // __ResetCDOsForTests -- test-only state reset.
    //
    // Clears the pending queue. Does NOT touch FClass::
    // ClassDefaultObject slots (those are per-test-FClass; the
    // harness is responsible for re-instantiating its FClass
    // fixtures between tests).
    // =================================================================
    void __ResetCDOsForTests() noexcept
    {
        ::XCore::HAL::FScopedLock Lock(g_EagerCDOLock);
        g_PendingEagerCDOs.Reset();
    }

} // namespace XCore
