// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XStackRootScaffolding.Tests/ForwardDeclShape.cpp -- ABI layout pin
// (XCoreXObject Rev 4 §5.4; Phase 5.e).
// =====================================================================
//
// Pins the FStackRootMap (24 bytes) + FStackRootEntry (16 bytes)
// layouts. XIL2CPP at System 6 will emit binary-compatible records
// against these struct shapes; the test runs the offsetof-asserts at
// the test-link site as a runtime check (the header's static_asserts
// already cover compile-time).
//
// =====================================================================

#include "XObject/XStackRootScaffolding.h"

#include <cstddef>
#include <cstdint>
#include <iostream>
#include <type_traits>

int main()
{
    using ::XCore::Detail::FStackRootEntry;
    using ::XCore::Detail::FStackRootMap;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // ---- FStackRootEntry layout ----
    Check(sizeof(FStackRootEntry) == 16u,
          "sizeof FStackRootEntry != 16");
    Check(alignof(FStackRootEntry) == 4u,
          "alignof FStackRootEntry != 4");
    Check(offsetof(FStackRootEntry, ProgramCounterOffset) == 0u,
          "FStackRootEntry::ProgramCounterOffset != 0");
    Check(offsetof(FStackRootEntry, StackOffset) == 4u,
          "FStackRootEntry::StackOffset != 4");
    Check(offsetof(FStackRootEntry, SlotCount) == 8u,
          "FStackRootEntry::SlotCount != 8");
    Check(offsetof(FStackRootEntry, _pad) == 10u,
          "FStackRootEntry::_pad != 10");
    Check(::std::is_trivially_copyable_v<FStackRootEntry>,
          "FStackRootEntry not trivially copyable");
    Check(::std::is_trivially_destructible_v<FStackRootEntry>,
          "FStackRootEntry not trivially destructible");

    // ---- FStackRootMap layout ----
    Check(sizeof(FStackRootMap) == 24u,
          "sizeof FStackRootMap != 24");
    Check(alignof(FStackRootMap) == 8u,
          "alignof FStackRootMap != 8");
    Check(offsetof(FStackRootMap, FunctionAddress) == 0u,
          "FStackRootMap::FunctionAddress != 0");
    Check(offsetof(FStackRootMap, SafepointCount) == 8u,
          "FStackRootMap::SafepointCount != 8");
    Check(offsetof(FStackRootMap, _pad) == 12u,
          "FStackRootMap::_pad != 12");
    Check(offsetof(FStackRootMap, Safepoints) == 16u,
          "FStackRootMap::Safepoints != 16");
    Check(::std::is_trivially_copyable_v<FStackRootMap>,
          "FStackRootMap not trivially copyable");
    Check(::std::is_trivially_destructible_v<FStackRootMap>,
          "FStackRootMap not trivially destructible");

    if (FailureCount == 0)
    {
        std::cout << "XStackRootScaffolding.ForwardDeclShape: PASS\n";
        return 0;
    }
    std::cerr << "XStackRootScaffolding.ForwardDeclShape: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
