// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectRefSchema.Tests/WalkSimpleStruct.cpp -- exercise the
// schema-vector GC walker with a synthetic struct carrying 3 ref-
// property opcodes (XCoreXObject Rev 4 §7.4; Phase 5.g').
// =====================================================================
//
// Synthesises:
//   * A bytes-buffer 64 bytes wide modelling an instance with three
//     ref slots at offsets 0, 8, 16 (raw XObject* slots).
//   * An FXObjectRefSchema with 4 opcodes: Object @ 0, Object @ 8,
//     Object @ 16, Terminator.
//   * Invokes WalkSchemaRefsWithSchema and checks the visitor sees
//     exactly the 3 ref pointers from the bytes buffer, in order.
//
// Verifies:
//   1. The walker visits exactly NumOps-1 ref slots (Terminator
//      stops the walk; the count excludes the sentinel).
//   2. The visitor receives the correct XObject* values (the slot
//      bytes from the synthetic instance).
//   3. The visit order matches the opcode order.
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
    // Synthetic XObject* "sentinel" values. We don't dereference these
    // -- the walker only passes them through to the visitor; the
    // visitor compares for pointer-equality.
    // -----------------------------------------------------------------
    auto* Sentinel0 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xDEAD0010));
    auto* Sentinel1 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xDEAD0020));
    auto* Sentinel2 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xDEAD0030));

    // -----------------------------------------------------------------
    // 64-byte instance buffer; place sentinel pointers at offsets
    // 0, 8, 16. The remaining bytes are zero so any spurious read
    // surfaces as nullptr.
    // -----------------------------------------------------------------
    alignas(8) ::std::uint8_t Instance[64] = {};
    std::memcpy(Instance + 0,  &Sentinel0, sizeof(Sentinel0));
    std::memcpy(Instance + 8,  &Sentinel1, sizeof(Sentinel1));
    std::memcpy(Instance + 16, &Sentinel2, sizeof(Sentinel2));

    // -----------------------------------------------------------------
    // Build schema: 3 Object opcodes + Terminator.
    // -----------------------------------------------------------------
    static constexpr FXObjectRefSchemaOp Ops[] =
    {
        { EXObjectRefSchemaOp::Object,     0, 0,  0, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Object,     0, 0,  8, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Object,     0, 0, 16, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Terminator, 0, 0,  0, 0, 0, nullptr },
    };

    static constexpr FXObjectRefSchema Schema =
    {
        /*NumOps=*/4,
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

    // -----------------------------------------------------------------
    // Expect 3 visits in order.
    // -----------------------------------------------------------------
    Check(Visited.size() == 3, "Visit count != 3");
    if (Visited.size() == 3)
    {
        Check(Visited[0] == Sentinel0, "Visited[0] != Sentinel0");
        Check(Visited[1] == Sentinel1, "Visited[1] != Sentinel1");
        Check(Visited[2] == Sentinel2, "Visited[2] != Sentinel2");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FXObjectRefSchema.WalkSimpleStruct: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FXObjectRefSchema.WalkSimpleStruct: PASS\n";
    return 0;
}
