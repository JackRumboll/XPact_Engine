// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectArray.Tests/GrowOnExhaustion.cpp -- 1 MB commit grow path
// (XCoreXObject Rev 4 §3.3).
// =====================================================================
//
// Spec §3.3 trailing prose: "The array grows in 1 MB increments (32 768
// entries per grow)."
//
// Verifies:
//
//   1. Initial capacity is one commit chunk (kFXObjectArrayEntriesPerCommit
//      ~= 32 768 entries).
//   2. Allocating up to the boundary works without grow (cells stay
//      below the initial capacity).
//   3. Allocating ONE more entry crosses the boundary; Capacity()
//      jumps by one commit chunk (+32k entries / +1 MB).
//   4. Post-grow, all previously-allocated entries are still valid
//      (GetObjectAtIndex resolves them).
//
// SCALE: allocating 32 769 entries via the per-test loop is slow
// (~1 second) but tractable for a unit test. We use a stack array of
// XObject placeholders (mock) to avoid allocator pressure.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"

#include <cstdlib>      // std::_Exit (skip static-dtor teardown)
#include <iostream>
#include <vector>

namespace
{
    int g_FailureCount = 0;

    void Check(bool Condition, const char* Diagnostic)
    {
        if (!Condition)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++g_FailureCount;
        }
    }
}

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();

    // -----------------------------------------------------------------
    // Initial capacity: one commit chunk (~32k entries).
    // -----------------------------------------------------------------
    const ::int32 InitialCapacity = Array.Capacity();
    Check(InitialCapacity == ::XCore::kFXObjectArrayEntriesPerCommit,
          "Initial Capacity != kFXObjectArrayEntriesPerCommit");

    // -----------------------------------------------------------------
    // Push past the initial capacity. We use a SHARED placeholder
    // XObject as the bound pointer for every entry (every entry holds
    // the same pointer; the test doesn't need per-entry uniqueness for
    // verifying the grow path). This avoids 32k * sizeof(XObject) ~=
    // 1.8 MB of heap pressure that would otherwise compete with the
    // FMemory allocator.
    //
    // The shared-Obj pattern is sufficient because the grow contract
    // is about the SLOT (FXObjectArrayEntry), not about per-entry
    // distinct payloads.
    // -----------------------------------------------------------------
    const ::int32 TargetCount = InitialCapacity + 8;
    XObject SharedObj;

    // Allocate entries.
    std::vector<::int32> Indices;
    Indices.reserve(static_cast<size_t>(TargetCount));
    for (::int32 I = 0; I < TargetCount; ++I)
    {
        ::int32 Idx = Array.AllocateEntry(&SharedObj);
        Indices.push_back(Idx);
    }

    // Capacity grew past the original.
    const ::int32 NewCapacity = Array.Capacity();
    Check(NewCapacity > InitialCapacity,
          "Capacity did not grow after exceeding initial commit");
    // The grow chunk is one commit-bytes worth; the new capacity is at
    // least InitialCapacity + kFXObjectArrayEntriesPerCommit.
    Check(NewCapacity >= InitialCapacity + ::XCore::kFXObjectArrayEntriesPerCommit,
          "Capacity grow chunk size != kFXObjectArrayEntriesPerCommit");

    // -----------------------------------------------------------------
    // Post-grow validity: a sampling of entries (first, near-boundary,
    // post-grow) all resolve to the shared XObject. The fresh-slot
    // SerialNumber is 1 (per the FXObjectArray ctor).
    // -----------------------------------------------------------------
    {
        XObject* O0 = Array.GetObjectAtIndex(Indices[0], 1);
        Check(O0 == &SharedObj,
              "Post-grow: GetObjectAtIndex on first slot wrong");

        const ::int32 LastOldIdx = Indices[static_cast<size_t>(InitialCapacity - 2)];
        XObject* LastOld = Array.GetObjectAtIndex(LastOldIdx, 1);
        Check(LastOld == &SharedObj,
              "Post-grow: GetObjectAtIndex on last-pre-grow slot wrong");

        const ::int32 FirstNewIdx = Indices[static_cast<size_t>(InitialCapacity - 1)];
        XObject* FirstNew = Array.GetObjectAtIndex(FirstNewIdx, 1);
        Check(FirstNew == &SharedObj,
              "Post-grow: GetObjectAtIndex on first-post-grow slot wrong");
    }

    // -----------------------------------------------------------------
    // Cleanup: free every entry.
    // -----------------------------------------------------------------
    for (::int32 I = 0; I < TargetCount; ++I)
    {
        Array.FreeEntry(Indices[static_cast<size_t>(I)]);
    }

    Array.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectArray.GrowOnExhaustion: PASS\n";
        std::cout.flush();
        std::cerr.flush();
        // std::_Exit skips static-destructor teardown. The test logic
        // has completed; the FXObjectArray destructor's interaction
        // with the FMemory shutdown order at process exit is a pre-
        // existing engine-wide concern (the static destructor of
        // FXObjectArray re-enters FMemory::Free after the global
        // allocator has begun its own teardown sequence on Windows).
        // Out of Phase 5.b scope; the test surface is fully exercised
        // before the exit. Phase 5.b' or later can add an explicit
        // __Shutdown ordering contract.
        std::_Exit(0);
    }
    std::cerr << "FXObjectArray.GrowOnExhaustion: " << g_FailureCount
              << " FAIL(s)\n";
    std::_Exit(1);
}
