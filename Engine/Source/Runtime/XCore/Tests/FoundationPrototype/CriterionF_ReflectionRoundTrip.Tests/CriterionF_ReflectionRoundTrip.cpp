// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// CriterionF_ReflectionRoundTrip.cpp -- Foundation Prototype
// criterion (f): reflection round-trip clean.
// =====================================================================
//
// Criterion (f) spec wording (spec §13.1):
//   "a transpiled C# property is settable/gettable through XProperty
//    Editor's reflection-driven path; no marshalling errors."
//
// JUDGEMENT CALL (Phase 5.l criterion-f scope). The full criterion
// involves:
//   * A transpiled C# property (XIL2CPP-emitted; System 6 deliverable).
//   * The XPropertyEditor (post-XCoreXObject; editor-side deliverable).
//
// At Phase 5.l those two producers do NOT yet ship. The CORE invariant
// the criterion asserts -- that an FProperty's GetValue / SetValue
// round-trip is byte-clean -- IS testable today against the XCore-4b
// FProperty subclass set (Phase 4b.4a + 4b.4b shipped 28 subclasses
// with full dispatch surface).
//
// This test verifies the FProperty round-trip layer (the BOTTOM of
// the XPropertyEditor stack) for the most common primitive types.
// The XPropertyEditor's UI surface routes user-typed values through
// the SAME GetValue / SetValue dispatch slots; verifying the dispatch
// is byte-clean is the load-bearing precondition for the
// XPropertyEditor's round-trip cleanliness.
//
// =====================================================================

#include "../Phase5LCommon.h"

#include "Containers/FString.h"
#include "Reflection/FBoolProperty.h"
#include "Reflection/FFloatProperty.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FInt64Property.h"
#include "Reflection/FNameProperty.h"
#include "Reflection/FStrProperty.h"

#include <cstdint>
#include <cstring>

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    using ::XCore::FString;
    using ::XCore::Reflect::FBoolProperty;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FFloatProperty;
    using ::XCore::Reflect::FInt64Property;
    using ::XCore::Reflect::FIntProperty;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FNameProperty;
    using ::XCore::Reflect::FStrProperty;

    Phase5L::ResetAllForTests();

    // -----------------------------------------------------------------
    // FIntProperty round-trip (the canonical numeric path).
    // -----------------------------------------------------------------
    {
        FIntProperty Prop(FFieldVariant{}, FName("TestInt"));
        ::int32 Slot = 0;
        ::int32 WriteValue = -1'234'567;

        Prop.SetValue(&Slot, &WriteValue);
        P5L_CHECK(Slot == WriteValue,
                  "Criterion (f): FIntProperty SetValue did not write "
                  "the expected bytes");

        ::int32 ReadBack = 0;
        Prop.GetValue(&Slot, &ReadBack);
        P5L_CHECK(ReadBack == WriteValue,
                  "Criterion (f): FIntProperty GetValue did not return "
                  "the expected bytes");

        P5L_CHECK(Prop.Identical(&WriteValue, &ReadBack),
                  "Criterion (f): FIntProperty Identical disagrees on "
                  "byte-identical inputs");
    }

    // -----------------------------------------------------------------
    // FInt64Property round-trip (large numeric path).
    // -----------------------------------------------------------------
    {
        FInt64Property Prop(FFieldVariant{}, FName("TestInt64"));
        ::int64 Slot = 0;
        ::int64 WriteValue = static_cast<::int64>(0x7FFF'FFFF'FFFF'FFFFLL);

        Prop.SetValue(&Slot, &WriteValue);
        P5L_CHECK(Slot == WriteValue,
                  "Criterion (f): FInt64Property SetValue round-trip "
                  "failed");

        ::int64 ReadBack = 0;
        Prop.GetValue(&Slot, &ReadBack);
        P5L_CHECK(ReadBack == WriteValue,
                  "Criterion (f): FInt64Property GetValue round-trip "
                  "failed");
    }

    // -----------------------------------------------------------------
    // FFloatProperty round-trip (IEEE 754 single-precision; bytewise
    // identical store / load is the X-DET-adjacent invariant).
    // -----------------------------------------------------------------
    {
        FFloatProperty Prop(FFieldVariant{}, FName("TestFloat"));
        float Slot = 0.0f;
        float WriteValue = 3.14159265f;

        Prop.SetValue(&Slot, &WriteValue);
        P5L_CHECK(::std::memcmp(&Slot, &WriteValue, sizeof(float)) == 0,
                  "Criterion (f): FFloatProperty SetValue did not produce "
                  "byte-identical bytes");

        float ReadBack = 0.0f;
        Prop.GetValue(&Slot, &ReadBack);
        P5L_CHECK(::std::memcmp(&ReadBack, &WriteValue, sizeof(float)) == 0,
                  "Criterion (f): FFloatProperty GetValue round-trip "
                  "byte-mismatched");
    }

    // -----------------------------------------------------------------
    // FBoolProperty (plain-bool path) round-trip.
    // -----------------------------------------------------------------
    {
        FBoolProperty Prop(FFieldVariant{}, FName("TestBool"));
        bool Slot = false;
        bool WriteValue = true;

        Prop.SetValue(&Slot, &WriteValue);
        P5L_CHECK(Slot == WriteValue,
                  "Criterion (f): FBoolProperty SetValue round-trip "
                  "failed");

        bool ReadBack = false;
        Prop.GetValue(&Slot, &ReadBack);
        P5L_CHECK(ReadBack == WriteValue,
                  "Criterion (f): FBoolProperty GetValue round-trip "
                  "failed");
    }

    // -----------------------------------------------------------------
    // FNameProperty round-trip (the FName intern-table-shared path).
    // -----------------------------------------------------------------
    {
        FNameProperty Prop(FFieldVariant{}, FName("TestName"));
        FName Slot;
        FName WriteValue("CriterionFName");

        Prop.SetValue(&Slot, &WriteValue);
        P5L_CHECK(Slot == WriteValue,
                  "Criterion (f): FNameProperty SetValue round-trip "
                  "failed");

        FName ReadBack;
        Prop.GetValue(&Slot, &ReadBack);
        P5L_CHECK(ReadBack == WriteValue,
                  "Criterion (f): FNameProperty GetValue round-trip "
                  "failed");
    }

    // -----------------------------------------------------------------
    // FStrProperty round-trip (the heap-allocated value path).
    // -----------------------------------------------------------------
    {
        FStrProperty Prop(FFieldVariant{}, FName("TestStr"));
        FString Slot;
        FString WriteValue("Reflection round-trip clean!");

        Prop.SetValue(&Slot, &WriteValue);
        P5L_CHECK(Slot.Equals(WriteValue),
                  "Criterion (f): FStrProperty SetValue round-trip "
                  "failed (value-equal check)");

        FString ReadBack;
        Prop.GetValue(&Slot, &ReadBack);
        P5L_CHECK(ReadBack.Equals(WriteValue),
                  "Criterion (f): FStrProperty GetValue round-trip "
                  "failed (value-equal check)");
    }

    std::cout << "Criterion (f): primitive FProperty round-trip layer "
                 "verified (6 subclass families). XPropertyEditor + "
                 "XIL2CPP-transpiled-property gates run when System 6 + "
                 "editor ship.\n";

    return P5L_REPORT_PASS("FoundationPrototype.CriterionF_ReflectionRoundTrip");
}
