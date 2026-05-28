// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectLifecycleTable.Tests/Capabilities.cpp -- bitmask probe
// (XCoreXObject Rev 4 §2.4).
// =====================================================================
//
// Verifies EXObjectLifecycleCapability bit assignments + the
// HasCapability/HasSlot predicates against a constructed table.
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
    using ::XCore::EXObjectLifecycleCapability;
    using ::XCore::EXObjectLifecycleSlot;
    using ::XCore::ToUnderlying;

    // -----------------------------------------------------------------
    // Bit assignments (per spec §2.4): bit N <=> slot N.
    // -----------------------------------------------------------------
    Check(ToUnderlying(EXObjectLifecycleCapability::HasPostInitProperties)      == (1u << 0), "HasPostInitProperties != bit 0");
    Check(ToUnderlying(EXObjectLifecycleCapability::HasBeginDestroy)            == (1u << 1), "HasBeginDestroy != bit 1");
    Check(ToUnderlying(EXObjectLifecycleCapability::HasIsReadyForFinishDestroy) == (1u << 2), "HasIsReadyForFinishDestroy != bit 2");
    Check(ToUnderlying(EXObjectLifecycleCapability::HasFinishDestroy)           == (1u << 3), "HasFinishDestroy != bit 3");
    Check(ToUnderlying(EXObjectLifecycleCapability::HasAddReferencedObjects)    == (1u << 4), "HasAddReferencedObjects != bit 4");
    Check(ToUnderlying(EXObjectLifecycleCapability::HasSerialize)               == (1u << 5), "HasSerialize != bit 5");
    Check(ToUnderlying(EXObjectLifecycleCapability::HasPostLoad)                == (1u << 6), "HasPostLoad != bit 6");
    Check(ToUnderlying(EXObjectLifecycleCapability::HasConvertFromType)         == (1u << 7), "HasConvertFromType != bit 7");

    // -----------------------------------------------------------------
    // HasCapability probe -- single bit set.
    // -----------------------------------------------------------------
    {
        FXObjectLifecycleTable Table{};
        Table.Capabilities = ToUnderlying(EXObjectLifecycleCapability::HasPostInitProperties);

        Check(Table.HasCapability(EXObjectLifecycleCapability::HasPostInitProperties),
              "HasCapability(HasPostInitProperties) false for set bit");
        Check(!Table.HasCapability(EXObjectLifecycleCapability::HasBeginDestroy),
              "HasCapability(HasBeginDestroy) true for unset bit");

        Check(Table.HasSlot(EXObjectLifecycleSlot::PostInitProperties),
              "HasSlot(PostInitProperties) false for set bit");
        Check(!Table.HasSlot(EXObjectLifecycleSlot::BeginDestroy),
              "HasSlot(BeginDestroy) true for unset bit");
    }

    // -----------------------------------------------------------------
    // HasCapability probe -- multiple bits set (bitwise composition).
    // -----------------------------------------------------------------
    {
        FXObjectLifecycleTable Table{};
        Table.Capabilities = ToUnderlying(
            EXObjectLifecycleCapability::HasPostInitProperties |
            EXObjectLifecycleCapability::HasBeginDestroy |
            EXObjectLifecycleCapability::HasFinishDestroy);

        Check(Table.HasCapability(EXObjectLifecycleCapability::HasPostInitProperties),
              "composed: HasPostInitProperties false");
        Check(Table.HasCapability(EXObjectLifecycleCapability::HasBeginDestroy),
              "composed: HasBeginDestroy false");
        Check(Table.HasCapability(EXObjectLifecycleCapability::HasFinishDestroy),
              "composed: HasFinishDestroy false");

        Check(!Table.HasCapability(EXObjectLifecycleCapability::HasSerialize),
              "composed: HasSerialize true");
        Check(!Table.HasCapability(EXObjectLifecycleCapability::HasPostLoad),
              "composed: HasPostLoad true");
    }

    // -----------------------------------------------------------------
    // Empty table -- all bits clear.
    // -----------------------------------------------------------------
    {
        FXObjectLifecycleTable Table{};
        Table.Capabilities = 0u;

        Check(!Table.HasCapability(EXObjectLifecycleCapability::HasPostInitProperties),
              "empty: HasPostInitProperties true");
        Check(!Table.HasCapability(EXObjectLifecycleCapability::HasConvertFromType),
              "empty: HasConvertFromType true");
        Check(!Table.HasSlot(EXObjectLifecycleSlot::PostInitProperties),
              "empty: HasSlot(PostInitProperties) true");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectLifecycleTable.Capabilities: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectLifecycleTable.Capabilities: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
