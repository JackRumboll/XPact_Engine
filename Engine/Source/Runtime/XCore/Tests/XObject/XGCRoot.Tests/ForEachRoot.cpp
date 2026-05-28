// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRoot.Tests/ForEachRoot.cpp -- visitor sees every pinned object
// (XCoreXObject Rev 4 §5.2; Phase 5.e).
// =====================================================================
//
// ForEachRoot iterates FXObjectArray under SHARED lock; the visitor is
// invoked once per pinned XObject. The visitor receives
// (int32 InternalIndex, XObject* Object).
//
// This test pins a known set of objects + verifies the visitor sees
// EXACTLY that set (every pinned object visited exactly once; no
// unpinned objects visited).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCRoot.h"
#include "XObject/XObject.h"

#include <iostream>
#include <cstring>          // memset

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XGCRoot;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    constexpr int kN = 32;
    XObject Objs[kN];
    ::int32 Indices[kN];
    for (int I = 0; I < kN; ++I)
    {
        ::uint32 Serial = 0;
        Indices[I] = Array.ReserveSlot(&Serial);
        Objs[I].InternalIndex = Indices[I];
        Objs[I].SerialNumber  = Serial;
        Array.BindObject(Indices[I], &Objs[I]);
    }

    // Pin only the even-indexed objects.
    for (int I = 0; I < kN; I += 2)
    {
        (void)XGCRoot::AddRoot(&Objs[I]);
    }

    // Sweep via ForEachRoot. Mark each visited object's index in a
    // bitmap; verify only even indices were visited exactly once.
    bool Visited[kN];
    std::memset(Visited, 0, sizeof(Visited));
    int VisitCount = 0;

    XGCRoot::ForEachRoot(
        [&](::int32 /*InternalIndex*/, XObject* Object) noexcept
        {
            // Find which Objs[I] this is.
            for (int I = 0; I < kN; ++I)
            {
                if (Object == &Objs[I])
                {
                    if (Visited[I])
                    {
                        ++FailureCount;
                        std::cerr << "FAIL: Object " << I
                                  << " visited multiple times\n";
                    }
                    Visited[I] = true;
                    ++VisitCount;
                    return;
                }
            }
            // Unknown object: not in our set.
            ++FailureCount;
            std::cerr << "FAIL: ForEachRoot visited unknown XObject\n";
        });

    Check(VisitCount == kN / 2,
          "ForEachRoot visit count != kN/2");

    for (int I = 0; I < kN; ++I)
    {
        if ((I % 2) == 0)
        {
            Check(Visited[I], "Even-indexed pinned object NOT visited");
        }
        else
        {
            Check(!Visited[I], "Odd-indexed UNpinned object WAS visited");
        }
    }

    // Cleanup.
    for (int I = 0; I < kN; ++I)
    {
        if ((I % 2) == 0) (void)XGCRoot::RemoveRoot(&Objs[I]);
        Array.FreeEntry(Indices[I]);
    }
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCRoot.ForEachRoot: PASS\n";
        return 0;
    }
    std::cerr << "XGCRoot.ForEachRoot: " << FailureCount << " FAIL(s)\n";
    return 1;
}
