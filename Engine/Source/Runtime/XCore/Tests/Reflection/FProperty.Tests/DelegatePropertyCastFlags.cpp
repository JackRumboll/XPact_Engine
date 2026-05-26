// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/DelegatePropertyCastFlags.cpp -- FDelegateProperty /
// FMulticastInlineDelegateProperty / FMulticastSparseDelegateProperty
// CastFlag bit assignments (XCore-4b §5.5; Phase 4b.4b).
// =====================================================================
//
// Verifies:
//
//   1. FDelegateProperty CastFlags include kFDelegateProperty
//      (0x400000) + kFProperty.
//   2. FMulticastInlineDelegateProperty CastFlags include
//      kFMulticastInlineDelegateProperty (0x8000000) + kFProperty.
//   3. FMulticastSparseDelegateProperty CastFlags include
//      kFMulticastSparseDelegateProperty (0x10000000) + kFProperty.
//   4. The three bits are mutually distinct.
//   5. Each subclass's StaticClass()->FakeVTable is populated and
//      has ContainsObjectReference (the load-bearing slot for GC
//      reachability of delegate Target fields).
//   6. IsA round-trip via FFieldClass::IsChildOf works for the
//      delegate hierarchy.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FDelegateProperty.h"
#include "Reflection/FMulticastInlineDelegateProperty.h"
#include "Reflection/FMulticastSparseDelegateProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"

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
    using ::XCore::Reflect::ESlot;
    using ::XCore::Reflect::FDelegateProperty;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FMulticastInlineDelegateProperty;
    using ::XCore::Reflect::FMulticastSparseDelegateProperty;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FProperty;
    using ::XCore::Reflect::HasAllCastFlags;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // (1) FDelegateProperty CastFlags + own-bit value.
    // -----------------------------------------------------------------
    {
        FDelegateProperty Prop(FFieldVariant{}, FName("Del"));
        const auto Flags = FDelegateProperty::StaticClass()->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFDelegateProperty),
              "FDelegateProperty CastFlags missing kFDelegateProperty");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFProperty),
              "FDelegateProperty CastFlags missing kFProperty");

        // Verify exact bit position: 0x400000 (bit 22).
        Check(static_cast<::uint64>(EClassCastFlags::kFDelegateProperty) == 0x400000ull,
              "kFDelegateProperty != 0x400000");

        // IsA(FProperty) succeeds via SuperClass walk / CastFlags fast path.
        Check(Prop.IsA(FProperty::StaticClass()),
              "FDelegateProperty IsA(FProperty) returned false");
    }

    // -----------------------------------------------------------------
    // (2) FMulticastInlineDelegateProperty CastFlags + own-bit value.
    // -----------------------------------------------------------------
    {
        FMulticastInlineDelegateProperty Prop(FFieldVariant{}, FName("Multi"));
        const auto Flags = FMulticastInlineDelegateProperty::StaticClass()->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFMulticastInlineDelegateProperty),
              "FMulticastInlineDelegateProperty CastFlags missing own bit");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFProperty),
              "FMulticastInlineDelegateProperty CastFlags missing kFProperty");

        // Verify exact bit position: 0x8000000 (bit 27).
        Check(static_cast<::uint64>(EClassCastFlags::kFMulticastInlineDelegateProperty)
                  == 0x8000000ull,
              "kFMulticastInlineDelegateProperty != 0x8000000");

        Check(Prop.IsA(FProperty::StaticClass()),
              "FMulticastInlineDelegateProperty IsA(FProperty) returned false");
    }

    // -----------------------------------------------------------------
    // (3) FMulticastSparseDelegateProperty CastFlags + own-bit value.
    // -----------------------------------------------------------------
    {
        FMulticastSparseDelegateProperty Prop(FFieldVariant{}, FName("Sparse"));
        const auto Flags = FMulticastSparseDelegateProperty::StaticClass()->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFMulticastSparseDelegateProperty),
              "FMulticastSparseDelegateProperty CastFlags missing own bit");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFProperty),
              "FMulticastSparseDelegateProperty CastFlags missing kFProperty");

        // Verify exact bit position: 0x10000000 (bit 28).
        Check(static_cast<::uint64>(EClassCastFlags::kFMulticastSparseDelegateProperty)
                  == 0x10000000ull,
              "kFMulticastSparseDelegateProperty != 0x10000000");

        Check(Prop.IsA(FProperty::StaticClass()),
              "FMulticastSparseDelegateProperty IsA(FProperty) returned false");
    }

    // -----------------------------------------------------------------
    // (4) The three bits are mutually distinct.
    // -----------------------------------------------------------------
    {
        const ::uint64 D  = static_cast<::uint64>(EClassCastFlags::kFDelegateProperty);
        const ::uint64 MI = static_cast<::uint64>(EClassCastFlags::kFMulticastInlineDelegateProperty);
        const ::uint64 MS = static_cast<::uint64>(EClassCastFlags::kFMulticastSparseDelegateProperty);
        Check((D & MI) == 0, "kFDelegateProperty overlaps kFMulticastInline");
        Check((D & MS) == 0, "kFDelegateProperty overlaps kFMulticastSparse");
        Check((MI & MS) == 0, "kFMulticastInline overlaps kFMulticastSparse");
    }

    // -----------------------------------------------------------------
    // (5) ContainsObjectReference slot population.
    //
    // All three delegate variants MUST populate the slot (the delegate
    // value's Target field is an FObject*; GC must trace).
    // -----------------------------------------------------------------
    {
        FDelegateProperty                  Single(FFieldVariant{}, FName("D"));
        FMulticastInlineDelegateProperty   MInline(FFieldVariant{}, FName("MI"));
        FMulticastSparseDelegateProperty   MSparse(FFieldVariant{}, FName("MS"));

        Check(Single.GetDispatchTable()->HasSlot(ESlot::ContainsObjectReference),
              "FDelegateProperty.ContainsObjectReference slot not populated");
        Check(MInline.GetDispatchTable()->HasSlot(ESlot::ContainsObjectReference),
              "FMulticastInlineDelegateProperty.ContainsObjectReference slot not populated");
        Check(MSparse.GetDispatchTable()->HasSlot(ESlot::ContainsObjectReference),
              "FMulticastSparseDelegateProperty.ContainsObjectReference slot not populated");
    }

    // -----------------------------------------------------------------
    // (6) SuperClass chain reaches FProperty for all three.
    // -----------------------------------------------------------------
    {
        Check(FDelegateProperty::StaticClass()->GetSuperClass()
                  == FProperty::StaticClass(),
              "FDelegateProperty SuperClass != FProperty");
        Check(FMulticastInlineDelegateProperty::StaticClass()->GetSuperClass()
                  == FProperty::StaticClass(),
              "FMulticastInlineDelegateProperty SuperClass != FProperty");
        Check(FMulticastSparseDelegateProperty::StaticClass()->GetSuperClass()
                  == FProperty::StaticClass(),
              "FMulticastSparseDelegateProperty SuperClass != FProperty");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.DelegatePropertyCastFlags: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.DelegatePropertyCastFlags: PASS\n";
    return 0;
}
