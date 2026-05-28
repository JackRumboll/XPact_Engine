// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectLifecycleTable.Tests/SlotsAreNull.cpp -- default-init posture.
// =====================================================================
//
// XCoreXObject Rev 4 §2.4 + Phase 5.d default-init check.
//
// Aggregate-initialised FXObjectLifecycleTable{} has all 8 slot
// pointers nullptr + Capabilities == 0. This is the "no lifecycle
// hooks" posture that classes without explicit hook implementations
// emit; the dispatcher short-circuits on the empty bitmask.
//
// =====================================================================

#include "XObject/FXObjectLifecycleTable.h"

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
    using ::XCore::FXObjectLifecycleTable;

    // Aggregate-init: zero-initialise everything.
    FXObjectLifecycleTable Table{};

    Check(Table.Capabilities == 0u,        "default-init: Capabilities != 0");
    Check(Table._padHeader   == 0u,        "default-init: _padHeader != 0");

    for (std::size_t i = 0; i < 8; ++i)
    {
        if (Table.Slots[i] != nullptr)
        {
            std::cerr << "FAIL: default-init: Slots[" << i << "] != nullptr\n";
            ++g_FailureCount;
        }
    }

    // The HasCapability / HasSlot probes return false for every bit.
    using ::XCore::EXObjectLifecycleCapability;
    using ::XCore::EXObjectLifecycleSlot;

    Check(!Table.HasCapability(EXObjectLifecycleCapability::HasPostInitProperties),
          "default-init: HasPostInitProperties true");
    Check(!Table.HasCapability(EXObjectLifecycleCapability::HasBeginDestroy),
          "default-init: HasBeginDestroy true");
    Check(!Table.HasCapability(EXObjectLifecycleCapability::HasConvertFromType),
          "default-init: HasConvertFromType true");

    Check(!Table.HasSlot(EXObjectLifecycleSlot::PostInitProperties),
          "default-init: HasSlot(PostInitProperties) true");
    Check(!Table.HasSlot(EXObjectLifecycleSlot::ConvertFromType),
          "default-init: HasSlot(ConvertFromType) true");

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectLifecycleTable.SlotsAreNull: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectLifecycleTable.SlotsAreNull: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
