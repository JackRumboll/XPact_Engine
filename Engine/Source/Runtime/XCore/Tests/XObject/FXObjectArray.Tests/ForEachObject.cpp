// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectArray.Tests/ForEachObject.cpp -- live-entry iteration
// (XCoreXObject Rev 4 §3.3).
// =====================================================================
//
// Spec §3.3: "ForEachObject visits each live entry (Object != nullptr)
// exactly once in index order."
//
// Verifies:
//
//   1. ForEachObject visits exactly the live entries (skips index 0
//      sentinel + skips freed slots).
//   2. The visitor is called in index-ascending order.
//   3. After Free, the freed entries are skipped on subsequent
//      iterations.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"

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

    // Allocate 5 XObject entries.
    XObject Objs[5];
    ::int32 Indices[5];
    for (int I = 0; I < 5; ++I)
    {
        Indices[I] = Array.AllocateEntry(&Objs[I]);
    }

    // -----------------------------------------------------------------
    // Test 1: ForEachObject visits all 5 entries.
    // -----------------------------------------------------------------
    {
        std::vector<::int32> VisitedIndices;
        std::vector<XObject*> VisitedObjects;
        Array.ForEachObject([&](::int32 Idx, XObject* Obj)
        {
            VisitedIndices.push_back(Idx);
            VisitedObjects.push_back(Obj);
        });
        Check(VisitedIndices.size() == 5,
              "ForEachObject: expected 5 visits, got different count");

        // The visited indices are in ascending order (the
        // implementation walks 1..Capacity).
        for (size_t i = 1; i < VisitedIndices.size(); ++i)
        {
            Check(VisitedIndices[i] > VisitedIndices[i - 1],
                  "ForEachObject: visit not in ascending index order");
        }

        // Each of the 5 allocated objects appears.
        for (int I = 0; I < 5; ++I)
        {
            bool Found = false;
            for (XObject* V : VisitedObjects)
            {
                if (V == &Objs[I]) { Found = true; break; }
            }
            Check(Found, "ForEachObject: missing one of the live objects");
        }
    }

    // -----------------------------------------------------------------
    // Test 2: After freeing 2 entries (Objs[1] and Objs[3]),
    // ForEachObject visits only the remaining 3.
    // -----------------------------------------------------------------
    Array.FreeEntry(Indices[1]);
    Array.FreeEntry(Indices[3]);

    {
        std::vector<XObject*> Visited;
        Array.ForEachObject([&](::int32 /*Idx*/, XObject* Obj)
        {
            Visited.push_back(Obj);
        });
        Check(Visited.size() == 3,
              "ForEachObject after 2 frees: expected 3 visits");
        // Verify Obj[1] and Obj[3] are NOT in the visited list.
        for (XObject* V : Visited)
        {
            Check(V != &Objs[1], "ForEachObject: freed Obj[1] still visited");
            Check(V != &Objs[3], "ForEachObject: freed Obj[3] still visited");
        }
    }

    // -----------------------------------------------------------------
    // Test 3: After freeing everything, ForEachObject visits zero
    // entries.
    // -----------------------------------------------------------------
    Array.FreeEntry(Indices[0]);
    Array.FreeEntry(Indices[2]);
    Array.FreeEntry(Indices[4]);

    {
        int VisitCount = 0;
        Array.ForEachObject([&](::int32 /*Idx*/, XObject* /*Obj*/)
        {
            ++VisitCount;
        });
        Check(VisitCount == 0,
              "ForEachObject on fully-freed array: expected 0 visits");
    }

    Array.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectArray.ForEachObject: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectArray.ForEachObject: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
