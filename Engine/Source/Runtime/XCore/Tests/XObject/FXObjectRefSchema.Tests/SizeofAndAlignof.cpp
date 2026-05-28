// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectRefSchema.Tests/SizeofAndAlignof.cpp -- ABI lock for the
// schema header + opcode descriptor (XCoreXObject Rev 4 §7.4 + §11.3
// + Contract Rev 13.9 XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG; Phase 5.g').
// =====================================================================
//
// Pins:
//   * sizeof(FXObjectRefSchema)   == 24 bytes
//   * alignof(FXObjectRefSchema)  ==  8 bytes
//   * offsetof every field of FXObjectRefSchema
//   * sizeof(FXObjectRefSchemaOp) == 24 bytes
//   * alignof(FXObjectRefSchemaOp)==  8 bytes
//   * offsetof every field of FXObjectRefSchemaOp
//   * EXObjectRefSchemaOp underlying type is uint8_t (sizeof == 1)
//
// =====================================================================

#include "Reflection/FXObjectRefSchema.h"

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
    using namespace ::XCore::Reflect;

    // -----------------------------------------------------------------
    // FXObjectRefSchema (header).
    // -----------------------------------------------------------------
    Check(sizeof(FXObjectRefSchema)  == 24, "sizeof(FXObjectRefSchema) != 24");
    Check(alignof(FXObjectRefSchema) ==  8, "alignof(FXObjectRefSchema) != 8");
    Check(offsetof(FXObjectRefSchema, NumOps)   ==  0, "NumOps not at offset 0");
    Check(offsetof(FXObjectRefSchema, Version)  ==  4, "Version not at offset 4");
    Check(offsetof(FXObjectRefSchema, Ops)      ==  8, "Ops not at offset 8");
    Check(offsetof(FXObjectRefSchema, _padTail) == 16, "_padTail not at offset 16");

    // -----------------------------------------------------------------
    // FXObjectRefSchemaOp (per-opcode descriptor).
    // -----------------------------------------------------------------
    Check(sizeof(FXObjectRefSchemaOp)  == 24, "sizeof(FXObjectRefSchemaOp) != 24");
    Check(alignof(FXObjectRefSchemaOp) ==  8, "alignof(FXObjectRefSchemaOp) != 8");
    Check(offsetof(FXObjectRefSchemaOp, Op)           ==  0, "Op not at offset 0");
    Check(offsetof(FXObjectRefSchemaOp, _padOp)       ==  1, "_padOp not at offset 1");
    Check(offsetof(FXObjectRefSchemaOp, ArrayDim)     ==  2, "ArrayDim not at offset 2");
    Check(offsetof(FXObjectRefSchemaOp, Offset)       ==  4, "Offset not at offset 4");
    Check(offsetof(FXObjectRefSchemaOp, StrideBytes)  ==  8, "StrideBytes not at offset 8");
    Check(offsetof(FXObjectRefSchemaOp, _padAlign)    == 12, "_padAlign not at offset 12");
    Check(offsetof(FXObjectRefSchemaOp, NestedSchema) == 16, "NestedSchema not at offset 16");

    // -----------------------------------------------------------------
    // EXObjectRefSchemaOp underlying type lock.
    // -----------------------------------------------------------------
    Check(sizeof(EXObjectRefSchemaOp) == 1, "Op enum underlying type != uint8_t");

    // -----------------------------------------------------------------
    // Trait locks. Trivially-copyable + standard-layout (.rodata
    // constinit invariants).
    // -----------------------------------------------------------------
    Check(std::is_trivially_copyable_v<FXObjectRefSchema>,
          "FXObjectRefSchema not trivially copyable");
    Check(std::is_trivially_destructible_v<FXObjectRefSchema>,
          "FXObjectRefSchema not trivially destructible");
    Check(std::is_standard_layout_v<FXObjectRefSchema>,
          "FXObjectRefSchema not standard-layout");
    Check(std::is_trivially_copyable_v<FXObjectRefSchemaOp>,
          "FXObjectRefSchemaOp not trivially copyable");
    Check(std::is_trivially_destructible_v<FXObjectRefSchemaOp>,
          "FXObjectRefSchemaOp not trivially destructible");
    Check(std::is_standard_layout_v<FXObjectRefSchemaOp>,
          "FXObjectRefSchemaOp not standard-layout");

    if (g_FailureCount > 0)
    {
        std::cerr << "FXObjectRefSchema.SizeofAndAlignof: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FXObjectRefSchema.SizeofAndAlignof: PASS\n";
    return 0;
}
