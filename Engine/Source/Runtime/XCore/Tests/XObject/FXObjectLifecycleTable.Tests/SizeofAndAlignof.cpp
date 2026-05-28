// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectLifecycleTable.Tests/SizeofAndAlignof.cpp -- Phase 5.d ABI
// lock runtime check.
// =====================================================================
//
// XCoreXObject Rev 4 §2.4 + §11.3 + Contract Rev 13.9 layout tag
// XPACT_XOBJECT_LIFECYCLE_TABLE_TAG.
//
// Verifies sizeof / alignof / per-member offsets at RUNTIME so any
// toolchain divergence in offsetof or padding computation is caught.
// The header-side static_asserts are the primary ABI lock; this
// runtime gate is the belt-and-braces CI artifact.
//
// =====================================================================

#include "XObject/FXObjectLifecycleTable.h"

#include <cstddef>
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
    using ::XCore::EXObjectLifecycleSlot;

    // -----------------------------------------------------------------
    // FXObjectLifecycleTable -- 72 bytes; alignof 8.
    // -----------------------------------------------------------------
    Check(sizeof(FXObjectLifecycleTable)  == 72, "sizeof(FXObjectLifecycleTable) != 72");
    Check(alignof(FXObjectLifecycleTable) ==  8, "alignof(FXObjectLifecycleTable) != 8");

    // Per-member offsets.
    Check(offsetof(FXObjectLifecycleTable, Capabilities) == 0, "Capabilities offset != 0");
    Check(offsetof(FXObjectLifecycleTable, _padHeader)   == 4, "_padHeader offset != 4");
    Check(offsetof(FXObjectLifecycleTable, Slots)        == 8, "Slots offset != 8");

    // Field-size locks.
    Check(sizeof(FXObjectLifecycleTable::Capabilities) == 4, "sizeof(Capabilities) != 4");
    Check(sizeof(FXObjectLifecycleTable::_padHeader)   == 4, "sizeof(_padHeader) != 4");
    Check(sizeof(FXObjectLifecycleTable::Slots)        == 64, "sizeof(Slots[]) != 64");

    // Slot count enum lock.
    Check(static_cast<std::uint32_t>(EXObjectLifecycleSlot::Count) == 8,
          "EXObjectLifecycleSlot::Count != 8");

    // Slot index assignments.
    Check(static_cast<std::uint32_t>(EXObjectLifecycleSlot::PostInitProperties)      == 0, "slot 0 != PostInitProperties");
    Check(static_cast<std::uint32_t>(EXObjectLifecycleSlot::BeginDestroy)            == 1, "slot 1 != BeginDestroy");
    Check(static_cast<std::uint32_t>(EXObjectLifecycleSlot::IsReadyForFinishDestroy) == 2, "slot 2 != IsReadyForFinishDestroy");
    Check(static_cast<std::uint32_t>(EXObjectLifecycleSlot::FinishDestroy)           == 3, "slot 3 != FinishDestroy");
    Check(static_cast<std::uint32_t>(EXObjectLifecycleSlot::AddReferencedObjects)    == 4, "slot 4 != AddReferencedObjects");
    Check(static_cast<std::uint32_t>(EXObjectLifecycleSlot::Serialize)               == 5, "slot 5 != Serialize");
    Check(static_cast<std::uint32_t>(EXObjectLifecycleSlot::PostLoad)                == 6, "slot 6 != PostLoad");
    Check(static_cast<std::uint32_t>(EXObjectLifecycleSlot::ConvertFromType)         == 7, "slot 7 != ConvertFromType");

    // Trait locks.
    static_assert(std::is_trivially_copyable_v<FXObjectLifecycleTable>);
    static_assert(std::is_trivially_destructible_v<FXObjectLifecycleTable>);
    static_assert(std::is_standard_layout_v<FXObjectLifecycleTable>);
    static_assert(!std::is_polymorphic_v<FXObjectLifecycleTable>);

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectLifecycleTable.SizeofAndAlignof: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectLifecycleTable.SizeofAndAlignof: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
