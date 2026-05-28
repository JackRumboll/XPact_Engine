// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectGlobalSatbLog.Tests/AppendBatch.cpp -- global log append
// (XCoreXObject Rev 4 §4.3 + §5.6).
// =====================================================================
//
// Verifies:
//   * Empty log: Size() == 0.
//   * AppendBatch(N entries) -> Size() == N.
//   * Multiple AppendBatch calls accumulate.
//   * Append-zero is a no-op.
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

    // -----------------------------------------------------------------
    // Test 1: Empty log.
    // -----------------------------------------------------------------
    Check(Log.Size() == 0, "Fresh log: Size != 0");

    // -----------------------------------------------------------------
    // Test 2: AppendBatch of 5 entries.
    // -----------------------------------------------------------------
    XObject Sentinels[5];
    XObject* Batch[5] = {
        &Sentinels[0], &Sentinels[1], &Sentinels[2],
        &Sentinels[3], &Sentinels[4]
    };
    {
        Log.AppendBatch(Batch, 5);
        Check(Log.Size() == 5, "After AppendBatch(5): Size != 5");
    }

    // -----------------------------------------------------------------
    // Test 3: Second AppendBatch accumulates.
    // -----------------------------------------------------------------
    {
        Log.AppendBatch(Batch, 3);
        Check(Log.Size() == 8, "After second AppendBatch(3): Size != 8");
    }

    // -----------------------------------------------------------------
    // Test 4: AppendBatch(0) is no-op.
    // -----------------------------------------------------------------
    {
        Log.AppendBatch(Batch, 0);
        Check(Log.Size() == 8, "Append-zero changed Size");
    }

    // -----------------------------------------------------------------
    // Test 5: Large batch triggers grow.
    //
    // The initial capacity is 256 entries. Append 300 entries to force
    // a grow path.
    // -----------------------------------------------------------------
    {
        Log.__ResetForTests();
        std::vector<XObject*> LargeBatch(300, &Sentinels[0]);
        Log.AppendBatch(LargeBatch.data(), 300);
        Check(Log.Size() == 300, "After AppendBatch(300): Size != 300");
    }

    Log.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectGlobalSatbLog.AppendBatch: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectGlobalSatbLog.AppendBatch: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
