// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/PrimitiveGetSet.cpp -- per-subclass GetValue/SetValue
// round-trip + dispatch correctness (XCore-4b §5.4).
// =====================================================================
//
// For each primitive FProperty subclass, build an instance whose
// Offset points at a mock owner-struct's field, then exercise the
// dispatch slots: SetValue + GetValue round-trip + Identical agrees.
//
// =====================================================================

#include "Reflection/FBoolProperty.h"
#include "Reflection/FByteProperty.h"
#include "Reflection/FDoubleProperty.h"
#include "Reflection/FFloatProperty.h"
#include "Reflection/FInt16Property.h"
#include "Reflection/FInt64Property.h"
#include "Reflection/FInt8Property.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FNameProperty.h"
#include "Reflection/FStrProperty.h"
#include "Reflection/FUInt16Property.h"
#include "Reflection/FUInt32Property.h"
#include "Reflection/FUInt64Property.h"

#include "Containers/FString.h"
#include "HAL/FMemory.h"
#include "Reflection/FName.h"

#include <cstdint>
#include <cstring>
#include <iostream>
#include <string>

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

    // Mock owner struct -- used as the "Container" pointer for the
    // FProperty round-trip tests. Each field is at a known offset.
    struct MockOwner
    {
        ::int8                   I8Field;
        ::int16                  I16Field;
        ::int32                  I32Field;
        ::int64                  I64Field;
        ::uint16                 U16Field;
        ::uint32                 U32Field;
        ::uint64                 U64Field;
        float                    FloatField;
        double                   DoubleField;
        ::XCore::Reflect::FName  NameField;
        ::XCore::FString         StringField;
    };

    template <typename PropertyT, typename ValueT>
    void TestNumericRoundTrip(const char* Tag, ValueT TestValue)
    {
        using ::XCore::Reflect::FFieldVariant;
        using ::XCore::Reflect::FName;

        PropertyT Prop(FFieldVariant{}, FName("TestField"));
        ValueT Slot = ValueT{};
        ValueT Initial = ValueT{};
        std::memcpy(&Initial, &Slot, sizeof(Slot));

        // SetValue path: write TestValue into Slot.
        Prop.SetValue(&Slot, &TestValue);
        Check(Slot == TestValue,
              (std::string("SetValue round-trip failed for ") + Tag).c_str());

        // GetValue path: read Slot back into ReadBack.
        ValueT ReadBack = ValueT{};
        Prop.GetValue(&Slot, &ReadBack);
        Check(ReadBack == TestValue,
              (std::string("GetValue round-trip failed for ") + Tag).c_str());

        // Identical: TestValue == TestValue.
        ValueT Other = TestValue;
        Check(Prop.Identical(&TestValue, &Other),
              (std::string("Identical(self) failed for ") + Tag).c_str());

        // Identical with different value: false (unless equal).
        if (TestValue != Initial)
        {
            Check(!Prop.Identical(&TestValue, &Initial),
                  (std::string("Identical(diff) returned true for ") + Tag).c_str());
        }

        // CopySingleValue.
        ValueT Copied = ValueT{};
        Prop.CopySingleValue(&Copied, &TestValue);
        Check(Copied == TestValue,
              (std::string("CopySingleValue failed for ") + Tag).c_str());

        // InitializeValue / DestroyValue (single element).
        ValueT InitBuf;
        std::memset(&InitBuf, 0xFF, sizeof(InitBuf));
        Prop.InitializeValue(&InitBuf, 1);
        // For numeric, InitializeValue zeros the memory.
        ValueT Zero = ValueT{};
        Check(std::memcmp(&InitBuf, &Zero, sizeof(InitBuf)) == 0,
              (std::string("InitializeValue did not zero for ") + Tag).c_str());

        Prop.DestroyValue(&InitBuf, 1);  // no-op for trivials

        // GetValueTypeHash: nonzero for nonzero values.
        if (TestValue != ValueT{})
        {
            ::std::uint64_t Hash = Prop.GetValueTypeHash(&TestValue);
            Check(Hash != 0,
                  (std::string("GetValueTypeHash returned 0 for nonzero ") + Tag).c_str());
        }
    }
}

