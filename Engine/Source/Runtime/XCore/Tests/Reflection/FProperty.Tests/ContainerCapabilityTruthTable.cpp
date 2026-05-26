// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/ContainerCapabilityTruthTable.cpp -- verify that the
// cleared-bit + sentinel-return discipline (XCore-4b Subagent A FIX-A1)
// holds across container + delegate + struct FProperty subclasses.
// =====================================================================
//
// Prior Phase 4b.4b had every value-operation slot on FArrayProperty /
// FMapProperty / FSetProperty / FStructProperty /
// FMulticastInlineDelegateProperty / FMulticastSparseDelegateProperty
// declared as "supported" in their kCapabilities mask while the slot
// bodies were silently no-ops. This test pins the post-FIX-A1 contract:
//
//   * For each value-op slot (GetValue / SetValue / CopySingleValue /
//     CopyCompleteValue / InitializeValue / DestroyValue / Identical /
//     GetValueTypeHash) on container/delegate/struct subclasses, the
//     capability bit MUST be cleared at Phase 4b.4b.
//
//   * HasSlot(ESlot::X) returns false for every cleared slot.
//
//   * ContainsObjectReference IS implemented and remains set on
//     Array/Map/Set/MulticastInline/MulticastSparse (returns
//     conservative TRUE) and CLEARED on Struct (pending Phase 4b.5
//     Struct->ObjectRefProperties rewire; wrapper returns false sentinel).
//
//   * FDelegateProperty IS fully implemented (POD memcpy is correct for
//     the 16-byte FScriptDelegate) and keeps all bits SET.
//
// The test does NOT exercise the XPACT_CHECK(false) bodies (they would
// crash in Debug); it only verifies HasSlot's reported truth.
//
// =====================================================================

#include "Containers/TArray.h"
#include "HAL/FMemory.h"

