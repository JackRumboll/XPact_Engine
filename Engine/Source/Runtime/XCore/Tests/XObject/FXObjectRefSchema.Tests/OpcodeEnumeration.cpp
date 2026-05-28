// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectRefSchema.Tests/OpcodeEnumeration.cpp -- verify the 22
// active opcodes per Rev 3 FIX-H-R2-3 (XCoreXObject Rev 4 §7.4;
// Phase 5.g').
// =====================================================================
//
// Pins:
//   1. Terminator   == 0 (sentinel; XHT-emit appends one at end)
//   2. Active opcodes 1..22 (Object .. MulticastSparseDelegate) are
//      present with the expected sequential values.
//   3. _Reserved255 == 255 (canary; XHT MUST NOT emit).
//   4. kEXObjectRefSchemaOpActiveCount == 22.
//   5. The per-opcode capability bit constants (kObjectOp etc.) match
//      `1ULL << opcode-value` for every active opcode.
//
// =====================================================================

#include "Reflection/FXObjectRefSchema.h"

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
    using namespace ::XCore::Reflect;

    // -----------------------------------------------------------------
    // Sequential opcode value lock.
    //
    // Terminator (0), Object (1), WeakObject (2), ...,
    // MulticastSparseDelegate (22), _Reserved255 (255).
    //
    // The values are LOAD-BEARING: XHT-emit writes the integer
    // representation into the .rodata opcode array; the runtime
    // walker compares against the enum values. A value-rotation
    // breaks every emitted .gen.cpp.
    // -----------------------------------------------------------------
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::Terminator)              == 0,
          "Terminator != 0");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::Object)                  == 1,
          "Object != 1");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::WeakObject)              == 2,
          "WeakObject != 2");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::SoftObject)              == 3,
          "SoftObject != 3");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::ArrayOfObject)           == 4,
          "ArrayOfObject != 4");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::ArrayOfStruct)           == 5,
          "ArrayOfStruct != 5");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::StridedArrayOfObject)    == 6,
          "StridedArrayOfObject != 6");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::MapOfObject_KeyValue)    == 7,
          "MapOfObject_KeyValue != 7");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::SetOfObject)             == 8,
          "SetOfObject != 8");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::Struct)                  == 9,
          "Struct != 9");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::FieldPath)               == 10,
          "FieldPath != 10");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::FieldPathArray)          == 11,
          "FieldPathArray != 11");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::OptionalObject)          == 12,
          "OptionalObject != 12");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::DynamicallyTypedValue)   == 13,
          "DynamicallyTypedValue != 13");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::ARO)                     == 14,
          "ARO != 14");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::SlowARO)                 == 15,
          "SlowARO != 15");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::MemberARO)               == 16,
          "MemberARO != 16");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::Interface)               == 17,
          "Interface != 17");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::ClassProperty)           == 18,
          "ClassProperty != 18");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::SoftClass)               == 19,
          "SoftClass != 19");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::Delegate)                == 20,
          "Delegate != 20");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::MulticastInlineDelegate) == 21,
          "MulticastInlineDelegate != 21");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::MulticastSparseDelegate) == 22,
          "MulticastSparseDelegate != 22");
    Check(static_cast<std::uint8_t>(EXObjectRefSchemaOp::_Reserved255)            == 255,
          "_Reserved255 != 255");

    // -----------------------------------------------------------------
    // Active opcode count (Rev 3 per FIX-H-R2-3).
    // -----------------------------------------------------------------
    Check(kEXObjectRefSchemaOpActiveCount == 22u,
          "kEXObjectRefSchemaOpActiveCount != 22");

    // -----------------------------------------------------------------
    // Per-opcode capability bit constants align with opcode values.
    //
    // The capability mask carries one bit per opcode for the fast
    // "does this schema contain any X-kind ref?" query; spec §7.4
    // defines bit-N as `1ULL << opcode-value(N)` for the active set.
    // -----------------------------------------------------------------
    Check(kObjectOp                  == (1ULL <<  1), "kObjectOp mismatch");
    Check(kWeakObjectOp              == (1ULL <<  2), "kWeakObjectOp mismatch");
    Check(kSoftObjectOp              == (1ULL <<  3), "kSoftObjectOp mismatch");
    Check(kArrayOfObjectOp           == (1ULL <<  4), "kArrayOfObjectOp mismatch");
    Check(kArrayOfStructOp           == (1ULL <<  5), "kArrayOfStructOp mismatch");
    Check(kStridedArrayOfObjectOp    == (1ULL <<  6), "kStridedArrayOfObjectOp mismatch");
    Check(kMapOfObjectKeyValueOp     == (1ULL <<  7), "kMapOfObjectKeyValueOp mismatch");
    Check(kSetOfObjectOp             == (1ULL <<  8), "kSetOfObjectOp mismatch");
    Check(kStructOp                  == (1ULL <<  9), "kStructOp mismatch");
    Check(kFieldPathOp               == (1ULL << 10), "kFieldPathOp mismatch");
    Check(kFieldPathArrayOp          == (1ULL << 11), "kFieldPathArrayOp mismatch");
    Check(kOptionalObjectOp          == (1ULL << 12), "kOptionalObjectOp mismatch");
    Check(kDynamicallyTypedValueOp   == (1ULL << 13), "kDynamicallyTypedValueOp mismatch");
    Check(kAROOp                     == (1ULL << 14), "kAROOp mismatch");
    Check(kSlowAROOp                 == (1ULL << 15), "kSlowAROOp mismatch");
    Check(kMemberAROOp               == (1ULL << 16), "kMemberAROOp mismatch");
    Check(kInterfaceOp               == (1ULL << 17), "kInterfaceOp mismatch");
    Check(kClassPropertyOp           == (1ULL << 18), "kClassPropertyOp mismatch");
    Check(kSoftClassOp               == (1ULL << 19), "kSoftClassOp mismatch");
    Check(kDelegateOp                == (1ULL << 20), "kDelegateOp mismatch");
    Check(kMulticastInlineOp         == (1ULL << 21), "kMulticastInlineOp mismatch");
    Check(kMulticastSparseOp         == (1ULL << 22), "kMulticastSparseOp mismatch");

    if (g_FailureCount > 0)
    {
        std::cerr << "FXObjectRefSchema.OpcodeEnumeration: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FXObjectRefSchema.OpcodeEnumeration: PASS\n";
    return 0;
}
