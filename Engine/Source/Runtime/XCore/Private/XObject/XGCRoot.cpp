// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRoot.cpp -- native-code GC root pinning (Phase 5.e).
// =====================================================================
//
// XCoreXObject Rev 4 Section 5 ("Write Barriers + GC Root Protocol")
// Section 5.1 + Section 5.2. Phase 5.e body for the non-template
// XGCRoot static methods.
//
// All bodies are thin dispatch onto FXObjectArray's per-entry CAS-loop
// primitives (FXObjectArray::SetRootPin / ClearRootPin / IsRootPinned /
// IsRootPinnedUnchecked, added at Phase 5.e). The dispatch indirection
// gives XGCRoot a stable C++ surface while letting FXObjectArray own
// the actual atomic-CAS primitives + the lock-discipline contract.
//
// =====================================================================

#include "XObject/XGCRoot.h"

#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"

#include <atomic>
#include <cstddef>
#include <cstdint>

namespace XCore
{

    // =================================================================
    // AddRoot (per spec §5.2 + Phase 5.e bit-pin divergence).
    //
    // Routes to FXObjectArray::SetRootPin. The bit-set is atomic CAS;
    // lock-free; idempotent.
    //
    // PRE-CONDITION CHECKS:
    //   * Object nullptr -> return false (no-op).
    //   * Object->InternalIndex out-of-range -> SetRootPin returns
    //     false (defence-in-depth; the caller is supposed to pass a
    //     live registered XObject).
    //
    // The Dev validity probe (XPACT_CHECK on IsValidLowLevel) is
    // DELIBERATELY NOT FIRED here: the spec contemplates pre-CDO
    // bootstrap rooting where the XObject may not have a fully-
    // populated ClassPrivate yet (the bootstrap path registers the
    // engine singletons before their FClass is finalised). The
    // InternalIndex range check in SetRootPin is the load-bearing
    // gate.
    // =================================================================
    bool XGCRoot::AddRoot(XObject* Object) noexcept
    {
        if (Object == nullptr)
        {
            return false;
        }
        return FXObjectArray::Get().SetRootPin(Object->InternalIndex);
    }

    // =================================================================
    // RemoveRoot (per spec §5.2 + Phase 5.e bit-pin divergence).
    //
    // Routes to FXObjectArray::ClearRootPin. Returns true iff the bit
    // transitioned from set to clear on this call.
    //
    // CAUTION (documented at the header): clearing while other
    // AddRoot-holders exist is UB-equivalent (no refcount). Multi-
    // holder use cases should use XStrongPtr<T> (Phase 5.c).
    // =================================================================
    bool XGCRoot::RemoveRoot(XObject* Object) noexcept
    {
        if (Object == nullptr)
        {
            return false;
        }
        return FXObjectArray::Get().ClearRootPin(Object->InternalIndex);
    }

    // =================================================================
    // IsRooted (per spec §5.2).
    //
    // SHARED-lock-acquired read of the kRootPinnedBit via FXObjectArray::
    // IsRootPinned. Returns false on nullptr / unregistered XObject /
    // out-of-range InternalIndex.
    // =================================================================
    bool XGCRoot::IsRooted(const XObject* Object) noexcept
    {
        if (Object == nullptr)
        {
            return false;
        }
        return FXObjectArray::Get().IsRootPinned(Object->InternalIndex);
    }

    // =================================================================
    // GetRootedCount (Phase 5.e linear-scan baseline).
    //
    // Walks FXObjectArray under SHARED lock counting pinned entries.
    // The count is an atomic snapshot; a concurrent AddRoot /
    // RemoveRoot may have already mutated the bit state by the time
    // the caller observes the return value.
    //
    // PERF: O(N) in committed capacity. At Foundation Prototype scale
    // (~50k objects) the walk is ~50us on Win64 desktop. For tighter
    // monitoring scenarios a cached counter bumped on AddRoot /
    // RemoveRoot is a future optimisation; the realistic root count
    // ceiling (~1-5k pinned per spec §4.8) makes this baseline
    // perfectly acceptable for Phase 5.e ship.
    // =================================================================
    ::std::size_t XGCRoot::GetRootedCount() noexcept
    {
        ::std::size_t Count = 0;
        FXObjectArray& Array = FXObjectArray::Get();
        Array.ForEachObject(
            [&Count, &Array](::int32 InternalIndex, XObject* /*Object*/) noexcept
            {
                // The ForEachObject visitor body runs under the SHARED
                // lock; IsRootPinnedUnchecked is the correct entry
                // point (no double-lock-acquisition).
                if (Array.IsRootPinnedUnchecked(InternalIndex))
                {
                    ++Count;
                }
            });
        return Count;
    }

} // namespace XCore
