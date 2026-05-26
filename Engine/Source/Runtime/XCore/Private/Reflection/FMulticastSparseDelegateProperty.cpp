// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMulticastSparseDelegateProperty.cpp -- XCore-4b §5.5 + §11.2;
// Phase 4b.4b. Sparse multicast delegate property
// (Rev 2 MVP add per FIX-3).
// =====================================================================
//
// FMulticastSparseDelegateProperty's value slot holds an
// FMulticastSparseDelegate by-value (sparse subscriber map).
//
// CAPABILITY TRUTH-TABLE DISCIPLINE (XCore-4b Subagent A FIX-A1):
//
//   Prior Phase 4b.4b shape claimed every slot was supported while
//   the slot bodies were silently no-ops. FIX: the value-operation
//   slots have their `kMulticastSparseCapabilities` bit CLEARED.
//   Direct dispatch (bypassing HasSlot) hits XPACT_CHECK(false).
//
//   ContainsObjectReference IS implemented (returns TRUE; subscriber
//   Targets are GC roots). Its bit remains set.
//
// =====================================================================

#include "Reflection/FMulticastSparseDelegateProperty.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"

#include "Macros/XPactMacros.h"   // XPACT_CHECK

#include <new>

namespace XCore::Reflect
{

namespace
{
    // -----------------------------------------------------------------
    // Unimplemented-slot bodies (FIX-A1). Phase 4b.5 wires typed
    // dispatch via FMulticastSparseDelegate's operations.
    // -----------------------------------------------------------------

    void GetValueSlot(const void* /*Instance*/, ::int32 /*ElementIndex*/, void* /*OutValue*/) noexcept
    {
        XPACT_CHECK(!"FMulticastSparseDelegateProperty::GetValue dispatch slot not yet "
                     "implemented; Phase 4b.5 will land typed delegate traversal. "
                     "Capability bit cleared in kMulticastSparseCapabilities.");
    }

    void SetValueSlot(void* /*Instance*/, ::int32 /*ElementIndex*/, const void* /*InValue*/) noexcept
    {
        XPACT_CHECK(!"FMulticastSparseDelegateProperty::SetValue dispatch slot not yet "
                     "implemented; Phase 4b.5 will land typed delegate traversal. "
                     "Capability bit cleared in kMulticastSparseCapabilities.");
    }

    void CopySingleValueSlot(void* /*Dest*/, const void* /*Src*/) noexcept
    {
        XPACT_CHECK(!"FMulticastSparseDelegateProperty::CopySingleValue dispatch slot not "
                     "yet implemented; Phase 4b.5 will deep-copy sparse subscriber map. "
                     "Capability bit cleared in kMulticastSparseCapabilities.");
    }

    void CopyCompleteValueSlot(void* /*Dest*/, const void* /*Src*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FMulticastSparseDelegateProperty::CopyCompleteValue dispatch slot "
                     "not yet implemented; Phase 4b.5 will deep-copy sparse subscriber map. "
                     "Capability bit cleared in kMulticastSparseCapabilities.");
    }

    void InitializeValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FMulticastSparseDelegateProperty::InitializeValue dispatch slot not "
                     "yet implemented; Phase 4b.5 will zero-init the delegate header. "
                     "Capability bit cleared in kMulticastSparseCapabilities.");
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FMulticastSparseDelegateProperty::DestroyValue dispatch slot not yet "
                     "implemented; Phase 4b.5 will release sparse map storage. "
                     "Capability bit cleared in kMulticastSparseCapabilities.");
    }

    bool IdenticalSlot(const void* /*A*/, const void* /*B*/, ::uint32 /*PortFlags*/) noexcept
    {
        XPACT_CHECK(!"FMulticastSparseDelegateProperty::Identical dispatch slot not yet "
                     "implemented; Phase 4b.5 will subscriber-wise compare. "
                     "Capability bit cleared in kMulticastSparseCapabilities.");
        return false;
    }

    ::uint64 GetValueTypeHashSlot(const void* /*PropertyValue*/) noexcept
    {
        XPACT_CHECK(!"FMulticastSparseDelegateProperty::GetValueTypeHash dispatch slot "
                     "not yet implemented; multicast delegates are not hashable keys. "
                     "Capability bit cleared in kMulticastSparseCapabilities.");
        return 0;
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- LOAD-BEARING; capability bit kept set.
    //
    // Subscriber map values contain FObject* Target references when
    // populated; the slot returns TRUE conservatively.
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        return true;
    }

    // -----------------------------------------------------------------
    // kMulticastSparseCapabilities -- TRUTH TABLE per FIX-A1.
    //
    // SET: ContainsObjectReference (returns TRUE).
    // CLEARED: all value-op slots (Phase 4b.5 deferred).
    // -----------------------------------------------------------------
    constexpr ::uint32 kMulticastSparseCapabilities =
          CapabilityBit(ESlot::ContainsObjectReference);

} // anonymous

constinit const FFakeVTable kFMulticastSparseDelegatePropertyFakeVTable{
    /* Capabilities    */ kMulticastSparseCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&GetValueSlot,
        (void(*)(void))&SetValueSlot,
        (void(*)(void))&CopySingleValueSlot,
        (void(*)(void))&CopyCompleteValueSlot,
        (void(*)(void))&InitializeValueSlot,
        (void(*)(void))&DestroyValueSlot,
        (void(*)(void))&IdenticalSlot,
        nullptr, nullptr,
        (void(*)(void))&ContainsObjectReferenceSlot,
        nullptr, nullptr,
        (void(*)(void))&GetValueTypeHashSlot,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFMulticastSparseDelegatePropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFMulticastSparseDelegateProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FMulticastSparseDelegateProperty::ConstructFn,
    /* FakeVTable */ &kFMulticastSparseDelegatePropertyFakeVTable,
};

FMulticastSparseDelegateProperty::FMulticastSparseDelegateProperty(
    FFieldVariant InOwner, FName InName,
    FFunctionDescriptor* InSignatureFunction) noexcept
    : FProperty(&kFMulticastSparseDelegatePropertyStaticClass, InOwner, InName)
    , SignatureFunction(InSignatureFunction)
{
    // ElementSize populated at FClass::Link time (Phase 4b.5).
}

void FMulticastSparseDelegateProperty::ConstructFn(
    FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FMulticastSparseDelegateProperty(Owner, Name);
}

const FFieldClass* FMulticastSparseDelegateProperty::StaticClass() noexcept
{
    return &GetFMulticastSparseDelegatePropertyStaticClass();
}

const FFieldClass& GetFMulticastSparseDelegatePropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFMulticastSparseDelegatePropertyStaticClass.Name =
            FName("FMulticastSparseDelegateProperty");
        return true;
    }();
    (void)Init;
    return kFMulticastSparseDelegatePropertyStaticClass;
}

} // namespace XCore::Reflect
