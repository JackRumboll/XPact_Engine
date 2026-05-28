// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectGlobalSatbLog.Tests/DrainAll.cpp -- visitor drain
// (XCoreXObject Rev 4 §4.3).
// =====================================================================
//
// Verifies:
//   * DrainAll invokes the visitor exactly N times for N appended
//     entries.
//   * Log is empty after DrainAll.
//   * Empty log + DrainAll is a no-op.
//   * Append after Drain works.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XObject.h"

#include <cstdint>
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
    using ::XCore::FXObjectGlobalSatbLog;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectGlobalSatbLog& Log = FXObjectGlobalSatbLog::Get();
    Log.__ResetForTests();

    XObject Sentinels[6];

    // -----------------------------------------------------------------
    // Test 1: Empty log DrainAll is no-op.
    // -----------------------------------------------------------------
    {
        int Visited = 0;
        const ::std::size_t Drained = Log.DrainAll(
            [&Visited](XObject* /*OldValue*/) { ++Visited; });
        Check(Drained == 0, "Empty log DrainAll returned non-zero");
        Check(Visited == 0, "Empty log DrainAll invoked visitor");
    }

    // -----------------------------------------------------------------
    // Test 2: Append + Drain visits each entry exactly once.
    // -----------------------------------------------------------------
    {
        XObject* Batch[6] = {
            &Sentinels[0], &Sentinels[1], &Sentinels[2],
            &Sentinels[3], &Sentinels[4], &Sentinels[5]
        };
        Log.AppendBatch(Batch, 6);

        std::vector<XObject*> Visited;
        const ::std::size_t Drained = Log.DrainAll(
            [&Visited](XObject* OldValue) {
                Visited.push_back(OldValue);
            });

        Check(Drained == 6, "DrainAll(6 entries) returned != 6");
        Check(Visited.size() == 6, "DrainAll visited != 6 times");
        Check(Log.Size() == 0, "Log not empty after DrainAll");
    }

    // -----------------------------------------------------------------
    // Test 3: Append after Drain works.
    // -----------------------------------------------------------------
    {
        XObject* Batch[2] = { &Sentinels[0], &Sentinels[1] };
        Log.AppendBatch(Batch, 2);
        Check(Log.Size() == 2, "Append after Drain: Size != 2");

        ::std::size_t Drained = Log.DrainAll([](XObject*) {});
        Check(Drained == 2, "Second DrainAll returned != 2");
    }

    // -----------------------------------------------------------------
    // Test 4: Drain after grow (capacity > initial 256).
    // -----------------------------------------------------------------
    {
        std::vector<XObject*> LargeBatch(500, &Sentinels[0]);
        Log.AppendBatch(LargeBatch.data(), 500);

        ::std::size_t Drained = Log.DrainAll([](XObject*) {});
        Check(Drained == 500, "DrainAll after grow: count != 500");
        Check(Log.Size() == 0, "Log not empty after large drain");
    }

    Log.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectGlobalSatbLog.DrainAll: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectGlobalSatbLog.DrainAll: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
