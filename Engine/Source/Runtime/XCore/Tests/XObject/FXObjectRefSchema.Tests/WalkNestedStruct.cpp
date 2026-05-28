// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectRefSchema.Tests/WalkNestedStruct.cpp -- exercise the
// schema-vector GC walker with a Struct opcode whose NestedSchema
// points at an inner schema; verifies the walker recurses correctly
// (XCoreXObject Rev 4 §7.4 + §7.4.1; Phase 5.g').
// =====================================================================
//
// Synthesises:
//   * An outer instance buffer 64 bytes wide carrying:
//       offset 0  -> raw XObject* (outer ref)
//       offset 8  -> bytes for an inner struct (16 bytes wide)
//   * The inner struct's 16 bytes hold two XObject* refs at offsets
//     0 and 8 (within the inner struct).
//   * An inner schema with {Object@0, Object@8, Terminator}.
//   * An outer schema with {Object@0, Struct@8 -> inner schema,
//     Terminator}.
//
// Verifies:
//   1. The walker visits outer.Object @ 0.
//   2. The walker recurses into the inner schema; the inner sees
//      both Object slots at their inner-relative offsets.
//   3. Visit order is outer-first, inner-after (the inner walk
//      executes when the Struct opcode is processed).
//
// =====================================================================

#include "Reflection/FXObjectRefSchema.h"
#include "XObject/FXObjectSchemaWalker.h"
#include "XObject/XObject.h"

#include <cstdint>
#include <cstring>
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
    using namespace ::XCore::Reflect;

    auto* OuterRef  = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xC0FFEE01));
    auto* InnerRef0 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xC0FFEE02));
    auto* InnerRef1 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xC0FFEE03));

    // -----------------------------------------------------------------
    // Outer instance: 64 bytes.
    //   offset 0:  OuterRef
    //   offset 8:  InnerRef0 (begin of inner struct's bytes)
    //   offset 16: InnerRef1 (8 bytes into inner struct)
    // -----------------------------------------------------------------
    alignas(8) ::std::uint8_t Instance[64] = {};
    std::memcpy(Instance + 0,  &OuterRef,  sizeof(OuterRef));
    std::memcpy(Instance + 8,  &InnerRef0, sizeof(InnerRef0));
    std::memcpy(Instance + 16, &InnerRef1, sizeof(InnerRef1));

    // -----------------------------------------------------------------
    // Inner schema: two Object opcodes (relative to inner-struct
    // start at outer offset 8).
    // -----------------------------------------------------------------
    static constexpr FXObjectRefSchemaOp InnerOps[] =
    {
        { EXObjectRefSchemaOp::Object,     0, 0, 0, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Object,     0, 0, 8, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Terminator, 0, 0, 0, 0, 0, nullptr },
    };
    static constexpr FXObjectRefSchema InnerSchema =
    {
        /*NumOps=*/3,
        /*Version=*/kFXObjectRefSchemaCurrentVersion,
        /*Ops=*/InnerOps,
        /*_padTail=*/0,
    };

    // -----------------------------------------------------------------
    // Outer schema: outer Object + Struct (recurse into inner) +
    // Terminator.
    // -----------------------------------------------------------------
    static constexpr FXObjectRefSchemaOp OuterOps[] =
    {
        { EXObjectRefSchemaOp::Object,     0, 0, 0, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Struct,     0, 0, 8, 0, 0, &InnerSchema },
        { EXObjectRefSchemaOp::Terminator, 0, 0, 0, 0, 0, nullptr },
    };
    static constexpr FXObjectRefSchema OuterSchema =
    {
        /*NumOps=*/3,
        /*Version=*/kFXObjectRefSchemaCurrentVersion,
        /*Ops=*/OuterOps,
        /*_padTail=*/0,
    };

    // -----------------------------------------------------------------
    // Walk + collect.
    // -----------------------------------------------------------------
    std::vector<::XCore::XObject*> Visited;
    ::XCore::WalkSchemaRefsWithSchema(&OuterSchema, Instance,
        [&](::XCore::XObject* Ref) noexcept
        {
            Visited.push_back(Ref);
        });

    // -----------------------------------------------------------------
    // Expect outer ref + 2 inner refs = 3 visits, in walk order.
    // -----------------------------------------------------------------
    Check(Visited.size() == 3, "Visit count != 3 (expected outer + 2 inner)");
    if (Visited.size() == 3)
    {
        Check(Visited[0] == OuterRef,  "Visited[0] != OuterRef");
        Check(Visited[1] == InnerRef0, "Visited[1] != InnerRef0");
        Check(Visited[2] == InnerRef1, "Visited[2] != InnerRef1");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FXObjectRefSchema.WalkNestedStruct: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FXObjectRefSchema.WalkNestedStruct: PASS\n";
    return 0;
}
