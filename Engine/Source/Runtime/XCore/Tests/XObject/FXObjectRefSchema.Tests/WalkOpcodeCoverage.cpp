// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectRefSchema.Tests/WalkOpcodeCoverage.cpp -- exercise the
// schema-vector GC walker across the remaining active opcodes
// (single-slot variants, delegate slots, terminator early-exit,
// version-mismatch fallback). (XCoreXObject Rev 4 §7.4; Phase 5.g').
// =====================================================================
//
// Pins:
//   1. Single-slot opcodes (WeakObject / SoftObject / Interface /
//      ClassProperty / SoftClass / Delegate / Multicast variants)
//      each visit ONE pointer at Op.Offset.
//   2. Terminator opcode short-circuits the walk; opcodes after the
//      sentinel are NOT visited (defence-in-depth alongside NumOps).
//   3. Version mismatch (Schema.Version != current) -> walker no-ops
//      (the consumer falls back to slow-path ObjectRefProperties).
//   4. OptionalObject visits Op.Offset + 8 (the Value slot after
//      bSet + pad).
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
    // Single-slot opcode coverage. 7 ref slots at offsets 0..48, one
    // per (WeakObject, SoftObject, Interface, ClassProperty,
    // SoftClass, Delegate, MulticastInlineDelegate). Each opcode
    // visits its slot.
    // -----------------------------------------------------------------
    auto* R0 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xE0000001));
    auto* R1 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xE0000002));
    auto* R2 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xE0000003));
    auto* R3 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xE0000004));
    auto* R4 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xE0000005));
    auto* R5 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xE0000006));
    auto* R6 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xE0000007));

    alignas(8) ::std::uint8_t Instance[64] = {};
    std::memcpy(Instance +  0, &R0, sizeof(R0));
    std::memcpy(Instance +  8, &R1, sizeof(R1));
    std::memcpy(Instance + 16, &R2, sizeof(R2));
    std::memcpy(Instance + 24, &R3, sizeof(R3));
    std::memcpy(Instance + 32, &R4, sizeof(R4));
    std::memcpy(Instance + 40, &R5, sizeof(R5));
    std::memcpy(Instance + 48, &R6, sizeof(R6));

    static constexpr FXObjectRefSchemaOp Ops[] =
    {
        { EXObjectRefSchemaOp::WeakObject,              0, 0,  0, 0, 0, nullptr },
        { EXObjectRefSchemaOp::SoftObject,              0, 0,  8, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Interface,               0, 0, 16, 0, 0, nullptr },
        { EXObjectRefSchemaOp::ClassProperty,           0, 0, 24, 0, 0, nullptr },
        { EXObjectRefSchemaOp::SoftClass,               0, 0, 32, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Delegate,                0, 0, 40, 0, 0, nullptr },
        { EXObjectRefSchemaOp::MulticastInlineDelegate, 0, 0, 48, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Terminator,              0, 0,  0, 0, 0, nullptr },
    };
    static constexpr FXObjectRefSchema Schema =
    {
        /*NumOps=*/8,
        /*Version=*/kFXObjectRefSchemaCurrentVersion,
        /*Ops=*/Ops,
        /*_padTail=*/0,
    };

    std::vector<::XCore::XObject*> Visited;
    ::XCore::WalkSchemaRefsWithSchema(&Schema, Instance,
        [&](::XCore::XObject* Ref) noexcept
        {
            Visited.push_back(Ref);
        });

    Check(Visited.size() == 7,
          "Single-slot opcode coverage: visit count != 7");
    if (Visited.size() == 7)
    {
        Check(Visited[0] == R0, "WeakObject slot != R0");
        Check(Visited[1] == R1, "SoftObject slot != R1");
        Check(Visited[2] == R2, "Interface slot != R2");
        Check(Visited[3] == R3, "ClassProperty slot != R3");
        Check(Visited[4] == R4, "SoftClass slot != R4");
        Check(Visited[5] == R5, "Delegate slot != R5");
        Check(Visited[6] == R6, "MulticastInline slot != R6");
    }

    // -----------------------------------------------------------------
    // Terminator early-exit. The walker MUST stop at the Terminator
    // opcode even if NumOps says there are more opcodes after it.
    // (Defence-in-depth against truncated schemas / NumOps drift.)
    // -----------------------------------------------------------------
    auto* PreTerm  = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xE000000A));
    auto* PostTerm = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xE000000B));

    alignas(8) ::std::uint8_t TermInstance[16] = {};
    std::memcpy(TermInstance + 0, &PreTerm,  sizeof(PreTerm));
    std::memcpy(TermInstance + 8, &PostTerm, sizeof(PostTerm));

    static constexpr FXObjectRefSchemaOp TermOps[] =
    {
        { EXObjectRefSchemaOp::Object,     0, 0, 0, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Terminator, 0, 0, 0, 0, 0, nullptr },
        // NumOps is 3 (deliberately overcounts; Terminator stops walk).
        { EXObjectRefSchemaOp::Object,     0, 0, 8, 0, 0, nullptr },
    };
    static constexpr FXObjectRefSchema TermSchema =
    {
        /*NumOps=*/3,
        /*Version=*/kFXObjectRefSchemaCurrentVersion,
        /*Ops=*/TermOps,
        /*_padTail=*/0,
    };

    std::vector<::XCore::XObject*> TermVisited;
    ::XCore::WalkSchemaRefsWithSchema(&TermSchema, TermInstance,
        [&](::XCore::XObject* Ref) noexcept
        {
            TermVisited.push_back(Ref);
        });

    Check(TermVisited.size() == 1,
          "Terminator early-exit: visited more than 1 slot");
    if (TermVisited.size() == 1)
    {
        Check(TermVisited[0] == PreTerm,
              "Terminator early-exit: visited != PreTerm");
    }

    // -----------------------------------------------------------------
    // Version mismatch defence.
    //
    // A schema with Version != kFXObjectRefSchemaCurrentVersion MUST
    // produce no visits (the walker no-ops; the caller's recovery is
    // the slow-path ObjectRefProperties walk).
    // -----------------------------------------------------------------
    static constexpr FXObjectRefSchemaOp StaleOps[] =
    {
        { EXObjectRefSchemaOp::Object,     0, 0, 0, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Terminator, 0, 0, 0, 0, 0, nullptr },
    };
    static constexpr FXObjectRefSchema StaleSchema =
    {
        /*NumOps=*/2,
        /*Version=*/9999u,   // intentionally != current
        /*Ops=*/StaleOps,
        /*_padTail=*/0,
    };

    std::vector<::XCore::XObject*> StaleVisited;
    ::XCore::WalkSchemaRefsWithSchema(&StaleSchema, TermInstance,
        [&](::XCore::XObject* Ref) noexcept
        {
            StaleVisited.push_back(Ref);
        });

    Check(StaleVisited.empty(),
          "Version mismatch: walker visited slot despite stale Version");

    // -----------------------------------------------------------------
    // OptionalObject visits Op.Offset + 8 (the Value slot after
    // bSet + pad).
    // -----------------------------------------------------------------
    auto* OptVal = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xE000000F));
    alignas(8) ::std::uint8_t OptInstance[32] = {};
    // bSet @ 0..7 (zero/pad); value @ 8.
    std::memcpy(OptInstance + 8, &OptVal, sizeof(OptVal));

    static constexpr FXObjectRefSchemaOp OptOps[] =
    {
        { EXObjectRefSchemaOp::OptionalObject, 0, 0, 0, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Terminator,     0, 0, 0, 0, 0, nullptr },
    };
    static constexpr FXObjectRefSchema OptSchema =
    {
        /*NumOps=*/2,
        /*Version=*/kFXObjectRefSchemaCurrentVersion,
        /*Ops=*/OptOps,
        /*_padTail=*/0,
    };

    std::vector<::XCore::XObject*> OptVisited;
    ::XCore::WalkSchemaRefsWithSchema(&OptSchema, OptInstance,
        [&](::XCore::XObject* Ref) noexcept
        {
            OptVisited.push_back(Ref);
        });

    Check(OptVisited.size() == 1, "OptionalObject: visit count != 1");
    if (OptVisited.size() == 1)
    {
        Check(OptVisited[0] == OptVal,
              "OptionalObject: visited slot != OptVal (offset+8 misread)");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FXObjectRefSchema.WalkOpcodeCoverage: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FXObjectRefSchema.WalkOpcodeCoverage: PASS\n";
    return 0;
}