#include "Reflection/FArrayProperty.h"
#include "Reflection/FDelegateProperty.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FMapProperty.h"
#include "Reflection/FMulticastInlineDelegateProperty.h"
#include "Reflection/FMulticastSparseDelegateProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"
#include "Reflection/FSetProperty.h"
#include "Reflection/FStructProperty.h"

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

    using ::XCore::Reflect::ESlot;
    using ::XCore::Reflect::FFakeVTable;
    using ::XCore::Reflect::FFieldClass;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FName;

    void CheckSlot(const FFakeVTable* Vt,
                   ESlot Slot,
                   bool bExpectedSet,
                   const char* TypeName,
                   const char* SlotName)
    {
        const bool bActual = Vt->HasSlot(Slot);
        if (bActual != bExpectedSet)
        {
            Check(false,
                  (std::string(TypeName) + "::" + SlotName +
                   " HasSlot=" + (bActual ? "true" : "false") +
                   "; expected " + (bExpectedSet ? "true" : "false"))
                      .c_str());
        }
    }

    template <typename PropT>
    void VerifyContainerCleared(const char* TypeName)
    {
        const FFieldClass* Cls = PropT::StaticClass();
        Check(Cls != nullptr, (std::string(TypeName) + " StaticClass null").c_str());
        if (Cls == nullptr) return;
        const FFakeVTable* Vt = Cls->FakeVTable;
        Check(Vt != nullptr, (std::string(TypeName) + " FakeVTable null").c_str());
        if (Vt == nullptr) return;

        // Value-op slots: every bit MUST be cleared.
        CheckSlot(Vt, ESlot::GetValue,           false, TypeName, "GetValue");
        CheckSlot(Vt, ESlot::SetValue,           false, TypeName, "SetValue");
        CheckSlot(Vt, ESlot::CopySingleValue,    false, TypeName, "CopySingleValue");
        CheckSlot(Vt, ESlot::CopyCompleteValue,  false, TypeName, "CopyCompleteValue");
        CheckSlot(Vt, ESlot::InitializeValue,    false, TypeName, "InitializeValue");
        CheckSlot(Vt, ESlot::DestroyValue,       false, TypeName, "DestroyValue");
        CheckSlot(Vt, ESlot::Identical,          false, TypeName, "Identical");
        CheckSlot(Vt, ESlot::GetValueTypeHash,   false, TypeName, "GetValueTypeHash");

        // ContainsObjectReference: SET on container/delegate subclasses
        // (conservative TRUE). Test caller asserts this explicitly per
        // subclass below via VerifyHasObjectRef.
    }

    template <typename PropT>
    void VerifyHasObjectRef(const char* TypeName, bool bExpected)
    {
        const FFieldClass* Cls = PropT::StaticClass();
        if (Cls == nullptr || Cls->FakeVTable == nullptr) return;
        CheckSlot(Cls->FakeVTable, ESlot::ContainsObjectReference,
                  bExpected, TypeName, "ContainsObjectReference");
    }

    template <typename PropT>
    void VerifyContainsObjectRefReturn(const char* TypeName, bool bExpected)
    {
        // End-to-end: through the FProperty wrapper. When HasSlot=false,
        // the wrapper returns false sentinel; when HasSlot=true, the
        // slot is invoked and the result is returned.
        PropT Prop(FFieldVariant{}, FName("F"));
        ::XCore::TArray<const ::XCore::Reflect::FStructProperty*> Encountered;
        const bool bActual = Prop.ContainsObjectReference(Encountered);
        if (bActual != bExpected)
        {
            Check(false,
                  (std::string(TypeName) + ".ContainsObjectReference returned " +
                   (bActual ? "true" : "false") + ", expected " +
                   (bExpected ? "true" : "false"))
                      .c_str());
        }
    }

    template <typename PropT>
    void VerifyDelegateAllSet(const char* TypeName)
    {
        // FDelegateProperty's slots ARE correctly implemented for the
        // 16-byte POD FScriptDelegate; all bits remain SET (unchanged
        // from pre-FIX-A1).
        const FFieldClass* Cls = PropT::StaticClass();
        if (Cls == nullptr || Cls->FakeVTable == nullptr) return;
        const FFakeVTable* Vt = Cls->FakeVTable;
        CheckSlot(Vt, ESlot::GetValue,                true, TypeName, "GetValue");
        CheckSlot(Vt, ESlot::SetValue,                true, TypeName, "SetValue");
        CheckSlot(Vt, ESlot::CopySingleValue,         true, TypeName, "CopySingleValue");
        CheckSlot(Vt, ESlot::CopyCompleteValue,       true, TypeName, "CopyCompleteValue");
        CheckSlot(Vt, ESlot::InitializeValue,         true, TypeName, "InitializeValue");
        CheckSlot(Vt, ESlot::DestroyValue,            true, TypeName, "DestroyValue");
        CheckSlot(Vt, ESlot::Identical,               true, TypeName, "Identical");
        CheckSlot(Vt, ESlot::GetValueTypeHash,        true, TypeName, "GetValueTypeHash");
        CheckSlot(Vt, ESlot::ContainsObjectReference, true, TypeName, "ContainsObjectReference");
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using namespace ::XCore::Reflect;

    // -----------------------------------------------------------------
    // FIX-A1 truth table -- container + multicast-delegate subclasses
    // have ONLY ContainsObjectReference set; all value-op slots cleared.
    // -----------------------------------------------------------------
    VerifyContainerCleared<FArrayProperty>                  ("FArrayProperty");
    VerifyContainerCleared<FMapProperty>                    ("FMapProperty");
    VerifyContainerCleared<FSetProperty>                    ("FSetProperty");
    VerifyContainerCleared<FMulticastInlineDelegateProperty>("FMulticastInlineDelegateProperty");
    VerifyContainerCleared<FMulticastSparseDelegateProperty>("FMulticastSparseDelegateProperty");

    // ContainsObjectReference bit SET on container / multicast-delegate.
    VerifyHasObjectRef<FArrayProperty>                  ("FArrayProperty",                  true);
    VerifyHasObjectRef<FMapProperty>                    ("FMapProperty",                    true);
    VerifyHasObjectRef<FSetProperty>                    ("FSetProperty",                    true);
    VerifyHasObjectRef<FMulticastInlineDelegateProperty>("FMulticastInlineDelegateProperty", true);
    VerifyHasObjectRef<FMulticastSparseDelegateProperty>("FMulticastSparseDelegateProperty", true);

    // End-to-end wrapper return: conservative TRUE for containers /
    // multicast-delegates with the bit set.
    VerifyContainsObjectRefReturn<FArrayProperty>                  ("FArrayProperty",                  true);
    VerifyContainsObjectRefReturn<FMapProperty>                    ("FMapProperty",                    true);
    VerifyContainsObjectRefReturn<FSetProperty>                    ("FSetProperty",                    true);
    VerifyContainsObjectRefReturn<FMulticastInlineDelegateProperty>("FMulticastInlineDelegateProperty", true);
    VerifyContainsObjectRefReturn<FMulticastSparseDelegateProperty>("FMulticastSparseDelegateProperty", true);

    // -----------------------------------------------------------------
    // FStructProperty -- ALL bits cleared at Phase 4b.4b (including
    // ContainsObjectReference; Phase 4b.5 will rewire to consult the
    // per-instance Struct->ObjectRefProperties dense array).
    // Wrapper returns false sentinel.
    // -----------------------------------------------------------------
    VerifyContainerCleared<FStructProperty>("FStructProperty");
    VerifyHasObjectRef<FStructProperty>      ("FStructProperty", false);
    VerifyContainsObjectRefReturn<FStructProperty>("FStructProperty", false);

    // -----------------------------------------------------------------
    // FDelegateProperty -- 16-byte POD FScriptDelegate; all slots
    // correctly implemented; all bits remain SET (unchanged by FIX-A1).
    // -----------------------------------------------------------------
    VerifyDelegateAllSet<FDelegateProperty>("FDelegateProperty");
    VerifyContainsObjectRefReturn<FDelegateProperty>("FDelegateProperty", true);

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.ContainerCapabilityTruthTable: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.ContainerCapabilityTruthTable: PASS\n";
    return 0;
}
