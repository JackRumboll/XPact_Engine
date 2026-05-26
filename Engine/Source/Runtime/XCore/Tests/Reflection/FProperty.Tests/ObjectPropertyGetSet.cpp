// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/ObjectPropertyGetSet.cpp -- FObjectProperty dispatch
// correctness (XCore-4b §5.4 + §5.5; Phase 4b.4b).
// =====================================================================
//
// Verifies:
//
//   1. FObjectProperty.GetValue / SetValue round-trip a pointer.
//   2. FObjectProperty.CopySingleValue copies a pointer.
//   3. FObjectProperty.InitializeValue zeroes a slot.
//   4. FObjectProperty.Identical does pointer-equality compare.
//   5. FObjectProperty.GetValueTypeHash hashes the pointer bits.
//   6. FObjectProperty's CastFlags include the FObjectPropertyBase
//      parent gate bit AND its own subclass bit AND the FProperty
//      parent gate bit.
//   7. FWeakObjectProperty / FSoftObjectProperty / FClassProperty /
//      FSoftClassProperty / FInterfaceProperty all round-trip their
//      8-byte payloads via the same dispatch shape.
//
// FObject is forward-declared. The test uses raw uint64-typed mock
// containers to avoid needing the full FObject type (which lives at
// System 5).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FClassProperty.h"
#include "Reflection/FInterfaceProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FObjectProperty.h"
#include "Reflection/FProperty.h"
#include "Reflection/FSoftClassProperty.h"
#include "Reflection/FSoftObjectProperty.h"
#include "Reflection/FWeakObjectProperty.h"

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
    using ::XCore::Reflect::EClassCastFlags;
    using ::XCore::Reflect::FClassProperty;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FInterfaceProperty;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FObjectProperty;
    using ::XCore::Reflect::FProperty;
    using ::XCore::Reflect::FSoftClassProperty;
    using ::XCore::Reflect::FSoftObjectProperty;
    using ::XCore::Reflect::FWeakObjectProperty;
    using ::XCore::Reflect::HasAllCastFlags;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // FObjectProperty GetValue / SetValue round-trip.
    //
    // The slot stores an FObject* (8 bytes). We use a fake address as
    // the pointer; the FObject type is not dereferenced.
    // -----------------------------------------------------------------
    {
        FObjectProperty Prop(FFieldVariant{}, FName("ObjField"));

        void* Slot = nullptr;
        void* TestVal = reinterpret_cast<void*>(static_cast<::uintptr_t>(0xDEADBEEFCAFEBABEull));

        // SetValue
        Prop.SetValue(&Slot, &TestVal);
        Check(Slot == TestVal, "FObjectProperty SetValue round-trip failed");

        // GetValue
        void* ReadBack = nullptr;
        Prop.GetValue(&Slot, &ReadBack);
        Check(ReadBack == TestVal, "FObjectProperty GetValue round-trip failed");

        // CopySingleValue
        void* Copied = nullptr;
        Prop.CopySingleValue(&Copied, &TestVal);
        Check(Copied == TestVal, "FObjectProperty CopySingleValue failed");

        // Identical(self, self)
        Check(Prop.Identical(&Slot, &Slot), "FObjectProperty Identical(self) failed");

        // Identical(diff)
        void* OtherVal = reinterpret_cast<void*>(static_cast<::uintptr_t>(0x1234567890ABCDEFull));
        Check(!Prop.Identical(&Slot, &OtherVal),
              "FObjectProperty Identical(diff) returned true");

        // InitializeValue
        void* InitBuf = TestVal;
        Prop.InitializeValue(&InitBuf, 1);
        Check(InitBuf == nullptr, "FObjectProperty InitializeValue did not nullptr");

        // GetValueTypeHash: nonzero for nonzero pointer
        ::std::uint64_t Hash = Prop.GetValueTypeHash(&TestVal);
        Check(Hash != 0, "FObjectProperty GetValueTypeHash returned 0 for nonzero ptr");

        // -- StaticClass / CastFlags assertions -------------------------
        const ::XCore::Reflect::FFieldClass* Cls = FObjectProperty::StaticClass();
        Check(Cls != nullptr, "FObjectProperty::StaticClass() returned nullptr");
        const EClassCastFlags Flags = Cls->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFProperty),
              "FObjectProperty CastFlags missing kFProperty");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFObjectProperty),
              "FObjectProperty CastFlags missing kFObjectProperty");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFObjectPropertyBase),
              "FObjectProperty CastFlags missing kFObjectPropertyBase parent gate");
    }

    // -----------------------------------------------------------------
    // FWeakObjectProperty GetValue / SetValue round-trip.
    // -----------------------------------------------------------------
    {
        FWeakObjectProperty Prop(FFieldVariant{}, FName("WeakField"));
        ::uint64 Slot = 0;
        ::uint64 TestVal = 0xCAFEBABE12345678ull;
        Prop.SetValue(&Slot, &TestVal);
        Check(Slot == TestVal, "FWeakObjectProperty SetValue failed");

        ::uint64 ReadBack = 0;
        Prop.GetValue(&Slot, &ReadBack);
        Check(ReadBack == TestVal, "FWeakObjectProperty GetValue failed");

        // CastFlags include FObjectPropertyBase parent gate.
        const EClassCastFlags Flags = FWeakObjectProperty::StaticClass()->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFObjectPropertyBase),
              "FWeakObjectProperty CastFlags missing kFObjectPropertyBase");
    }

    // -----------------------------------------------------------------
    // FSoftObjectProperty round-trip.
    // -----------------------------------------------------------------
    {
        FSoftObjectProperty Prop(FFieldVariant{}, FName("SoftField"));
        ::uint64 Slot = 0;
        ::uint64 TestVal = 0xABCDEF0123456789ull;
        Prop.SetValue(&Slot, &TestVal);
        Check(Slot == TestVal, "FSoftObjectProperty SetValue failed");

        const EClassCastFlags Flags = FSoftObjectProperty::StaticClass()->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFSoftObjectProperty),
              "FSoftObjectProperty CastFlags missing own bit");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFObjectPropertyBase),
              "FSoftObjectProperty CastFlags missing kFObjectPropertyBase");
    }

    // -----------------------------------------------------------------
    // FClassProperty round-trip (MetaClass payload, value slot is a
    // pointer).
    // -----------------------------------------------------------------
    {
        FClassProperty Prop(FFieldVariant{}, FName("ClassField"));
        void* Slot = nullptr;
        void* TestVal = reinterpret_cast<void*>(static_cast<::uintptr_t>(0x1122334455667788ull));
        Prop.SetValue(&Slot, &TestVal);
        Check(Slot == TestVal, "FClassProperty SetValue failed");

        const EClassCastFlags Flags = FClassProperty::StaticClass()->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFClassProperty),
              "FClassProperty CastFlags missing own bit");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFObjectPropertyBase),
              "FClassProperty CastFlags missing kFObjectPropertyBase");

        // MetaClass default is nullptr.
        Check(Prop.GetMetaClass() == nullptr, "FClassProperty default MetaClass != nullptr");
    }

    // -----------------------------------------------------------------
    // FSoftClassProperty round-trip.
    // -----------------------------------------------------------------
    {
        FSoftClassProperty Prop(FFieldVariant{}, FName("SoftClassField"));
        ::uint64 Slot = 0;
        ::uint64 TestVal = 0xFEDCBA9876543210ull;
        Prop.SetValue(&Slot, &TestVal);
        Check(Slot == TestVal, "FSoftClassProperty SetValue failed");

        const EClassCastFlags Flags = FSoftClassProperty::StaticClass()->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFSoftClassProperty),
              "FSoftClassProperty CastFlags missing own bit");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFObjectPropertyBase),
              "FSoftClassProperty CastFlags missing kFObjectPropertyBase");
    }

    // -----------------------------------------------------------------
    // FInterfaceProperty: pointer round-trip. CastFlags do NOT include
    // kFObjectPropertyBase (FInterfaceProperty is a peer of, not a
    // child of, FObjectProperty in XPact's flat hierarchy).
    // -----------------------------------------------------------------
    {
        FInterfaceProperty Prop(FFieldVariant{}, FName("InterfaceField"));
        void* Slot = nullptr;
        void* TestVal = reinterpret_cast<void*>(static_cast<::uintptr_t>(0x9876543210FEDCBAull));
        Prop.SetValue(&Slot, &TestVal);
        Check(Slot == TestVal, "FInterfaceProperty SetValue failed");

        const EClassCastFlags Flags = FInterfaceProperty::StaticClass()->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFInterfaceProperty),
              "FInterfaceProperty CastFlags missing own bit");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFProperty),
              "FInterfaceProperty CastFlags missing kFProperty parent gate");
        // FInterfaceProperty does NOT include kFObjectPropertyBase in
        // XPact's hierarchy (UE-relative divergence; see header doc).
        Check(!HasAllCastFlags(Flags, EClassCastFlags::kFObjectPropertyBase),
              "FInterfaceProperty CastFlags should NOT include kFObjectPropertyBase");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.ObjectPropertyGetSet: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.ObjectPropertyGetSet: PASS\n";
    return 0;
}