int main()
{
    // FMallocBinnedX requires explicit __Init before any FName / FString
    // touches the allocator. Test runner does this once per process.
    ::XCore::HAL::FMemory::__Init();

    using namespace ::XCore::Reflect;

    // -----------------------------------------------------------------
    // Numeric primitives.
    // -----------------------------------------------------------------
    TestNumericRoundTrip<FInt8Property,   ::int8>  ("FInt8Property",   ::int8(-42));
    TestNumericRoundTrip<FInt16Property,  ::int16> ("FInt16Property",  ::int16(-1234));
    TestNumericRoundTrip<FIntProperty,    ::int32> ("FIntProperty",    ::int32(-987654));
    TestNumericRoundTrip<FInt64Property,  ::int64> ("FInt64Property",  ::int64(-9876543210LL));
    TestNumericRoundTrip<FUInt16Property, ::uint16>("FUInt16Property", ::uint16(54321));
    TestNumericRoundTrip<FUInt32Property, ::uint32>("FUInt32Property", ::uint32(0xDEADBEEFu));
    TestNumericRoundTrip<FUInt64Property, ::uint64>("FUInt64Property", ::uint64(0xCAFEBABEDEADBEEFull));
    TestNumericRoundTrip<FFloatProperty,  float>   ("FFloatProperty",  3.14159f);
    TestNumericRoundTrip<FDoubleProperty, double>  ("FDoubleProperty", 2.718281828);

    // FByteProperty (uint8) round-trip.
    {
        FByteProperty Prop(FFieldVariant{}, FName("ByteField"));
        ::uint8 Slot = 0;
        ::uint8 TestVal = 0xA5;
        Prop.SetValue(&Slot, &TestVal);
        Check(Slot == 0xA5, "FByteProperty SetValue failed");
        ::uint8 ReadBack = 0;
        Prop.GetValue(&Slot, &ReadBack);
        Check(ReadBack == 0xA5, "FByteProperty GetValue failed");
        Check(Prop.GetUnderlyingEnum() == nullptr,
              "FByteProperty default UnderlyingEnum not nullptr");
    }

    // -----------------------------------------------------------------
    // FBoolProperty plain-bool round-trip.
    // -----------------------------------------------------------------
    {
        FBoolProperty Prop(FFieldVariant{}, FName("BoolField"));
        ::uint8 Slot = 0;
        ::uint8 TrueByte  = 1;
        ::uint8 FalseByte = 0;
        Prop.SetValue(&Slot, &TrueByte);
        Check(Slot == 1, "FBoolProperty SetValue(true) failed");
        Prop.SetValue(&Slot, &FalseByte);
        Check(Slot == 0, "FBoolProperty SetValue(false) failed");
        Check(!Prop.IsBitfield(), "FBoolProperty default IsBitfield() should be false");
        Check(Prop.GetFieldSize() == 1, "FBoolProperty default FieldSize != 1");
        Check(Prop.GetByteMask()  == 0xFFu, "FBoolProperty default ByteMask != 0xFF");

        // Identical comparing 0x01 vs 0xFF (both `true` semantically).
        ::uint8 LhsByte = 0x01;
        ::uint8 RhsByte = 0xFF;
        Check(Prop.Identical(&LhsByte, &RhsByte),
              "FBoolProperty Identical(0x01, 0xFF) returned false (both are 'true')");
    }

    // -----------------------------------------------------------------
    // FNameProperty round-trip.
    // -----------------------------------------------------------------
    {
        FNameProperty Prop(FFieldVariant{}, FName("NameField"));
        FName Slot;          // NAME_None
        FName TestVal("HelloName");
        Prop.SetValue(&Slot, &TestVal);
        Check(Slot == TestVal, "FNameProperty SetValue failed");

        FName ReadBack;
        Prop.GetValue(&Slot, &ReadBack);
        Check(ReadBack == TestVal, "FNameProperty GetValue failed");

        Check(Prop.Identical(&TestVal, &Slot), "FNameProperty Identical(self) failed");

        FName OtherVal("DifferentName");
        Check(!Prop.Identical(&TestVal, &OtherVal),
              "FNameProperty Identical(diff) returned true");
    }

    // -----------------------------------------------------------------
    // FStrProperty round-trip.
    // -----------------------------------------------------------------
    {
        FStrProperty Prop(FFieldVariant{}, FName("StringField"));
        ::XCore::FString Slot;
        ::XCore::FString TestVal("Hello, World!");
        Prop.SetValue(&Slot, &TestVal);
        Check(Slot == TestVal, "FStrProperty SetValue failed");

        ::XCore::FString OtherVal("Different string");
        Check(!Prop.Identical(&TestVal, &OtherVal),
              "FStrProperty Identical(diff) returned true");
        Check(Prop.Identical(&TestVal, &Slot),
              "FStrProperty Identical(self) failed");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.PrimitiveGetSet: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.PrimitiveGetSet: PASS\n";
    return 0;
}
