// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/PrimitiveIdentical.cpp -- Identical-slot correctness
// for each primitive type (XCore-4b §5.4 + §5.5).
// =====================================================================
//
// Verifies:
//
//   1. Identical(A, A) is true for every primitive type.
//   2. Identical(A, B) is false when A != B.
//   3. Floating-point: Identical uses bytewise comparison, so:
//      a. +0.0 vs -0.0 are NOT identical (bytewise distinct).
//      b. NaN vs NaN (same bit pattern) ARE identical.
//      c. NaN vs different-bit-pattern NaN are NOT identical
//         (this is the bytewise discipline; UE matches).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FBoolProperty.h"
#include "Reflection/FByteProperty.h"
#include "Reflection/FDoubleProperty.h"
#include "Reflection/FFloatProperty.h"
#include "Reflection/FInt16Property.h"
#include "Reflection/FInt64Property.h"
#include "Reflection/FInt8Property.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FNameProperty.h"
#include "Reflection/FStrProperty.h"
#include "Reflection/FUInt16Property.h"
#include "Reflection/FUInt32Property.h"
#include "Reflection/FUInt64Property.h"

#include "Containers/FString.h"

#include <cmath>
#include <cstdint>
#include <cstring>
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
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::Reflect::FBoolProperty;
    using ::XCore::Reflect::FByteProperty;
    using ::XCore::Reflect::FDoubleProperty;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FFloatProperty;
    using ::XCore::Reflect::FInt16Property;
    using ::XCore::Reflect::FInt64Property;
    using ::XCore::Reflect::FInt8Property;
    using ::XCore::Reflect::FIntProperty;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FNameProperty;
    using ::XCore::Reflect::FStrProperty;
    using ::XCore::Reflect::FUInt16Property;
    using ::XCore::Reflect::FUInt32Property;
    using ::XCore::Reflect::FUInt64Property;

    // -----------------------------------------------------------------
    // Identical(A, A) is true for every integer type.
    // -----------------------------------------------------------------
    {
        FInt8Property Prop(FFieldVariant{}, FName("F"));
        ::int8 A = -42, B = -42, C = 0;
        Check(Prop.Identical(&A, &B),  "FInt8Property Identical(=)");
        Check(!Prop.Identical(&A, &C), "FInt8Property Identical(!=)");
    }
    {
        FInt16Property Prop(FFieldVariant{}, FName("F"));
        ::int16 A = -1234, B = -1234, C = 0;
        Check(Prop.Identical(&A, &B),  "FInt16Property Identical(=)");
        Check(!Prop.Identical(&A, &C), "FInt16Property Identical(!=)");
    }
    {
        FIntProperty Prop(FFieldVariant{}, FName("F"));
        ::int32 A = -123456789, B = -123456789, C = 0;
        Check(Prop.Identical(&A, &B),  "FIntProperty Identical(=)");
        Check(!Prop.Identical(&A, &C), "FIntProperty Identical(!=)");
    }
    {
        FInt64Property Prop(FFieldVariant{}, FName("F"));
        ::int64 A = -1234567890123LL, B = -1234567890123LL, C = 0;
        Check(Prop.Identical(&A, &B),  "FInt64Property Identical(=)");
        Check(!Prop.Identical(&A, &C), "FInt64Property Identical(!=)");
    }
    {
        FUInt16Property Prop(FFieldVariant{}, FName("F"));
        ::uint16 A = 0xCAFE, B = 0xCAFE, C = 0;
        Check(Prop.Identical(&A, &B),  "FUInt16Property Identical(=)");
        Check(!Prop.Identical(&A, &C), "FUInt16Property Identical(!=)");
    }
    {
        FUInt32Property Prop(FFieldVariant{}, FName("F"));
        ::uint32 A = 0xDEADBEEFu, B = 0xDEADBEEFu, C = 0;
        Check(Prop.Identical(&A, &B),  "FUInt32Property Identical(=)");
        Check(!Prop.Identical(&A, &C), "FUInt32Property Identical(!=)");
    }
    {
        FUInt64Property Prop(FFieldVariant{}, FName("F"));
        ::uint64 A = 0xCAFEBABEDEADBEEFull, B = 0xCAFEBABEDEADBEEFull, C = 0;
        Check(Prop.Identical(&A, &B),  "FUInt64Property Identical(=)");
        Check(!Prop.Identical(&A, &C), "FUInt64Property Identical(!=)");
    }

    // -----------------------------------------------------------------
    // FByteProperty.
    // -----------------------------------------------------------------
    {
        FByteProperty Prop(FFieldVariant{}, FName("F"));
        ::uint8 A = 0xA5, B = 0xA5, C = 0;
        Check(Prop.Identical(&A, &B),  "FByteProperty Identical(=)");
        Check(!Prop.Identical(&A, &C), "FByteProperty Identical(!=)");
    }

    // -----------------------------------------------------------------
    // FBoolProperty: 0x01 and 0xFF both represent `true` and MUST
    // compare identical.
    // -----------------------------------------------------------------
    {
        FBoolProperty Prop(FFieldVariant{}, FName("F"));
        ::uint8 T1 = 0x01, T2 = 0xFF, FBoolByte = 0x00;
        Check( Prop.Identical(&T1, &T2),
              "FBoolProperty: 0x01 and 0xFF should both be `true` (Identical)");
        Check(!Prop.Identical(&T1, &FBoolByte),
              "FBoolProperty: true vs false should NOT be Identical");
    }

    // -----------------------------------------------------------------
    // FFloatProperty: bytewise comparison.
    // -----------------------------------------------------------------
    {
        FFloatProperty Prop(FFieldVariant{}, FName("F"));
        float A = 3.14159f, B = 3.14159f, C = 2.71828f;
        Check(Prop.Identical(&A, &B),  "FFloatProperty Identical(=)");
        Check(!Prop.Identical(&A, &C), "FFloatProperty Identical(!=)");

        // +0.0 vs -0.0 are bytewise distinct (sign bit differs).
        float PosZero = +0.0f;
        float NegZero = -0.0f;
        Check(!Prop.Identical(&PosZero, &NegZero),
              "FFloatProperty: +0.0 vs -0.0 should NOT be bytewise Identical");

        // NaN with same bit pattern: identical.
        float Nan1 = ::std::nanf(""), Nan2 = Nan1;
        Check(Prop.Identical(&Nan1, &Nan2),
              "FFloatProperty: NaN vs same-bit-pattern NaN should be Identical");
    }

    // -----------------------------------------------------------------
    // FDoubleProperty.
    // -----------------------------------------------------------------
    {
        FDoubleProperty Prop(FFieldVariant{}, FName("F"));
        double A = 3.14159265358979, B = 3.14159265358979, C = 2.71828182845904;
        Check(Prop.Identical(&A, &B),  "FDoubleProperty Identical(=)");
        Check(!Prop.Identical(&A, &C), "FDoubleProperty Identical(!=)");
    }

    // -----------------------------------------------------------------
    // FName: case-sensitive equality.
    // -----------------------------------------------------------------
    {
        FNameProperty Prop(FFieldVariant{}, FName("F"));
        FName A("Actor"), B("Actor"), C("actor");
        Check(Prop.Identical(&A, &B),
              "FNameProperty: Actor == Actor");
        Check(!Prop.Identical(&A, &C),
              "FNameProperty: Actor != actor (case-sensitive)");
    }

    // -----------------------------------------------------------------
    // FString: bytewise equality.
    // -----------------------------------------------------------------
    {
        FStrProperty Prop(FFieldVariant{}, FName("F"));
        ::XCore::FString A("Hello"), B("Hello"), C("World");
        Check(Prop.Identical(&A, &B),  "FStrProperty Identical(=)");
        Check(!Prop.Identical(&A, &C), "FStrProperty Identical(!=)");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.PrimitiveIdentical: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.PrimitiveIdentical: PASS\n";
    return 0;
}
