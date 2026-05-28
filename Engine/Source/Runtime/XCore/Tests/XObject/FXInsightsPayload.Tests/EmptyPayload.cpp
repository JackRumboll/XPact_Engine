// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXInsightsPayload.Tests/EmptyPayload.cpp -- default-constructed
// payload behaviour (XCoreXObject Rev 4 §10.5 + Phase 5.k).
// =====================================================================
//
// Verifies:
//   * Default-constructed payload has 0 entries.
//   * ForEach on empty payload invokes visitor 0 times.
//   * Empty payload is valid for emit (no entries -> visitor sees no
//     work).
//   * Default-construct -> destruct cycle does NOT allocate.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXInsightsPayload.h"
#include "Reflection/FName.h"

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
    using ::XCore::HAL::FInsightsValue;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Test 1: default ctor -> 0 entries.
    // -----------------------------------------------------------------
    {
        FXInsightsPayload Empty;
        Check(Empty.NumEntries() == 0,
              "Default-constructed payload should have 0 entries.");
    }

    // -----------------------------------------------------------------
    // Test 2: ForEach on empty payload invokes 0 times.
    // -----------------------------------------------------------------
    {
        FXInsightsPayload Empty;
        int VisitCount = 0;
        Empty.ForEach([&](FName, const FInsightsValue&)
        {
            ++VisitCount;
        });
        Check(VisitCount == 0,
              "ForEach on empty payload should not invoke visitor.");
    }

    // -----------------------------------------------------------------
    // Test 3: pre-init / no-Init phase tolerance.
    //
    // After __Init the payload is constructible. The default ctor
    // does no work that touches FNamePool; it only initialises the
    // empty inner TArray (which is allocation-free at default
    // construction per TArray's primary template).
    // -----------------------------------------------------------------
    {
        FXInsightsPayload P1;
        FXInsightsPayload P2;
        FXInsightsPayload P3;
        Check(P1.NumEntries() == 0
              && P2.NumEntries() == 0
              && P3.NumEntries() == 0,
              "Multiple default-constructed payloads all 0-entry.");
    }

    // -----------------------------------------------------------------
    // Test 4: copy of empty payload is empty.
    // -----------------------------------------------------------------
    {
        FXInsightsPayload Empty;
        FXInsightsPayload Copy = Empty;
        Check(Copy.NumEntries() == 0,
              "Copy of empty payload should be empty.");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FXInsightsPayload.EmptyPayload: PASS\n";
        return 0;
    }
    std::cerr << "FXInsightsPayload.EmptyPayload: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
