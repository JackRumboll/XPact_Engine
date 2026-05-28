// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XInsightsBridge.Tests/EmitDefaultIsNoOp.cpp -- default weak-symbol
// stub is a no-op (XCoreXObject Rev 4 §10.5 + Phase 5.k).
// =====================================================================
//
// Verifies:
//   * Calling XInsightsBridge::Emit with the default stub does NOT
//     crash, does NOT abort, does NOT throw.
//   * The payload is not touched (no mutation) by the no-op stub.
//   * Multiple Emit calls succeed in sequence.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FName.h"
#include "XObject/FXInsightsPayload.h"
#include "XObject/XInsightsBridge.h"

#include <cstdint>
#include <iostream>

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
    using ::XCore::HAL::FXInsightsPayload;
    using ::XCore::Reflect::FName;
    namespace Bridge = ::XCore::HAL::XInsightsBridge;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Test 1: empty payload, default stub -> no crash.
    // -----------------------------------------------------------------
    {
        const FName Cat("TestCategory");
        const FName Evt("TestEvent");
        Bridge::Emit(Cat, Evt);
        Check(true, "Emit with empty payload did not crash.");
    }

    // -----------------------------------------------------------------
    // Test 2: populated payload, default stub -> no mutation.
    // -----------------------------------------------------------------
    {
        FXInsightsPayload Payload;
        Payload.Add(FName("k1"), static_cast<::std::int64_t>(1));
        Payload.Add(FName("k2"), 2.5);

        const ::SIZE_T NumBefore = Payload.NumEntries();
        const FName    Cat("Cat");
        const FName    Evt("Evt");

        Bridge::Emit(Cat, Evt, Payload);

        Check(Payload.NumEntries() == NumBefore,
              "Default stub should not modify payload.");
    }

    // -----------------------------------------------------------------
    // Test 3: many sequential Emits -> no crash.
    // -----------------------------------------------------------------
    {
        const FName Cat("CatBatch");
        const FName Evt("EvtBatch");

        for (int i = 0; i < 1000; ++i)
        {
            FXInsightsPayload Payload;
            Payload.Add(FName("iter"), static_cast<::std::int64_t>(i));
            Bridge::Emit(Cat, Evt, Payload);
        }
        Check(true, "1000 sequential Emits did not crash.");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "XInsightsBridge.EmitDefaultIsNoOp: PASS\n";
        return 0;
    }
    std::cerr << "XInsightsBridge.EmitDefaultIsNoOp: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
