// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectRefSchema.Tests/WalkArrayOfObject.cpp -- exercise the
// schema-vector GC walker with an ArrayOfObject opcode; the walker
// must visit every element of the synthesised TArray
// (XCoreXObject Rev 4 §7.4 + §7.4.1; Phase 5.g').
// =====================================================================
//
// Synthesises:
//   * A TArray-shaped slot at offset 0 of an instance buffer. The
//     slot's first 16 bytes are the non-GC TArrayCore prefix:
//       m_data @ 0  -> pointer to a 3-element XObject* array
//       m_num  @ 8  -> 3
//       m_max  @ 12 -> 3
//   * An array of 3 XObject* sentinel pointers; the m_data pointer
//     references this array.
//   * Schema: { ArrayOfObject @ offset 0, Terminator }.
//
// Verifies:
//   1. The walker reads the slot's TArrayCore prefix correctly
//      (m_data + m_num).
//   2. The walker visits each of the 3 elements in order.
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

    // -----------------------------------------------------------------
    // Synthetic XObject* element array.
    // -----------------------------------------------------------------
    auto* Elem0 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xA0A0A001));
    auto* Elem1 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xA0A0A002));
    auto* Elem2 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xA0A0A003));

    alignas(8) ::XCore::XObject* Buffer[3];
    Buffer[0] = Elem0;
    Buffer[1] = Elem1;
    Buffer[2] = Elem2;

    // -----------------------------------------------------------------
    // Instance buffer with a TArray-shaped slot at offset 0. The
    // slot's first 16 bytes encode {m_data, m_num, m_max}. The
    // remaining 8 bytes (16..23) are the m_alloc field; not read by
    // the walker.
    //
    // We do NOT use the real TArray type here -- the walker reads via
    // SchemaWalkerByteCopy / TArrayCoreView and only requires the
    // first 16 bytes match the non-GC TArrayCore prefix layout.
    // -----------------------------------------------------------------
    alignas(8) ::std::uint8_t Instance[64] = {};

    void* DataPtr = static_cast<void*>(Buffer);
    ::std::int32_t Num = 3;
    ::std::int32_t Max = 3;

    std::memcpy(Instance + 0, &DataPtr, sizeof(DataPtr));   // m_data @ 0
    std::memcpy(Instance + 8, &Num,     sizeof(Num));        // m_num  @ 8
    std::memcpy(Instance + 12, &Max,    sizeof(Max));        // m_max  @ 12

    // -----------------------------------------------------------------
    // Schema: { ArrayOfObject @ 0, Terminator }.
    // -----------------------------------------------------------------
    static constexpr FXObjectRefSchemaOp Ops[] =
    {
        { EXObjectRefSchemaOp::ArrayOfObject, 0, 0, 0, 8, 0, nullptr },
        { EXObjectRefSchemaOp::Terminator,    0, 0, 0, 0, 0, nullptr },
    };
    static constexpr FXObjectRefSchema Schema =
    {
        /*NumOps=*/2,
        /*Version=*/kFXObjectRefSchemaCurrentVersion,
        /*Ops=*/Ops,
        /*_padTail=*/0,
    };

    // -----------------------------------------------------------------
    // Walk + collect.
    // -----------------------------------------------------------------
    std::vector<::XCore::XObject*> Visited;
    ::XCore::WalkSchemaRefsWithSchema(&Schema, Instance,
        [&](::XCore::XObject* Ref) noexcept
        {
            Visited.push_back(Ref);
        });

    Check(Visited.size() == 3, "Visit count != 3 (expected 3 array elements)");
    if (Visited.size() == 3)
    {
        Check(Visited[0] == Elem0, "Visited[0] != Elem0");
        Check(Visited[1] == Elem1, "Visited[1] != Elem1");
        Check(Visited[2] == Elem2, "Visited[2] != Elem2");
    }

    // -----------------------------------------------------------------
    // Empty-array case: m_num = 0; walker must not invoke the visitor.
    // -----------------------------------------------------------------
    {
        alignas(8) ::std::uint8_t EmptyInstance[64] = {};
        // m_data = nullptr; m_num = 0; m_max = 0. Already zero-init.

        std::vector<::XCore::XObject*> EmptyVisited;
        ::XCore::WalkSchemaRefsWithSchema(&Schema, EmptyInstance,
            [&](::XCore::XObject* Ref) noexcept
            {
                EmptyVisited.push_back(Ref);
            });
        Check(EmptyVisited.empty(),
              "Empty-array walk visited a slot (should be no-op)");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FXObjectRefSchema.WalkArrayOfObject: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FXObjectRefSchema.WalkArrayOfObject: PASS\n";
    return 0;
}
